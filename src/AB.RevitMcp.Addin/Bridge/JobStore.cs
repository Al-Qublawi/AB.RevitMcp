using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;

namespace AB.RevitMcp.Addin.Bridge
{
    /// <summary>
    /// Calls that were still running on Revit's UI thread when their caller stopped waiting.
    ///
    /// Before 1.6.0 such a call answered TIMEOUT and its result was thrown away even though the
    /// work went on to finish - so a long export or test run could never be collected, and a
    /// blind retry ran it twice. Now the caller gets a jobId and revit_get_job_result picks the
    /// result up later. Lives in memory for the Revit session; the oldest finished jobs are
    /// dropped past <see cref="IpcConstants.MaxJobs"/>. Thread-safe; never touches the Revit API.
    /// </summary>
    public sealed class JobStore
    {
        private sealed class Job
        {
            public string Id;
            public string Tool;
            public string Client;
            public DateTime StartedUtc;
            public DateTime FinishedUtc;
            public BridgeResponse Response;      // null while running
            public Task<object> Completion;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>();
        private readonly RequestLog _log;

        public JobStore(RequestLog log) { _log = log; }

        public int RunningCount
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    foreach (Job j in _jobs.Values) if (j.Response == null) n++;
                    return n;
                }
            }
        }

        /// <summary>Registers a running call and returns what its caller is told instead of a result.</summary>
        public JsonValue Register(BridgeRequest request, DateTime startedUtc, Task<object> completion, int waitedMs)
        {
            var job = new Job
            {
                Id = request.Id,
                Tool = request.Tool,
                Client = request.ClientName,
                StartedUtc = startedUtc,
                Completion = completion
            };

            lock (_lock)
            {
                _jobs[job.Id] = job;
                _order.Add(job.Id);
                Trim();
            }

            completion.ContinueWith(delegate (Task<object> t)
            {
                BridgeResponse response;
                if (t.Status == TaskStatus.RanToCompletion && t.Result is BridgeResponse)
                    response = (BridgeResponse)t.Result;
                else if (t.IsCanceled)
                    response = BridgeResponse.Failure(job.Id, BridgeErrorCodes.Cancelled, "The job was cancelled.");
                else
                {
                    Exception ex = t.Exception != null ? t.Exception.GetBaseException() : null;
                    response = BridgeResponse.Failure(job.Id, BridgeErrorCodes.Internal,
                        "The job failed: " + (ex != null ? ex.Message : "unknown error"));
                }

                lock (_lock)
                {
                    job.Response = response;
                    job.FinishedUtc = DateTime.UtcNow;
                }

                try
                {
                    ToolDescriptor d = ToolCatalog.Find(job.Tool);
                    _log.LogRequest(job.Id, job.Tool,
                        (d != null ? d.Category.ToString().ToLowerInvariant() : "bridge") + "-job",
                        response.Ok, (job.FinishedUtc - job.StartedUtc).TotalMilliseconds,
                        response.ErrorCode, response.ErrorMessage,
                        response.Ok && response.Result != null ? response.Result.ToJson().Length : 0, job.Client);
                }
                catch (Exception) { }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return J.O(
                "state", "running",
                "jobId", job.Id,
                "tool", job.Tool,
                "startedUtc", startedUtc.ToString("o"),
                "waitedMs", waitedMs,
                "message", "NOT FINISHED YET - '" + job.Tool + "' is still running inside Revit and will run to " +
                           "the end. Do NOT call it again (it would run twice). Collect the result with " +
                           ToolCatalog.GetJobResultToolName + " {\"jobId\": \"" + job.Id + "\"}.");
        }

        /// <summary>Answers revit_get_job_result, optionally waiting for the job to finish first.</summary>
        public async Task<BridgeResponse> LookupAsync(string requestId, string jobId, int waitSeconds, CancellationToken ct)
        {
            Job job;
            lock (_lock) { _jobs.TryGetValue(jobId ?? string.Empty, out job); }

            if (job == null)
            {
                return BridgeResponse.Failure(requestId, BridgeErrorCodes.UnknownJob,
                    "No job '" + jobId + "' in this Revit session. Jobs are kept in memory until Revit closes " +
                    "(the last " + IpcConstants.MaxJobs + "); a call that finished within its wait never became a job.");
            }

            if (waitSeconds > 0 && !job.Completion.IsCompleted)
            {
                Task finished = await Task.WhenAny(job.Completion,
                    Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct)).ConfigureAwait(false);
                if (finished == job.Completion)
                {
                    // The continuation above records the response; give it its moment.
                    for (int i = 0; i < 50; i++)
                    {
                        lock (_lock) { if (job.Response != null) break; }
                        await Task.Delay(20, ct).ConfigureAwait(false);
                    }
                }
            }

            BridgeResponse response;
            DateTime finishedUtc;
            lock (_lock) { response = job.Response; finishedUtc = job.FinishedUtc; }

            if (response == null)
            {
                return BridgeResponse.Success(requestId, J.O(
                    "jobId", job.Id,
                    "tool", job.Tool,
                    "state", "running",
                    "startedUtc", job.StartedUtc.ToString("o"),
                    "runningSeconds", Math.Round((DateTime.UtcNow - job.StartedUtc).TotalSeconds, 1),
                    "message", "Still running inside Revit. Ask again later (waitSeconds lets this call wait); " +
                               "do not repeat the original call."), 0);
            }

            double seconds = Math.Round((finishedUtc - job.StartedUtc).TotalSeconds, 1);
            if (!response.Ok)
            {
                JsonValue details = response.ErrorDetails != null && response.ErrorDetails.IsObject
                    ? response.ErrorDetails.Clone() : JsonValue.NewObject();
                details.Set("jobId", job.Id);
                details.Set("tool", job.Tool);
                details.Set("runSeconds", seconds);
                return BridgeResponse.Failure(requestId, response.ErrorCode,
                    "Job " + job.Id + " ('" + job.Tool + "') finished with an error: " + response.ErrorMessage, details);
            }

            JsonValue result = response.Result != null && response.Result.IsObject
                ? response.Result.Clone()
                : J.O("value", response.Result);
            result.Set("jobId", job.Id);
            result.Set("state", "finished");
            result.Set("tool", job.Tool);
            result.Set("runSeconds", seconds);
            return BridgeResponse.Success(requestId, result, 0);
        }

        private void Trim()
        {
            // Called under _lock. Drop the oldest FINISHED jobs first; never a running one.
            for (int i = 0; i < _order.Count && _jobs.Count > IpcConstants.MaxJobs;)
            {
                Job j;
                if (_jobs.TryGetValue(_order[i], out j) && j.Response == null) { i++; continue; }
                _jobs.Remove(_order[i]);
                _order.RemoveAt(i);
            }
        }
    }
}

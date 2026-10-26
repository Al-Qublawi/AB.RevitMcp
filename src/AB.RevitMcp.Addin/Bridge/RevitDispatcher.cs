using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace AB.RevitMcp.Addin.Bridge
{
    /// <summary>
    /// THE thread-safety boundary of the whole product.
    ///
    /// Named-pipe handlers run on thread-pool threads. The Revit API may only be touched from
    /// Revit's main UI thread, and only inside an API context. This class is the single legal
    /// crossing: background code calls <see cref="EnqueueAsync"/>, the work item lands in a
    /// lock-free queue, an ExternalEvent is raised, and Revit calls <see cref="Execute"/> back on
    /// the UI thread when it is safe to do so.
    ///
    /// Nothing else in this add-in is allowed to call the Revit API off the UI thread.
    /// </summary>
    public sealed class RevitDispatcher : IExternalEventHandler, IDisposable
    {
        /// <summary>Milliseconds of continuous execution after which we yield the UI thread back to Revit.</summary>
        private const int UiSliceMs = 1500;

        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        private ExternalEvent _externalEvent;
        private long _completed;
        private long _timedOut;
        private long _stillRunning;
        private volatile WorkItem _current;
        private double _totalExecMs;
        private volatile string _lastError;
        private int _disposed;

        /// <summary>Must be called from Revit's main thread (IExternalApplication.OnStartup).</summary>
        public void Initialize()
        {
            if (_externalEvent == null) _externalEvent = ExternalEvent.Create(this);
        }

        public bool IsInitialized { get { return _externalEvent != null; } }
        public int PendingCount { get { return _queue.Count; } }
        public long CompletedCount { get { return Interlocked.Read(ref _completed); } }
        public long TimedOutCount { get { return Interlocked.Read(ref _timedOut); } }
        public string LastError { get { return _lastError; } }

        public double AverageExecutionMs
        {
            get
            {
                long done = Interlocked.Read(ref _completed);
                return done == 0 ? 0 : Math.Round(_totalExecMs / done, 2);
            }
        }

        /// <summary>
        /// Queues work for the Revit UI thread and awaits its result.
        ///
        /// The timeout protects the CALLER, not Revit: if a Revit API call blocks (a modal dialog,
        /// a huge regeneration), we stop waiting and return a TIMEOUT error rather than letting the
        /// MCP client hang forever. Work that has not started yet is discarded on timeout; work
        /// already running on the UI thread is allowed to finish, because forcibly aborting the
        /// Revit UI thread would corrupt the document.
        /// </summary>
        public async Task<T> EnqueueAsync<T>(string label, Func<UIApplication, T> work, int timeoutMs, CancellationToken ct,
                                             string jobId = null)
        {
            if (work == null) throw new ArgumentNullException("work");
            if (_externalEvent == null)
                throw new InvalidOperationException("RevitDispatcher.Initialize() has not run - the bridge is not ready.");

            var item = new WorkItem(label, jobId, delegate (UIApplication app) { return (object)work(app); });
            _queue.Enqueue(item);

            // Legal from any thread: this is the whole point of ExternalEvent.
            _externalEvent.Raise();

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task<object> resultTask = item.Completion.Task;
                Task delayTask = Task.Delay(timeoutMs <= 0 ? Timeout.Infinite : timeoutMs, timeoutCts.Token);
                Task winner = await Task.WhenAny(resultTask, delayTask).ConfigureAwait(false);

                if (winner == resultTask)
                {
                    timeoutCts.Cancel();                 // stop the timer task
                    object value = await resultTask.ConfigureAwait(false);
                    return (T)value;
                }

                ct.ThrowIfCancellationRequested();

                if (!item.TryAbandon())
                {
                    // It is already running on the UI thread and will run to the end: hand the
                    // caller a job to collect instead of a TIMEOUT that throws the result away.
                    Interlocked.Increment(ref _stillRunning);
                    throw new StillRunningException(label, item.StartedUtc, item.Completion.Task);
                }

                // Never started, and now never will: nothing of it ran.
                Interlocked.Increment(ref _timedOut);
                WorkItem busy = _current;
                string busyWith = busy != null
                    ? " Revit is busy with '" + busy.Label + "'" +
                      (busy.JobId != null ? " (job " + busy.JobId + ")" : string.Empty) + ", running for " +
                      Math.Round((DateTime.UtcNow - busy.StartedUtc).TotalSeconds) + " s."
                    : " Revit may be busy or showing a dialog.";
                throw new TimeoutException(
                    "Revit did not start '" + label + "' within " + timeoutMs + " ms, so it was not run." +
                    busyWith + " Retry when Revit is free.");
            }
        }

        /// <summary>The work item running on the UI thread right now, if any (for status and messages).</summary>
        public string CurrentLabel { get { WorkItem w = _current; return w != null ? w.Label : null; } }
        public string CurrentJobId { get { WorkItem w = _current; return w != null ? w.JobId : null; } }
        public DateTime CurrentStartedUtc { get { WorkItem w = _current; return w != null ? w.StartedUtc : DateTime.MinValue; } }
        public long StillRunningCount { get { return Interlocked.Read(ref _stillRunning); } }

        /// <summary>
        /// Called by Revit ON THE UI THREAD. Drains the queue, yielding back to Revit periodically
        /// so a burst of requests cannot freeze the application.
        /// </summary>
        public void Execute(UIApplication app)
        {
            var slice = Stopwatch.StartNew();

            WorkItem item;
            while (_queue.TryDequeue(out item))
            {
                // Atomic: either this starts it, or the caller already gave up on it.
                if (!item.TryStart())
                {
                    item.Completion.TrySetCanceled();
                    continue;
                }

                var sw = Stopwatch.StartNew();
                _current = item;
                try
                {
                    object result = item.Work(app);
                    sw.Stop();
                    RecordSuccess(sw.Elapsed.TotalMilliseconds);
                    item.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _lastError = item.Label + ": " + ex.Message;
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    _current = null;
                }

                if (slice.ElapsedMilliseconds > UiSliceMs && !_queue.IsEmpty)
                {
                    // Hand the UI thread back to Revit and ask to be called again immediately.
                    try { _externalEvent.Raise(); } catch (Exception) { }
                    return;
                }
            }
        }

        private void RecordSuccess(double elapsedMs)
        {
            Interlocked.Increment(ref _completed);
            // Not strictly atomic, but this only feeds a status readout.
            _totalExecMs += elapsedMs;
        }

        public string GetName() { return "AB Revit MCP Bridge dispatcher"; }

        /// <summary>Fails every queued item - used when the bridge stops or Revit shuts down.</summary>
        public void DrainAndFail(string reason)
        {
            WorkItem item;
            while (_queue.TryDequeue(out item))
                item.Completion.TrySetException(new OperationCanceledException(reason));
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1) return;
            DrainAndFail("The Revit MCP bridge is shutting down.");
            try { if (_externalEvent != null) _externalEvent.Dispose(); }
            catch (Exception) { }
            _externalEvent = null;
        }

        // ------------------------------------------------------------------

        private sealed class WorkItem
        {
            private const int Queued = 0, Started = 1, Abandoned = 2;
            private int _state;

            public WorkItem(string label, string jobId, Func<UIApplication, object> work)
            {
                Label = label ?? "work";
                JobId = jobId;
                Work = work;
                // RunContinuationsAsynchronously is REQUIRED: without it the awaiting pipe handler
                // would resume inline on Revit's UI thread and could deadlock the application.
                Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string Label { get; private set; }
            public string JobId { get; private set; }
            public Func<UIApplication, object> Work { get; private set; }
            public TaskCompletionSource<object> Completion { get; private set; }
            public DateTime StartedUtc { get; private set; }

            /// <summary>
            /// Queued -> Started. Compare-and-swap with <see cref="TryAbandon"/>, so an item can never
            /// be reported to the caller as "not run" and then run anyway.
            /// </summary>
            public bool TryStart()
            {
                StartedUtc = DateTime.UtcNow;
                return Interlocked.CompareExchange(ref _state, Started, Queued) == Queued;
            }

            /// <summary>Queued -> Abandoned. False when it has already started.</summary>
            public bool TryAbandon()
            {
                return Interlocked.CompareExchange(ref _state, Abandoned, Queued) == Queued;
            }
        }
    }

    /// <summary>
    /// The caller's wait ran out while its work was ALREADY running on the UI thread. The work
    /// continues to the end; <see cref="Completion"/> delivers its result.
    /// </summary>
    public sealed class StillRunningException : Exception
    {
        public StillRunningException(string label, DateTime startedUtc, Task<object> completion)
            : base("'" + label + "' is still running in Revit.")
        {
            StartedUtc = startedUtc;
            Completion = completion;
        }

        public DateTime StartedUtc { get; private set; }
        public Task<object> Completion { get; private set; }
    }
}

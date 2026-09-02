using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Addin.Bridge
{
    public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    /// <summary>
    /// Structured newline-delimited JSON logging. One object per line means the log can be piped
    /// straight into jq, Excel or a log shipper without parsing English sentences.
    ///
    /// Files roll daily into %LOCALAPPDATA%\ABRevitMcp\logs. The most recent entries are also kept
    /// in memory so the ribbon's Status dialog can show them without touching the disk.
    /// </summary>
    public sealed class RequestLog
    {
        private const int RingCapacity = 200;

        private readonly object _sync = new object();
        private readonly Queue<string> _ring = new Queue<string>(RingCapacity);
        private readonly UTF8Encoding _utf8 = new UTF8Encoding(false);
        private readonly int _processId;
        private readonly string _revitVersion;

        private string _currentFile;
        private DateTime _currentDate = DateTime.MinValue;
        private bool _diskFailureReported;

        public RequestLog(int processId, string revitVersion)
        {
            _processId = processId;
            _revitVersion = revitVersion;
            MinimumLevel = LogLevel.Info;
        }

        public LogLevel MinimumLevel { get; set; }

        public string LogDirectory { get { return IpcConstants.LogDirectory; } }

        /// <summary>Logs a completed tool call with its timing and outcome.</summary>
        public void LogRequest(string requestId, string tool, string category, bool ok,
                               double durationMs, string errorCode, string errorMessage,
                               int resultBytes, string client)
        {
            JsonValue entry = NewEntry(ok ? LogLevel.Info : LogLevel.Warn, "request");
            entry.Set("requestId", requestId);
            entry.Set("tool", tool);
            entry.Set("toolCategory", category);
            entry.Set("ok", ok);
            entry.Set("durationMs", Math.Round(durationMs, 2));
            entry.Set("resultBytes", resultBytes);
            if (!string.IsNullOrEmpty(client)) entry.Set("client", client);
            if (!ok)
            {
                entry.Set("errorCode", errorCode);
                entry.Set("errorMessage", Truncate(errorMessage, 800));
            }
            Write(entry);
        }

        public void Info(string message, JsonValue data = null) { Event(LogLevel.Info, message, data); }
        public void Warn(string message, JsonValue data = null) { Event(LogLevel.Warn, message, data); }
        public void Debug(string message, JsonValue data = null) { Event(LogLevel.Debug, message, data); }

        public void Error(string message, Exception ex = null, JsonValue data = null)
        {
            JsonValue entry = NewEntry(LogLevel.Error, "event");
            entry.Set("message", Truncate(message, 800));
            if (ex != null)
            {
                entry.Set("exception", ex.GetType().FullName);
                entry.Set("exceptionMessage", Truncate(ex.Message, 800));
                entry.Set("stack", Truncate(ex.StackTrace, 4000));
            }
            if (data != null && !data.IsNull) entry.Set("data", data);
            Write(entry);
        }

        private void Event(LogLevel level, string message, JsonValue data)
        {
            if (level < MinimumLevel) return;
            JsonValue entry = NewEntry(level, "event");
            entry.Set("message", Truncate(message, 800));
            if (data != null && !data.IsNull) entry.Set("data", data);
            Write(entry);
        }

        private JsonValue NewEntry(LogLevel level, string kind)
        {
            JsonValue e = JsonValue.NewObject();
            e.Set("ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            e.Set("level", level.ToString().ToLowerInvariant());
            e.Set("kind", kind);
            e.Set("pid", _processId);
            e.Set("revit", _revitVersion);
            return e;
        }

        private void Write(JsonValue entry)
        {
            string line = entry.ToJson(false);

            lock (_sync)
            {
                if (_ring.Count >= RingCapacity) _ring.Dequeue();
                _ring.Enqueue(line);

                try
                {
                    EnsureFile();
                    File.AppendAllText(_currentFile, line + Environment.NewLine, _utf8);
                    _diskFailureReported = false;
                }
                catch (Exception)
                {
                    // Never let logging break a request. Report the problem once, then stay quiet.
                    if (!_diskFailureReported)
                    {
                        _diskFailureReported = true;
                        System.Diagnostics.Debug.WriteLine("[AB.RevitMcp] Unable to write the log file.");
                    }
                }
            }
        }

        private void EnsureFile()
        {
            DateTime today = DateTime.UtcNow.Date;
            if (_currentFile != null && _currentDate == today) return;

            Directory.CreateDirectory(IpcConstants.LogDirectory);
            _currentDate = today;
            _currentFile = Path.Combine(IpcConstants.LogDirectory,
                "bridge-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".ndjson");
        }

        /// <summary>Most recent entries, newest last - for the ribbon Status dialog.</summary>
        public List<string> RecentEntries(int count)
        {
            lock (_sync)
            {
                var all = new List<string>(_ring);
                if (count >= all.Count) return all;
                return all.GetRange(all.Count - count, count);
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...[truncated]";
        }
    }
}

// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace ABAdvTools
{
    internal enum UpdateStatus
    {
        /// <summary>No check has run yet, or checks are switched off.</summary>
        Unknown,
        UpToDate,
        UpdateAvailable,

        /// <summary>
        /// GitHub could not be reached or answered with no release: offline, a proxy, a rate limit,
        /// a private repository, or a repository with nothing published yet.
        /// </summary>
        Unavailable
    }

    /// <summary>What one check found for one add-in.</summary>
    internal sealed class UpdateResult
    {
        public AdvToolsProduct Product { get; set; }
        public UpdateStatus Status { get; set; }
        public string LatestVersion { get; set; }

        /// <summary>The release page to send the user to (falls back to /releases/latest).</summary>
        public string ReleaseUrl { get; set; }

        /// <summary>True when this is a newer version the user has not been told about yet.</summary>
        public bool ShouldNotify { get; set; }

        /// <summary>Why the check produced no answer, for the About dialog.</summary>
        public string Detail { get; set; }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case UpdateStatus.UpToDate: return "Up to date";
                    case UpdateStatus.UpdateAvailable: return "Update available";
                    case UpdateStatus.Unavailable: return "No release information";
                    default: return "Not checked";
                }
            }
        }
    }

    /// <summary>
    /// Asks GitHub whether an AB add-in has a newer release.
    ///
    /// The rules, because this runs inside someone's Revit or Navisworks:
    ///   - never on the UI thread, never blocking startup
    ///   - fails silently: no network, a proxy, a firewall or a rate limit are all normal
    ///   - at most one automatic request a day per add-in, and one notice per new version
    ///   - one anonymous GET to the public releases API. Nothing is uploaded, no telemetry,
    ///     no identifiers
    ///   - switchable off for the whole suite from the About dialog
    /// </summary>
    internal static class UpdateChecker
    {
        private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
        private static readonly object Gate = new object();

        /// <summary>Test hook: replaces the network call. Null in production.</summary>
        internal static Func<AdvToolsProduct, string> FetchOverride { get; set; }

        /// <summary>
        /// The automatic startup check. Throttled to once a day per add-in; between checks it
        /// still reports what the last check saw. Returns null when checks are off.
        /// Call from a background thread.
        /// </summary>
        public static UpdateResult CheckAutomatically(AdvToolsProduct product)
        {
            try
            {
                if (product == null || !product.HasRepository) return null;
                if (!AdvToolsSettings.CheckForUpdates) return null;
                if (product.AutomaticChecksAllowed != null && !product.AutomaticChecksAllowed()) return null;

                return Check(product, false);
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Automatic update check failed for " + SafeId(product) + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Checks now, ignoring the daily throttle and the on/off setting - the user asked.
        /// Call from a background thread.
        /// </summary>
        public static UpdateResult CheckNow(AdvToolsProduct product)
        {
            try
            {
                return Check(product, true);
            }
            catch (Exception ex)
            {
                return new UpdateResult { Product = product, Status = UpdateStatus.Unavailable, Detail = ex.Message };
            }
        }

        /// <summary>Records that the user has been shown this version, so it is shown only once.</summary>
        public static void MarkNotified(AdvToolsProduct product, string version)
        {
            if (product == null) return;
            UpdateState state = UpdateState.Load(product.Id);
            state.NotifiedVersion = version;
            state.Save(product.Id);
        }

        private static UpdateResult Check(AdvToolsProduct product, bool force)
        {
            if (product == null) return null;
            if (!product.HasRepository)
                return new UpdateResult { Product = product, Status = UpdateStatus.Unavailable, Detail = "No repository configured." };

            UpdateState state = UpdateState.Load(product.Id);

            string latestTag = state.LastSeenVersion;
            string releaseUrl = state.LastSeenUrl;
            string detail = null;

            if (force || state.IsDue(AutomaticInterval))
            {
                string json = Fetch(product, out detail);
                state.LastCheckUtc = DateTime.UtcNow;

                if (json != null)
                {
                    string tag = ExtractJsonString(json, "tag_name");
                    if (!string.IsNullOrEmpty(tag))
                    {
                        latestTag = tag;
                        releaseUrl = ExtractJsonString(json, "html_url");
                        state.LastSeenVersion = tag;
                        state.LastSeenUrl = releaseUrl;
                    }
                    else
                    {
                        detail = "GitHub returned no release tag.";
                    }
                }

                state.Save(product.Id);
            }

            if (string.IsNullOrEmpty(latestTag))
                return new UpdateResult { Product = product, Status = UpdateStatus.Unavailable, Detail = detail };

            string latest = Normalise(latestTag);
            bool newer = Compare(latest, product.Version) > 0;

            var result = new UpdateResult
            {
                Product = product,
                LatestVersion = latest,
                ReleaseUrl = IsReleasePage(releaseUrl) ? releaseUrl : product.LatestReleasePageUrl,
                Status = newer ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate,
                ShouldNotify = newer && !string.Equals(latest, Normalise(state.NotifiedVersion), StringComparison.OrdinalIgnoreCase),
                Detail = detail
            };

            if (newer)
                AdvToolsLog.Info(product.Id + ": " + latest + " is available (running " + product.Version + ").");

            return result;
        }

        private static string Fetch(AdvToolsProduct product, out string detail)
        {
            detail = null;

            if (FetchOverride != null) return FetchOverride(product);

            try
            {
                // .NET Framework can still negotiate TLS 1.0, which GitHub refuses.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                catch { }

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);

                    // GitHub rejects requests with no User-Agent.
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "ABAdvTools/" + AdvToolsBrand.KitVersion + " " + product.Id + "/" + product.Version);
                    client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                    using (HttpResponseMessage response = client.GetAsync(product.LatestReleaseApiUrl)
                                                                .GetAwaiter().GetResult())
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            // A private repository and a repository with no release look identical.
                            detail = "No public release found on GitHub.";
                            return null;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            detail = "GitHub answered " + (int)response.StatusCode + ".";
                            return null;
                        }

                        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex)
            {
                // Offline, proxied, firewalled or rate limited are all ordinary. Not an error.
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                detail = "Could not reach GitHub (" + root.Message + ").";
                AdvToolsLog.Info(product.Id + ": update check could not reach GitHub: " + root.Message);
                return null;
            }
        }

        /// <summary>
        /// Only a GitHub release page is trusted as a link target. The response also carries the
        /// author's profile under the same field name, nested, so anything else falls back to the
        /// repository's /releases/latest page.
        /// </summary>
        private static bool IsReleasePage(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                   url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
                   url.IndexOf("/releases/", StringComparison.OrdinalIgnoreCase) > 0;
        }

        private static string SafeId(AdvToolsProduct product)
        {
            return product == null ? "(null)" : product.Id;
        }

        // ------------------------------------------------------------------ parsing

        /// <summary>
        /// Pulls one top-level string field out of a JSON document. Hand-parsed on purpose: a JSON
        /// library inside an add-in that loads beside other vendors' add-ins is a known source of
        /// assembly-version conflicts, and two fields do not justify that risk.
        /// </summary>
        internal static string ExtractJsonString(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return null;

            string needle = "\"" + field + "\"";
            int at = 0;
            while (true)
            {
                at = json.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0) return null;

                int colon = SkipWhitespace(json, at + needle.Length);
                if (colon < json.Length && json[colon] == ':')
                {
                    int open = SkipWhitespace(json, colon + 1);
                    if (open >= json.Length || json[open] != '"') return null;   // not a string value
                    return ReadString(json, open + 1);
                }

                // The name appeared as a value, not a key. Keep looking.
                at += needle.Length;
            }
        }

        private static int SkipWhitespace(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            return index;
        }

        private static string ReadString(string json, int start)
        {
            var value = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') return value.ToString();

                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[++i];
                    switch (next)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'n': value.Append('\n'); break;
                        case 't': value.Append('\t'); break;
                        case 'r': value.Append('\r'); break;
                        case 'u':
                            int code;
                            if (i + 4 < json.Length &&
                                int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code))
                            {
                                value.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: value.Append(next); break;
                    }
                    continue;
                }

                value.Append(c);
            }
            return null;   // unterminated
        }

        // ------------------------------------------------------------------ versions

        /// <summary>"v1.2.0" and "1.2.0" are the same thing.</summary>
        internal static string Normalise(string version)
        {
            if (string.IsNullOrEmpty(version)) return string.Empty;
            string v = version.Trim();
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
            return v;
        }

        /// <summary>
        /// Numeric comparison, part by part. Returns &gt;0 when left is newer. "1.10.0" must beat
        /// "1.9.0", which a string comparison gets wrong.
        /// </summary>
        internal static int Compare(string left, string right)
        {
            string[] a = Normalise(left).Split('.');
            string[] b = Normalise(right).Split('.');
            int parts = Math.Max(a.Length, b.Length);

            for (int i = 0; i < parts; i++)
            {
                int x = PartAt(a, i);
                int y = PartAt(b, i);
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static int PartAt(string[] parts, int index)
        {
            if (index >= parts.Length) return 0;

            // Tolerate "1.2.0-beta" by reading the leading digits only.
            var digits = new StringBuilder();
            foreach (char c in parts[index])
            {
                if (c < '0' || c > '9') break;
                digits.Append(c);
            }

            int value;
            return int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }

        // ------------------------------------------------------------------ state

        /// <summary>Per add-in record of the last check, in %LOCALAPPDATA%\AB Adv Tools\Updates.</summary>
        private sealed class UpdateState
        {
            public DateTime? LastCheckUtc;
            public string LastSeenVersion;
            public string LastSeenUrl;
            public string NotifiedVersion;

            private static string PathFor(string productId)
            {
                return Path.Combine(AdvToolsBrand.DataRoot, "Updates", productId + ".txt");
            }

            public bool IsDue(TimeSpan interval)
            {
                return LastCheckUtc == null || DateTime.UtcNow - LastCheckUtc.Value >= interval;
            }

            public static UpdateState Load(string productId)
            {
                var state = new UpdateState();
                try
                {
                    lock (Gate)
                    {
                        string file = PathFor(productId);
                        if (!File.Exists(file)) return state;

                        foreach (string raw in File.ReadAllLines(file))
                        {
                            int eq = raw.IndexOf('=');
                            if (eq <= 0) continue;
                            string key = raw.Substring(0, eq).Trim();
                            string value = raw.Substring(eq + 1).Trim();

                            if (key == "LastCheckUtc")
                            {
                                DateTime when;
                                if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                                      DateTimeStyles.RoundtripKind, out when))
                                    state.LastCheckUtc = when.ToUniversalTime();
                            }
                            else if (key == "LastSeenVersion") state.LastSeenVersion = value;
                            else if (key == "LastSeenUrl") state.LastSeenUrl = value;
                            else if (key == "NotifiedVersion") state.NotifiedVersion = value;
                        }
                    }
                }
                catch { }
                return state;
            }

            public void Save(string productId)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# AB Adv Tools update state for " + productId + ". Safe to delete.");
                    sb.AppendLine("LastCheckUtc=" + (LastCheckUtc.HasValue
                        ? LastCheckUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                        : string.Empty));
                    sb.AppendLine("LastSeenVersion=" + (LastSeenVersion ?? string.Empty));
                    sb.AppendLine("LastSeenUrl=" + (LastSeenUrl ?? string.Empty));
                    sb.AppendLine("NotifiedVersion=" + (NotifiedVersion ?? string.Empty));

                    lock (Gate)
                    {
                        string file = PathFor(productId);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                    }
                }
                catch { }
            }
        }
    }
}

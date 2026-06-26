using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Udobl.Native;

namespace Udobl.Core
{
    public class UsageEntry
    {
        public string Key { get; set; }      // stable identity ("app:<path>" or "url:<host>")
        public string Display { get; set; }  // human label
        public string Target { get; set; }   // launch path or host
        public bool IsUrl { get; set; }
        public double Count { get; set; }     // accumulated dwell samples
        public long LastSeen { get; set; }    // UTC ticks
    }

    public class UsageData
    {
        public List<UsageEntry> Entries { get; set; } = new List<UsageEntry>();
    }

    /// <summary>
    /// Samples the foreground window on an interval to learn which apps (and, for
    /// browsers, which sites) the user spends time in. Ranking is dwell * recency.
    /// All work is isolated on a background timer; failures never bubble up.
    /// </summary>
    public sealed class UsageTracker : IDisposable
    {
        private const double HalfLifeDays = 12.0;
        private const int SampleIntervalMs = 1500;

        private static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "iexplore", "browser"
        };

        /// <summary>True if a process name (lowercase, no ".exe") is a known web browser.</summary>
        public static bool IsBrowser(string proc) => !string.IsNullOrEmpty(proc) && Browsers.Contains(proc);

        private readonly object _gate = new object();
        private readonly object _saveLock = new object();
        private readonly Dictionary<string, UsageEntry> _entries = new Dictionary<string, UsageEntry>();
        private readonly string _selfName;

        private Timer _timer;
        private int _samplesSinceSave;
        private int _urlReadInFlight; // 0/1 guard so UIA reads never pile up

        public volatile bool TrackUsage = true;
        public volatile bool TrackUrls = true;

        public UsageTracker()
        {
            try { _selfName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName).ToLowerInvariant(); }
            catch { _selfName = "udobl"; }
            LoadFromDisk();
        }

        public void Start()
        {
            if (_timer == null)
                _timer = new Timer(_ => SafeSample(), null, SampleIntervalMs, SampleIntervalMs);
        }

        public void Stop()
        {
            var t = _timer;
            _timer = null;
            if (t != null)
            {
                // Drain any in-flight callback before the final save (avoids a save race).
                using (var done = new ManualResetEvent(false))
                {
                    if (t.Dispose(done)) done.WaitOne(2000);
                }
            }
            SaveToDisk();
        }

        public void Dispose() => Stop();

        private void SafeSample()
        {
            try { Sample(); } catch { /* tracking must never crash the app */ }
        }

        private void Sample()
        {
            if (!TrackUsage) return;

            IntPtr hwnd = Win32Window.Foreground();
            if (hwnd == IntPtr.Zero) return;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;

            string path = Win32Window.ProcessPath(pid);
            if (string.IsNullOrEmpty(path)) return;

            string proc = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (proc.Length == 0 || proc == _selfName) return;

            Bump("app:" + path.ToLowerInvariant(), FriendlyName(path), path, false);

            if (TrackUrls && Browsers.Contains(proc))
                TryReadUrl(hwnd);

            if (Interlocked.Increment(ref _samplesSinceSave) >= 20)
            {
                Interlocked.Exchange(ref _samplesSinceSave, 0);
                SaveToDisk();
            }
        }

        private void Bump(string key, string display, string target, bool isUrl)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var e))
                {
                    e = new UsageEntry { Key = key, IsUrl = isUrl, Count = 0 };
                    _entries[key] = e;
                }
                e.Display = display;
                e.Target = target;
                e.Count += 1;
                e.LastSeen = DateTime.UtcNow.Ticks;
            }
        }

        private void TryReadUrl(IntPtr hwnd)
        {
            if (Interlocked.CompareExchange(ref _urlReadInFlight, 1, 0) != 0)
                return; // a previous read is still running; skip this round

            // UIA is a cross-process RPC that can hang on an unresponsive browser. A watchdog
            // frees the gate after a timeout so a single stuck read can't permanently kill
            // URL tracking (worst case the wedged worker lingers; new reads resume).
            var watchdog = new Timer(_ => Interlocked.Exchange(ref _urlReadInFlight, 0), null, 2500, Timeout.Infinite);

            Task.Run(() =>
            {
                try
                {
                    string host = ExtractHost(ReadAddressBar(hwnd));
                    if (host != null)
                        Bump("url:" + host, host, host, true);
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _urlReadInFlight, 0);
                    try { watchdog.Dispose(); } catch { }
                }
            });
        }

        private static string ReadAddressBar(IntPtr hwnd)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            AutomationElementCollection edits = root.FindAll(TreeScope.Descendants, editCond);

            foreach (AutomationElement edit in edits)
            {
                string name = SafeName(edit);
                bool looksLikeBar = name != null &&
                    (name.IndexOf("address", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("search or enter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("enter address", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!looksLikeBar) continue;

                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object pat))
                {
                    string v = ((ValuePattern)pat).Current.Value;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            return null;
        }

        private static string SafeName(AutomationElement e)
        {
            try { return e.Current.Name; } catch { return null; }
        }

        /// <summary>Turns an address-bar value into a clean host, or null if it isn't one.</summary>
        public static string ExtractHost(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();

            int scheme = v.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) v = v.Substring(scheme + 3);

            // cut path / query / fragment / port
            int cut = v.IndexOfAny(new[] { '/', '?', '#', ':', ' ' });
            if (cut >= 0) v = v.Substring(0, cut);

            v = v.Trim().ToLowerInvariant();
            if (v.StartsWith("www.")) v = v.Substring(4);

            // A real host has a dot and no spaces; otherwise it's a search query.
            if (v.Length < 3 || v.Contains(' ') || !v.Contains(".")) return null;
            foreach (char c in v)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-')) return null;

            return v;
        }

        private static string FriendlyName(string path)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(fvi.FileDescription))
                    return fvi.FileDescription.Trim();
            }
            catch { }
            string n = Path.GetFileNameWithoutExtension(path);
            return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n.Substring(1) : path;
        }

        private static double Score(UsageEntry e)
        {
            double ageDays = (DateTime.UtcNow.Ticks - e.LastSeen) / (double)TimeSpan.TicksPerDay;
            if (ageDays < 0) ageDays = 0;
            return e.Count * Math.Pow(0.5, ageDays / HalfLifeDays);
        }

        public List<UsageEntry> TopApps(int n)
        {
            lock (_gate)
                return _entries.Values.Where(e => !e.IsUrl)
                    .OrderByDescending(Score).Take(n).ToList();
        }

        public List<UsageEntry> TopUrls(int n)
        {
            lock (_gate)
                return _entries.Values.Where(e => e.IsUrl)
                    .OrderByDescending(Score).Take(n).ToList();
        }

        public void Reset()
        {
            lock (_gate) _entries.Clear();
            SaveToDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(ConfigStore.UsagePath)) return;
                var data = Json.Deserialize<UsageData>(File.ReadAllText(ConfigStore.UsagePath));
                if (data?.Entries == null) return;
                lock (_gate)
                {
                    foreach (var e in data.Entries)
                        if (!string.IsNullOrEmpty(e.Key)) _entries[e.Key] = e;
                }
            }
            catch { }
        }

        public void SaveToDisk()
        {
            try
            {
                UsageData data;
                lock (_gate)
                    data = new UsageData { Entries = _entries.Values.ToList() };

                string json = Json.Serialize(data);
                // Serialize writers and swap atomically so two saves can't collide on the file.
                lock (_saveLock)
                {
                    string path = ConfigStore.UsagePath;
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
            }
            catch { }
        }
    }
}

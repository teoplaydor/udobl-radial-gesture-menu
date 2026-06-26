using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;

namespace Udobl.Core
{
    /// <summary>Shape of the user-hosted version.json: { "version":"1.2.0", "url":"...", "notes":"..." }.</summary>
    public class UpdateManifest
    {
        public string version { get; set; } = "";
        public string url { get; set; } = "";
        public string notes { get; set; } = "";
    }

    /// <summary>
    /// Notify-only update check (never auto-downloads). Reads a version.json the user hosts
    /// anywhere static (GitHub Releases asset, own site), compares to the assembly version,
    /// and reports via the callback. Fails silently when offline / on the auto path.
    /// </summary>
    public static class UpdateChecker
    {
        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

        /// <summary>
        /// Intended to run inside Task.Run. onResult(kind, message, url): kind is
        /// "update" | "current" | "error"; url is the page to open (only for "update").
        /// </summary>
        public static void Check(AppConfig cfg, bool manual, Action<string, string, string> onResult)
        {
            try
            {
                var s = cfg.Settings;
                if (string.IsNullOrWhiteSpace(s.UpdateUrl))
                {
                    if (manual) onResult("error", "Не указан адрес обновлений (укажите его в настройках).", null);
                    return;
                }
                if (!manual)
                {
                    if (!s.UpdateCheckEnabled) return;
                    DateTime last;
                    if (DateTime.TryParse(s.LastUpdateCheck, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out last)
                        && (DateTime.UtcNow - last) < TimeSpan.FromHours(20))
                        return;
                }

                // net48 defaults to TLS 1.0/1.1; most hosts now require 1.2+.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { } // Tls13

                var req = (HttpWebRequest)WebRequest.Create(s.UpdateUrl);
                req.Timeout = 8000;
                req.UserAgent = "Udobl-UpdateCheck";
                req.AllowAutoRedirect = true;

                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    json = sr.ReadToEnd();

                var m = Json.Deserialize<UpdateManifest>(json);
                Version remote;
                if (m != null && Version.TryParse((m.version ?? "").Trim(), out remote) && remote > CurrentVersion)
                {
                    string page = !string.IsNullOrWhiteSpace(m.url) ? m.url : s.UpdateUrl;
                    onResult("update", "Доступно обновление " + m.version.Trim() + ". Нажмите, чтобы открыть.", page);
                }
                else if (manual)
                {
                    onResult("current", "У вас последняя версия (" + CurrentVersion.ToString(3) + ").", null);
                }
            }
            catch
            {
                if (manual) onResult("error", "Не удалось проверить обновления.", null);
            }
        }
    }
}

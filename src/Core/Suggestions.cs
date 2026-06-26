using System;
using System.Collections.Generic;
using System.Linq;

namespace Udobl.Core
{
    /// <summary>Turns usage stats into menu slices and fills out the ring.</summary>
    public static class Suggestions
    {
        public static MenuItemConfig FromApp(UsageEntry e) => new MenuItemConfig
        {
            Label = Shorten(e.Display),
            Kind = (int)ActionKind.LaunchApp,
            Target = e.Target,
            Glyph = ConfigStore.Ico(0xECAA), // AppIconDefault
        };

        public static MenuItemConfig FromUrl(UsageEntry e) => new MenuItemConfig
        {
            Label = Shorten(e.Display),
            Kind = (int)ActionKind.OpenUrl,
            Target = e.Target,
            Glyph = ConfigStore.Ico(0xE774), // Globe
        };

        /// <summary>
        /// Builds the slices to display: enabled user items first, then top
        /// suggestions (deduped by target) until MaxSlots is reached.
        /// </summary>
        public static List<MenuItemConfig> BuildMenu(AppConfig config, UsageTracker tracker)
        {
            var result = new List<MenuItemConfig>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in config.Items.Where(i => i.Enabled))
            {
                if (result.Count >= config.Settings.MaxSlots) break;
                result.Add(item);
                if (!item.IsGroup) seen.Add(KeyOf(item.Target)); // groups have no target; pass through as one slice
            }

            if (config.Settings.FillWithSuggestions && tracker != null)
            {
                // Interleave top apps and top sites so both kinds surface.
                var apps = tracker.TopApps(config.Settings.MaxSlots).Select(FromApp);
                var urls = tracker.TopUrls(config.Settings.MaxSlots).Select(FromUrl);

                foreach (var sug in Interleave(apps, urls))
                {
                    if (result.Count >= config.Settings.MaxSlots) break;
                    string k = KeyOf(sug.Target);
                    if (seen.Contains(k)) continue;
                    seen.Add(k);
                    result.Add(sug);
                }
            }

            return result;
        }

        private static IEnumerable<T> Interleave<T>(IEnumerable<T> a, IEnumerable<T> b)
        {
            using (var ea = a.GetEnumerator())
            using (var eb = b.GetEnumerator())
            {
                bool ha = true, hb = true;
                while (ha || hb)
                {
                    if (ha && (ha = ea.MoveNext())) yield return ea.Current;
                    if (hb && (hb = eb.MoveNext())) yield return eb.Current;
                }
            }
        }

        private static string KeyOf(string target)
        {
            target = (target ?? "").Trim().ToLowerInvariant();
            int scheme = target.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) target = target.Substring(scheme + 3);
            if (target.StartsWith("www.")) target = target.Substring(4);
            return target.TrimEnd('/');
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= 18 ? s : s.Substring(0, 17) + "…";
        }
    }
}

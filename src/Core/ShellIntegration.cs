using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Udobl.Core
{
    /// <summary>
    /// Adds an "Add to Udobl" verb to the Windows right-click menu (per-user, no admin) for any
    /// file, shortcut and folder, invoking `Udobl.exe --add "%1"`. Also turns a path into a menu item.
    /// </summary>
    public static class ShellIntegration
    {
        private const string Verb = "Udobl.AddToRing";
        private static readonly string[] Roots =
        {
            @"Software\Classes\*\shell\",          // any file (exe, lnk, url, ...)
            @"Software\Classes\Directory\shell\",  // folders
        };

        public static void Register(string exePath)
        {
            foreach (var root in Roots)
            {
                using (var k = Registry.CurrentUser.CreateSubKey(root + Verb))
                {
                    if (k == null) continue;
                    k.SetValue("", "Добавить в Udobl");
                    k.SetValue("Icon", "\"" + exePath + "\",0");
                    using (var c = k.CreateSubKey("command"))
                        if (c != null) c.SetValue("", "\"" + exePath + "\" --add \"%1\"");
                }
            }
        }

        public static void Unregister()
        {
            foreach (var root in Roots)
                try { Registry.CurrentUser.DeleteSubKeyTree(root + Verb, false); } catch { }
        }

        public static void Apply(bool enabled, string exePath)
        {
            try { if (enabled) Register(exePath); else Unregister(); } catch { }
        }

        /// <summary>Turn a right-clicked path into a menu item (icon resolves automatically from the target).</summary>
        public static MenuItemConfig BuildItem(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim().Trim('"');

            string label = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(label)) label = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(label)) label = path;

            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (Directory.Exists(path))
                return new MenuItemConfig { Label = label, Kind = (int)ActionKind.LaunchApp, Target = path };

            if (ext == ".url")
            {
                string url = ReadUrlFile(path);
                if (!string.IsNullOrEmpty(url))
                    return new MenuItemConfig { Label = label, Kind = (int)ActionKind.OpenUrl, Target = url };
            }

            return new MenuItemConfig { Label = label, Kind = (int)ActionKind.LaunchApp, Target = path };
        }

        /// <summary>
        /// Resolve a "/"-separated index path (e.g. "0" or "2/1") to the target group's Children list.
        /// Falls back to the root Items on any null/invalid/stale segment, so callers never crash.
        /// </summary>
        public static List<MenuItemConfig> ResolveGroup(AppConfig cfg, string indexPath)
        {
            if (cfg == null) return null;
            if (string.IsNullOrWhiteSpace(indexPath)) return cfg.Items;
            var list = cfg.Items;
            foreach (var seg in indexPath.Split('/'))
            {
                int i;
                if (!int.TryParse(seg.Trim(), out i) || i < 0 || i >= list.Count || !list[i].IsGroup)
                    return cfg.Items;
                if (list[i].Children == null) list[i].Children = new List<MenuItemConfig>();
                list = list[i].Children;
            }
            return list;
        }

        private static string ReadUrlFile(string path)
        {
            try
            {
                foreach (var line in File.ReadAllLines(path))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(4).Trim();
            }
            catch { }
            return null;
        }
    }
}

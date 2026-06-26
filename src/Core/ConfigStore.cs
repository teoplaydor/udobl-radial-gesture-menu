using System;
using System.Collections.Generic;
using System.IO;

namespace Udobl.Core
{
    /// <summary>
    /// Loads/saves config + usage under %APPDATA%\Udobl (stable across versions/folders),
    /// migrating any older next-to-exe config on first run. Drop a "udobl.portable" marker file
    /// next to the exe to force portable mode (config kept next to the exe instead).
    /// </summary>
    public static class ConfigStore
    {
        private static readonly object _gate = new object();
        private static string _baseDir;

        public static string BaseDir
        {
            get
            {
                if (_baseDir != null) return _baseDir;
                _baseDir = ResolveBaseDir();
                return _baseDir;
            }
        }

        public static string ConfigPath => Path.Combine(BaseDir, "config.json");
        public static string UsagePath => Path.Combine(BaseDir, "usage.json");

        private static string ResolveBaseDir()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            // Explicit portable mode: drop an empty file named "udobl.portable" next to the exe
            // (e.g. for a USB stick). Otherwise config lives in the per-user profile and persists
            // across versions / download folders.
            try { if (File.Exists(Path.Combine(exeDir, "udobl.portable"))) return exeDir; } catch { }

            string appData;
            try
            {
                appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Udobl");
                Directory.CreateDirectory(appData);
            }
            catch { return exeDir; }

            // One-time migration: if an older build left a config next to the exe and the profile
            // has none yet, copy the user's items over so nothing is lost moving to the new layout.
            try
            {
                string appCfg = Path.Combine(appData, "config.json");
                string exeCfg = Path.Combine(exeDir, "config.json");
                if (!File.Exists(appCfg) && File.Exists(exeCfg))
                {
                    File.Copy(exeCfg, appCfg, false);
                    string exeUsage = Path.Combine(exeDir, "usage.json");
                    string appUsage = Path.Combine(appData, "usage.json");
                    if (File.Exists(exeUsage) && !File.Exists(appUsage)) File.Copy(exeUsage, appUsage, false);
                }
            }
            catch { }

            return appData;
        }

        public static AppConfig Load()
        {
            lock (_gate)
            {
                if (File.Exists(ConfigPath))
                {
                    try
                    {
                        var cfg = Json.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                        if (cfg != null)
                        {
                            if (cfg.Settings == null) cfg.Settings = new AppSettings();
                            if (cfg.Items == null) cfg.Items = new List<MenuItemConfig>();
                            return cfg;
                        }
                    }
                    catch
                    {
                        // Existing config is locked/corrupt. NEVER overwrite it — back it up and
                        // return defaults in-memory only, so the user's file is preserved.
                        try { File.Copy(ConfigPath, ConfigPath + ".bak", true); } catch { }
                    }
                    return Defaults();
                }

                // No config yet: seed and persist.
                var seeded = Defaults();
                try { Save(seeded); } catch { }
                return seeded;
            }
        }

        public static void Save(AppConfig config)
        {
            lock (_gate)
            {
                string json = Json.Serialize(config);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);
                try
                {
                    if (File.Exists(ConfigPath))
                        File.Replace(tmp, ConfigPath, ConfigPath + ".bak", true); // atomic swap + backup
                    else
                        File.Move(tmp, ConfigPath);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }
            }
        }

        // Segoe MDL2 Assets / Segoe Fluent Icons codepoint -> string (built in pure-ASCII source).
        public static string Ico(int codepoint) => char.ConvertFromUtf32(codepoint);

        public static AppConfig Defaults()
        {
            var cfg = new AppConfig();
            // LaunchApp / OpenUrl слайсы получают НАСТОЯЩИЙ значок Windows; Glyph — запасной.
            cfg.Items.AddRange(new[]
            {
                new MenuItemConfig { Label = "Проводник",        Kind = (int)ActionKind.LaunchApp,  Target = "explorer.exe",           Glyph = Ico(0xE8B7) },
                new MenuItemConfig { Label = "Браузер",          Kind = (int)ActionKind.OpenUrl,    Target = "https://www.google.com", Glyph = Ico(0xE774) },
                new MenuItemConfig { Label = "Командная строка", Kind = (int)ActionKind.LaunchApp,  Target = "cmd.exe",                Glyph = Ico(0xE756) },
                new MenuItemConfig { Label = "PowerShell",       Kind = (int)ActionKind.LaunchApp,  Target = "powershell.exe",         Glyph = Ico(0xE756) },
                new MenuItemConfig { Label = "Калькулятор",      Kind = (int)ActionKind.LaunchApp,  Target = "calc.exe",               Glyph = Ico(0xE8EF) },
                new MenuItemConfig { Label = "Снимок экрана",    Kind = (int)ActionKind.Hotkey,     Target = "Win+Shift+S",            Glyph = Ico(0xE722) },
                new MenuItemConfig { Label = "Копировать",       Kind = (int)ActionKind.Hotkey,     Target = "Ctrl+C",                 Glyph = Ico(0xE8C8) },
                new MenuItemConfig { Label = "Вставить",         Kind = (int)ActionKind.Hotkey,     Target = "Ctrl+V",                 Glyph = Ico(0xE77F) },
                new MenuItemConfig { Label = "Заблокировать ПК", Kind = (int)ActionKind.RunCommand, Target = "rundll32.exe user32.dll,LockWorkStation", Glyph = Ico(0xE72E) },
            });
            return cfg;
        }
    }
}

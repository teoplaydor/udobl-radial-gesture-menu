using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Udobl.Native;

namespace Udobl.Core
{
    /// <summary>Executes a selected menu action. Errors are reported via the callback.</summary>
    public static class ActionRunner
    {
        public static Action<string> OnError;

        public static void Run(MenuItemConfig item)
        {
            if (item == null) return;

            // Hotkeys must be sent synchronously (immediately on the gesture thread) so
            // they reach the window that was focused before the overlay appeared.
            if (item.KindEnum == ActionKind.Hotkey)
            {
                TryRun(item);
                return;
            }

            // Launching / opening can be slow; do it off the UI thread.
            Task.Run(() => TryRun(item));
        }

        private static void TryRun(MenuItemConfig item)
        {
            try
            {
                switch (item.KindEnum)
                {
                    case ActionKind.LaunchApp:
                        LaunchApp(item);
                        break;
                    case ActionKind.OpenUrl:
                        OpenUrl(item.Target);
                        break;
                    case ActionKind.RunCommand:
                        RunCommand(item.Target);
                        break;
                    case ActionKind.Hotkey:
                        InputSender.SendHotkey(item.Target);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"«{item.Label}» — ошибка: {ex.Message}");
            }
        }

        private static void LaunchApp(MenuItemConfig item)
        {
            string target = Environment.ExpandEnvironmentVariables(item.Target ?? "").Trim();
            if (target.Length == 0) return;

            var psi = new ProcessStartInfo
            {
                FileName = target,
                Arguments = Environment.ExpandEnvironmentVariables(item.Args ?? ""),
                UseShellExecute = true, // resolves PATH, file associations, .lnk, store apps
            };
            Process.Start(psi);
        }

        private static void OpenUrl(string url) => OpenInBrowser(url);

        /// <summary>Open a URL in the default browser (shared by actions and the update balloon).</summary>
        public static void OpenInBrowser(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return;

            // Add a scheme if the user typed a bare host like "youtube.com".
            if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }

        private static void RunCommand(string command)
        {
            command = (command ?? "").Trim();
            if (command.Length == 0) return;

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
    }
}

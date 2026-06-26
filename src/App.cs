using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Udobl.Core;
using Udobl.Native;
using Udobl.UI;
using WF = System.Windows.Forms;

namespace Udobl
{
    public static class Program
    {
        private const string MutexName = "Udobl.SingleInstance.v1";
        internal const string ShowEventName = "Udobl.ShowSettings.v1";
        internal const string ReloadEventName = "Udobl.Reload.v1";

        [STAThread]
        public static int Main()
        {
            // Hidden D3D probe: prove the Direct3D pipeline works in this environment.
            string d3dTest = Environment.GetEnvironmentVariable("UDOBL_D3DTEST");
            if (!string.IsNullOrEmpty(d3dTest))
                return Udobl.Render.D3DProbe.RunTest(d3dTest);

            string glassTest = Environment.GetEnvironmentVariable("UDOBL_D3DGLASS");
            if (!string.IsNullOrEmpty(glassTest))
                return Udobl.Render.D3DGlass.RenderTest(glassTest);

            // Hidden preview mode: render the ring to a PNG and exit (visual QA only).
            string renderPath = Environment.GetEnvironmentVariable("UDOBL_RENDER");
            if (!string.IsNullOrEmpty(renderPath))
                return RenderPreview(renderPath);

            // Explorer right-click: `Udobl.exe --add "<path>" [--into "<groupIndexPath>"]` → add to the
            // ring (optionally into a group), tell the running app.
            var cmd = Environment.GetCommandLineArgs();
            string addPath = null, into = null;
            for (int i = 1; i < cmd.Length; i++)
            {
                if (string.Equals(cmd[i], "--add", StringComparison.OrdinalIgnoreCase) && i + 1 < cmd.Length) addPath = cmd[++i];
                else if (string.Equals(cmd[i], "--into", StringComparison.OrdinalIgnoreCase) && i + 1 < cmd.Length) into = cmd[++i];
            }
            if (addPath != null) return AddTarget(addPath, into);

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Another instance is running — ask it to surface its settings, then exit.
                    try
                    {
                        var ev = EventWaitHandle.OpenExisting(ShowEventName);
                        ev.Set();
                    }
                    catch { }
                    return 0;
                }

                var app = new App();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.Run();
                GC.KeepAlive(mutex);
                return 0;
            }
        }

        private static int AddTarget(string path, string into)
        {
            try
            {
                var item = ShellIntegration.BuildItem(path);
                if (item != null)
                {
                    var cfg = ConfigStore.Load();
                    var list = ShellIntegration.ResolveGroup(cfg, into); // group's Children, or root on a bad path
                    list.Add(item);
                    ConfigStore.Save(cfg);
                }
                try { EventWaitHandle.OpenExisting(ReloadEventName).Set(); } catch { } // refresh the running app
            }
            catch { }
            return 0;
        }

        private static int RenderPreview(string path)
        {
            var app = new Application();
            var win = new UI.RadialMenuWindow();
            win.Configure(34, 52, System.Windows.Media.Color.FromRgb(0x8A, 0x93, 0xA1));
            int idx = 1;
            int.TryParse(Environment.GetEnvironmentVariable("UDOBL_RENDER_IDX"), out idx);

            var items = ConfigStore.Defaults().Items;
            // QA: turn item[idx] into a group so UDOBL_RENDER_DEPTH can descend and render a sub-ring.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UDOBL_RENDER_DEPTH"))
                && idx >= 0 && idx < items.Count)
            {
                var children = ConfigStore.Defaults().Items.GetRange(0, 4);
                items[idx] = new MenuItemConfig
                {
                    Label = "Папка",
                    Kind = (int)ActionKind.Group,
                    Glyph = ConfigStore.Ico(0xE8B7),
                    Children = children,
                };
            }
            win.PreviewRender(items, idx, path);
            app.Shutdown();
            return 0;
        }
    }

    public sealed class App : Application
    {
        private enum GestureState { Idle, Pending, Engaged }

        private const int EngageDistDip = 22;

        private AppConfig _config;
        private UsageTracker _tracker;
        private MouseHook _hook;
        private RadialMenuWindow _radial;
        private TrayIcon _tray;
        private SettingsWindow _settings;
        private DispatcherTimer _timer;
        private EventWaitHandle _showEvent;
        private RegisteredWaitHandle _showWait;
        private EventWaitHandle _reloadEvent;
        private RegisteredWaitHandle _reloadWait;

        private double _scale = 1.0;
        private string _selfName = "udobl";
        private string _exePath;

        private GestureState _state = GestureState.Idle;
        private Point _startDip;
        private int _startTick;
        private System.Collections.Generic.List<MenuItemConfig> _pendingItems;

        // Track buttons we swallowed so the matching "up" can always be reconciled,
        // even if the gesture was cancelled before release (no orphan up leaks).
        private bool _swallowedMiddleDown;
        private bool _swallowedRightDown;
        private bool _pendingInBrowser; // press began over a browser → quick-tap = Ctrl+Left-click

        private string _pendingUpdateUrl;     // release page from the last "update available" balloon
        private bool _lastBalloonWasUpdate;   // guards the single global BalloonTipClicked event

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Never let a UI-thread exception (e.g. a GPU device-loss during render) kill the tray app.
            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                try { _tray?.Notify("Внутренняя ошибка: " + ex.Exception.Message, WF.ToolTipIcon.Warning); } catch { }
            };

            _scale = NativeMethods.GetSystemScale();
            try
            {
                _exePath = Process.GetCurrentProcess().MainModule.FileName;
                _selfName = Path.GetFileNameWithoutExtension(_exePath).ToLowerInvariant();
            }
            catch { _exePath = AppDomain.CurrentDomain.FriendlyName; }

            _config = ConfigStore.Load();
            // Warm the Windows icon cache in the background so the wheel opens instantly.
            System.Threading.Tasks.Task.Run(() => IconResolver.Prewarm(_config.Items));

            ActionRunner.OnError = msg => Dispatcher.BeginInvoke((Action)(() => _tray?.Notify(msg, WF.ToolTipIcon.Warning)));

            _tracker = new UsageTracker
            {
                TrackUsage = _config.Settings.TrackUsage,
                TrackUrls = _config.Settings.TrackUrls,
            };
            _tracker.Start();

            _radial = new RadialMenuWindow();
            ApplyRadialConfig();

            _tray = new TrayIcon(_config.Settings.GestureEnabled, _config.Settings.RunOnStartup, _config.Settings.ContextMenuEnabled);
            _tray.OpenSettings += OpenSettings;
            _tray.Exit += () => Dispatcher.BeginInvoke((Action)ExitApp);
            _tray.ToggleEnabled += v => { _config.Settings.GestureEnabled = v; SaveConfig(); };
            _tray.ToggleStartup += v => { _config.Settings.RunOnStartup = v; ApplyStartup(); SaveConfig(); };
            _tray.ToggleContextMenu += v => { _config.Settings.ContextMenuEnabled = v; ShellIntegration.Apply(v, _exePath); SaveConfig(); };
            _tray.CheckUpdates += () => System.Threading.Tasks.Task.Run(() => CheckForUpdates(true));
            _tray.BalloonClicked += () =>
            {
                if (_lastBalloonWasUpdate && !string.IsNullOrEmpty(_pendingUpdateUrl))
                {
                    ActionRunner.OpenInBrowser(_pendingUpdateUrl);
                    _pendingUpdateUrl = null;
                }
            };

            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            _timer.Tick += OnTick;

            _hook = new MouseHook
            {
                MiddleDown = (x, y) => OnMiddleDown(),
                MiddleUp = (x, y) => OnMiddleUp(),
                RightDown = OnRightDown,
                RightUp = OnRightUp,
            };
            try
            {
                _hook.Install();
            }
            catch (Exception ex)
            {
                _tray.Notify("Не удалось установить перехват мыши: " + ex.Message, WF.ToolTipIcon.Error);
            }

            ApplyStartup();
            if (_config.Settings.ContextMenuEnabled) ShellIntegration.Apply(true, _exePath); // refresh path
            SetupShowEvent();
            SetupReloadEvent();

            _tray.Notify("Запущено (" + (_radial.UsingDComp ? "GPU/DirectComposition" : "совместимый режим") +
                "). Удерживайте среднюю кнопку мыши, чтобы открыть круговое меню.");

            // Quiet background update check (throttled, notify-only, fails silently offline).
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Thread.Sleep(4000); CheckForUpdates(false); } catch { }
            });
        }

        private void CheckForUpdates(bool manual)
        {
            UpdateChecker.Check(_config, manual, (kind, msg, url) =>
            {
                try
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (kind == "update")
                            {
                                _pendingUpdateUrl = url;
                                _lastBalloonWasUpdate = true;
                                _tray.Notify(msg);
                            }
                            else
                            {
                                _lastBalloonWasUpdate = false;
                                _tray.Notify(msg, kind == "error" ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);
                            }
                        }
                        catch { }
                    }));
                }
                catch { }
            });

            // Stamp the check time (only when the feature is in use) so a flaky network can't nag every launch.
            if (!string.IsNullOrWhiteSpace(_config.Settings.UpdateUrl))
            {
                try
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        _config.Settings.LastUpdateCheck = DateTime.UtcNow.ToString("o");
                        SaveConfig();
                    }));
                }
                catch { }
            }
        }

        // ---- gesture handlers (run on UI thread via the hook) ----

        private bool OnMiddleDown()
        {
            if (!_config.Settings.GestureEnabled) return false;

            // Sticky sub-ring: the menu is already open (we descended/popped on a previous release).
            // Swallow extra presses so we don't rebuild from root — the matching release acts on the ring.
            if (_state == GestureState.Engaged) { _swallowedMiddleDown = true; return true; }

            string fg = Win32Window.ForegroundProcessName();
            if (fg == _selfName) return false; // don't fire over our own windows
            if (IsExcluded(fg)) return false;  // pass a normal middle click through

            var items = Suggestions.BuildMenu(_config, _tracker);
            if (items.Count == 0) return false; // nothing to show; behave normally

            _pendingItems = items;
            _startDip = CursorDip();
            _startTick = Environment.TickCount;
            _state = GestureState.Pending;
            _swallowedMiddleDown = true;
            _pendingInBrowser = UsageTracker.IsBrowser(fg);
            if (!_timer.IsEnabled) _timer.Start();
            return true; // swallow the middle-down (prevents autoscroll); we may replay it on a quick tap
        }

        private bool OnMiddleUp()
        {
            bool wasSwallowed = _swallowedMiddleDown;
            _swallowedMiddleDown = false;

            if (_state == GestureState.Engaged)
            {
                // Released in the dead zone with history → go up one level; keep the menu open.
                if (_radial.InDeadZone && _radial.Depth > 0) { _radial.Pop(); return true; }
                // Released on a group → descend into its sub-ring; keep the menu open (sticky).
                if (_radial.HighlightIsGroup) { _radial.Descend(); return true; }
                // Released on a leaf (or empty center at root) → run it and close.
                var item = _radial.HighlightedItem;
                _radial.HideMenu();
                ResetGesture();
                if (item != null) ActionRunner.Run(item);
                return true;
            }

            if (_state == GestureState.Pending)
            {
                // Quick tap (no hold/drag). Keep the middle button useful but NEVER let a browser
                // start autoscroll (which hides the cursor): the physical middle-down was swallowed,
                // so in a browser we synthesize Ctrl+Left-click (opens a hovered link in a new tab,
                // harmless on empty space, no autoscroll); elsewhere a normal middle click.
                ResetGesture();
                if (_pendingInBrowser) InputSender.SendCtrlLeftClick();
                else InputSender.ReplayMiddleClick();
                return true;
            }

            // Idle but we had swallowed the down (e.g. the gesture was cancelled via right-click):
            // consume this orphan up too so the app sees a clean no-op instead of a lone middle-up.
            return wasSwallowed;
        }

        private bool OnRightDown()
        {
            if (_state == GestureState.Idle) return false;
            // Right-click cancels an in-progress gesture (we keep _swallowedMiddleDown so its up is consumed).
            if (_state == GestureState.Engaged) _radial.HideMenu();
            ResetGesture();
            _swallowedRightDown = true;
            return true;
        }

        private bool OnRightUp()
        {
            if (!_swallowedRightDown) return false;
            _swallowedRightDown = false;
            return true; // swallow the matching up so no lone right-up leaks to the app
        }

        private void OnTick(object sender, EventArgs e)
        {
            Point cur = CursorDip();

            if (_state == GestureState.Pending)
            {
                double dx = cur.X - _startDip.X, dy = cur.Y - _startDip.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                int elapsed = Environment.TickCount - _startTick;
                if (elapsed >= _config.Settings.HoldMs || dist >= EngageDistDip)
                    Engage();
            }
            else if (_state == GestureState.Engaged)
            {
                _radial.UpdateCursor(cur.X, cur.Y);
            }
            else
            {
                _timer.Stop();
            }
        }

        private void Engage()
        {
            _state = GestureState.Engaged;
            _radial.ShowMenu(_pendingItems, _startDip.X, _startDip.Y);
            Point cur = CursorDip();
            _radial.UpdateCursor(cur.X, cur.Y);
        }

        private void ResetGesture()
        {
            _state = GestureState.Idle;
            _timer.Stop();
        }

        private Point CursorDip()
        {
            NativeMethods.GetCursorPos(out NativeMethods.POINT p);
            return new Point(p.x / _scale, p.y / _scale);
        }

        private bool IsExcluded(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;
            foreach (var ex in _config.Settings.ExcludedProcesses)
                if (string.Equals(ex, procName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- settings / config ----

        private void OpenSettings()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_settings != null)
                {
                    _settings.Activate();
                    return;
                }
                _settings = new SettingsWindow(CloneConfig(_config), _tracker);
                _settings.Applied += newCfg =>
                {
                    _config = newCfg;
                    SaveConfig();
                    ApplyAll();
                };
                _settings.Closed += (s, e2) => _settings = null;
                _settings.Show();
                _settings.Activate();
            }));
        }

        private void ApplyAll()
        {
            _tracker.TrackUsage = _config.Settings.TrackUsage;
            _tracker.TrackUrls = _config.Settings.TrackUrls;
            ApplyRadialConfig();
            ApplyStartup();
            _tray.SetEnabled(_config.Settings.GestureEnabled);
            _tray.SetStartup(_config.Settings.RunOnStartup);
            _tray.SetContextMenu(_config.Settings.ContextMenuEnabled);
            ShellIntegration.Apply(_config.Settings.ContextMenuEnabled, _exePath);
        }

        private void ApplyRadialConfig()
        {
            Color accent = RadialMenuWindow.TryParseColor(_config.Settings.AccentColor, out Color c)
                ? c : Color.FromRgb(0x4F, 0xC3, 0xF7);
            _radial.Configure(_config.Settings.TiltDegrees, _config.Settings.DeadZoneRadius, accent, _config.Settings.MaxDepth);
        }

        private void ApplyStartup()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                {
                    if (key == null) return;
                    if (_config.Settings.RunOnStartup && !string.IsNullOrEmpty(_exePath))
                        key.SetValue("Udobl", "\"" + _exePath + "\"");
                    else
                        key.DeleteValue("Udobl", false);
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try { ConfigStore.Save(_config); }
            catch (Exception ex) { _tray?.Notify("Не удалось сохранить настройки: " + ex.Message, WF.ToolTipIcon.Warning); }
        }

        private static AppConfig CloneConfig(AppConfig src)
        {
            // Round-trip through JSON so the settings window edits a detached copy.
            return Json.Deserialize<AppConfig>(Json.Serialize(src));
        }

        private void SetupShowEvent()
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
                _showWait = ThreadPool.RegisterWaitForSingleObject(
                    _showEvent,
                    (state, timedOut) => Dispatcher.BeginInvoke((Action)OpenSettings),
                    null, -1, false);
            }
            catch { }
        }

        private void SetupReloadEvent()
        {
            try
            {
                _reloadEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ReloadEventName);
                _reloadWait = ThreadPool.RegisterWaitForSingleObject(
                    _reloadEvent,
                    (state, timedOut) => Dispatcher.BeginInvoke((Action)ReloadConfig),
                    null, -1, false);
            }
            catch { }
        }

        // Re-read config after an external `--add` (Explorer right-click) so the new item appears live.
        private void ReloadConfig()
        {
            _config = ConfigStore.Load();
            System.Threading.Tasks.Task.Run(() => IconResolver.Prewarm(_config.Items));
            ApplyAll();
            _tray?.Notify("Добавлено в круговое меню.");
        }

        private void ExitApp()
        {
            try { _showWait?.Unregister(null); } catch { }
            try { _showEvent?.Dispose(); } catch { }
            try { _reloadWait?.Unregister(null); } catch { }
            try { _reloadEvent?.Dispose(); } catch { }
            try { _hook?.Dispose(); } catch { }
            try { _timer?.Stop(); } catch { }
            try { _tracker?.Stop(); } catch { }
            try { _radial?.Close(); } catch { }
            try { _settings?.Close(); } catch { }
            try { _tray?.Dispose(); } catch { }
            Shutdown();
        }
    }
}

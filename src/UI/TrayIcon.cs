using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace Udobl.UI
{
    /// <summary>Wraps a WinForms NotifyIcon with a procedurally drawn icon (no .ico file shipped).</summary>
    public sealed class TrayIcon : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly WF.NotifyIcon _icon;
        private readonly WF.ContextMenuStrip _menu;
        private IntPtr _hIcon;
        private Icon _iconClone;

        private readonly WF.ToolStripMenuItem _enableItem;
        private readonly WF.ToolStripMenuItem _startupItem;
        private readonly WF.ToolStripMenuItem _ctxItem;

        public event Action OpenSettings;
        public event Action<bool> ToggleEnabled;
        public event Action<bool> ToggleStartup;
        public event Action<bool> ToggleContextMenu;
        public event Action CheckUpdates;
        public event Action BalloonClicked;
        public event Action Exit;

        public TrayIcon(bool enabled, bool startup, bool contextMenu)
        {
            _menu = new WF.ContextMenuStrip();

            var header = new WF.ToolStripMenuItem("Udobl — круговое меню жестов") { Enabled = false };
            _menu.Items.Add(header);
            _menu.Items.Add(new WF.ToolStripSeparator());

            _enableItem = new WF.ToolStripMenuItem("Жесты включены") { Checked = enabled, CheckOnClick = true };
            _enableItem.CheckedChanged += (s, e) => ToggleEnabled?.Invoke(_enableItem.Checked);
            _menu.Items.Add(_enableItem);

            _startupItem = new WF.ToolStripMenuItem("Запускать при старте Windows") { Checked = startup, CheckOnClick = true };
            _startupItem.CheckedChanged += (s, e) => ToggleStartup?.Invoke(_startupItem.Checked);
            _menu.Items.Add(_startupItem);

            _ctxItem = new WF.ToolStripMenuItem("«Добавить в Udobl» в меню проводника") { Checked = contextMenu, CheckOnClick = true };
            _ctxItem.CheckedChanged += (s, e) => ToggleContextMenu?.Invoke(_ctxItem.Checked);
            _menu.Items.Add(_ctxItem);

            _menu.Items.Add(new WF.ToolStripSeparator());

            var settings = new WF.ToolStripMenuItem("Настройки…");
            settings.Click += (s, e) => OpenSettings?.Invoke();
            _menu.Items.Add(settings);

            var update = new WF.ToolStripMenuItem("Проверить обновления…");
            update.Click += (s, e) => CheckUpdates?.Invoke();
            _menu.Items.Add(update);

            var exit = new WF.ToolStripMenuItem("Выход");
            exit.Click += (s, e) => Exit?.Invoke();
            _menu.Items.Add(exit);

            _icon = new WF.NotifyIcon
            {
                Icon = BuildIcon(),
                Text = "Udobl — удерживайте среднюю кнопку мыши",
                Visible = true,
                ContextMenuStrip = _menu,
            };
            _icon.DoubleClick += (s, e) => OpenSettings?.Invoke();
            _icon.BalloonTipClicked += (s, e) => BalloonClicked?.Invoke();
        }

        public void SetEnabled(bool value)
        {
            if (_enableItem.Checked != value) _enableItem.Checked = value;
        }

        public void SetStartup(bool value)
        {
            if (_startupItem.Checked != value) _startupItem.Checked = value;
        }

        public void SetContextMenu(bool value)
        {
            if (_ctxItem.Checked != value) _ctxItem.Checked = value;
        }

        public void Notify(string message, WF.ToolTipIcon kind = WF.ToolTipIcon.Info)
        {
            try { _icon.ShowBalloonTip(2500, "Udobl", message, kind); } catch { }
        }

        private Icon BuildIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var pen = new Pen(Color.FromArgb(255, 79, 195, 247), 4f))
                    g.DrawEllipse(pen, 5, 5, 22, 22);
                using (var br = new SolidBrush(Color.FromArgb(255, 129, 212, 250)))
                    g.FillEllipse(br, 12, 12, 8, 8);
            }
            _hIcon = bmp.GetHicon();
            using (var tmp = Icon.FromHandle(_hIcon))
                _iconClone = (Icon)tmp.Clone();
            bmp.Dispose();
            return _iconClone;
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                _icon.Icon = null;
                _icon.Dispose();
                _menu.Dispose();
                _iconClone?.Dispose();
                if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
            }
            catch { }
        }
    }
}

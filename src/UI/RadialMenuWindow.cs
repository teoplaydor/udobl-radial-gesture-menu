using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Udobl.Core;
using Udobl.Native;
using Udobl.Render;
using SharpDX.DXGI;
using D3D11 = SharpDX.Direct3D11;
using RawColor4 = SharpDX.Mathematics.Interop.RawColor4;

namespace Udobl.UI
{
    /// <summary>
    /// The radial wheel, rendered by a real Direct3D 11 glass shader (refraction + reflection +
    /// Fresnel sampling a captured screen). The D3D output is shown in an Image inside a transparent
    /// overlay; icons are projected WPF visuals on top; the hub is a 2D overlay.
    /// </summary>
    public sealed class RadialMenuWindow : Window
    {
        private const double W = 600;
        private const double H = 600;
        private const double InnerRadius = 96;
        private const double OuterRadius = 270;
        private const double GapDegrees = 3.0;
        private const double Unit = 300.0;
        private const double Thickness = 0.19;
        private const double IconPx = 30;

        private D3DGlass _glass;
        private bool _glassOk;
        private Image _glassImage;
        private Grid _root;

        // DirectComposition present (full GPU, no readback / no WPF composite). Falls back to the WPF
        // WriteableBitmap path (_glassImage) if init fails or the OS is too old.
        private bool _useDComp;
        private IntPtr _dcompHwnd;
        private SwapChain1 _swap;
        private D3D11.Texture2D _backBuffer;
        private SharpDX.DXGI.Device _dxgiDevice;
        private IDCompositionDevice _dcDev;
        private IDCompositionTarget _dcTarget;
        private IDCompositionVisual _dcVisual;
        private NativeMethods.WndProcDelegate _wndProc; // keep alive (GC would free the function pointer)
        private static int _dcompClassReg;
        private int _pw, _ph;

        private Grid _hub;
        private TextBlock _hubGlyph;
        private Image _hubImage;
        private TextBlock _hubLabel;
        private TextBlock _hubKind;

        private readonly List<D3DGlass.SpriteDef> _iconDefs = new List<D3DGlass.SpriteDef>();
        private const int HubW = 220, HubH = 200;
        private List<MenuItemConfig> _items = new List<MenuItemConfig>();
        private int _count;
        private int _highlight = -1;

        private double _maxTilt = 26;
        private int _deadZone = 52;
        private int _maxDepth = 4;
        private Color _accent = Color.FromRgb(0x8A, 0x93, 0xA1);
        private double _scale = 1.0;

        private double _tiltX, _tiltY;
        private DispatcherTimer _closeTimer;
        private ScaleTransform _rootScale;

        // live screen-reflection + pulse
        private DispatcherTimer _renderTimer;
        private double _pulseT;
        private int _frame;
        private bool _dirty;           // a frame is only rendered when something actually changed
        private bool _liveOk;          // overlay excluded from capture → safe to reflect live
        private IntPtr _hwnd;
        private System.Drawing.Bitmap _capBmp;
        private byte[] _capBytes;
        private int _regL, _regT, _regW, _regH; // capture region (physical px)

        private sealed class RingFrame { public List<MenuItemConfig> Items; public int Highlight; }
        private readonly List<RingFrame> _navStack = new List<RingFrame>();

        public int Depth => _navStack.Count;
        public bool InDeadZone { get; private set; }
        public bool HighlightIsGroup => HighlightedItem != null && HighlightedItem.IsGroup;
        public double CenterXDip { get; private set; }
        public double CenterYDip { get; private set; }
        public int HighlightIndex => _highlight;
        public MenuItemConfig HighlightedItem =>
            (_highlight >= 0 && _highlight < _items.Count) ? _items[_highlight] : null;

        public RadialMenuWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            Width = W;
            Height = H;
            FontFamily = new FontFamily("Segoe UI");
            _scale = NativeMethods.GetSystemScale();

            try { _glass = new D3DGlass((int)W, (int)H); _glassOk = true; } catch { _glassOk = false; }
            TryInitDComp(); // full-GPU present; on failure _useDComp stays false → WPF fallback below

            BuildVisualTree();
            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Win32Window.MakeOverlay(_hwnd);
                // Exclude our overlay from screen capture so the live reflection doesn't mirror itself.
                try { _liveOk = NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE); }
                catch { _liveOk = false; }
            };
            Closed += (s, e) => { StopRenderLoop(); CleanupDComp(); try { _glass?.Dispose(); } catch { } _capBmp?.Dispose(); };
        }

        public bool UsingDComp => _useDComp;

        [HandleProcessCorruptedStateExceptions]
        private void TryInitDComp()
        {
            if (!_glassOk) return;
            if (Environment.GetEnvironmentVariable("UDOBL_NODCOMP") != null) return; // escape hatch → WPF path
            if (Environment.OSVersion.Version < new Version(6, 2)) return; // DComp + flip model need Win8+
            try
            {
                _pw = (int)W; _ph = (int)H; // 1:1 with the D3DGlass render target (600x600)

                _wndProc = (h, m, w, l) => NativeMethods.DefWindowProc(h, m, w, l);
                if (System.Threading.Interlocked.Exchange(ref _dcompClassReg, 1) == 0)
                {
                    var wc = new NativeMethods.WNDCLASSEX
                    {
                        cbSize = Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX)),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                        hInstance = NativeMethods.GetModuleHandle(null),
                        lpszClassName = "UdoblDComp",
                    };
                    if (NativeMethods.RegisterClassEx(ref wc) == 0) { _dcompClassReg = 0; throw new InvalidOperationException("RegisterClassEx"); }
                }
                int ex = NativeMethods.WS_EX_NOREDIRECTIONBITMAP | NativeMethods.WS_EX_TOPMOST
                       | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
                _dcompHwnd = NativeMethods.CreateWindowEx(ex, "UdoblDComp", "Udobl", NativeMethods.WS_POPUP,
                    0, 0, _pw, _ph, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
                if (_dcompHwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx");
                // Exclude from capture (no self-mirror) AND gate live recapture — the WPF SourceInitialized
                // that normally sets _liveOk never fires in DComp mode (the WPF window isn't shown).
                try { _liveOk = NativeMethods.SetWindowDisplayAffinity(_dcompHwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE); } catch { _liveOk = false; }

                var device = _glass.Device;
                _dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>();
                using (var adapter = _dxgiDevice.Adapter)
                using (var factory = adapter.GetParent<Factory2>())
                {
                    var desc = new SwapChainDescription1
                    {
                        Width = _pw, Height = _ph, Format = Format.B8G8R8A8_UNorm, Stereo = false,
                        SampleDescription = new SampleDescription(1, 0), Usage = Usage.RenderTargetOutput,
                        BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
                        AlphaMode = AlphaMode.Premultiplied, Flags = SwapChainFlags.None,
                    };
                    _swap = new SwapChain1(factory, device, ref desc);
                }
                _backBuffer = D3D11.Texture2D.FromSwapChain<D3D11.Texture2D>(_swap, 0);

                var iid = DCompIids.IDCompositionDevice;
                int hr = NativeMethods.DCompositionCreateDevice(_dxgiDevice.NativePointer, ref iid, out IntPtr devPtr);
                Marshal.ThrowExceptionForHR(hr);
                _dcDev = (IDCompositionDevice)Marshal.GetObjectForIUnknown(devPtr);
                Marshal.Release(devPtr);
                Marshal.ThrowExceptionForHR(_dcDev.CreateTargetForHwnd(_dcompHwnd, true, out _dcTarget));
                Marshal.ThrowExceptionForHR(_dcDev.CreateVisual(out _dcVisual));
                Marshal.ThrowExceptionForHR(_dcVisual.SetContent(_swap.NativePointer));
                Marshal.ThrowExceptionForHR(_dcTarget.SetRoot(_dcVisual));
                Marshal.ThrowExceptionForHR(_dcDev.Commit());
                _useDComp = true;
            }
            catch { _useDComp = false; CleanupDComp(); }
        }

        private void CleanupDComp()
        {
            try { if (_dcVisual != null) { Marshal.ReleaseComObject(_dcVisual); _dcVisual = null; } } catch { }
            try { if (_dcTarget != null) { Marshal.ReleaseComObject(_dcTarget); _dcTarget = null; } } catch { }
            try { if (_dcDev != null) { Marshal.ReleaseComObject(_dcDev); _dcDev = null; } } catch { }
            try { _backBuffer?.Dispose(); _backBuffer = null; } catch { }
            try { _swap?.Dispose(); _swap = null; } catch { }
            try { _dxgiDevice?.Dispose(); _dxgiDevice = null; } catch { }
            try { if (_dcompHwnd != IntPtr.Zero) { NativeMethods.DestroyWindow(_dcompHwnd); _dcompHwnd = IntPtr.Zero; } } catch { }
        }

        private void BuildVisualTree()
        {
            // The window now hosts ONLY the glass image — icons + hub are rendered INTO the D3D scene
            // (as sprites), so the layered (software-composited) window has just one element per frame.
            _glassImage = new Image { Width = W, Height = H, Stretch = Stretch.None, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(_glassImage, BitmapScalingMode.LowQuality);
            BuildHub(); // builds the offscreen _hub visual used as a sprite source (not added to the tree)

            _root = new Grid { Width = W, Height = H };
            _rootScale = new ScaleTransform(1, 1);
            _root.RenderTransform = _rootScale;
            _root.RenderTransformOrigin = new Point(0.5, 0.5);
            _root.Children.Add(_glassImage);
            Content = _root;
        }

        public void Configure(double tiltDegrees, int deadZoneRadius, Color accent, int maxDepth = 4)
        {
            _maxTilt = Math.Max(0, Math.Min(45, tiltDegrees));
            _deadZone = Math.Max(20, deadZoneRadius);
            _maxDepth = Math.Max(1, maxDepth);
            _accent = accent;
        }

        public void ShowMenu(List<MenuItemConfig> items, double centerXDip, double centerYDip)
        {
            StopCloseTimer();
            _navStack.Clear();
            _items = items ?? new List<MenuItemConfig>();
            _count = _items.Count;
            _highlight = -1;
            _tiltX = _tiltY = 0;

            ClampAndPosition(centerXDip, centerYDip);
            SetupCapture();
            BuildCells();
            UpdateHub();   // build hub sprite before the first render
            RenderGlass();

            if (_useDComp)
            {
                PositionDComp(true); // present is already done; now show the topmost DComp window
            }
            else
            {
                Show();
                AnimateOpen();
            }
            StartRenderLoop();
        }

        private void PositionDComp(bool show)
        {
            int x = (int)Math.Round(Left * _scale), y = (int)Math.Round(Top * _scale);
            uint flags = NativeMethods.SWP_NOACTIVATE | (show ? NativeMethods.SWP_SHOWWINDOW : 0);
            try { NativeMethods.SetWindowPos(_dcompHwnd, NativeMethods.HWND_TOPMOST, x, y, _pw, _ph, flags); } catch { }
        }

        public void HideMenu()
        {
            if (_useDComp)
            {
                StopRenderLoop();
                _navStack.Clear();
                try { NativeMethods.ShowWindow(_dcompHwnd, NativeMethods.SW_HIDE); } catch { }
            }
            else AnimateClose();
        }

        // ---- sub-ring navigation (center pinned) ----
        public bool Descend()
        {
            if (!HighlightIsGroup || _navStack.Count >= _maxDepth) return false;
            _navStack.Add(new RingFrame { Items = _items, Highlight = _highlight });
            _items = HighlightedItem.Children ?? new List<MenuItemConfig>();
            _count = _items.Count;
            _highlight = -1;
            BuildCells();
            UpdateHub();
            RenderGlass();
            AnimateBloom();
            return true;
        }

        public bool Pop()
        {
            if (_navStack.Count == 0) return false;
            var f = _navStack[_navStack.Count - 1];
            _navStack.RemoveAt(_navStack.Count - 1);
            _items = f.Items;
            _count = _items.Count;
            _highlight = f.Highlight;
            BuildCells();
            UpdateHub();
            RenderGlass();
            AnimateBloom();
            return true;
        }

        public bool UpdateCursor(double cursorXDip, double cursorYDip)
        {
            double dx = cursorXDip - CenterXDip, dy = cursorYDip - CenterYDip;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            int idx;
            double tX = 0, tY = 0;
            bool dead = _count == 0 || dist < _deadZone;
            if (dead) idx = -1;
            else
            {
                double angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
                if (angle < 0) angle += 360;
                double step = 360.0 / _count;
                idx = (int)Math.Round(angle / step) % _count;
                double frac = Math.Min(1.0, dist / 170.0);
                tX = (dx / dist) * _maxTilt * frac;
                tY = (dy / dist) * _maxTilt * frac;
            }

            double px = _tiltX, py = _tiltY;
            _tiltX += (tX - _tiltX) * 0.25;
            _tiltY += (tY - _tiltY) * 0.25;
            if (Math.Abs(_tiltX - px) > 0.05 || Math.Abs(_tiltY - py) > 0.05) _dirty = true; // wheel is still moving
            bool deadChanged = dead != InDeadZone;
            InDeadZone = dead;

            bool hiChanged = idx != _highlight;
            if (hiChanged) { _highlight = idx; _dirty = true; }
            if (hiChanged || deadChanged) UpdateHub(); // the glass itself is rendered by the render loop, only when dirty
            return hiChanged;
        }

        // ---- D3D render ----
        private void RenderGlass()
        {
            if (!_glassOk) return;
            double step = _count > 0 ? 360.0 / _count : 360;
            double hiC = _highlight >= 0 ? _highlight * step : 0;
            double pulse = 0.5 + 0.5 * Math.Sin(_pulseT);
            try
            {
                if (_useDComp)
                {
                    // Full GPU: render straight into the swapchain backbuffer and present to DWM (no readback).
                    _glass.RenderToTarget(_tiltX, _tiltY, pulse, hiC, step / 2 - GapDegrees / 2, _highlight >= 0,
                        new RawColor4(0, 0, 0, 0), _backBuffer);
                    _swap.Present(0, PresentFlags.None);
                }
                else
                {
                    var bmp = _glass.Render(_tiltX, _tiltY, pulse, hiC, step / 2 - GapDegrees / 2, _highlight >= 0,
                        new RawColor4(0, 0, 0, 0));
                    _glassImage.Source = bmp;
                }
            }
            catch { }
        }

        // Capture region = the window + a margin (for refraction/reflection offsets), clamped to the
        // virtual screen. Fixed for this open so the env texture size stays constant for live updates.
        private void SetupCapture()
        {
            if (!_glassOk) return;
            try
            {
                int vsL = (int)Math.Round(SystemParameters.VirtualScreenLeft * _scale);
                int vsT = (int)Math.Round(SystemParameters.VirtualScreenTop * _scale);
                int vsW = Math.Max(1, (int)Math.Round(SystemParameters.VirtualScreenWidth * _scale));
                int vsH = Math.Max(1, (int)Math.Round(SystemParameters.VirtualScreenHeight * _scale));
                int winL = (int)Math.Round(Left * _scale), winT = (int)Math.Round(Top * _scale);
                // Overlay physical size: DComp window is _pw px (1:1 with the RT); the WPF window is W DIP = W*scale px.
                int winW = _useDComp ? _pw : (int)Math.Round(W * _scale);
                int winH = _useDComp ? _ph : (int)Math.Round(H * _scale);
                int m = (int)Math.Round(120 * _scale);

                int rl = winL - m, rt = winT - m, rw = winW + 2 * m, rh = winH + 2 * m;
                if (rl < vsL) { rw -= (vsL - rl); rl = vsL; }
                if (rt < vsT) { rh -= (vsT - rt); rt = vsT; }
                if (rl + rw > vsL + vsW) rw = vsL + vsW - rl;
                if (rt + rh > vsT + vsH) rh = vsT + vsH - rt;
                _regL = rl; _regT = rt; _regW = Math.Max(1, rw); _regH = Math.Max(1, rh);

                _capBmp?.Dispose();
                _capBmp = new System.Drawing.Bitmap(_regW, _regH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _capBytes = new byte[_regW * _regH * 4];
                CaptureRegionInto();
                _glass.SetEnvironment(_capBytes, _regW, _regH);
                // RT pixel (0..W) -> screen px = winL + px*(winW/W); env-uv = (screenPx - regL)/regW.
                double du = (winW / W) / _regW, dv = (winH / H) / _regH;
                _glass.SetMapping((winL - _regL) / (double)_regW, (winT - _regT) / (double)_regH, du, dv);
            }
            catch { }
        }

        private void CaptureRegionInto()
        {
            if (_capBmp == null) return;
            try
            {
                using (var g = System.Drawing.Graphics.FromImage(_capBmp))
                    g.CopyFromScreen(_regL, _regT, 0, 0, new System.Drawing.Size(_regW, _regH));
                var rect = new System.Drawing.Rectangle(0, 0, _regW, _regH);
                var data = _capBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                for (int y = 0; y < _regH; y++)
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _capBytes, y * _regW * 4, _regW * 4);
                _capBmp.UnlockBits(data);
            }
            catch { }
        }

        // ~30fps loop while the menu is open: animate pulse, periodically re-capture the live screen, render.
        private void StartRenderLoop()
        {
            StopRenderLoop();
            _frame = 0;
            _dirty = true;
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _renderTimer.Tick += RenderTick;
            _renderTimer.Start();
        }

        private void StopRenderLoop()
        {
            if (_renderTimer != null) { _renderTimer.Stop(); _renderTimer.Tick -= RenderTick; _renderTimer = null; }
        }

        private void RenderTick(object sender, EventArgs e)
        {
            _frame++;
            try
            {
                // Live screen re-capture ~7.5 Hz (every 4th tick) → marks dirty so we reflect the live screen.
                if (_liveOk && _glassOk && (_frame % 4 == 0)) { CaptureRegionInto(); _glass.UpdateEnvironment(_capBytes); _dirty = true; }
                // Render ONLY when something changed (moving / live update) — idle costs nothing.
                if (_dirty) { _pulseT += 0.20; RenderGlass(); _dirty = false; }
            }
            catch
            {
                // Device lost / GPU hiccup — stop touching the GPU so a driver reset can't crash the tray app.
                StopRenderLoop();
            }
        }

        // ---- cells / icon+hub sprites (rendered in the D3D scene, not as WPF overlays) ----
        private const int IconBox = 44;

        private void BuildCells()
        {
            if (_glassOk) _glass.ClearRing(); // drop old geometry so an empty/new ring shows nothing stale
            _iconDefs.Clear();
            if (!_glassOk || _count == 0) { _glass?.SetIcons(_iconDefs); return; }

            double step = 360.0 / _count;
            double rMid = (InnerRadius + OuterRadius) / 2;
            var cells = new List<D3DGlass.CellDef>(_count);
            for (int i = 0; i < _count; i++)
            {
                double center = i * step;
                Color tint = TintFor(_items[i]);
                if (_items[i].IsGroup) tint = Lighten(tint, 0.15);
                cells.Add(new D3DGlass.CellDef(center, step / 2 - GapDegrees / 2,
                    new SharpDX.Vector3(tint.R / 255f, tint.G / 255f, tint.B / 255f)));
                _iconDefs.Add(new D3DGlass.SpriteDef
                {
                    Bgra = VisualToBgra(BuildIconVisual(_items[i]), IconBox, IconBox),
                    W = IconBox, H = IconBox,
                    Anchor = D3DGlass.ModelPoint(rMid, center, Thickness / 2 + 0.01),
                    Centered = false,
                });
            }
            _glass.BuildRing(cells, InnerRadius, OuterRadius, Thickness);
            _glass.SetIcons(_iconDefs);
        }

        private FrameworkElement BuildIconVisual(MenuItemConfig item)
        {
            var back = new Ellipse
            {
                Width = IconBox, Height = IconBox,
                Fill = new RadialGradientBrush(Color.FromArgb(150, 10, 12, 16), Color.FromArgb(70, 10, 12, 16)),
            };
            UIElement glyph;
            var icon = IconResolver.ForItem(item);
            if (icon != null)
            {
                var img = new Image { Source = icon, Width = 30, Height = 30, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                glyph = img;
            }
            else
            {
                glyph = new TextBlock
                {
                    Text = string.IsNullOrEmpty(item.Glyph) ? "" : item.Glyph,
                    FontFamily = GlyphFont, FontSize = 22,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            var g = new Grid { Width = IconBox, Height = IconBox };
            g.Children.Add(back);
            g.Children.Add(glyph);
            return g;
        }

        // Rasterize a WPF visual to premultiplied BGRA bytes (Pbgra32) for a D3D sprite texture.
        private static byte[] VisualToBgra(FrameworkElement fe, int w, int h)
        {
            fe.Measure(new Size(w, h));
            fe.Arrange(new Rect(0, 0, w, h));
            fe.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(fe);
            var bytes = new byte[w * h * 4];
            rtb.CopyPixels(bytes, w * 4, 0);
            return bytes;
        }

        // ---- animations ----
        private void AnimateOpen()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var s = new DoubleAnimation(0.84, 1.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, s.Clone());
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
        }

        private void AnimateBloom()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var s = new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, s.Clone());
        }

        private void AnimateClose()
        {
            _navStack.Clear();
            StopRenderLoop(); // freeze on the last frame while it fades out
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var s = new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease };
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, s.Clone());
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));
            StartCloseTimer(140);
        }

        private void StartCloseTimer(double ms)
        {
            StopCloseTimer();
            _closeTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
            _closeTimer.Tick += (s, e) => { StopCloseTimer(); Hide(); };
            _closeTimer.Start();
        }
        private void StopCloseTimer() { if (_closeTimer != null) { _closeTimer.Stop(); _closeTimer = null; } }

        private void ClampAndPosition(double cxDip, double cyDip)
        {
            double left = cxDip - W / 2, top = cyDip - H / 2;
            double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            if (left < vx) left = vx;
            if (top < vy) top = vy;
            if (left + W > vx + vw) left = vx + vw - W;
            if (top + H > vy + vh) top = vy + vh - H;
            Left = left; Top = top;
            CenterXDip = left + W / 2; CenterYDip = top + H / 2;
        }

        // ---- 2D hub ----
        private void BuildHub()
        {
            var disc = new Ellipse
            {
                Width = InnerRadius * 2 * 0.62, Height = InnerRadius * 2 * 0.62,
                Fill = new RadialGradientBrush(Color.FromArgb(150, 14, 16, 22), Color.FromArgb(190, 8, 10, 14)),
                Stroke = new SolidColorBrush(WithAlpha(Lighten(_accent, 0.2), 150)), StrokeThickness = 1.2,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.4 },
            };
            _hubGlyph = new TextBlock { FontSize = 26, Foreground = new SolidColorBrush(Lighten(_accent, 0.7)), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = GlyphFont };
            _hubImage = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(_hubImage, BitmapScalingMode.HighQuality);
            _hubLabel = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 120, Margin = new Thickness(0, 3, 0, 0) };
            _hubKind = new TextBlock { FontSize = 10.5, Foreground = new SolidColorBrush(WithAlpha(Lighten(_accent, 0.45), 185)), TextAlignment = TextAlignment.Center };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(_hubGlyph); stack.Children.Add(_hubImage); stack.Children.Add(_hubLabel); stack.Children.Add(_hubKind);
            // Fixed-size offscreen visual rasterized to the hub sprite (centered in the wheel).
            _hub = new Grid { IsHitTestVisible = false, Width = HubW, Height = HubH };
            _hub.Children.Add(disc); _hub.Children.Add(stack);
        }

        private void UpdateHub()
        {
            string dots = _navStack.Count > 0 ? new string('•', _navStack.Count) + " " : "";
            var item = HighlightedItem;
            if (item == null)
            {
                _hubImage.Visibility = Visibility.Collapsed;
                _hubGlyph.Visibility = Visibility.Visible;
                if (_navStack.Count > 0)
                {
                    _hubGlyph.Text = ConfigStore.Ico(0xE72B);
                    _hubGlyph.Foreground = new SolidColorBrush(Lighten(_accent, 0.6));
                    _hubLabel.Text = "Назад";
                    _hubKind.Text = dots + "к предыдущему кругу ‹";
                }
                else
                {
                    _hubGlyph.Text = "";
                    _hubGlyph.Foreground = new SolidColorBrush(WithAlpha(Lighten(_accent, 0.3), 200));
                    _hubLabel.Text = _count == 0 ? "Нет действий" : "Отмена";
                    _hubKind.Text = _count == 0 ? "настройте в параметрах" : "отпустите здесь";
                }
            }
            else
            {
                var icon = IconResolver.ForItem(item);
                if (icon != null) { _hubImage.Source = icon; _hubImage.Visibility = Visibility.Visible; _hubGlyph.Visibility = Visibility.Collapsed; }
                else { _hubGlyph.Text = string.IsNullOrEmpty(item.Glyph) ? "" : item.Glyph; _hubGlyph.Foreground = new SolidColorBrush(Lighten(_accent, 0.7)); _hubGlyph.Visibility = Visibility.Visible; _hubImage.Visibility = Visibility.Collapsed; }
                _hubLabel.Text = item.Label;
                _hubKind.Text = dots + (item.IsGroup ? "открыть подкруг ›" : KindCaption(item.KindEnum));
            }

            // Rasterize the hub to its sprite (centered in the scene). Re-uploaded only here (on change).
            if (_glassOk)
                _glass.SetHub(new D3DGlass.SpriteDef
                {
                    Bgra = VisualToBgra(_hub, HubW, HubH),
                    W = HubW, H = HubH,
                    Anchor = default(SharpDX.Vector3),
                    Centered = true,
                });
        }

        private static readonly FontFamily GlyphFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private Color TintFor(MenuItemConfig item)
        {
            if (IconResolver.TryGetTint(item, out Color c))
            {
                double max = Math.Max(c.R, Math.Max(c.G, c.B));
                return max < 70 ? Lighten(c, 0.4) : c;
            }
            return _accent;
        }

        private static string KindCaption(ActionKind k)
        {
            switch (k)
            {
                case ActionKind.LaunchApp: return "запустить приложение";
                case ActionKind.OpenUrl: return "открыть ссылку";
                case ActionKind.RunCommand: return "выполнить команду";
                case ActionKind.Hotkey: return "горячая клавиша";
                case ActionKind.Group: return "открыть подкруг ›";
                default: return "";
            }
        }

        public static bool TryParseColor(string hex, out Color color)
        {
            color = Colors.White;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            try { var o = ColorConverter.ConvertFromString(hex.Trim()); if (o is Color c) { color = c; return true; } } catch { }
            return false;
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        private static Color Lighten(Color c, double amount) => Color.FromRgb(
            (byte)Math.Min(255, c.R + (255 - c.R) * amount), (byte)Math.Min(255, c.G + (255 - c.G) * amount), (byte)Math.Min(255, c.B + (255 - c.B) * amount));

        // ---- offscreen preview (UDOBL_RENDER) ----
        public void PreviewRender(List<MenuItemConfig> items, int highlightIndex, string path)
        {
            _useDComp = false; // offscreen QA always uses the WriteableBitmap path (DComp can't be read back)
            _items = items ?? new List<MenuItemConfig>();
            _count = _items.Count;
            CenterXDip = W / 2; CenterYDip = H / 2;
            _highlight = (highlightIndex >= 0 && highlightIndex < _count) ? highlightIndex : -1;

            if (_glassOk) { _glass.SetEnvironment(MakeEnvBytes(), (int)W, (int)H); _glass.SetMapping(0, 0, 1.0 / W, 1.0 / H); }
            BuildCells();

            if (_highlight >= 0)
            {
                double step = 360.0 / Math.Max(1, _count);
                double rad = _highlight * step * Math.PI / 180.0;
                _tiltX = Math.Sin(rad) * _maxTilt;
                _tiltY = -Math.Cos(rad) * _maxTilt;
                InDeadZone = false;
            }
            UpdateHub();   // build the hub sprite first
            RenderGlass(); // then render glass + icons + hub into the bitmap

            _rootScale.ScaleX = _rootScale.ScaleY = 1; _root.Opacity = 1;
            _root.Measure(new Size(W, H));
            _root.Arrange(new Rect(0, 0, W, H));
            _root.UpdateLayout();

            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                var bg = new LinearGradientBrush(Color.FromRgb(0x10, 0x12, 0x18), Color.FromRgb(0x24, 0x28, 0x36), 45);
                dc.DrawRectangle(bg, null, new Rect(0, 0, W, H));
                dc.DrawRectangle(new VisualBrush(_root) { Stretch = Stretch.None }, null, new Rect(0, 0, W, H));
            }
            var outb = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            outb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(outb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        }

        private static byte[] MakeEnvBytes()
        {
            int w = (int)W, h = (int)H;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    double fx = (double)x / w, fy = (double)y / h;
                    byte r = (byte)(40 + 200 * fx), gg = (byte)(40 + 200 * fy), b = (byte)(160 - 120 * fx);
                    if ((x % 60 < 4) || (y % 60 < 4)) { r = 245; gg = 245; b = 250; }
                    px[o] = b; px[o + 1] = gg; px[o + 2] = r; px[o + 3] = 255;
                }
            return px;
        }
    }
}

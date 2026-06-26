using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Udobl.Core;
using static Udobl.Native.NativeMethods;

namespace Udobl.Native
{
    /// <summary>
    /// Pulls real icons straight out of Windows (the shell icon of an exe, the
    /// default browser's icon for URLs). Results are cached + frozen so they can
    /// be produced on a background thread and used on the UI thread.
    /// </summary>
    public static class IconResolver
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _cache =
            new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static string _browserExe;

        /// <summary>Real icon for a menu item, or null (caller falls back to a glyph).</summary>
        public static ImageSource ForItem(MenuItemConfig item)
        {
            if (item == null) return null;
            try
            {
                switch (item.KindEnum)
                {
                    case ActionKind.LaunchApp: return ForExe(item.Target);
                    case ActionKind.OpenUrl: return ForExe(DefaultBrowserExe());
                    default: return null; // RunCommand / Hotkey → glyph
                }
            }
            catch { return null; }
        }

        public static ImageSource ForExe(string target)
        {
            string path = ResolveExe(target);
            if (path == null) return null;
            ImageSource img;
            if (_cache.TryGetValue(path, out img)) return img;
            img = ExtractIcon(path);
            _cache[path] = img; // cache nulls too, so we don't retry failures
            return img;
        }

        private static readonly ConcurrentDictionary<ImageSource, Color> _tintCache =
            new ConcurrentDictionary<ImageSource, Color>();

        /// <summary>The icon's characteristic (saturation-weighted) color, for tinting its glass cell.</summary>
        public static bool TryGetTint(MenuItemConfig item, out Color color)
        {
            color = Colors.Gray;
            var src = ForItem(item) as BitmapSource;
            if (src == null) return false;
            try { color = _tintCache.GetOrAdd(src, s => Dominant((BitmapSource)s)); return true; }
            catch { return false; }
        }

        private static Color Dominant(BitmapSource src)
        {
            // Downscale to a small image for a fast, representative average.
            BitmapSource s = src;
            if (src.PixelWidth > 24 || src.PixelHeight > 24)
            {
                double sx = 20.0 / src.PixelWidth, sy = 20.0 / src.PixelHeight;
                s = new TransformedBitmap(src, new ScaleTransform(sx, sy));
            }
            var conv = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            conv.CopyPixels(px, stride, 0);

            double rs = 0, gs = 0, bs = 0, ws = 0;       // saturation-weighted (the "vivid" color)
            double ra = 0, ga = 0, ba = 0, aa = 0;       // plain alpha-weighted average (fallback)
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a < 40) continue;
                double af = a / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double wgt = af * (0.12 + sat);
                rs += r * wgt; gs += g * wgt; bs += b * wgt; ws += wgt;
                ra += r * af; ga += g * af; ba += b * af; aa += af;
            }

            if (ws > 0.001) return Color.FromRgb((byte)(rs / ws), (byte)(gs / ws), (byte)(bs / ws));
            if (aa > 0.001) return Color.FromRgb((byte)(ra / aa), (byte)(ga / aa), (byte)(ba / aa));
            return Colors.Gray;
        }

        private static ImageSource ExtractIcon(string path)
        {
            var info = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DestroyIcon(info.hIcon); }
        }

        /// <summary>Resolve "calc.exe" / "explorer.exe" / a full path / a path with args to a real file.</summary>
        public static string ResolveExe(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));

            if (Path.IsPathRooted(target) && File.Exists(target)) return target;

            string[] dirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            };
            foreach (var d in dirs)
            {
                try { string p = Path.Combine(d, target); if (File.Exists(p)) return p; } catch { }
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var d in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try { string p = Path.Combine(d.Trim(), target); if (File.Exists(p)) return p; } catch { }
            }

            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + Path.GetFileName(target)))
                {
                    string v = key?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v))
                    {
                        v = v.Trim('"');
                        if (File.Exists(v)) return v;
                    }
                }
            }
            catch { }

            return null;
        }

        public static string DefaultBrowserExe()
        {
            if (_browserExe != null) return _browserExe.Length == 0 ? null : _browserExe;
            try
            {
                var sb = new StringBuilder(1024);
                uint len = (uint)sb.Capacity;
                int hr = AssocQueryString(0, ASSOCSTR_EXECUTABLE, "http", "open", sb, ref len);
                if (hr == 0)
                {
                    string p = sb.ToString();
                    if (File.Exists(p)) { _browserExe = p; return p; }
                }
            }
            catch { }
            _browserExe = "";
            return null;
        }

        /// <summary>Extract icons ahead of time (background thread) so the menu opens instantly.</summary>
        public static void Prewarm(IEnumerable<MenuItemConfig> items)
        {
            try { DefaultBrowserExe(); } catch { }
            if (items == null) return;
            foreach (var it in items)
            {
                try { ForItem(it); } catch { }
                if (it != null && it.Children != null) Prewarm(it.Children); // warm sub-ring icons too
            }
        }
    }
}

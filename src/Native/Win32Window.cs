using System;
using System.Collections.Concurrent;
using System.Text;
using static Udobl.Native.NativeMethods;

namespace Udobl.Native
{
    /// <summary>Helpers around the foreground window and overlay window styling.</summary>
    public static class Win32Window
    {
        // hwnd -> process name (lowercase, no extension). Cheap cache for the hot path.
        private static readonly ConcurrentDictionary<IntPtr, string> _nameCache = new ConcurrentDictionary<IntPtr, string>();

        public static IntPtr Foreground() => GetForegroundWindow();

        /// <summary>Process name of the foreground window, lowercased, without ".exe".</summary>
        public static string ForegroundProcessName()
        {
            return ProcessNameOf(GetForegroundWindow());
        }

        public static string ProcessNameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            if (_nameCache.TryGetValue(hwnd, out var cached)) return cached;

            string name = string.Empty;
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0)
                {
                    string path = ProcessPath(pid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    }
                }
            }
            catch { }

            // Cache modestly; foreground hwnds are reused heavily.
            if (_nameCache.Count > 512) _nameCache.Clear();
            _nameCache[hwnd] = name;
            return name;
        }

        public static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                    return sb.ToString(0, (int)size);
            }
            catch { }
            finally { CloseHandle(h); }
            return string.Empty;
        }

        /// <summary>Make a WPF window a non-activating, click-through tool overlay.</summary>
        public static void MakeOverlay(IntPtr hwnd)
        {
            IntPtr ex = GetWindowLongAuto(hwnd, GWL_EXSTYLE);
            long style = ex.ToInt64();
            style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
            SetWindowLongAuto(hwnd, GWL_EXSTYLE, (IntPtr)style);
        }
    }
}

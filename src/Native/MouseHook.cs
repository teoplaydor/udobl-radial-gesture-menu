using System;
using System.Runtime.InteropServices;
using static Udobl.Native.NativeMethods;

namespace Udobl.Native
{
    /// <summary>
    /// Global low-level mouse hook. Must be installed on a thread that pumps
    /// messages (the WPF UI thread does). Callbacks run on that thread, so the
    /// suppress decision is synchronous and fast; heavier UI work is dispatched.
    /// </summary>
    public sealed class MouseHook : IDisposable
    {
        // Return true from a handler to swallow the event (it won't reach apps below).
        public Func<int, int, bool> MiddleDown;  // (x, y) -> suppress?
        public Func<int, int, bool> MiddleUp;    // (x, y) -> suppress?
        public Func<bool> RightDown;             // -> suppress? (used to cancel an open menu)
        public Func<bool> RightUp;               // -> suppress? (consume the up that pairs a swallowed down)

        private IntPtr _hook = IntPtr.Zero;
        // Keep a strong reference so the delegate is not collected while the hook lives.
        private readonly LowLevelMouseProc _proc;
        private bool _disposed;

        public MouseHook()
        {
            _proc = HookCallback;
        }

        public bool IsInstalled => _hook != IntPtr.Zero;

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            IntPtr hMod = GetModuleHandle(null); // null == current module handle, valid for WH_MOUSE_LL
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            if (_hook == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
        }

        public void Uninstall()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    bool injected = (data.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;

                    // Never react to synthetic input (our own click replays / hotkeys).
                    if (!injected)
                    {
                        int msg = wParam.ToInt32();
                        if (msg == WM_MBUTTONDOWN)
                        {
                            if (MiddleDown != null && MiddleDown(data.pt.x, data.pt.y))
                                return (IntPtr)1;
                        }
                        else if (msg == WM_MBUTTONUP)
                        {
                            if (MiddleUp != null && MiddleUp(data.pt.x, data.pt.y))
                                return (IntPtr)1;
                        }
                        else if (msg == WM_RBUTTONDOWN)
                        {
                            if (RightDown != null && RightDown())
                                return (IntPtr)1;
                        }
                        else if (msg == WM_RBUTTONUP)
                        {
                            if (RightUp != null && RightUp())
                                return (IntPtr)1;
                        }
                    }
                }
                catch
                {
                    // A throwing hook proc is fatal to input handling; swallow everything.
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Uninstall();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using static Udobl.Native.NativeMethods;

namespace Udobl.Native
{
    /// <summary>Synthesises keyboard chords and a middle-click replay via SendInput.</summary>
    public static class InputSender
    {
        public static void ReplayMiddleClick()
        {
            var inputs = new[]
            {
                MouseInput(MOUSEEVENTF_MIDDLEDOWN),
                MouseInput(MOUSEEVENTF_MIDDLEUP),
            };
            SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// Ctrl+Left-click at the current cursor position. In browsers this opens a hovered
        /// link in a new background tab (just like a middle click) but, being a LEFT click,
        /// never starts autoscroll — so the cursor never turns into the scroll anchor.
        /// </summary>
        public static void SendCtrlLeftClick()
        {
            const ushort VK_CONTROL = 0x11;
            var inputs = new[]
            {
                KeyDown(VK_CONTROL),
                MouseInput(MOUSEEVENTF_LEFTDOWN),
                MouseInput(MOUSEEVENTF_LEFTUP),
                KeyUp(VK_CONTROL),
            };
            SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT MouseInput(uint flags)
        {
            var input = new INPUT { type = INPUT_MOUSE };
            input.u.mi = new MOUSEINPUT { dwFlags = flags };
            return input;
        }

        /// <summary>
        /// Parses a chord like "Ctrl+Shift+T" or "Win+D" and sends it to the
        /// currently focused window. Returns false if no real key was parsed.
        /// </summary>
        public static bool SendHotkey(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return false;

            var mods = new List<ushort>();
            ushort key = 0;

            foreach (var raw in chord.Split('+'))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;

                switch (token.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        mods.Add(0x11); break; // VK_CONTROL
                    case "shift":
                        mods.Add(0x10); break; // VK_SHIFT
                    case "alt":
                        mods.Add(0x12); break; // VK_MENU
                    case "win":
                    case "windows":
                    case "meta":
                        mods.Add(0x5B); break; // VK_LWIN
                    default:
                        key = ParseKey(token);
                        break;
                }
            }

            if (key == 0 && mods.Count == 0) return false;

            var seq = new List<INPUT>();
            foreach (var m in mods) seq.Add(KeyDown(m));
            if (key != 0)
            {
                seq.Add(KeyDown(key));
                seq.Add(KeyUp(key));
            }
            for (int i = mods.Count - 1; i >= 0; i--) seq.Add(KeyUp(mods[i]));

            var arr = seq.ToArray();
            SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
            return true;
        }

        private static INPUT KeyDown(ushort vk) => KeyInput(vk, false);
        private static INPUT KeyUp(ushort vk) => KeyInput(vk, true);

        private static INPUT KeyInput(ushort vk, bool up)
        {
            uint flags = up ? KEYEVENTF_KEYUP : 0;
            if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT { type = INPUT_KEYBOARD };
            input.u.ki = new KEYBDINPUT { wVk = vk, dwFlags = flags };
            return input;
        }

        private static bool IsExtended(ushort vk)
        {
            switch (vk)
            {
                case 0x21: // PageUp
                case 0x22: // PageDown
                case 0x23: // End
                case 0x24: // Home
                case 0x25: // Left
                case 0x26: // Up
                case 0x27: // Right
                case 0x28: // Down
                case 0x2D: // Insert
                case 0x2E: // Delete
                case 0x5B: // LWin
                case 0x5C: // RWin
                case 0x6F: // Divide
                case 0xAD: // VolumeMute
                case 0xAE: // VolumeDown
                case 0xAF: // VolumeUp
                case 0xB0: // MediaNextTrack
                case 0xB1: // MediaPrevTrack
                case 0xB2: // MediaStop
                case 0xB3: // MediaPlayPause
                    return true;
                default:
                    return false;
            }
        }

        private static ushort ParseKey(string token)
        {
            string t = token.ToLowerInvariant();

            // Named keys / media keys
            switch (t)
            {
                case "enter":
                case "return": return 0x0D;
                case "tab": return 0x09;
                case "esc":
                case "escape": return 0x1B;
                case "space": return 0x20;
                case "backspace": return 0x08;
                case "delete":
                case "del": return 0x2E;
                case "insert":
                case "ins": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": return 0x21;
                case "pagedown": return 0x22;
                case "left": return 0x25;
                case "up": return 0x26;
                case "right": return 0x27;
                case "down": return 0x28;
                case "printscreen":
                case "prtsc": return 0x2C;
                case "volup":
                case "volumeup": return 0xAF;
                case "voldown":
                case "volumedown": return 0xAE;
                case "mute":
                case "volumemute": return 0xAD;
                case "playpause":
                case "mediaplaypause": return 0xB3;
                case "nexttrack":
                case "medianext": return 0xB0;
                case "prevtrack":
                case "mediaprev": return 0xB1;
            }

            // F1..F24
            if (t.Length >= 2 && t[0] == 'f' && int.TryParse(t.Substring(1), out int fn) && fn >= 1 && fn <= 24)
                return (ushort)(0x70 + (fn - 1));

            // Single letter or digit
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c >= 'A' && c <= 'Z') return (ushort)c;
                if (c >= '0' && c <= '9') return (ushort)c;
            }

            // Fall back to the Forms Keys enum parser for anything else.
            try
            {
                if (Enum.TryParse(token, true, out Keys k))
                    return (ushort)k;
            }
            catch { }

            return 0;
        }
    }
}

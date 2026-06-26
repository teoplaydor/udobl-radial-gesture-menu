using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace Udobl.Core
{
    /// <summary>What a radial slice does when selected.</summary>
    public enum ActionKind
    {
        LaunchApp = 0,   // Target = path/exe (env vars ok), Args = arguments
        OpenUrl = 1,     // Target = url (scheme optional)
        RunCommand = 2,  // Target = command line executed via cmd /c
        Hotkey = 3,      // Target = chord, e.g. "Ctrl+Shift+T"
        Group = 4         // a sub-ring: Children are its slices (release-on = descend)
    }

    /// <summary>One configurable slice of the radial menu.</summary>
    public class MenuItemConfig
    {
        public string Label { get; set; } = "New action";
        public int Kind { get; set; } = (int)ActionKind.LaunchApp; // stored as int for stable JSON
        public string Target { get; set; } = "";
        public string Args { get; set; } = "";
        public string Glyph { get; set; } = "✦"; // ✦
        public string Color { get; set; } = "";        // optional per-slice accent (#RRGGBB)
        public bool Enabled { get; set; } = true;

        // Nested sub-ring. null = leaf; non-null = group (even when empty). Absent in JSON for
        // leaves so old config stays byte-identical; round-trips recursively when present.
        public List<MenuItemConfig> Children { get; set; } = null;

        [ScriptIgnore]
        public ActionKind KindEnum => (ActionKind)Kind;

        [ScriptIgnore]
        public bool IsGroup => Kind == (int)ActionKind.Group;
    }

    public class AppSettings
    {
        public bool GestureEnabled { get; set; } = true;
        public bool RunOnStartup { get; set; } = false;
        public bool TrackUsage { get; set; } = true;
        public bool TrackUrls { get; set; } = true;
        public bool FillWithSuggestions { get; set; } = true;
        public bool ContextMenuEnabled { get; set; } = false; // "Add to Udobl" in Explorer right-click

        public int MaxSlots { get; set; } = 12;       // hard cap on slices shown (room for suggestions)
        public int DeadZoneRadius { get; set; } = 52; // px around center = "cancel"
        public double TiltDegrees { get; set; } = 34; // 3D lean of the ring
        public int HoldMs { get; set; } = 170;        // press time before the menu engages
        public int MaxDepth { get; set; } = 4;        // sub-ring nesting guard
        public string AccentColor { get; set; } = "#8A93A1"; // muted slate (low saturation)

        // Update check (notify-only; user hosts the version.json wherever they like).
        public bool UpdateCheckEnabled { get; set; } = true;
        public string UpdateUrl { get; set; } = "";       // URL of a version.json manifest
        public string LastUpdateCheck { get; set; } = ""; // ISO-8601 UTC; throttle marker

        // Process names (lowercase, no ".exe") where the gesture is disabled.
        public List<string> ExcludedProcesses { get; set; } = new List<string>();
    }

    public class AppConfig
    {
        public AppSettings Settings { get; set; } = new AppSettings();
        public List<MenuItemConfig> Items { get; set; } = new List<MenuItemConfig>();
    }
}

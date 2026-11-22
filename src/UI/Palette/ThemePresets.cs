using System.Collections.Generic;
using TaskbarMonitor.Config;

namespace TaskbarMonitor.UI.Palette;

/// <summary>
/// Named theme presets — each maps a theme name to a full ColorsConfig (7 keys).
/// Used by SettingsPaletteViewModel for the Theme command group.
/// </summary>
public static class ThemePresets
{
    public static readonly IReadOnlyDictionary<string, ColorsConfig> All = new Dictionary<string, ColorsConfig>
    {
        ["Midnight"] = new ColorsConfig
        {
            Cpu = "#007ACC",
            Ram = "#28A745",
            Network = "#17A2B8",
            CommandCode = "#6F42C1",
            OpenCode = "#FD7E14",
            Antigravity = "#4285F4",
            Claude = "#D97706"
        },
        ["Ocean"] = new ColorsConfig
        {
            Cpu = "#006D77",
            Ram = "#0096C7",
            Network = "#00B4D8",
            CommandCode = "#48CAE4",
            OpenCode = "#90E0EF",
            Antigravity = "#ADE8F4",
            Claude = "#CAF0F8"
        },
        ["Forest"] = new ColorsConfig
        {
            Cpu = "#1B4332",
            Ram = "#2D6A4F",
            Network = "#40916C",
            CommandCode = "#52B788",
            OpenCode = "#74C69D",
            Antigravity = "#95D5B2",
            Claude = "#B7E4C7"
        },
        ["Sakura"] = new ColorsConfig
        {
            Cpu = "#C9184A",
            Ram = "#E63946",
            Network = "#F72585",
            CommandCode = "#F77F00",
            OpenCode = "#F4A261",
            Antigravity = "#FF6B6B",
            Claude = "#E9C46A"
        }
    };

    /// <summary>Default theme name used when config has no theme set.</summary>
    public const string DefaultTheme = "Midnight";

    /// <summary>Returns the ColorsConfig for a theme name, or DefaultTheme if not found.</summary>
    public static ColorsConfig Get(string themeName)
    {
        if (All.TryGetValue(themeName, out var preset))
            return preset;
        return All[DefaultTheme];
    }

    /// <summary>Checks if current config colors match a known preset (for IsActive).</summary>
    public static bool Matches(ColorsConfig configColors, string themeName)
    {
        if (!All.TryGetValue(themeName, out var preset))
            return false;

        return configColors.Cpu == preset.Cpu
            && configColors.Ram == preset.Ram
            && configColors.Network == preset.Network
            && configColors.CommandCode == preset.CommandCode
            && configColors.OpenCode == preset.OpenCode
            && configColors.Antigravity == preset.Antigravity
            && configColors.Claude == preset.Claude;
    }
}
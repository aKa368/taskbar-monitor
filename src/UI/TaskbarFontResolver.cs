using System.Windows;
using System.Windows.Media;

namespace TaskbarMonitor.UI;

/// <summary>Chooses an installed font while preserving a valid user preference.</summary>
internal static class TaskbarFontResolver
{
    internal const string SafeFallback = "Segoe UI";
    private const string PreferredMonospaceFallback = "Cascadia Mono";

    public static string Resolve(string? configured, IEnumerable<string>? installedFamilies = null)
    {
        string requested = configured?.Trim() ?? string.Empty;
        try
        {
            IEnumerable<string> source = installedFamilies
                ?? Fonts.SystemFontFamilies.Select(static family => family.Source);
            var available = new HashSet<string>(source, StringComparer.OrdinalIgnoreCase);

            if (requested.Length > 0 && available.Contains(requested))
                return requested;
            if (available.Contains(PreferredMonospaceFallback))
                return PreferredMonospaceFallback;

            string systemFont = SystemFonts.MessageFontFamily.Source;
            return available.Contains(systemFont) ? systemFont : SafeFallback;
        }
        catch
        {
            return SafeFallback;
        }
    }
}

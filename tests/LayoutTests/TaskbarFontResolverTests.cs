using TaskbarMonitor.UI;
using Xunit;

namespace LayoutTests;

public sealed class TaskbarFontResolverTests
{
    [Fact]
    public void KeepsConfiguredFontWhenItIsInstalled()
    {
        string actual = TaskbarFontResolver.Resolve(
            "Custom Mono",
            new[] { "Segoe UI", "Custom Mono" });

        Assert.Equal("Custom Mono", actual);
    }

    [Fact]
    public void UsesInstalledMonospaceFallbackWhenConfiguredFontIsMissing()
    {
        string actual = TaskbarFontResolver.Resolve(
            "Missing Font",
            new[] { "Segoe UI", "Cascadia Mono" });

        Assert.Equal("Cascadia Mono", actual);
    }

    [Fact]
    public void UsesSafeFallbackWhenNoKnownFontIsAvailable()
    {
        string actual = TaskbarFontResolver.Resolve(null, Array.Empty<string>());

        Assert.Equal(TaskbarFontResolver.SafeFallback, actual);
    }
}

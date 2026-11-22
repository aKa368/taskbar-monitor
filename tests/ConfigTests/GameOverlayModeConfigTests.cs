using System.Text.Json;
using TaskbarMonitor.Config;
using Xunit;

namespace ConfigTests;

public sealed class GameOverlayModeConfigTests
{
    [Fact]
    public void GameOverlayMode_DefaultIsAutoHide()
    {
        var config = new ConfigData();
        Assert.Equal("AutoHide", config.GameOverlayMode);
    }

    [Fact]
    public void GameOverlayMode_CanSerializeAndDeserializeRoundtrip()
    {
        var config = new ConfigData
        {
            GameOverlayMode = "MiniHud"
        };

        string json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<ConfigData>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("MiniHud", deserialized.GameOverlayMode);
    }

    [Theory]
    [InlineData("AutoHide")]
    [InlineData("MiniHud")]
    [InlineData("None")]
    public void GameOverlayMode_SupportsVariousModes(string mode)
    {
        var config = new ConfigData { GameOverlayMode = mode };
        Assert.Equal(mode, config.GameOverlayMode);
    }
}

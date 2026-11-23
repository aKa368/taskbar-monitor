using System.Windows;
using System.Windows.Controls;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI;
using TaskbarMonitor.UI.Palette;
using Xunit;

namespace TaskbarMonitor.Tests.Palette;

public class SettingsPaletteViewModelTests
{
    private static (SettingsPaletteViewModel vm, ConfigData cfg, List<ConfigData> saved) MakeVm()
    {
        var cfg = new ConfigData { Metrics = new() { Cpu = true }, Layout = "Compact" };
        var saved = new List<ConfigData>();
        var vm = new SettingsPaletteViewModel(cfg, saved.Add);
        return (vm, cfg, saved);
    }

    [Fact]
    public void Builds_Commands_For_All_Groups()
    {
        var (vm, _, _) = MakeVm();
        Assert.Contains(vm.Commands, c => c.Id == "toggle-metric-cpu");
        Assert.DoesNotContain(vm.Commands, c => c.Id == "toggle-metric-power");
        Assert.Contains(vm.Commands, c => c.Id == "toggle-metric-clock");
        Assert.Contains(vm.Commands, c => c.Id == "toggle-agent-opencode" && c.Label == "Agent: OpenCode");
        Assert.Contains(vm.Commands, c => c.Id == "choose-layout-TwoLine");
        Assert.Contains(vm.Commands, c => c.Id == "set-interval-1");
        Assert.Contains(vm.Commands, c => c.Id == "set-autostart");
    }

    [Fact]
    public void Builds_Theme_Commands_For_All_Presets()
    {
        var (vm, _, _) = MakeVm();
        var presets = new[] { "Midnight", "Ocean", "Forest", "Sakura" };
        foreach (var preset in presets)
        {
            var cmd = vm.Commands.FirstOrDefault(c => c.Id == $"theme-{preset}");
            Assert.NotNull(cmd);
            Assert.Equal($"Theme: {preset}", cmd.Label);
            Assert.Equal(SettingsCommandKind.ChooseTheme, cmd.Kind);
        }
    }

    [Fact]
    public void Theme_Execute_Applies_Preset_Colors_And_Saves()
    {
        var (vm, cfg, saved) = MakeVm();
        cfg.Colors = new ColorsConfig(); // start with empty-ish
        cfg.Theme = "Midnight";

        var cmd = vm.Commands.First(c => c.Id == "theme-Ocean");
        cmd.Execute();

        // Verify all 7 colors applied from Ocean preset
        Assert.Equal("#006D77", cfg.Colors.Cpu);
        Assert.Equal("#0096C7", cfg.Colors.Ram);
        Assert.Equal("#00B4D8", cfg.Colors.Network);
        Assert.Equal("#48CAE4", cfg.Colors.CommandCode);
        Assert.Equal("#90E0EF", cfg.Colors.OpenCode);
        Assert.Equal("#ADE8F4", cfg.Colors.Antigravity);
        Assert.Equal("#CAF0F8", cfg.Colors.Claude);
        Assert.Equal("Ocean", cfg.Theme);
        Assert.Single(saved); // save callback called
    }

    [Fact]
    public void Theme_IsActive_Matches_Current_Colors()
    {
        var (vm, cfg, _) = MakeVm();
        // Apply Ocean preset manually
        var ocean = ThemePresets.Get("Ocean");
        cfg.Colors.Cpu = ocean.Cpu;
        cfg.Colors.Ram = ocean.Ram;
        cfg.Colors.Network = ocean.Network;
        cfg.Colors.CommandCode = ocean.CommandCode;
        cfg.Colors.OpenCode = ocean.OpenCode;
        cfg.Colors.Antigravity = ocean.Antigravity;
        cfg.Colors.Claude = ocean.Claude;
        cfg.Theme = "Ocean";

        Assert.True(vm.Commands.First(c => c.Id == "theme-Ocean").IsActiveValue);
        Assert.False(vm.Commands.First(c => c.Id == "theme-Midnight").IsActiveValue);
        Assert.False(vm.Commands.First(c => c.Id == "theme-Forest").IsActiveValue);
        Assert.False(vm.Commands.First(c => c.Id == "theme-Sakura").IsActiveValue);
    }

    [Fact]
    public void Each_Preset_Has_All_7_Colors_NonEmpty()
    {
        var presets = new[] { "Midnight", "Ocean", "Forest", "Sakura" };
        foreach (var presetName in presets)
        {
            var preset = ThemePresets.Get(presetName);
            Assert.False(string.IsNullOrWhiteSpace(preset.Cpu), $"{presetName}.Cpu empty");
            Assert.False(string.IsNullOrWhiteSpace(preset.Ram), $"{presetName}.Ram empty");
            Assert.False(string.IsNullOrWhiteSpace(preset.Network), $"{presetName}.Network empty");
            Assert.False(string.IsNullOrWhiteSpace(preset.CommandCode), $"{presetName}.CommandCode empty");
            Assert.False(string.IsNullOrWhiteSpace(preset.OpenCode), $"{presetName}.OpenCode empty");
            Assert.False(string.IsNullOrWhiteSpace(preset.Antigravity), $"{presetName}.Antigravity empty");
            Assert.False(string.IsNullOrWhiteSpace(preset.Claude), $"{presetName}.Claude empty");
        }
    }

    [Fact]
    public void Search_Filters_By_Label_And_Description()
    {
        var (vm, _, _) = MakeVm();
        vm.SearchText = "cpu";
        Assert.Contains(vm.FilteredCommands, c => c.Label.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        Assert.All(vm.FilteredCommands, c =>
        {
            bool match = c.Label.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains("cpu", StringComparison.OrdinalIgnoreCase);
            Assert.True(match, $"Command {c.Id} không match 'cpu'");
        });
    }

    [Fact]
    public void Search_Empty_Shows_All()
    {
        var (vm, _, _) = MakeVm();
        vm.SearchText = "";
        Assert.Equal(vm.Commands.Count, vm.FilteredCommands.Count);
    }

    [Fact]
    public void Toggle_Metric_Flips_Config_And_Saves()
    {
        var (vm, cfg, saved) = MakeVm();
        Assert.True(cfg.Metrics.Cpu);
        var cmd = vm.Commands.First(c => c.Id == "toggle-metric-cpu");
        cmd.Execute();
        Assert.False(cfg.Metrics.Cpu);
        Assert.Single(saved); // save callback được gọi
    }

    [Fact]
    public void Toggle_Clock_Metric_Flips_Config_And_Saves()
    {
        var (vm, cfg, saved) = MakeVm();
        Assert.False(cfg.Metrics.Clock); // default off
        var cmd = vm.Commands.First(c => c.Id == "toggle-metric-clock");
        cmd.Execute();
        Assert.True(cfg.Metrics.Clock);
        Assert.Single(saved);
    }

    [Fact]
    public void Choose_Layout_Sets_Config_Layout()
    {
        var (vm, cfg, _) = MakeVm();
        var cmd = vm.Commands.First(c => c.Id == "choose-layout-TwoLine");
        cmd.Execute();
        Assert.Equal("TwoLine", cfg.Layout);
    }

    [Fact]
    public void Choose_Layout_IsActive_Matches_Current()
    {
        var (vm, cfg, _) = MakeVm();
        cfg.Layout = "TwoLine";
        Assert.True(vm.Commands.First(c => c.Id == "choose-layout-TwoLine").IsActiveValue);
        Assert.False(vm.Commands.First(c => c.Id == "choose-layout-Minimal").IsActiveValue);
    }

    [Fact]
    public void Toggle_Agent_Flips_And_Saves()
    {
        var (vm, cfg, saved) = MakeVm();
        Assert.True(cfg.Agents.OpenCode);
        var cmd = vm.Commands.First(c => c.Id == "toggle-agent-opencode");
        cmd.Execute();
        Assert.False(cfg.Agents.OpenCode);
        Assert.Single(saved);
    }

    [Fact]
    public void Set_Interval_Updates_Config()
    {
        var (vm, cfg, _) = MakeVm();
        vm.Commands.First(c => c.Id == "set-interval-5").Execute();
        Assert.Equal(5, cfg.UpdateIntervalSeconds);
    }

    [Fact]
    public void Set_Density_Updates_Config()
    {
        var (vm, cfg, _) = MakeVm();
        vm.Commands.First(c => c.Id == "set-density-Comfortable").Execute();
        Assert.Equal("Comfortable", cfg.Density);
    }

    [Fact]
    public void Toggle_Autostart_Flips()
    {
        var (vm, cfg, _) = MakeVm();
        Assert.False(cfg.Autostart);
        vm.Commands.First(c => c.Id == "set-autostart").Execute();
        Assert.True(cfg.Autostart);
    }

    [Fact]
    public void SettingsWindow_LoadsTemplates_OnStaThread()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                var window = new SettingsPaletteWindow();
                window.Show();
                window.UpdateLayout();
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "Settings window did not finish loading.");
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SharedContextMenuUsesSystemFontAndCleanLabels()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                ContextMenu menu = TaskbarContextMenu.Build();
                Assert.Equal(SystemFonts.MenuFontFamily, menu.FontFamily);

                string[] headers = menu.Items
                    .OfType<MenuItem>()
                    .Select(static item => item.Header?.ToString() ?? string.Empty)
                    .ToArray();
                Assert.Contains("Settings Palette...", headers);
                Assert.DoesNotContain(headers, static header => header.Contains('â'));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "Context menu did not finish building.");
        thread.Join();
        Assert.Null(failure);
    }
}

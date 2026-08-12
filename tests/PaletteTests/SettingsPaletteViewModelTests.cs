using TaskbarMonitor.Config;
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
        Assert.Contains(vm.Commands, c => c.Id == "toggle-agent-codex" && c.Label == "Agent: ChatGPT Usage");
        Assert.Contains(vm.Commands, c => c.Id == "choose-layout-TwoLine");
        Assert.Contains(vm.Commands, c => c.Id == "set-interval-1");
        Assert.Contains(vm.Commands, c => c.Id == "set-autostart");
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
    public void Toggle_ChatGptUsage_ChangesOnlyLegacyCodexFlag()
    {
        var (vm, cfg, saved) = MakeVm();
        cfg.Agents.Codex = true;
        cfg.Agents.CommandCode = true;

        vm.Commands.Single(c => c.Id == "toggle-agent-codex").Execute();

        Assert.False(cfg.Agents.Codex);
        Assert.True(cfg.Agents.CommandCode);
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
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Settings window did not finish loading.");
        thread.Join();
        Assert.Null(failure);
    }
}

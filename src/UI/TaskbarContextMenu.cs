using System.Windows;
using System.Windows.Controls;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI.Palette;

namespace TaskbarMonitor.UI;

/// <summary>
/// Context menu chuá»™t pháº£i cho widget â€” settings trá»±c quan toÃ n bá»™.
/// Submenu cÃ³ checkmark theo tráº¡ng thÃ¡i config hiá»‡n táº¡i.
/// Má»i thay Ä‘á»•i â†’ ConfigManager.Save() â†’ hot-reload tá»± rebuild UI.
/// </summary>
public static class TaskbarContextMenu
{
    public static ContextMenu Build(FloatingMonitorWindow? floatingWindow = null)
    {
        var menu = new ContextMenu
        {
            FontFamily = SystemFonts.MenuFontFamily,
            FontSize = SystemFonts.MenuFontSize,
            FontStyle = SystemFonts.MenuFontStyle,
            FontWeight = SystemFonts.MenuFontWeight
        };

        var config = ConfigManager.Instance.Config;
        bool isFloating = string.Equals(config.Mode, "Floating", StringComparison.OrdinalIgnoreCase);

        // 1. Tùy chọn nhanh cho Thanh nổi
        if (isFloating)
        {
            var lockItem = BuildCheckItem("Khóa vị trí thanh nổi", () => config.FloatingLocked, v =>
            {
                config.FloatingLocked = v;
                ConfigManager.Instance.Save();
                floatingWindow?.SetLocked(v);
            });
            menu.Items.Add(lockItem);

            var orientMenu = new MenuItem { Header = "Hướng thanh nổi" };
            var horzItem = new MenuItem { Header = "Nằm ngang (Horizontal)", IsCheckable = true, IsChecked = !config.FloatingVertical };
            var vertItem = new MenuItem { Header = "Dựng dọc (Vertical)", IsCheckable = true, IsChecked = config.FloatingVertical };
            horzItem.Click += (_, _) =>
            {
                config.FloatingVertical = false;
                ConfigManager.Instance.Save();
            };
            vertItem.Click += (_, _) =>
            {
                config.FloatingVertical = true;
                ConfigManager.Instance.Save();
            };
            orientMenu.Items.Add(horzItem);
            orientMenu.Items.Add(vertItem);
            menu.Items.Add(orientMenu);

            var gameModeMenu = new MenuItem { Header = "Khi chơi game (Fullscreen)" };
            var autoHideGameItem = new MenuItem
            {
                Header = "Tự ẩn khi chơi game",
                IsCheckable = true,
                IsChecked = !string.Equals(config.GameOverlayMode, "MiniHud", StringComparison.OrdinalIgnoreCase)
            };
            var miniHudGameItem = new MenuItem
            {
                Header = "Hiện Mini Game HUD",
                IsCheckable = true,
                IsChecked = string.Equals(config.GameOverlayMode, "MiniHud", StringComparison.OrdinalIgnoreCase)
            };

            autoHideGameItem.Click += (_, _) =>
            {
                config.GameOverlayMode = "AutoHide";
                config.AutoHideOnFullscreen = true;
                ConfigManager.Instance.Save();
                autoHideGameItem.IsChecked = true;
                miniHudGameItem.IsChecked = false;
            };

            miniHudGameItem.Click += (_, _) =>
            {
                config.GameOverlayMode = "MiniHud";
                config.AutoHideOnFullscreen = false;
                ConfigManager.Instance.Save();
                miniHudGameItem.IsChecked = true;
                autoHideGameItem.IsChecked = false;
            };

            gameModeMenu.Items.Add(autoHideGameItem);
            gameModeMenu.Items.Add(miniHudGameItem);
            menu.Items.Add(gameModeMenu);

            var opacityMenu = new MenuItem { Header = "Độ mờ thanh nổi" };
            foreach (var (label, val) in new[] { ("100%", 1.0), ("85%", 0.85), ("70%", 0.70), ("50%", 0.50) })
            {
                var opItem = new MenuItem { Header = label, IsCheckable = true, IsChecked = Math.Abs(config.FloatingOpacity - val) < 0.05 };
                double capturedVal = val;
                opItem.Click += (_, _) =>
                {
                    config.FloatingOpacity = capturedVal;
                    ConfigManager.Instance.Save();
                    if (floatingWindow != null)
                        floatingWindow.Opacity = capturedVal;
                    foreach (var sibling in opacityMenu.Items.OfType<MenuItem>())
                        sibling.IsChecked = ReferenceEquals(sibling, opItem);
                };
                opacityMenu.Items.Add(opItem);
            }
            menu.Items.Add(opacityMenu);
            menu.Items.Add(new Separator());
        }

        // 2. Chế độ hiển thị
        var modeMenu = new MenuItem { Header = "Chế độ hiển thị" };
        var floatingMode = new MenuItem
        {
            Header = "Thanh nổi Capsule (Floating)",
            IsCheckable = true,
            IsChecked = isFloating
        };
        floatingMode.Click += (_, _) => SetMode("Floating");
        var taskbarMode = new MenuItem
        {
            Header = "Nhúng Taskbar (Deskband)",
            IsCheckable = true,
            IsChecked = !isFloating
        };
        taskbarMode.Click += (_, _) => SetMode("Taskbar");
        modeMenu.Items.Add(floatingMode);
        modeMenu.Items.Add(taskbarMode);
        menu.Items.Add(modeMenu);

        // Vị trí bám sát Taskbar (Center / Side)
        menu.Items.Add(BuildTaskbarPositionMenu());

        // 3. Chỉ số giám sát (Metrics)
        menu.Items.Add(BuildMetricsMenu());

        // 4. Trợ lý AI (Agents)
        menu.Items.Add(BuildAgentsMenu());

        // 5. Tần suất cập nhật
        menu.Items.Add(BuildIntervalMenu());

        // 6. Cỡ chữ
        menu.Items.Add(BuildFontSizeMenu());

        menu.Items.Add(new Separator());

        // 6. Tùy chọn hệ thống
        menu.Items.Add(BuildCheckItem("Khởi động cùng Windows", () => config.Autostart, v =>
        {
            config.Autostart = v;
            ConfigManager.Instance.Save();
        }));

        menu.Items.Add(BuildCheckItem("Hiển thị nhãn (Labels)", () => config.ShowLabels, v =>
        {
            config.ShowLabels = v;
            ConfigManager.Instance.Save();
        }));

        menu.Items.Add(new Separator());

        // Quick Action: Mở Task Manager
        var taskMgrItem = new MenuItem { Header = "⚡ Mở Task Manager (Trình quản lý tác vụ)" };
        taskMgrItem.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            }
            catch { }
        };
        menu.Items.Add(taskMgrItem);

        // 7. Palette & Config & Quit
        var paletteItem = new MenuItem { Header = "Settings Palette..." };
        paletteItem.Click += (_, _) => ShowSettingsPalette();
        menu.Items.Add(paletteItem);

        var openConfig = new MenuItem { Header = "Mở file config.json" };
        openConfig.Click += (_, _) => OpenConfigFile();
        menu.Items.Add(openConfig);

        var quit = new MenuItem { Header = "Thoát ứng dụng" };
        quit.Click += (_, _) => Application.Current?.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }

    private static MenuItem BuildMetricsMenu()
    {
        var item = new MenuItem { Header = "Chỉ số phần cứng (Metrics)" };
        AddMetricToggle(item, "CPU & GPU (Tự gộp khi APU)", c => c.Metrics.Cpu);
        AddMetricToggle(item, "RAM", c => c.Metrics.Ram);
        AddMetricToggle(item, "Thu / Phát mạng (Network)", c => c.Metrics.Network);
        AddMetricToggle(item, "Ổ đĩa (Disk)", c => c.Metrics.Disk);
        AddMetricToggle(item, "Nhiệt độ (Temperature)", c => c.Metrics.Temperature);
        item.Items.Add(new Separator());
        AddMetricToggle(item, "⭐ Game Overlay: FPS & Ping tự động", c => c.Metrics.GameOverlay);
        return item;
    }

    private static MenuItem BuildAgentsMenu()
    {
        var item = new MenuItem { Header = "Trợ lý AI (Agents)" };
        AddAgentToggle(item, "OpenCode", c => c.Agents.OpenCode);
        AddAgentToggle(item, "CommandCode", c => c.Agents.CommandCode);
        AddAgentToggle(item, "Antigravity", c => c.Agents.Antigravity);
        AddAgentToggle(item, "Claude", c => c.Agents.Claude);
        return item;
    }

    private static MenuItem BuildTaskbarPositionMenu()
    {
        var item = new MenuItem { Header = "Vị trí bám sát Taskbar" };
        var config = ConfigManager.Instance.Config;
        bool isCenter = string.Equals(config.Position, "Center", StringComparison.OrdinalIgnoreCase);

        var centerItem = new MenuItem
        {
            Header = "Bám sát giữa (Center)",
            IsCheckable = true,
            IsChecked = isCenter
        };
        centerItem.Click += (_, _) =>
        {
            ConfigManager.Instance.Config.Position = "Center";
            ConfigManager.Instance.Save();
        };

        var sideItem = new MenuItem
        {
            Header = "Bám sang 2 bên (Side)",
            IsCheckable = true,
            IsChecked = !isCenter
        };
        sideItem.Click += (_, _) =>
        {
            ConfigManager.Instance.Config.Position = "Side";
            ConfigManager.Instance.Save();
        };

        item.Items.Add(centerItem);
        item.Items.Add(sideItem);
        return item;
    }

    private static MenuItem BuildIntervalMenu()
    {
        var item = new MenuItem { Header = "Tần suất làm mới" };
        foreach (var interval in new[] { 1, 2, 5 })
        {
            var sub = new MenuItem
            {
                Header = $"{interval} giây",
                IsCheckable = true,
                IsChecked = ConfigManager.Instance.Config.UpdateIntervalSeconds == interval
            };
            sub.Click += (_, _) => SetInterval(interval);
            item.Items.Add(sub);
        }
        return item;
    }

    private static MenuItem BuildFontSizeMenu()
    {
        var item = new MenuItem { Header = "Cỡ chữ (Font Size)" };
        var config = ConfigManager.Instance.Config;
        foreach (var (label, size) in new[] { ("Nhỏ (9.5pt)", 9.5), ("Vừa (11.5pt)", 11.5), ("Lớn (13.5pt)", 13.5), ("Rất lớn (16pt)", 16.0) })
        {
            var sub = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = Math.Abs(config.FontSize - size) < 0.2
            };
            double captured = size;
            sub.Click += (_, _) =>
            {
                ConfigManager.Instance.Config.FontSize = captured;
                ConfigManager.Instance.Save();
            };
            item.Items.Add(sub);
        }
        return item;
    }

    private static void SetMode(string mode)
    {
        ConfigManager.Instance.Config.Mode = mode;
        ConfigManager.Instance.Save();
    }

    private static void AddMetricToggle(MenuItem parent, string label, Func<ConfigData, bool> get)
    {
        var sub = new MenuItem
        {
            Header = label,
            IsCheckable = true,
            IsChecked = get(ConfigManager.Instance.Config)
        };
        sub.Click += (_, _) =>
        {
            var cfg = ConfigManager.Instance.Config;
            SetMetric(cfg, label, !get(cfg));
            ConfigManager.Instance.Save();
        };
        parent.Items.Add(sub);
    }

    private static void AddAgentToggle(MenuItem parent, string label, Func<ConfigData, bool> get)
    {
        var sub = new MenuItem
        {
            Header = label,
            IsCheckable = true,
            IsChecked = get(ConfigManager.Instance.Config)
        };
        sub.Click += (_, _) =>
        {
            var cfg = ConfigManager.Instance.Config;
            SetAgent(cfg, label, !get(cfg));
            ConfigManager.Instance.Save();
        };
        parent.Items.Add(sub);
    }

    private static MenuItem BuildCheckItem(string header, Func<bool> get, Action<bool> set)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = get()
        };
        item.Click += (_, _) => set(!get());
        return item;
    }

    // --- Helpers mutate ConfigData ---

    private static void SetInterval(int interval)
    {
        ConfigManager.Instance.Config.UpdateIntervalSeconds = interval;
        ConfigManager.Instance.Save();
    }

    private static void SetPosition(string position)
    {
        ConfigManager.Instance.Config.Position = position;
        ConfigManager.Instance.Save();
    }

    private static void SetMetric(ConfigData cfg, string label, bool value)
    {
        if (label.StartsWith("CPU"))
        {
            cfg.Metrics.Cpu = value;
            cfg.Metrics.Gpu = value;
        }
        else if (label.StartsWith("RAM"))
        {
            cfg.Metrics.Ram = value;
        }
        else if (label.Contains("mạng") || label.Contains("Network"))
        {
            cfg.Metrics.Network = value;
        }
        else if (label.Contains("đĩa") || label.Contains("Disk"))
        {
            cfg.Metrics.Disk = value;
        }
        else if (label.Contains("Nhiệt độ") || label.Contains("Temperature"))
        {
            cfg.Metrics.Temperature = value;
        }
        else if (label.Contains("Game Overlay"))
        {
            cfg.Metrics.GameOverlay = value;
        }
    }

    private static void SetAgent(ConfigData cfg, string label, bool value)
    {
        if (label.Contains("OpenCode")) cfg.Agents.OpenCode = value;
        else if (label.Contains("CommandCode")) cfg.Agents.CommandCode = value;
        else if (label.Contains("Antigravity")) cfg.Agents.Antigravity = value;
        else if (label.Contains("Claude")) cfg.Agents.Claude = value;
    }

    public static void ShowSettingsPalette()
    {
        var existing = Application.Current?.Windows.OfType<SettingsPaletteWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsPaletteWindow();
        window.Show();
        window.Activate();
    }

    public static void ShowFor(FrameworkElement placementTarget)
    {
        ArgumentNullException.ThrowIfNull(placementTarget);
        var menu = Build();
        menu.PlacementTarget = placementTarget;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public static void ShowAtCursor()
    {
        var menu = Build();
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static void OpenConfigFile()
    {
        try
        {
            string path = ConfigManager.Instance.ConfigPath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}

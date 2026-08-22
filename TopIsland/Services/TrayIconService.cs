using System.Drawing;
using Forms = System.Windows.Forms;
using TopIsland.Models;

namespace TopIsland.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly MonitorService _monitorService;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu = new();

    public TrayIconService(MainWindow window, MonitorService monitorService)
    {
        _window = window;
        _monitorService = monitorService;

        var icon = TryLoadIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "TopIsland",
            Icon = icon,
            Visible = true,
            ContextMenuStrip = _menu
        };

        _menu.Opening += (_, _) => RebuildMenu();
        _notifyIcon.DoubleClick += (_, _) => Dispatch(_window.ToggleVisibilityFromTray);
    }

    private void RebuildMenu()
    {
        if (_menu.InvokeRequired)
        {
            _menu.BeginInvoke(RebuildMenu);
            return;
        }

        _menu.Items.Clear();
        var settings = _window.Settings;

        var title = new Forms.ToolStripMenuItem("TopIsland") { Enabled = false };
        _menu.Items.Add(title);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _menu.Items.Add(Item(_window.IsVisible ? "Hide" : "Show", _window.ToggleVisibilityFromTray));
        _menu.Items.Add(Item("Expand", _window.ExpandFromTray));

        _menu.Items.Add(Submenu("Style",
            CheckedItem("Dynamic Island", settings.Style == IslandStyle.DynamicIsland, () => _window.SetStyle(IslandStyle.DynamicIsland)),
            CheckedItem("Notch", settings.Style == IslandStyle.Notch, () => _window.SetStyle(IslandStyle.Notch))));

        _menu.Items.Add(Submenu("Width",
            CheckedItem("Authentic", settings.WidthPreset == WidthPreset.Authentic, () => _window.SetWidth(WidthPreset.Authentic)),
            CheckedItem("Compact", settings.WidthPreset == WidthPreset.Compact, () => _window.SetWidth(WidthPreset.Compact)),
            CheckedItem("Standard", settings.WidthPreset == WidthPreset.Standard, () => _window.SetWidth(WidthPreset.Standard)),
            CheckedItem("Wide", settings.WidthPreset == WidthPreset.Wide, () => _window.SetWidth(WidthPreset.Wide)),
            CheckedItem("Full Width", settings.WidthPreset == WidthPreset.FullWidth, () => _window.SetWidth(WidthPreset.FullWidth))));

        _menu.Items.Add(Submenu("Material",
            CheckedItem("Black", settings.Material == SurfaceMaterial.Solid, () => _window.SetMaterial(SurfaceMaterial.Solid)),
            CheckedItem("Mica", settings.Material == SurfaceMaterial.Mica, () => _window.SetMaterial(SurfaceMaterial.Mica)),
            CheckedItem("Acrylic", settings.Material == SurfaceMaterial.Acrylic, () => _window.SetMaterial(SurfaceMaterial.Acrylic)),
            CheckedItem("Glass", settings.Material == SurfaceMaterial.Glass, () => _window.SetMaterial(SurfaceMaterial.Glass)),
            CheckedItem("Material You", settings.Material == SurfaceMaterial.MaterialCopy, () => _window.SetMaterial(SurfaceMaterial.MaterialCopy))));

        _menu.Items.Add(Submenu("Theme",
            CheckedItem("System", settings.Theme == AppThemeMode.System, () => _window.SetTheme(AppThemeMode.System)),
            CheckedItem("Dark", settings.Theme == AppThemeMode.Dark, () => _window.SetTheme(AppThemeMode.Dark)),
            CheckedItem("Light", settings.Theme == AppThemeMode.Light, () => _window.SetTheme(AppThemeMode.Light))));

        var displayMenu = new Forms.ToolStripMenuItem("Display");
        displayMenu.DropDownItems.Add(CheckedItem(
            "Follow active app",
            settings.MonitorMode == MonitorMode.FollowActiveApp,
            () => _window.SetMonitor(MonitorMode.FollowActiveApp, null)));
        displayMenu.DropDownItems.Add(CheckedItem(
            "Primary display",
            settings.MonitorMode == MonitorMode.Primary,
            () => _window.SetMonitor(MonitorMode.Primary, null)));
        displayMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

        var monitors = _monitorService.GetMonitors();
        for (var i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var label = $"Display {i + 1} \u00B7 {monitor.DisplayLabel}" + (monitor.IsPrimary ? " \u00B7 Primary" : string.Empty);
            displayMenu.DropDownItems.Add(CheckedItem(
                label,
                settings.MonitorMode == MonitorMode.Fixed && string.Equals(settings.MonitorDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase),
                () => _window.SetMonitor(MonitorMode.Fixed, monitor.DeviceName)));
        }
        _menu.Items.Add(displayMenu);

        _menu.Items.Add(Submenu("Focus timer",
            Item("Start 25 minutes", () => _window.StartFocusTimer(25)),
            Item("Start 45 minutes", () => _window.StartFocusTimer(45)),
            Item("Pause / Resume", _window.ToggleFocusTimer),
            Item("Reset", _window.ResetFocusTimer)));

        if (!_window.NotificationsAllowed)
        {
            _menu.Items.Add(Item("Enable notifications", () => _ = _window.EnableNotificationsAsync()));
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CheckedItem(
            "Launch at startup",
            settings.StartWithWindows,
            () => _window.SetStartWithWindows(!settings.StartWithWindows)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Item("Quit TopIsland", () => System.Windows.Application.Current.Shutdown()));
    }

    private Forms.ToolStripMenuItem Item(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => Dispatch(action);
        return item;
    }

    private Forms.ToolStripMenuItem CheckedItem(string text, bool isChecked, Action action)
    {
        var item = Item(text, action);
        item.Checked = isChecked;
        return item;
    }

    private static Forms.ToolStripMenuItem Submenu(string text, params Forms.ToolStripItem[] items)
    {
        var menu = new Forms.ToolStripMenuItem(text);
        menu.DropDownItems.AddRange(items);
        return menu;
    }

    private void Dispatch(Action action)
    {
        if (_window.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _window.Dispatcher.BeginInvoke(action);
        }
    }

    private static Icon TryLoadIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            var extracted = !string.IsNullOrWhiteSpace(executable) ? Icon.ExtractAssociatedIcon(executable) : null;
            return extracted ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}

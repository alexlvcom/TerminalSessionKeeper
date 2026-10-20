// AlexLV Windows UI kit. Canonical copy: C:\projects\AlexLV.WindowsUi (see README.md).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace AlexLV.WindowsUi;

/// <summary>The user's Appearance → Theme choice. System follows the Windows app mode.</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>An app's accent: one value per theme, plus the text color drawn on it.</summary>
public sealed record AccentColors(Color Light, Color Dark, Color OnLight, Color OnDark)
{
    public static AccentColors FromHex(string light, string dark, string onLight = "#FFFFFF", string onDark = "#111111") =>
        new(Parse(light), Parse(dark), Parse(onLight), Parse(onDark));

    internal static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>The neutral palettes every app shares (windows-apps-common-standards.md).</summary>
public static class Palettes
{
    public static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["Surface"] = "#F5F5F5", ["Chrome"] = "#EBEBEB", ["Raised"] = "#FFFFFF", ["Border"] = "#D6D6D6",
        ["Text"] = "#1B1B1B", ["Muted"] = "#5F5F5F", ["Danger"] = "#B3261E", ["Ok"] = "#2E7D32", ["Warn"] = "#9A6700",
    };

    public static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["Surface"] = "#1C1C1C", ["Chrome"] = "#262626", ["Raised"] = "#2D2D2D", ["Border"] = "#3D3D3D",
        ["Text"] = "#F0F0F0", ["Muted"] = "#A6A6A6", ["Danger"] = "#F2A198", ["Ok"] = "#9FD39F", ["Warn"] = "#F0C97F",
    };
}

/// <summary>
/// Owns the WPF resources of a WinForms-hosted or WPF app: loads Theme.xaml, resolves
/// System/Light/Dark, follows Windows when the choice is System, and themes every window's
/// native title bar. Call <see cref="Initialize"/> once on the UI thread before showing a window.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static AccentColors _accent = AccentColors.FromHex("#005FB8", "#60A5FA");
    private static bool _initialized;

    /// <summary>The saved choice (or a live preview of an unsaved one).</summary>
    public static AppTheme Choice { get; private set; } = AppTheme.System;

    /// <summary>The theme actually drawn after resolving System.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The app's display name, used as the caption of <see cref="MessageDialog"/>.</summary>
    public static string AppName { get; private set; } = "";

    /// <summary>The icon every kit window shows in its title bar and taskbar button.</summary>
    public static ImageSource? WindowIcon { get; set; }

    /// <summary>Raised on the UI thread after the drawn theme changed (choice or Windows mode).</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Resolves a choice against the Windows app mode. Pure, so it can be tested without Windows.
    /// </summary>
    public static bool Resolve(AppTheme choice, bool systemUsesLight) => choice switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => !systemUsesLight,
    };

    /// <summary>Normalizes a hand-edited or unknown stored value to System.</summary>
    public static AppTheme Normalize(AppTheme choice) => Enum.IsDefined(choice) ? choice : AppTheme.System;

    /// <summary>Reads HKCU AppsUseLightTheme; light when the value is missing or unreadable.</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    public static void Initialize(string appName, AccentColors accent, AppTheme choice, Uri? themeDictionary = null)
    {
        AppName = appName;
        _accent = accent;

        // A WinForms-hosted app has no WPF Application yet; resources and Windows need one.
        var app = System.Windows.Application.Current ?? new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        if (!_initialized)
        {
            var assembly = typeof(ThemeManager).Assembly.GetName().Name;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = themeDictionary ?? new Uri($"/{assembly};component/AlexLV.WindowsUi/Theme.xaml", UriKind.Relative),
            });

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => PrepareWindow((Window)sender)));
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _initialized = true;
        }

        Apply(choice);
    }

    /// <summary>Applies a choice now. Settings calls this to preview and again to revert on Cancel.</summary>
    public static void Apply(AppTheme choice)
    {
        Choice = Normalize(choice);
        var dark = Resolve(Choice, SystemUsesLightTheme());
        var changed = dark != IsDark || !_appliedOnce;
        IsDark = dark;
        _appliedOnce = true;

        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;

        foreach (var (role, hex) in dark ? Palettes.Dark : Palettes.Light) Set(resources, role, AccentColors.Parse(hex));
        var accent = dark ? _accent.Dark : _accent.Light;
        Set(resources, "Accent", accent);
        Set(resources, "OnAccent", dark ? _accent.OnDark : _accent.OnLight);
        Set(resources, "Selection", Color.FromArgb(dark ? (byte)0x2E : (byte)0x24, accent.R, accent.G, accent.B));

        foreach (Window window in System.Windows.Application.Current!.Windows) ApplyTitleBar(window);
        if (changed) ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool _appliedOnce;

    /// <summary>The current color of a palette role, for code that draws outside WPF (tray menus).</summary>
    public static Color ColorOf(string role)
    {
        if (role == "Accent") return IsDark ? _accent.Dark : _accent.Light;
        if (role == "OnAccent") return IsDark ? _accent.OnDark : _accent.OnLight;
        var palette = IsDark ? Palettes.Dark : Palettes.Light;
        return AccentColors.Parse(palette.TryGetValue(role, out var hex) ? hex : palette["Text"]);
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        // Replace rather than mutate: brushes loaded from XAML may be frozen.
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Choice != AppTheme.System || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() => Apply(AppTheme.System));
    }

    private static void PrepareWindow(Window window)
    {
        if (window.Icon is null && WindowIcon is not null) window.Icon = WindowIcon;
        ApplyTitleBar(window);
    }

    /// <summary>Draws the native title bar in the theme: dark mode, caption and text color (Windows 11).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        ApplyTitleBar(handle);
    }

    public static void ApplyTitleBar(IntPtr handle)
    {
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var caption = ToColorRef(ColorOf("Chrome"));
        _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        var text = ToColorRef(ColorOf("Text"));
        _ = DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
    }

    /// <summary>
    /// Hides or reveals a shown window without changing its visibility state (DWM cloaking), so it
    /// can finish its first themed frame off screen. Returns whether DWM accepted the request.
    /// </summary>
    public static bool Cloak(IntPtr handle, bool cloak)
    {
        if (handle == IntPtr.Zero) return false;
        var value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(handle, DwmCloak, ref value, sizeof(int)) == 0;
    }

    /// <summary>Rounds a popup's corners on Windows 11 (tray menus, dropdowns).</summary>
    public static void RoundCorners(IntPtr handle)
    {
        var round = DwmCornerRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int));
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private const int DwmCloak = 13;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const int DwmCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

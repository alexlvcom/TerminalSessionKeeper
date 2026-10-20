// AlexLV Windows UI kit. Canonical copy: C:\projects\AlexLV.WindowsUi (see README.md).
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;

namespace AlexLV.WindowsUi;

/// <summary>
/// Base class for every app window. Windows shows a new window with a light frame and a white
/// client area before WPF draws its first frame, so a dark window flashes light. This window
/// themes its title bar as soon as its handle exists and stays cloaked (invisible but shown)
/// until its themed content has rendered.
/// <para>
/// Set <see cref="TitleVersion"/> (for example "v1.4.0") to draw the app's own title row instead
/// of the native one: icon and title on the left, the version right-aligned before the window
/// controls. Clicking the version raises <see cref="VersionClick"/>; Settings windows use it to
/// open their Updates tab.
/// </para>
/// </summary>
public class ThemedWindow : Window
{
    public static readonly DependencyProperty TitleVersionProperty = DependencyProperty.Register(
        nameof(TitleVersion), typeof(string), typeof(ThemedWindow),
        new PropertyMetadata(null, (d, _) => ((ThemedWindow)d).UpdateVersionText()));

    private const double TitleRowHeight = 36;
    private bool _isCloaked;
    private Button? _version;

    /// <summary>The version shown at the right end of a custom title row, without brackets.</summary>
    public string? TitleVersion
    {
        get => (string?)GetValue(TitleVersionProperty);
        set => SetValue(TitleVersionProperty, value);
    }

    /// <summary>Raised when the person clicks the version in the title row.</summary>
    public event EventHandler? VersionClick;

    protected override void OnSourceInitialized(EventArgs e)
    {
        BuildTitleRow();
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        ThemeManager.ApplyTitleBar(handle);
        _isCloaked = ThemeManager.Cloak(handle, true);
        // ContentRendered can be skipped (a window shown minimized); never leave one invisible.
        if (_isCloaked) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Uncloak);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Uncloak();
    }

    private void Uncloak()
    {
        if (!_isCloaked) return;
        _isCloaked = false;
        ThemeManager.Cloak(new WindowInteropHelper(this).Handle, false);
    }

    private void BuildTitleRow()
    {
        if (string.IsNullOrEmpty(TitleVersion) || _version is not null) return;

        var canResize = ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        // The app draws the whole frame; a 1px glass edge keeps the Windows shadow and rounded corners.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleRowHeight,
            ResizeBorderThickness = canResize ? new Thickness(6) : new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var row = new DockPanel { Height = TitleRowHeight, LastChildFill = true };
        row.SetResourceReference(Panel.BackgroundProperty, "Chrome");

        // Docked right in order: the first one added sits at the far right.
        AddRight(row, CaptionButton("\uE8BB", "Close", "CloseCaptionButton", () => SystemCommands.CloseWindow(this)));
        if (canResize)
        {
            var maximize = CaptionButton("\uE922", "Maximize", "CaptionButton", ToggleMaximize);
            StateChanged += (_, _) => maximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            AddRight(row, maximize);
        }
        if (ResizeMode != ResizeMode.NoResize)
            AddRight(row, CaptionButton("\uE921", "Minimize", "CaptionButton", () => SystemCommands.MinimizeWindow(this)));

        _version = new Button { ToolTip = "Show updates" };
        _version.SetResourceReference(StyleProperty, "TitleVersionButton");
        _version.Click += (_, _) => VersionClick?.Invoke(this, EventArgs.Empty);
        WindowChrome.SetIsHitTestVisibleInChrome(_version, true);
        System.Windows.Automation.AutomationProperties.SetName(_version, "Show updates");
        UpdateVersionText();
        AddRight(row, _version);

        var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(12, 0, 10, 0), Source = Icon ?? ThemeManager.WindowIcon };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var title = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        row.Children.Add(title);

        // Like the native frame, an inactive window dims its title row.
        Activated += (_, _) => row.Opacity = 1;
        Deactivated += (_, _) => row.Opacity = 0.65;

        var content = Content as UIElement;
        Content = null;
        var root = new DockPanel();
        DockPanel.SetDock(row, Dock.Top);
        root.Children.Add(row);
        if (content is not null) root.Children.Add(content);
        Content = root;
    }

    private void UpdateVersionText()
    {
        if (_version is not null) _version.Content = TitleVersion;
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private static void AddRight(DockPanel row, UIElement element)
    {
        DockPanel.SetDock(element, Dock.Right);
        row.Children.Add(element);
    }

    private static Button CaptionButton(string glyph, string name, string style, Action click)
    {
        var button = new Button { Content = glyph };
        button.SetResourceReference(StyleProperty, style);
        button.Click += (_, _) => click();
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
}

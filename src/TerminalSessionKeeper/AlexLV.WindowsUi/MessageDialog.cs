// AlexLV Windows UI kit. Canonical copy: C:\projects\AlexLV.WindowsUi (see README.md).
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AlexLV.WindowsUi;

public enum MessageKind
{
    Info,
    Success,
    Warning,
    Error,
    Question,
}

public enum MessageButtons
{
    Ok,
    OkCancel,
    YesNo,
}

public enum MessageResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
}

/// <summary>
/// The themed replacement for MessageBox: the app name as caption, an icon, the message and
/// right-aligned buttons. Esc and the title-bar X answer Cancel or No.
/// </summary>
public static class MessageDialog
{
    public static MessageResult Show(
        string message,
        MessageKind kind = MessageKind.Info,
        MessageButtons buttons = MessageButtons.Ok,
        Window? owner = null,
        bool defaultIsNegative = false,
        string? title = null)
    {
        var window = new ThemedWindow
        {
            Title = title ?? ThemeManager.AppName,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
        };
        window.SetResourceReference(FrameworkElement.StyleProperty, "KitWindow");

        var result = MessageResult.None;
        var root = new DockPanel();

        var footer = new Border();
        footer.SetResourceReference(FrameworkElement.StyleProperty, "Footer");
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Child = buttonRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new DockPanel { Margin = new Thickness(22, 20, 22, 20) };
        var (glyph, brushKey) = kind switch
        {
            MessageKind.Success => ("\uE73E", "Ok"),
            MessageKind.Warning => ("\uE7BA", "Warn"),
            MessageKind.Error => ("\uEA39", "Danger"),
            MessageKind.Question => ("\uE9CE", "Accent"),
            _ => ("\uE946", "Accent"),
        };
        var icon = new TextBlock { Text = glyph, FontSize = 26, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(FrameworkElement.StyleProperty, "Glyph");
        icon.FontSize = 26;
        icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(body);

        var specs = buttons switch
        {
            MessageButtons.OkCancel => new List<(string Text, MessageResult Result)> { ("OK", MessageResult.Ok), ("Cancel", MessageResult.Cancel) },
            MessageButtons.YesNo => new List<(string Text, MessageResult Result)> { ("_Yes", MessageResult.Yes), ("_No", MessageResult.No) },
            _ => new List<(string Text, MessageResult Result)> { ("OK", MessageResult.Ok) },
        };
        var negative = buttons switch { MessageButtons.OkCancel => MessageResult.Cancel, MessageButtons.YesNo => MessageResult.No, _ => MessageResult.Ok };
        var defaultResult = defaultIsNegative ? negative : specs[0].Result;

        Button? defaultButton = null;
        foreach (var (text, value) in specs)
        {
            var button = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), IsDefault = value == defaultResult, IsCancel = value == negative };
            if (value == specs[0].Result) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            button.Click += (_, _) => { result = value; window.Close(); };
            if (button.IsDefault) defaultButton = button;
            buttonRow.Children.Add(button);
        }

        window.Content = root;
        window.Loaded += (_, _) => defaultButton?.Focus();
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { result = negative; window.Close(); } };
        window.ShowDialog();
        return result == MessageResult.None ? negative : result;
    }
}

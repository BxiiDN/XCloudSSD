using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>Small themed dialogs built in code (input, choice, confirm, info).</summary>
public static class Dialogs
{
    public static string? Input(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 360, Margin = new Thickness(0, 8, 0, 0) };
        var ok = Show(owner, title, prompt, box, L.T("Ok"), L.T("Cancel"), () => box.Focus());
        return ok ? box.Text.Trim() : null;
    }

    public static string? Choose(Window owner, string title, string prompt, IEnumerable<string> options, string? selected = null)
    {
        var list = options.ToList();
        var combo = new ComboBox { ItemsSource = list, MinWidth = 200, Margin = new Thickness(0, 8, 0, 0) };
        combo.SelectedItem = selected is not null && list.Contains(selected) ? selected : list.FirstOrDefault();
        var ok = Show(owner, title, prompt, combo, L.T("Ok"), L.T("Cancel"), () => combo.Focus());
        return ok ? combo.SelectedItem as string : null;
    }

    public static bool Confirm(Window owner, string title, string message, string? yes = null)
        => Show(owner, title, message, null, yes ?? L.T("Yes"), L.T("Cancel"), null);

    public static void Info(Window owner, string title, string message)
        => Show(owner, title, message, null, L.T("Ok"), null, null);

    public static void Error(Window owner, Exception ex)
        => Show(owner, L.T("ErrorTitle"), ex.Message, null, L.T("Ok"), null, null);

    /// <summary>Generic dialog with optional extra buttons; returns the index of the clicked extra button or -1.</summary>
    public static int Actions(Window owner, string title, string message, params string[] buttons)
    {
        var result = -1;
        var win = Create(owner, title);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var idx = i;
            var b = new Button { Content = buttons[i], Margin = new Thickness(8, 0, 0, 0), MinWidth = 96 };
            b.Click += (_, _) =>
            {
                result = idx;
                win.Close();
            };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        win.Content = panel;
        win.ShowDialog();
        return result;
    }

    private static bool Show(Window owner, string title, string message, FrameworkElement? input, string okText, string? cancelText, Action? onLoaded)
    {
        var win = Create(owner, title);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        if (input is not null) panel.Children.Add(input);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 96, Style = (Style)Application.Current.FindResource("AccentButton") };
        ok.Click += (_, _) => win.DialogResult = true;
        row.Children.Add(ok);
        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            row.Children.Add(cancel);
        }
        panel.Children.Add(row);
        win.Content = panel;
        if (onLoaded is not null) win.Loaded += (_, _) => onLoaded();
        return win.ShowDialog() == true;
    }

    private static Window Create(Window owner, string title)
    {
        var win = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 380,
        };
        win.SetResourceReference(Control.BackgroundProperty, "AppBackground");
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.Close();
        };
        win.SourceInitialized += (_, _) => WindowEffects.Apply(win);
        return win;
    }
}

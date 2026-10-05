using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Classify.UI;

/// <summary>应用内模态对话框（深色、应用内呈现，替代系统 MessageBox 的混搭观感）。</summary>
public static class Dialogs
{
    public static Task ShowText(string title, string body) => ShowConfirm(title, body, closeOnly: true);

    public static Task<bool> Confirm(string title, string body, string okText = "确定", bool danger = false) =>
        ShowConfirm(title, body, okText: okText, danger: danger);

    /// <summary>单行输入对话框。返回 null 表示取消。</summary>
    public static Task<string?> Prompt(string title, string placeholder, string initial = "")
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var w = BuildWindow(title, 460);
        var stack = new StackPanel { Margin = new Thickness(22) };

        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["InputBox"],
            Margin = new Thickness(0, 2, 0, 0),
            Text = initial,
        };
        stack.Children.Add(new TextBlock
        {
            Text = placeholder,
            Foreground = (Brush)Application.Current.Resources["BrushSub"],
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
        });
        stack.Children.Add(box);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var cancel = new Button { Style = (Style)Application.Current.Resources["BtnTonal"], Content = "取消" };
        var ok = new Button { Style = (Style)Application.Current.Resources["BtnPrimary"], Content = "确定", Margin = new Thickness(10, 0, 0, 0) };
        void Close(bool okClicked)
        {
            w.DialogResult = okClicked;
            w.Close();
        }
        cancel.Click += (_, _) => Close(false);
        ok.Click += (_, _) => Close(true);
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        stack.Children.Add(btns);
        w.Content = stack;

        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Close(true);
            if (e.Key == System.Windows.Input.Key.Escape) Close(false);
        };
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.Closed += (_, _) => tcs.TrySetResult(w.DialogResult == true ? box.Text.Trim() : null);
        w.ShowDialog();
        return tcs.Task;
    }

    /// <summary>两行输入对话框（组名+姓名等）。返回 null 表示取消。</summary>
    public static Task<(string? A, string? B)?> Prompt2(string title, string phA, string phB)
    {
        var tcs = new TaskCompletionSource<(string?, string?)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var w = BuildWindow(title, 460);
        var stack = new StackPanel { Margin = new Thickness(22) };

        var boxA = new TextBox { Style = (Style)Application.Current.Resources["InputBox"], Margin = new Thickness(0, 2, 0, 10) };
        var boxB = new TextBox { Style = (Style)Application.Current.Resources["InputBox"] };
        stack.Children.Add(new TextBlock { Text = phA, Foreground = (Brush)Application.Current.Resources["BrushSub"], FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(boxA);
        stack.Children.Add(new TextBlock { Text = phB, Foreground = (Brush)Application.Current.Resources["BrushSub"], FontSize = 12, Margin = new Thickness(0, 10, 0, 6) });
        stack.Children.Add(boxB);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var cancel = new Button { Style = (Style)Application.Current.Resources["BtnTonal"], Content = "取消" };
        var ok = new Button { Style = (Style)Application.Current.Resources["BtnPrimary"], Content = "确定", Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => { w.DialogResult = false; w.Close(); };
        ok.Click += (_, _) => { w.DialogResult = true; w.Close(); };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        stack.Children.Add(btns);
        w.Content = stack;

        w.Loaded += (_, _) => boxA.Focus();
        w.Closed += (_, _) => tcs.TrySetResult(
            w.DialogResult == true ? (boxA.Text.Trim(), boxB.Text.Trim()) : null);
        w.ShowDialog();
        return tcs.Task;
    }

    private static Task<bool> ShowConfirm(string title, string body,
        string okText = "确定", bool danger = false, bool closeOnly = false)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var w = BuildWindow(title, 480);
        var stack = new StackPanel { Margin = new Thickness(22) };

        stack.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = (Brush)Application.Current.Resources["BrushText"],
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            MaxWidth = 420,
        });

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        void Close(bool ok)
        {
            w.DialogResult = ok;
            w.Close();
        }
        if (!closeOnly)
        {
            var cancel = new Button { Style = (Style)Application.Current.Resources["BtnTonal"], Content = "取消" };
            cancel.Click += (_, _) => Close(false);
            btns.Children.Add(cancel);
        }
        var ok = new Button
        {
            Style = (Style)Application.Current.Resources[danger ? "BtnDanger" : "BtnPrimary"],
            Content = closeOnly ? "关闭" : okText,
            Margin = new Thickness(10, 0, 0, 0),
        };
        ok.Click += (_, _) => Close(true);
        btns.Children.Add(ok);
        stack.Children.Add(btns);

        w.Content = stack;
        w.Closed += (_, _) => tcs.TrySetResult(w.DialogResult == true);
        w.ShowDialog();
        return tcs.Task;
    }

    private static Window BuildWindow(string title, double width)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
                 ?? Application.Current.MainWindow;
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = (Brush)Application.Current.Resources["BrushSurface"],
            FontFamily = (FontFamily)Application.Current.Resources["AppFont"],
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MaxWidth = 640,
        };
        if (owner is { IsVisible: true }) w.Owner = owner;
        w.SourceInitialized += (_, _) => WindowUtil.DarkTitleBar(w);
        return w;
    }
}

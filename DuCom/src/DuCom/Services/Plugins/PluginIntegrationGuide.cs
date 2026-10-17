using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DuCom.Controls;
using Wpf.Ui.Controls;

namespace DuCom.Services.Plugins;

internal static class PluginIntegrationGuide
{
    internal static void Show(Window? owner)
    {
        FluentWindow window = new()
        {
            Title = "恒玄接入文档 — 校验码查询工具", Width = 980, Height = 800,
            MinWidth = 640, MinHeight = 480, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ExtendsContentIntoTitleBar = true,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Brush.ShellSurface");
        window.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        Grid shell = new();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition());
        TitleBar title = new() { Title = window.Title, ShowClose = false, ShowMinimize = false, ShowMaximize = false,
            TrailingContent = new WindowCaptionButtons { ShowMinimize = true, ShowMaximize = true } };
        shell.Children.Add(title);
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DuCom.Docs.BesFirmwareIntegration")
            ?? throw new InvalidOperationException("内嵌接入文档未找到。");
        using StreamReader reader = new(stream);
        System.Windows.Controls.TextBox document = new()
        {
            Text = reader.ReadToEnd(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"), FontSize = 14,
            Padding = new Thickness(24), BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        document.SetResourceReference(Control.BackgroundProperty, "Brush.ShellSurface");
        document.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        Grid.SetRow(document, 1);
        shell.Children.Add(document);
        window.Content = shell;
        window.Show();
    }
}

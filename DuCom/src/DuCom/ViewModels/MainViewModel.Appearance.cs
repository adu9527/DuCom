using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    public partial bool IsChineseLanguage { get; private set; }

    [ObservableProperty]
    public partial bool IsEnglishLanguage { get; private set; }

    [ObservableProperty]
    public partial bool IsSystemTheme { get; private set; }

    [ObservableProperty]
    public partial bool IsLightTheme { get; private set; }

    [ObservableProperty]
    public partial bool IsDarkTheme { get; private set; }

    [RelayCommand]
    private void ApplyLightTheme()
    {
        ((App)Application.Current).ApplyTheme("Light");
        SyncAppearanceSelection();
        MarkSettingsDirty();
    }

    [RelayCommand]
    private void ApplyDarkTheme()
    {
        ((App)Application.Current).ApplyTheme("Dark");
        SyncAppearanceSelection();
        MarkSettingsDirty();
    }

    [RelayCommand]
    private void ApplyChineseLanguage()
    {
        ((App)Application.Current).ApplyLanguage("zh-CN");
        SyncAppearanceSelection();
    }

    [RelayCommand]
    private void ApplyEnglishLanguage()
    {
        ((App)Application.Current).ApplyLanguage("en-US");
        SyncAppearanceSelection();
    }

    private void SyncAppearanceSelection()
    {
        App app = (App)Application.Current;
        IsChineseLanguage = app.CurrentLanguage == "zh-CN";
        IsEnglishLanguage = app.CurrentLanguage == "en-US";
        IsSystemTheme = false;
        IsLightTheme = app.CurrentThemeMode == "Light";
        IsDarkTheme = app.CurrentThemeMode == "Dark";
    }

    [RelayCommand]
    private static void ApplyMicaSkin() => ApplyBackdrop(WindowBackdropType.Mica);

    [RelayCommand]
    private static void ApplyAcrylicSkin() => ApplyBackdrop(WindowBackdropType.Acrylic);

    [RelayCommand]
    private static void ApplySolidSkin() => ApplyBackdrop(WindowBackdropType.None);

    private static void ApplyBackdrop(WindowBackdropType backdrop)
    {
        ApplicationTheme theme = ApplicationThemeManager.GetAppTheme();
        if (theme == ApplicationTheme.Unknown)
        {
            theme = ApplicationTheme.Dark;
        }

        ApplicationThemeManager.Apply(theme, backdrop, true);
        if (Application.Current.MainWindow is FluentWindow window)
        {
            window.WindowBackdropType = backdrop;
        }
    }
}

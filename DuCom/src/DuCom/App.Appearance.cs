using System.Globalization;
using System.Windows;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DuCom;

public partial class App
{
    internal void ApplyLanguage(string language)
    {
        string normalized = string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        CurrentLanguage = normalized;
        ResourceDictionary? existing = Resources.MergedDictionaries.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString.StartsWith("Resources/Languages/", StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null)
        {
            Resources.MergedDictionaries.Remove(existing);
        }

        CultureInfo culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Languages/{normalized}.xaml", UriKind.Relative),
        });
    }

    private void LoadLanguageResources(string language) => ApplyLanguage(language);

    private static ApplicationTheme ResolveTheme(string requested)
    {
        if (string.Equals(requested, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationTheme.Light;
        }

        if (string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationTheme.Dark;
        }

        return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
    }

    internal void ApplyTheme(string mode)
    {
        CurrentThemeMode = string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        ApplicationTheme theme = CurrentThemeMode switch
        {
            "Light" => ApplicationTheme.Light,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color availableAccent) =>
        ApplyDuComColorTokens(currentApplicationTheme);

    private void ApplyDuComColorTokens(ApplicationTheme applicationTheme)
    {
        string tokenSuffix = applicationTheme == ApplicationTheme.Light ? "Light" : "Dark";
        System.Collections.ObjectModel.Collection<ResourceDictionary> dictionaries = Resources.MergedDictionaries;
        for (int index = 0; index < dictionaries.Count; index++)
        {
            string? original = dictionaries[index].Source?.OriginalString;
            if (original is not null && original.Contains("DesignTokens.Colors", StringComparison.OrdinalIgnoreCase))
            {
                if (!original.Contains($".{tokenSuffix}.", StringComparison.Ordinal))
                {
                    dictionaries[index] = new ResourceDictionary
                    {
                        Source = new Uri($"Resources/DesignTokens.Colors.{tokenSuffix}.xaml", UriKind.Relative),
                    };
                }

                return;
            }
        }

        DiagnosticLog?.Warning($"DuCom color token dictionary was not found when applying the {tokenSuffix} palette.");
    }
}

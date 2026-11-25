using System.Windows;

namespace OpenCam.Desktop.Services;

public static class ThemeService
{
    public static void ApplyTheme(string themeName)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var merged = app.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? string.Empty;
            if (src.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                merged.RemoveAt(i);
            }
        }

        var target = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Themes/LightTheme.xaml"
            : "Themes/DarkTheme.xaml";

        merged.Add(new ResourceDictionary
        {
            Source = new Uri(target, UriKind.Relative)
        });
    }
}

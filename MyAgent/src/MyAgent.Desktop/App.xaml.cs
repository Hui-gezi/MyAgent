using System.Windows;
using Microsoft.Win32;

namespace MyAgent.Desktop;

public partial class App : Application
{
    public static void ApplyTheme(string theme)
    {
        var dark = theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemUsesDarkTheme(),
        };
        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        Current.Resources.MergedDictionaries[0] = dict;
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
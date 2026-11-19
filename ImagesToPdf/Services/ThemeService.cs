using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ImagesToPdf.Services;

public static class ThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    public static void Apply(string? preference)
    {
        preference = Normalize(preference);
        var themeFile = IsDark(preference) ? "Dark.xaml" : "Light.xaml";
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ImagesToPdf;component/Themes/{themeFile}", UriKind.Absolute)
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(dictionary);
    }

    /// <summary>Synchronizes the native Windows title bar with the selected application theme.</summary>
    public static void ApplyWindowChrome(Window window, string? preference)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int useDarkMode = IsDark(Normalize(preference)) ? 1 : 0;
        try
        {
            int result = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
            if (result != 0)
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref useDarkMode, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static string Normalize(string? preference) => preference is "Light" or "Dark" or "System"
        ? preference : "System";

    private static bool IsDark(string preference) => preference.Equals("Dark", StringComparison.OrdinalIgnoreCase) ||
        (preference.Equals("System", StringComparison.OrdinalIgnoreCase) && IsWindowsDarkMode());

    private static bool IsWindowsDarkMode()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int lightTheme && lightTheme == 0;
        }
        catch
        {
            return false;
        }
    }
}

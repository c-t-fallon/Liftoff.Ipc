using CommunityToolkit.Mvvm.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IpcDemo.Wpf.Shared;

public sealed record TimelineEntry(DateTimeOffset At, string Kind, string Title, string Detail)
{
    public string Time => At.ToLocalTime().ToString("HH:mm:ss.fff");
}

public sealed partial class ThemeService : ObservableObject
{
    private readonly string _assemblyName;

    [ObservableProperty]
    private bool isDark;

    public ThemeService(string assemblyName) => _assemblyName = assemblyName;

    partial void OnIsDarkChanged(bool value)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count == 0)
        {
            return;
        }

        dictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/{_assemblyName};component/Themes/{(value ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };

        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBarTheme(window, value);
        }
    }

    private static void ApplyTitleBarTheme(Window window, bool useDark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = useDark ? 1 : 0;
        const int immersiveDarkMode = 20;
        if (DwmSetWindowAttribute(handle, immersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            const int immersiveDarkModeBefore20H1 = 19;
            DwmSetWindowAttribute(handle, immersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);
}

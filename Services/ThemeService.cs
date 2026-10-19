using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using Application = System.Windows.Application;

namespace InkCanvasForPDF.Services;

/// <summary>全应用颜色主题服务（移植 ICA MW_AutoTheme.cs）：
/// 换 App 级 Theme\Light.xaml / Theme\Dark.xaml 字典 + iNKORE ThemeManager 双管齐下，
/// "跟随系统"读注册表 SystemUsesLightTheme 并用 SystemEvents 实时监听切换。</summary>
public static class ThemeService
{
    private static readonly Uri LightUri = new("pack://application:,,,/Theme/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Theme/Dark.xaml");

    private static Window? _window;
    private static int _themePreference;   // 0浅/1深/2跟随系统
    private static bool _initialized;

    /// <summary>当前是否深色（Apply 后更新）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>主题实际发生变化后触发（订阅方：InkToolBar 重刷背景、BoardView 换画板字典等）。</summary>
    public static event Action? ThemeChanged;

    /// <summary>启动初始化：应用已保存的主题偏好并安装系统主题监听（幂等）。</summary>
    public static void Initialize(int themePreference, Window window)
    {
        _window = window;
        if (!_initialized)
        {
            _initialized = true;
            // SystemEvents 回调可能在后台线程，切回 UI 线程再动资源字典
            SystemEvents.UserPreferenceChanged += (_, _) =>
                _window?.Dispatcher.Invoke(() => Apply(_themePreference));
        }
        Apply(themePreference);
    }

    /// <summary>应用主题偏好：0=浅色，1=深色，2=跟随系统（抄 ICA SystemEvents_UserPreferenceChanged 分派）。</summary>
    public static void Apply(int themePreference)
    {
        _themePreference = themePreference;
        bool dark = themePreference switch
        {
            1 => true,
            2 => !IsSystemThemeLight(),
            _ => false,
        };
        SetTheme(dark);
    }

    /// <summary>换 App 级字典 + iNKORE 主题（抄 ICA SetTheme；触发 ThemeChanged）。</summary>
    private static void SetTheme(bool dark)
    {
        bool changed = dark != IsDark;
        IsDark = dark;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dark)
        {
            dictionaries.Add(new ResourceDictionary { Source = DarkUri });
            RemoveResourceDictionary(LightUri);
            if (_window != null) ThemeManager.SetRequestedTheme(_window, ElementTheme.Dark);
            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        }
        else
        {
            dictionaries.Add(new ResourceDictionary { Source = LightUri });
            RemoveResourceDictionary(DarkUri);
            if (_window != null) ThemeManager.SetRequestedTheme(_window, ElementTheme.Light);
            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
        }
        if (changed)
            ThemeChanged?.Invoke();
    }

    /// <summary>应用强调色（空/null 恢复库默认）；与深浅色正交，设置页选色与启动时调用。</summary>
    public static void ApplyAccent(string? hex) =>
        ThemeManager.Current.AccentColor = string.IsNullOrWhiteSpace(hex)
            ? null : (Color?)ColorConverter.ConvertFromString(hex);

    private static void RemoveResourceDictionary(Uri uri)
    {
        var dictionaryToRemove = Application.Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source == uri);
        if (dictionaryToRemove != null)
            Application.Current.Resources.MergedDictionaries.Remove(dictionaryToRemove);
    }

    /// <summary>读注册表 HKCU\...\Themes\Personalize\SystemUsesLightTheme（抄 ICA IsSystemThemeLight）。</summary>
    private static bool IsSystemThemeLight()
    {
        try
        {
            using var themeKey = Registry.CurrentUser
                .OpenSubKey(@"software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return themeKey != null && (int)(themeKey.GetValue("SystemUsesLightTheme") ?? 0) == 1;
        }
        catch
        {
            return true;   // 读不到按浅色兜底
        }
    }
}

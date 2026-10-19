using System.IO;
using System.Text.Json;
using InkCanvasForPDF.Models;

namespace InkCanvasForPDF.Services;

/// <summary>应用设置读写（运行目录 Data\Settings.json）。文件缺失/损坏时回退默认值。</summary>
public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "Settings.json");

    private static readonly JsonSerializerOptions SerOptions = new() { WriteIndented = true };

    /// <summary>加载设置；文件不存在或解析失败时返回默认实例。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>保存设置到运行目录 Data\Settings.json（自动建目录）。</summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SerOptions));
        }
        catch
        {
            // 保存失败不阻塞应用。
        }
    }
}
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

    /// <summary>本次启动首次 Load 时 Data\Settings.json 是否已存在（false = 全新首次使用）。
    /// 只在首次 Load 记录，后续 Load 不覆盖（首启运行中 Save 会创建文件、后台 Load 会再次进入）。</summary>
    public static bool LoadedFromFile { get; private set; }
    private static bool _loadCaptured;

    /// <summary>加载设置；文件不存在或解析失败时返回默认实例。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!_loadCaptured)
            {
                _loadCaptured = true;
                LoadedFromFile = File.Exists(SettingsPath);
            }
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
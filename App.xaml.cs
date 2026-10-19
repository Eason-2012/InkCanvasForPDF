using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern;

namespace InkCanvasForPDF;

public partial class App : Application
{
    /// <summary>启动参数带入的 PDF 路径（MainWindow 首次 Loaded 消费一次后置空）。</summary>
    internal static string? StartupFilePath;

    private static readonly object LogLock = new();

    /// <summary>日志路径：保存在运行目录 Data\ 下（error.log）。</summary>
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 启动参数：取第一个非开关（不以 - 或 / 开头）的参数作为要打开的 PDF；
        // 文件不存在或不是 .pdf 则静默忽略（文件关联打开时传入的就是裸路径）。
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith('-') || arg.StartsWith('/'))
                continue;
            try
            {
                var full = Path.GetFullPath(arg);
                if (string.Equals(Path.GetExtension(full), ".pdf", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(full))
                {
                    StartupFilePath = full;
                    break;
                }
            }
            catch
            {
                // 非法路径参数：忽略并继续尝试下一个。
            }
        }

        // 固定浅色主题，不跟随系统、不做深色模式。
        ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;

        // 注册带 pdf.ico 图标的 .pdf ProgID（HKCU，幂等；失败仅记日志）
        Services.FileAssociationService.Register();

        // 启动自动清理超期墨迹数据（"数据存储"设置，默认关；纯文件操作不阻塞启动，异常仅记日志）。
        try
        {
            var s = InkCanvasForPDF.Services.SettingsService.Load();
            if (s.IsAutoCleanInk)
                InkCanvasForPDF.Services.InkStoreService.CleanExpired(s.AutoCleanInkDays);
        }
        catch (Exception ex)
        {
            Log("Startup.CleanInk", ex);
        }

        // UI 线程未处理异常
        DispatcherUnhandledException += (_, args) =>
        {
            Log("DispatcherUnhandledException", args.Exception);
            // 保持默认行为：记录后交给系统终止，便于定位；需要时改为 args.Handled = true。
        };

        // 任意线程的致命未处理异常
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        // 未观察到的 Task 异常
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>追加一行日志到运行目录下的错误日志文件。</summary>
    public static void Log(string source, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
          .Append(source);
        if (ex != null)
            sb.AppendLine().Append(ex);
        sb.AppendLine().Append(new string('-', 80));

        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath) ?? AppContext.BaseDirectory);
                File.AppendAllText(LogPath, sb.ToString());
            }
            catch
            {
                // 日志写入失败不阻塞应用。
            }
        }
    }

    /// <summary>追加一段纯文本诊断日志（时间戳 + 分隔线与 Log 一致）。</summary>
    public static void LogText(string text)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
          .AppendLine(text).AppendLine(new string('-', 80));

        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath) ?? AppContext.BaseDirectory);
                File.AppendAllText(LogPath, sb.ToString());
            }
            catch
            {
                // 日志写入失败不阻塞应用。
            }
        }
    }
}

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

    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 挂接父控制台：诊断 Console.WriteLine 在终端启动/调试时可见（WinExe 默认无控制台；
        // 双击启动无父控制台时静默失败，不产生窗口）。error.log 只存错误，诊断不进 log。
        AttachConsole(AttachParentProcess);

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

        // 启动自动清理超期墨迹数据（"数据存储"设置）已移至 MainWindow Loaded 之后
        // 由 StartAutoCleanInkBackground 在后台线程执行：窗口先出现，清理不阻塞启动。

        // UI 线程未处理异常：记录后弹出错误弹窗并标记已处理，关闭弹窗后应用继续运行
        DispatcherUnhandledException += (_, args) =>
        {
            Log("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            try { ErrorWindow.ShowError("DispatcherUnhandledException", args.Exception); }
            catch (Exception showEx) { Log("ErrorWindow.Show", showEx); }   // 弹窗自身失败不得再入异常循环
        };

        // 任意线程的致命未处理异常：记录后尽力而为弹窗（进程随后仍会终止，弹窗可能来不及显示）
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    try { ErrorWindow.ShowError("AppDomain.UnhandledException", args.ExceptionObject as Exception); }
                    catch { /* 兜底失败仅记上面的日志 */ }
                });
            }
            catch { /* UI 调度器不可用时跳过 */ }
        };

        // 未观察到的 Task 异常
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

#if DEBUG
        // 调试阶段：启动时弹出示例错误窗口（仅 DEBUG 编译生效，后续按需移除）
        //ErrorWindow.ShowError("调试示例",
        //    new InvalidOperationException("这是启动时弹出的示例错误窗口（仅 DEBUG 编译生效）。"));
#endif
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

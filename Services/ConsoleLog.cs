namespace InkCanvasForPDF.Services;

/// <summary>控制台调试日志统一出口：每行输出 [功能名] 前缀用调试色，
/// 消息正文用终端默认色，写完恢复原色。
/// 只用于调试/诊断日志；错误日志仍走 App.Log（error.log）。</summary>
internal static class ConsoleLog
{
    /// <summary>调试日志颜色（青色，仅作用于 [功能名] 前缀，与普通日志默认色区分）。</summary>
    private const ConsoleColor DebugColor = ConsoleColor.Cyan;

    /// <param name="feature">中文功能名（如 抬笔墨迹纠正 / 停顿墨迹纠正 / 墨迹纠正 / 双曲线绘制 / PDF打开 / 自动删除墨迹数据）。</param>
    /// <param name="message">日志正文（不含前缀，前缀由本方法补齐；正文保持终端默认色）。</param>
    public static void Write(string feature, string message)
    {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = DebugColor;
        Console.Write($"[{feature}] ");
        Console.ForegroundColor = previous;
        Console.WriteLine(message);
    }
}

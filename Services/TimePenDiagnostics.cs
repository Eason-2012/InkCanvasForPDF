using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Ink;
using System.Windows.Input;

namespace InkCanvasForPDF.Services;

/// <summary>一次性取证（当前已暂停：TimePressureInk.cs 中的调用已注释，文件与实现保留）。
/// 需要取证时取消 TimePressureInk.cs 里 Begin/Batch/Native/Flush 四处调用的注释即可。
/// 内容：把时间笔锋收到的**原始**点批与原生笔画点列原样写入运行目录 Data\笔锋诊断.txt，
/// 不做任何去重/排序/插值，用于静态比对"刺"来自哪两个点。只记录前 8 笔，避免文件无限增长。</summary>
internal static class TimePenDiagnostics
{
    private const bool Enabled = true;
    private const int MaxStrokes = 8;

    private static readonly object Sync = new();
    private static readonly StringBuilder Buffer = new();
    private static int _strokeNo;
    private static int _batches;

    public static void Begin(int deviceId, int tabletDeviceId)
    {
        lock (Sync)
        {
            if (!Enabled || _strokeNo >= MaxStrokes)
                return;
            _strokeNo++;
            _batches = 0;
            Buffer.AppendLine($"笔={_strokeNo} 设备={deviceId} tablet={tabletDeviceId} 类型={TabletKind(tabletDeviceId)}");
        }
    }

    public static void Batch(int deviceId, StylusPointCollection points)
    {
        lock (Sync)
        {
            if (!Enabled || _strokeNo == 0 || _strokeNo > MaxStrokes || points == null)
                return;
            _batches++;
            Buffer.Append($"  批={_batches} dev={deviceId} 到达={DateTime.Now:HH:mm:ss.fff} 点数={points.Count}:");
            foreach (var p in points)
                Buffer.Append($" ({p.X:F1},{p.Y:F1},p={p.PressureFactor:F2})");
            Buffer.AppendLine();
        }
    }

    public static void Native(int deviceId, Stroke stroke)
    {
        lock (Sync)
        {
            if (!Enabled || _strokeNo == 0 || _strokeNo > MaxStrokes || stroke == null)
                return;
            Buffer.Append($"  原生 dev={deviceId} 点数={stroke.StylusPoints.Count}:");
            foreach (var p in stroke.StylusPoints)
                Buffer.Append($" ({p.X:F1},{p.Y:F1},p={p.PressureFactor:F2})");
            Buffer.AppendLine();
        }
    }

    /// <summary>会话结束写盘（每笔一次 IO，取证期间可接受）。</summary>
    public static void Flush()
    {
        lock (Sync)
        {
            if (Buffer.Length == 0)
                return;
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "笔锋诊断.txt"), Buffer.ToString());
            }
            catch
            {
                // 取证失败不影响墨迹
            }
            Buffer.Clear();
        }
    }

    private static string TabletKind(int tabletDeviceId)
    {
        try
        {
            foreach (TabletDevice tablet in Tablet.TabletDevices)
            {
                if (tablet.Id == tabletDeviceId)
                    return tablet.Type.ToString();
            }
        }
        catch
        {
            // 平板服务不可用
        }
        return "未知";
    }
}

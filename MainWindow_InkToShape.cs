using System.Windows;
using System.Windows.Ink;
using InkCanvasForPDF.Services;

namespace InkCanvasForPDF;

/// <summary>PDF 批注·墨迹纠正（ICA MW_SimulatePressure&amp;InkToShape.cs 的 InkToShapeProcess 移植，
/// 识别后端换成 WinRT）。引擎在收笔后异步识别最近几笔，命中规范图形时用具名几何替换手绘笔画；
/// 替换在 CommitReason.ShapeRecognition 提交门内完成，撤销/重做为单条 ShapeRecognition 历史。</summary>
public partial class MainWindow : Window
{
    /// <summary>墨迹纠正引擎（PDF 侧唯一实例，按页笔画集合分辨批次；构造函数内装配）。</summary>
    private readonly InkToShapeEngine _inkToShape;

    /// <summary>ShapeRecognition 提交门期间暂存的被替换笔画（对齐画板 ReplacedStroke 的用法）。</summary>
    private StrokeCollection? _shapeRecognitionReplaced;

    /// <summary>引擎命中后的替换（对齐 ICA：置提交门 → 移除手绘笔画 → 加回生成笔画 → 复位门）。
    /// 顺序必须是"先移除后添加"，StrokesChanged 才会先送来 Removed（暂存）再送来 Added（提交历史）。</summary>
    private void ApplyShapeReplacement(StrokeCollection target, StrokeCollection remove, StrokeCollection add)
    {
        var toRemove = new StrokeCollection();
        foreach (Stroke stroke in remove)
        {
            if (target.Contains(stroke))
                toRemove.Add(stroke);
        }

        _currentCommitType = CommitReason.ShapeRecognition;
        try
        {
            // 一次性移除整批（单个 StrokesChanged 事件），再添加生成笔画：与 ICA/画板"移除→添加"顺序一致
            if (toRemove.Count > 0)
                target.Remove(toRemove);
            target.Add(add);
        }
        finally
        {
            _currentCommitType = CommitReason.UserInput;
        }
    }

    /// <summary>墨迹纠正全局开关即时生效：PDF 引擎 + 画板墨迹面板镜像（单一真源 = AppSettings.IsInkToShapeEnabled）。</summary>
    private void ApplyInkToShapeEnabled(bool enabled)
    {
        _inkToShape.Enabled = enabled;
        Board.ApplyInkToShape(enabled);
    }

    /// <summary>把停顿强制识别 4 项设置推给画板（独立开关，与墨迹纠正互不影响；PDF 侧追踪器经活值 lambda 读设置）。</summary>
    private void PushPauseInkToShapeToBoard()
    {
        Board.ApplyPauseInkToShape(
            _viewModel.Settings.IsPauseInkToShapeEnabled,
            _viewModel.Settings.PauseInkToShapeDelayMs,
            _viewModel.Settings.PauseInkToShapeTolerancePx,
            _viewModel.Settings.PauseInkToShapeSensitivity / 100.0);
    }
}
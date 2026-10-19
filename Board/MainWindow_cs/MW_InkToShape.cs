using InkCanvasForPDF.Services;
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        // 墨迹纠正的算法语义来自 Ink-Canvas-Artistry 的 InkToShapeProcess（原代码版权归 WXRIW），
        // 识别后端换成本项目自研的 WinRT InkAnalyzer 实现。

        /// <summary>墨迹纠正引擎（画板侧唯一实例；构造函数内装配）。</summary>
        private readonly InkToShapeEngine _inkToShape;

        /// <summary>墨迹纠正开关状态（主设置 isInkToShapeEnabled；由 MainWindow 注入）。</summary>
        public bool InkToShapeEnabled { get; private set; }

        // ────── 停顿强制识别（独立开关，与墨迹纠正互不影响；配置由 MainWindow 推送）──────

        /// <summary>停顿强制识别追踪器（构造函数内挂到 inkCanvas）。</summary>
        private PauseInkToShapeTracker? _pauseTracker;
        private bool _pauseEnabled;
        private int _pauseDelayMs = 1000;
        private int _pauseTolerancePx = 5;
        private double _pauseThreshold = 0.75;

        /// <summary>停顿强制识别已提前提交在绘笔画的触摸设备（StylusUp 防重复提交；续画后自动失效）。</summary>
        private readonly HashSet<int> PauseCommittedDevices = new();

        /// <summary>停顿提交正在瞬时切换 EditingMode：用于抑制 inkCanvas_EditingModeChanged 中的副作用
        /// （光标刷新、时间笔锋武装重算；内部切换不应触发）。</summary>
        private bool _pauseCommitSwitchingMode;

        /// <summary>注入停顿强制识别配置（MainWindow 在启动与设置变更时推送）。</summary>
        public void ApplyPauseInkToShape(bool enabled, int delayMs, int tolerancePx, double threshold)
        {
            _pauseEnabled = enabled;
            _pauseDelayMs = Math.Clamp(delayMs, 50, 5000);
            _pauseTolerancePx = Math.Clamp(tolerancePx, 0, 100);
            _pauseThreshold = threshold;
            _inkToShape.PauseSimilarityThreshold = threshold;
            // UI 镜像必执行（无同值早退）：设置页/画板两侧改动经推送互相回填；构造期 isLoaded=false 早退保证 no-op
            if (ToggleSwitchEnablePauseInkToShape is { } hard) hard.IsOn = enabled;
            if (ToggleSwitchEnablePauseInkToShapeSoft is { } soft) soft.IsOn = enabled;
        }

        /// <summary>构造函数末尾调用：把停顿强制识别追踪器挂到 inkCanvas（自有实现，与多触摸挂接互不干扰）。
        /// 流程为"先试后断"：停顿时用临时笔画试识别，命中才断笔提交替换，未命中不断笔。</summary>
        private void InitializePauseInkToShape()
        {
            _pauseTracker = new PauseInkToShapeTracker(
                inkCanvas,
                isEnabled: () => _pauseEnabled,
                getDelayMs: () => _pauseDelayMs,
                getTolerancePx: () => _pauseTolerancePx,
                canTrigger: CanPauseTriggerBoard,
                tryPauseRecognizeAsync: TryPauseRecognizeProvisionalBoardAsync);
        }

        /// <summary>停顿强制识别触发门（画板宿主）：非多触摸、非形状模式、非荧光笔、内建墨迹或触摸自绘模式。</summary>
        private bool CanPauseTriggerBoard()
            => !isInMultiTouchMode
               && drawingShapeMode == 0
               && inkColor <= 100   // 荧光笔不参与
               && (inkCanvas.EditingMode == InkCanvasEditingMode.Ink
                   || inkCanvas.EditingMode == InkCanvasEditingMode.None);   // 数位笔=Ink（内建管线），触摸=None（自绘管线）

        /// <summary>停顿强制识别·试识别（画板宿主）：用在绘点集构建的临时笔画（不入画布）先判定，
        /// 命中才断笔提交 <see cref="CommitPendingBoardStroke"/> 并替换图形；未命中什么都不发生，笔迹不断。
        /// 命中回调里复查触发门与落笔会话（停顿后已抬笔/切模式则放弃，抬笔走正常收笔识别兜底）。</summary>
        private async Task<bool> TryPauseRecognizeProvisionalBoardAsync(
            int stylusId, IReadOnlyList<StylusPoint> pending)
        {
            if (pending.Count < 2)
                return false;
            var provisional = new Stroke(new StylusPointCollection(pending))
            {
                DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
            };
            return await _inkToShape.TryPauseCorrectProvisionalAsync(provisional, inkCanvas.Strokes,
                () => CanPauseTriggerBoard()
                      && _pauseTracker is not null && _pauseTracker.IsTracking(stylusId)
                    ? CommitPendingBoardStroke(stylusId, pending)
                    : null);
        }

        /// <summary>提交画板上正在绘制的笔画（停顿强制识别用），返回已入 Strokes 的笔画；null = 无法提交。
        /// 触摸自绘路径：湿墨预览在 StrokeVisual 里，手动入集合（并补发 StrokeCollected，对齐 StylusUp 触摸提交）；
        /// 内建管线（数位笔/鼠标）：瞬时切换 EditingMode 结束在绘输入（WPF 语义是丢弃湿墨而非提交，实测已确认），
        /// 因此按笔画数判断——确实提交了就取新笔画，否则用追踪器采集的点重建一条补回，保证墨迹不会消失。</summary>
        private Stroke? CommitPendingBoardStroke(int stylusId, IReadOnlyList<StylusPoint> pending)
        {
            if (inkCanvas.EditingMode == InkCanvasEditingMode.None
                && StrokeVisualList.TryGetValue(stylusId, out var visual))
            {
                var stroke = visual.Stroke;
                if (stroke.StylusPoints.Count < 2)
                    return null;
                if (VisualCanvasList.TryGetValue(stylusId, out var visualCanvas))
                    inkCanvas.Children.Remove(visualCanvas);   // 撤湿墨预览（干墨由 Strokes 承接）
                StrokeVisualList.Remove(stylusId);
                VisualCanvasList.Remove(stylusId);
                TouchDownPointsList.Remove(stylusId);
                PauseCommittedDevices.Add(stylusId);
                inkCanvas.Strokes.Add(stroke);
                // 手动 Strokes.Add 不触发 StrokeCollected 事件：对齐 StylusUp 触摸提交路径补发
                inkCanvas_StrokeCollected(inkCanvas, new InkCanvasStrokeCollectedEventArgs(stroke));
                return stroke;
            }

            // 内建管线（数位笔/鼠标）：瞬时切换 EditingMode 结束在绘输入，随后恢复原模式。
            // 切换会触发 inkCanvas_EditingModeChanged（其中有 forcePointEraser 翻转等副作用），用旗标抑制。
            int before = inkCanvas.Strokes.Count;
            var originalMode = inkCanvas.EditingMode;
            _pauseCommitSwitchingMode = true;
            try
            {
                inkCanvas.EditingMode = InkCanvasEditingMode.None;
                inkCanvas.EditingMode = originalMode;
            }
            finally
            {
                _pauseCommitSwitchingMode = false;
            }

            bool committedByCanvas = inkCanvas.Strokes.Count > before;
            var committed = committedByCanvas ? inkCanvas.Strokes[^1] : RebuildPendingBoardStroke(pending);
            inkCanvas.Controller.AbortStroke();   // 清时间笔锋湿墨叠加层，防残留

            return committed;
        }

        /// <summary>用追踪器采集的点重建在绘笔画并补入画布（画板内建管线）：模式切换丢弃湿墨，自己补回避免墨迹消失；
        /// 补入后补发 StrokeCollected，与正常收笔一致（伪压力/时间笔锋定格 + 正常识别排队）。</summary>
        private Stroke? RebuildPendingBoardStroke(IReadOnlyList<StylusPoint> pending)
        {
            if (pending.Count < 2)
                return null;
            var stroke = new Stroke(new StylusPointCollection(pending))
            {
                DrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone()
            };
            inkCanvas.Strokes.Add(stroke);
            inkCanvas_StrokeCollected(inkCanvas, new InkCanvasStrokeCollectedEventArgs(stroke));
            return stroke;
        }

        /// <summary>画板墨迹面板的开关改动 → 回流主设置（MainWindow 订阅后写回 AppSettings 并落盘）。</summary>
        public event Action<bool>? InkToShapeChanged;

        /// <summary>画板停顿纠正开关改动 → 回流主设置（MainWindow 订阅后写回 AppSettings 并落盘）。</summary>
        public event Action<bool>? PauseInkToShapeChanged;

        /// <summary>面板开关翻转（MW_BoardBarHandlers 调用）：镜像两个开关并回流主设置。</summary>
        private void RaisePauseInkToShapeChanged(bool enabled)
        {
            if (ToggleSwitchEnablePauseInkToShape is { } hard) hard.IsOn = enabled;
            if (ToggleSwitchEnablePauseInkToShapeSoft is { } soft) soft.IsOn = enabled;
            PauseInkToShapeChanged?.Invoke(enabled);
        }

        /// <summary>注入全局开关并即时生效：镜像两个页签的开关控件 + 引擎开关。
        /// 引擎侧同值跳过（MainWindow 的推送与画板开关的回流会互相调用，靠这里断开回环）；
        /// UI 镜像始终执行——否则持久化值与字段默认值相同时早退，开关会停在 XAML 默认值（"恢复默认"bug）。</summary>
        public void ApplyInkToShape(bool enabled)
        {
            if (InkToShapeEnabled != enabled)
            {
                InkToShapeEnabled = enabled;
                _inkToShape.Enabled = enabled;
            }
            if (ToggleSwitchEnableInkToShape is { } hard) hard.IsOn = enabled;
            if (ToggleSwitchEnableInkToShapeSoft is { } soft) soft.IsOn = enabled;
        }

        /// <summary>面板开关翻转（MW_BoardBarHandlers 调用）：镜像两个开关、更新引擎并回流主设置。</summary>
        private void RaiseInkToShapeChanged(bool enabled)
        {
            InkToShapeEnabled = enabled;
            _inkToShape.Enabled = enabled;
            if (ToggleSwitchEnableInkToShape is { } hard) hard.IsOn = enabled;
            if (ToggleSwitchEnableInkToShapeSoft is { } soft) soft.IsOn = enabled;
            InkToShapeChanged?.Invoke(enabled);
        }

        /// <summary>注入识别相似度门槛（MainWindow 推送，= 灵敏度滑块百分比 / 100；仅作用于引擎，无 UI 镜像）。</summary>
        public void ApplyInkToShapeSensitivity(double threshold) => _inkToShape.SimilarityThreshold = threshold;

        /// <summary>引擎命中后的替换（对齐 ICA：置提交门 → 移除手绘笔画 → 加回生成笔画 → 复位门）。
        /// 撤销通路已存在于 MW_TimeMachine（ShapeRecognition 分支），此处只需保证门与顺序正确。</summary>
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
                // 一次性移除整批（单个 StrokesChanged 事件），再添加生成笔画
                if (toRemove.Count > 0)
                    target.Remove(toRemove);
                target.Add(add);
            }
            finally
            {
                _currentCommitType = CommitReason.UserInput;
            }
        }
    }
}
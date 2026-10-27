#nullable disable warnings

using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.Services;
using InkCanvasForPDF.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        #region Multi-Touch

        bool isInMultiTouchMode = false;
        private void BorderMultiTouchMode_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isInMultiTouchMode)
            {
                isInMultiTouchMode = false;   // 先复位：避免下面 EditingMode=Ink 被多触摸逻辑强制成 None
                inkCanvas.StylusDown -= MainWindow_StylusDown;
                inkCanvas.StylusMove -= MainWindow_StylusMove;
                inkCanvas.StylusUp -= MainWindow_StylusUp;
                inkCanvas.TouchDown -= MainWindow_TouchDown;
                inkCanvas.TouchDown += Main_Grid_TouchDown;
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                inkCanvas.Children.Clear();
            }
            else
            {
                inkCanvas.StylusDown += MainWindow_StylusDown;
                inkCanvas.StylusMove += MainWindow_StylusMove;
                inkCanvas.StylusUp += MainWindow_StylusUp;
                inkCanvas.TouchDown += MainWindow_TouchDown;
                inkCanvas.TouchDown -= Main_Grid_TouchDown;
                inkCanvas.Children.Clear();
                isInMultiTouchMode = true;
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;   // 先给工具态，再由下面压到单一收墨管线（None）
                ApplyMultiTouchEditingMode();
            }
            RefreshTimePressureArmed();   // 多触摸模式启停影响时间笔锋武装状态（多触摸走自制自绘路径，不启用时间笔锋）
        }

        private void MainWindow_TouchDown(object sender, TouchEventArgs e)
        {
            if (inkCanvas.EditingMode == InkCanvasEditingMode.EraseByPoint
                || inkCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke
                || inkCanvas.EditingMode == InkCanvasEditingMode.Select) return;

            if (!isHidingSubPanelsWhenInking)
            {
                isHidingSubPanelsWhenInking = true;
                HideSubPanels(); // 书写时自动隐藏二级菜单
            }

            double boundWidth = e.GetTouchPoint(null).Bounds.Width;

            // 手掌擦除开关门控（仅书写模式）：关闭时大接触不再自动切面积擦，多指各自停墨
            if (Vm?.Settings.IsPalmEraseEnabled == true
                && PalmEraseMetrics.IsTouchEraseAllowed()   // 启用特殊屏幕且触摸倍数为 0 时禁用橡皮
                && boundWidth > BoundsWidth)
            {
                if (drawingShapeMode == 0 && forceEraser) return;
                // 手掌不是书写设备：丢弃它可能已建立的时间笔锋会话，防止手掌既擦又写
                inkCanvas.Controller.AbortStroke(e.TouchDevice.Id);
                // 尺寸 = 触点接触面积 √(W×H)（判定仍按触点宽度阈值）
                double footprint = PalmEraseMetrics.GetTouchFootprintSize(e);
                inkCanvas.EraserShape = new RectangleStylusShape(footprint, InkCircleCursor.EraserBoardHeight(footprint));
                TouchDownPointsList[e.TouchDevice.Id] = InkCanvasEditingMode.EraseByPoint;
                inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            }
            else
            {
                TouchDownPointsList[e.TouchDevice.Id] = InkCanvasEditingMode.None;
                inkCanvas.EditingMode = InkCanvasEditingMode.None;
            }
        }

        private void MainWindow_StylusDown(object sender, StylusDownEventArgs e)
        {
            if (inkCanvas.EditingMode == InkCanvasEditingMode.EraseByPoint
                || inkCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke
                || inkCanvas.EditingMode == InkCanvasEditingMode.Select) return;

            TouchDownPointsList[e.StylusDevice.Id] = InkCanvasEditingMode.None;
        }

        private async void MainWindow_StylusUp(object sender, StylusEventArgs e)
        {
            try
            {
                if (e.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus)
                {
                    // 数位板 TabletDeviceType.Stylus
                }
                else if (PauseCommittedDevices.Contains(e.StylusDevice.Id) && !StrokeVisualList.ContainsKey(e.StylusDevice.Id))
                {
                    // 停顿强制识别已提前提交该笔画且停顿后无续画：跳过重复入 Strokes，仅走下方公共清理
                }
                else if (isInMultiTouchMode)
                {
                    // 多触摸画笔模式：全部设备由时间笔锋控制器自绘并在抬笔处理里提交，禁走 ICA 预览兜底
                }
                else
                {
                    // 触摸屏 TabletDeviceType.Touch
                    // 时间笔锋叠加层已接管该设备（多触摸自绘）：控制器在抬笔处理里自行提交，
                    // 这里不再把 StrokeVisual 重复入集合（Move 阶段已跳过绘制，通常也没有 StrokeVisual）。
                    if (!inkCanvas.Controller.HasSession(e.StylusDevice.Id)
                        && StrokeVisualList.TryGetValue(e.StylusDevice.Id, out var touchVisual)
                        && touchVisual.Stroke != null)
                    {
                        inkCanvas.Strokes.Add(touchVisual.Stroke);
                        await Task.Delay(5); // 避免渲染墨迹完成前预览墨迹被删除导致墨迹闪烁
                        if (GetVisualCanvas(e.StylusDevice.Id) is { } visualCanvas)
                            inkCanvas.Children.Remove(visualCanvas);
                        inkCanvas_StrokeCollected(inkCanvas, new InkCanvasStrokeCollectedEventArgs(touchVisual.Stroke));
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }
            try
            {
                PauseCommittedDevices.Remove(e.StylusDevice.Id);
                StrokeVisualList.Remove(e.StylusDevice.Id);
                // 只清本设备：原“任一字典为空就全清”的判定会在多指书写时把其他手指的预览/状态一起清掉（丢墨）
                if (VisualCanvasList.Remove(e.StylusDevice.Id, out var staleCanvas))
                    inkCanvas.Children.Remove(staleCanvas);
                TouchDownPointsList.Remove(e.StylusDevice.Id);
                if (dec.Count == 0)
                {
                    inkCanvas.Children.Clear();
                    StrokeVisualList.Clear();
                    VisualCanvasList.Clear();
                    TouchDownPointsList.Clear();
                }
            }
            catch { }
        }

        private void MainWindow_StylusMove(object sender, StylusEventArgs e)
        {
            try
            {
                if (GetTouchDownPointsList(e.StylusDevice.Id) != InkCanvasEditingMode.None) return;
                // 多触摸画笔模式：全部设备由叠加层负责自绘，禁止 ICA StrokeVisual 预览（湿墨双份、丢墨/等宽）
                if (isInMultiTouchMode) return;
                // 叠加层已在绘制该设备（时间笔锋 / 硬笔屏蔽 / 多触摸自绘）：跳过 StrokeVisual，避免双份湿墨
                if (inkCanvas.Controller.HasSession(e.StylusDevice.Id)) return;
                try
                {
                    if (e.StylusDevice.StylusButtons[1].StylusButtonState == StylusButtonState.Down) return;
                }
                catch { }
                var strokeVisual = GetStrokeVisual(e.StylusDevice.Id);
                var stylusPointCollection = e.GetStylusPoints(this);
                foreach (var stylusPoint in stylusPointCollection)
                {
                    strokeVisual.Add(new StylusPoint(stylusPoint.X, stylusPoint.Y, stylusPoint.PressureFactor));
                }
                strokeVisual.Redraw();
            }
            catch { }
        }

        private StrokeVisual GetStrokeVisual(int id)
        {
            if (StrokeVisualList.TryGetValue(id, out var visual))
            {
                return visual;
            }

            var strokeVisual = new StrokeVisual(inkCanvas.DefaultDrawingAttributes.Clone());
            StrokeVisualList[id] = strokeVisual;
            StrokeVisualList[id] = strokeVisual;
            var visualCanvas = new VisualCanvas(strokeVisual);
            VisualCanvasList[id] = visualCanvas;
            inkCanvas.Children.Add(visualCanvas);

            return strokeVisual;
        }

        private VisualCanvas? GetVisualCanvas(int id)
        {
            if (VisualCanvasList.TryGetValue(id, out var visualCanvas))
            {
                return visualCanvas;
            }
            return null;
        }

        private InkCanvasEditingMode GetTouchDownPointsList(int id)
        {
            if (TouchDownPointsList.TryGetValue(id, out var inkCanvasEditingMode))
            {
                return inkCanvasEditingMode;
            }
            return inkCanvas.EditingMode;
        }

        private Dictionary<int, InkCanvasEditingMode> TouchDownPointsList { get; } = new Dictionary<int, InkCanvasEditingMode>();
        private Dictionary<int, StrokeVisual> StrokeVisualList { get; } = new Dictionary<int, StrokeVisual>();
        private Dictionary<int, VisualCanvas> VisualCanvasList { get; } = new Dictionary<int, VisualCanvas>();

        #endregion



        Point iniP = new Point(0, 0);
        private bool forcePointEraser = true;

        private void Main_Grid_TouchDown(object sender, TouchEventArgs e)
        {
            if (!isHidingSubPanelsWhenInking)
            {
                isHidingSubPanelsWhenInking = true;
                HideSubPanels(); // 书写时自动隐藏二级菜单
            }

            if (NeedUpdateIniP())
            {
                iniP = e.GetTouchPoint(inkCanvas).Position;
            }
            if (drawingShapeMode == 9 && isFirstTouchCuboid == false)
            {
                MouseTouchMove(iniP);
            }
            inkCanvas.Opacity = 1;
            double boundsWidth = GetTouchBoundWidth(e);

            // 手掌擦除（仅书写模式，受橡皮菜单开关门控）：接触面积超阈值 → 面积擦，
            // 尺寸 = 触点接触面积 √(W×H)（判定仍按触点宽度阈值）
            bool palmErase = Vm?.Settings.IsPalmEraseEnabled == true
                             && PalmEraseMetrics.IsTouchEraseAllowed()
                             && boundsWidth > BoundsWidth;
            if (palmErase)
            {
                isLastTouchEraser = true;
                // 手掌不是书写设备：丢弃它可能已建立的时间笔锋会话，防止手掌湿墨被叠加层画出来
                inkCanvas.Controller.AbortStroke(e.TouchDevice.Id);
                if (drawingShapeMode == 0 && forceEraser) return;
                if (boundsWidth <= BoundsWidth * 2.5
                    && BtnPPTSlideShowEnd.Visibility == Visibility.Visible && inkCanvas.Strokes.Count == 0
                    && Settings.PowerPointSettings.IsEnableFingerGestureSlideShowControl)
                {
                    isLastTouchEraser = false;   // PPT 翻页手势（不是擦除，照 ICA 保留）
                    inkCanvas.EditingMode = InkCanvasEditingMode.GestureOnly;
                    inkCanvas.Opacity = 0.1;
                }
                else
                {
                    double footprint = PalmEraseMetrics.GetTouchFootprintSize(e);
                    inkCanvas.EraserShape = new RectangleStylusShape(footprint, InkCircleCursor.EraserBoardHeight(footprint));
                    inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;   // 手掌擦除 = 仅面积擦
                }
            }
            else
            {
                isLastTouchEraser = false;
                inkCanvas.EraserShape = forcePointEraser
                    ? new RectangleStylusShape(EraserDiameter, InkCircleCursor.EraserBoardHeight(EraserDiameter))
                    : new EllipseStylusShape(5, 5);
                if (forceEraser) return;
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
        }

        /// <summary>触点接触宽度（ICA MW_TouchEvents L281-289）：转发到共享 PalmEraseMetrics，避免与 PDF 侧两份漂移。</summary>
        public double GetTouchBoundWidth(TouchEventArgs e) => PalmEraseMetrics.GetTouchBoundWidth(e);

        //记录触摸设备ID
        private List<int> dec = new List<int>();
        //中心点
        System.Windows.Point centerPoint;
        InkCanvasEditingMode lastInkCanvasEditingMode = InkCanvasEditingMode.Ink;
        bool isSingleFingerDragMode = false;

        /// <summary>双指手势成立时回滚橡皮批处理（CodeInput 门，不入撤销历史）：橡皮 + 双指缩放不留擦除痕迹。</summary>
        private void RevertEraseBatchForGesture()
        {
            if (ReplacedStroke == null && AddedStroke == null)
                return;
            var savedCommitType = _currentCommitType;
            _currentCommitType = CommitReason.CodeInput;
            try
            {
                if (AddedStroke is { Count: > 0 }) inkCanvas.Strokes.Remove(AddedStroke);
                if (ReplacedStroke is { Count: > 0 }) inkCanvas.Strokes.Add(ReplacedStroke);
            }
            finally
            {
                _currentCommitType = savedCommitType;
                AddedStroke = null;
                ReplacedStroke = null;
            }
        }

        private void inkCanvas_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            dec.Add(e.TouchDevice.Id);
            //设备1个的时候，记录中心点
            if (dec.Count == 1)
            {
                TouchPoint touchPoint = e.GetTouchPoint(inkCanvas);
                centerPoint = touchPoint.Position;
            }
            //设备两个及两个以上，将画笔功能关闭
            if (dec.Count > 1 || isSingleFingerDragMode || !Settings.Gesture.IsEnableTwoFingerGesture)
            {
                if (isInMultiTouchMode || !Settings.Gesture.IsEnableTwoFingerGesture) return;
                if (inkCanvas.EditingMode != InkCanvasEditingMode.None && inkCanvas.EditingMode != InkCanvasEditingMode.Select)
                {
                    lastInkCanvasEditingMode = inkCanvas.EditingMode;
                    inkCanvas.EditingMode = InkCanvasEditingMode.None;
                }
                if (dec.Count >= 2 && forceEraser)
                    RevertEraseBatchForGesture();   // 橡皮 + 双指手势成立：回滚本触摸已擦内容，不留擦除痕迹
            }
        }

        private void inkCanvas_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            //手势完成后切回之前的状态
            if (dec.Count > 1)
            {
                if (inkCanvas.EditingMode == InkCanvasEditingMode.None)
                {
                    inkCanvas.EditingMode = lastInkCanvasEditingMode;
                }
            }
            dec.Remove(e.TouchDevice.Id);
            inkCanvas.Opacity = 1;
        }
        private void inkCanvas_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            e.Mode = ManipulationModes.All;
        }

        private void inkCanvas_ManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
        {

        }

        private void Main_Grid_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            if (e.Manipulators.Count() == 0)
            {
                if (forceEraser) return;
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            }
        }

        private void Main_Grid_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (isInMultiTouchMode || !Settings.Gesture.IsEnableTwoFingerGesture) return;
            if ((dec.Count >= 2 && (Settings.PowerPointSettings.IsEnableTwoFingerGestureInPresentationMode || BtnPPTSlideShowEnd.Visibility != Visibility.Visible)) || isSingleFingerDragMode)
            {
                ManipulationDelta md = e.DeltaManipulation;
                Vector trans = md.Translation;  // 获得位移矢量

                Matrix m = new Matrix();

                if (Settings.Gesture.IsEnableTwoFingerTranslate)
                    m.Translate(trans.X, trans.Y);  // 移动

                if (Settings.Gesture.IsEnableTwoFingerGestureTranslateOrRotation && e.Source is FrameworkElement fe)
                {
                    double rotate = md.Rotation;  // 获得旋转角度
                    Vector scale = md.Scale;  // 获得缩放倍数

                    // Find center of element and then transform to get current location of center
                    Point center = new Point(fe.ActualWidth / 2, fe.ActualHeight / 2);
                    center = m.Transform(center);  // 转换为矩阵缩放和旋转的中心点

                    if (Settings.Gesture.IsEnableTwoFingerRotation)
                        m.RotateAt(rotate, center.X, center.Y);  // 旋转
                    if (Settings.Gesture.IsEnableTwoFingerZoom)
                        m.ScaleAt(scale.X, scale.Y, center.X, center.Y);  // 缩放
                }

                StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
                if (strokes.Count != 0)
                {
                    foreach (Stroke stroke in strokes)
                    {
                        stroke.Transform(m, false);

                        foreach (Circle circle in circles)
                        {
                            if (stroke == circle.Stroke)
                            {
                                circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                                circle.Centroid = new Point((circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                                                            (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2);
                                break;
                            }
                        }

                        if (Settings.Gesture.IsEnableTwoFingerZoom)
                        {
                            try
                            {
                                stroke.DrawingAttributes.Width *= md.Scale.X;
                                stroke.DrawingAttributes.Height *= md.Scale.Y;
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    if (Settings.Gesture.IsEnableTwoFingerZoom)
                    {
                        foreach (Stroke stroke in inkCanvas.Strokes)
                        {
                            stroke.Transform(m, false);
                            try
                            {
                                stroke.DrawingAttributes.Width *= md.Scale.X;
                                stroke.DrawingAttributes.Height *= md.Scale.Y;
                            }
                            catch { }
                        };
                    }
                    else
                    {
                        foreach (Stroke stroke in inkCanvas.Strokes)
                        {
                            stroke.Transform(m, false);
                        };
                    }

                    foreach (Circle circle in circles)
                    {
                        circle.R = GetDistance(circle.Stroke.StylusPoints[0].ToPoint(), circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].ToPoint()) / 2;
                        circle.Centroid = new Point(
                            (circle.Stroke.StylusPoints[0].X + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].X) / 2,
                            (circle.Stroke.StylusPoints[0].Y + circle.Stroke.StylusPoints[circle.Stroke.StylusPoints.Count / 2].Y) / 2
                        );
                    }
                }
            }
        }
    }
}

#nullable disable warnings

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Threading;
using InkCanvasForPDF.Services;
using InkCanvasForPDF.ViewModels;
using Point = System.Windows.Point;

namespace InkCanvasForPDF;

/// <summary>PDF 批注·形状手绘（自 Ink-Canvas-Artistry 4.9.4 MW_ShapeDrawing.cs 移植到页面画布）。
/// 模式状态存于 InkToolViewModel.ShapeDrawMode；临时笔画提交期间置 CommitReason.ShapeDrawing
/// 以跳过撤销记录，抬笔定型后统一 CommitStrokeUserInputHistory。</summary>
public partial class MainWindow : Window
{
    // ═══ 状态字段（ICA MW_ShapeDrawing.cs / MW_TouchEvents.cs 对应字段，加 shape 前缀防混用）═══

    private Point shapeIniP = new Point(0, 0);
    private bool shapeIsMouseDown;
    private bool _shapeFinishing;   // 抬笔完成流程进行中（含同步弹窗泵）：屏蔽绘制帧，防泵内 MouseMove 画幽灵帧删曲线/劫持 special
    private bool shapeIsWaitUntilNextTouchDown;
    private Stroke shapeLastTempStroke = null!;
    private StrokeCollection shapeLastTempStrokeCollection = null!;

    private int shapeMultiStepShapeCurrentStep;                                   //多笔完成的图形 当前所处在的笔画
    private StrokeCollection shapeMultiStepShapeSpecialStrokeCollection = new();  //多笔完成的图形（双曲线渐近线）
    private double shapeMultiStepShapeSpecialParameter3;                          //多笔完成的图形 特殊参数 通常用于表示k

    private bool shapeIsFirstTouchCuboid = true;
    private Point shapeCuboidFrontRectIniP = new Point();
    private Point shapeCuboidFrontRectEndP = new Point();
    private StrokeCollection shapeCuboidStrokeCollection = null!;

    private readonly List<int> shapeTouchIds = new List<int>();   //正在触控的设备 id（多指取消用）

    private int ShapeDrawMode => _viewModel.InkTool.ShapeDrawMode;

    /// <summary>两点距离（ICA GetDistance 原样）。</summary>
    private static double GetDistance(Point point1, Point point2)
    {
        return Math.Sqrt((point1.X - point2.X) * (point1.X - point2.X) + (point1.Y - point2.Y) * (point1.Y - point2.Y));
    }

    // ICA MW_ShapeDrawing.cs:1635-1642（双曲线第二笔不更新起点）
    private bool ShapeNeedUpdateIniP()
    {
        if (ShapeDrawMode == 24 || ShapeDrawMode == 25)
        {
            if (shapeMultiStepShapeCurrentStep == 1) return false;
        }
        return true;
    }

    // ═══ 鼠标/触摸事件（XAML PageInkCanvas 挂接）═══

    // ICA MW_ShapeDrawing.cs:1470-1477
    private void PageCanvas_ShapeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ShapeDrawMode == 0) return;
        if (sender is not InkCanvas canvas) return;
        canvas.CaptureMouse();   // 拖出画布仍持续预览（ICA/community 同款行为），MouseUp 统一释放
        shapeIsMouseDown = true;
        if (ShapeNeedUpdateIniP())
        {
            shapeIniP = e.GetPosition(canvas);
        }
    }

    // ICA MW_ShapeDrawing.cs:1479-1485
    private void PageCanvas_ShapeMouseMove(object sender, MouseEventArgs e)
    {
        if (ShapeDrawMode == 0) return;
        if (!shapeIsMouseDown || sender is not InkCanvas canvas) return;
        ShapeMouseTouchMove(canvas, e.GetPosition(canvas));
    }

    // ICA MW_ShapeDrawing.cs:1487（形状部分收尾在 ShapeDrawingMouseUp；抬手后清多指等待）
    private void PageCanvas_ShapeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ShapeDrawMode == 0) return;   // 非形状模式的画布 mouse-up 不得触碰捕获与形状临时状态
        if (sender is not InkCanvas canvas) return;
        canvas.ReleaseMouseCapture();
        ShapeDrawingMouseUp(canvas);
    }

    // ICA MW_TouchEvents.cs Main_Grid_TouchDown 形状相关段（NeedUpdateIniP / 长方体第二笔首帧预览）
    private void PageCanvas_ShapeTouchDown(object sender, TouchEventArgs e)
    {
        if (ShapeDrawMode == 0) return;
        if (sender is not InkCanvas canvas) return;
        shapeTouchIds.Add(e.TouchDevice.Id);
        if (ShapeNeedUpdateIniP())
        {
            shapeIniP = e.GetTouchPoint(canvas).Position;
        }
        if (ShapeDrawMode == 9 && shapeIsFirstTouchCuboid == false)
        {
            ShapeMouseTouchMove(canvas, shapeIniP);
        }
    }

    // ICA MW_ShapeDrawing.cs:471-500
    private void PageCanvas_ShapeTouchMove(object sender, TouchEventArgs e)
    {
        if (ShapeDrawMode == 0) return;
        if (sender is not InkCanvas canvas) return;
        if (shapeIsWaitUntilNextTouchDown) return;
        if (shapeTouchIds.Count > 1)
        {
            shapeIsWaitUntilNextTouchDown = true;
            try
            {
                canvas.Strokes.Remove(shapeLastTempStroke);
                canvas.Strokes.Remove(shapeLastTempStrokeCollection);
            }
            catch { }
            return;
        }
        if (canvas.EditingMode != InkCanvasEditingMode.None)
        {
            // 同 CommitPendingPdfStroke 手法：EditingMode 是 OneWay 绑定，裸赋值会清掉绑定
            // 导致该页之后不再随工具切换同步，故摘绑定→赋值→接回。
            var modeProperty = InkCanvas.EditingModeProperty;
            var binding = System.Windows.Data.BindingOperations.GetBindingExpression(canvas, modeProperty);
            if (binding != null)
                System.Windows.Data.BindingOperations.ClearBinding(canvas, modeProperty);
            canvas.EditingMode = InkCanvasEditingMode.None;
            if (binding != null)
                System.Windows.Data.BindingOperations.SetBinding(canvas, modeProperty, binding.ParentBinding);
        }
        ShapeMouseTouchMove(canvas, e.GetTouchPoint(canvas).Position);
    }

    // 触摸抬手（PreviewTouchUp：先于 InkCanvas_TouchUp 的擦除提交，形状模式无墨迹收集互不干扰）
    private void PageCanvas_ShapePreviewTouchUp(object sender, TouchEventArgs e)
    {
        if (sender is not InkCanvas canvas) return;
        shapeTouchIds.Remove(e.TouchDevice.Id);
        ShapeDrawingMouseUp(canvas);
        if (shapeTouchIds.Count == 0)
        {
            shapeIsWaitUntilNextTouchDown = false;
        }
    }

    // ═══ 抬笔定型（ICA MW_ShapeDrawing.cs:1487-1633 形状部分；PDF 渐近线弹窗为 iuwm ContentDialog 同步泵，见 AskKeepAsymptote）═══

    /// <summary>「是否保留渐近线？」询问（iuwm ContentDialog，措辞与设置卡片「保留双曲线渐近线」一致）。
    /// 同步版：Dispatcher.PushFrame 嵌套泵等弹窗关闭——完成流程保持 ICA/MessageBox 的同步语义，
    /// 不引入 async 续体（修复「弹窗点击后双曲线本体消失」的关键差异）。
    /// 返回 true=保留，false=移除；弹窗失败按保留处理。PDF 与画板共用（画板经 Window.GetWindow 调用）。</summary>
    internal bool AskKeepAsymptote()
    {
        try
        {
            var dialog = new iNKORE.UI.WPF.Modern.Controls.ContentDialog
            {
                Title = "双曲线渐近线",
                Content = "是否保留渐近线？",
                PrimaryButtonText = "保留",      // 强调按钮 = 安全侧
                SecondaryButtonText = "移除",
                Owner = this,
            };
            dialog.SetResourceReference(iNKORE.UI.WPF.Modern.Controls.ContentDialog.PrimaryButtonStyleProperty,
                iNKORE.UI.WPF.Modern.ThemeKeys.AccentButtonStyleKey);
            var task = dialog.ShowAsync();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame);   // 模态泵：处理弹窗输入，关闭后返回
            }
            if (task.Status != TaskStatus.RanToCompletion)
                throw task.Exception?.GetBaseException() ?? new InvalidOperationException("ContentDialog 未正常完成");
            return task.Result == iNKORE.UI.WPF.Modern.Controls.ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            App.Log("Shape.AsymptoteAsk", ex);
            return true;   // 失败保留渐近线（安全侧）
        }
    }

    /// <summary>选中双曲线类形状（24/25）时复位两笔状态机（ICA BtnDrawHyperbola_Click L381/393 语义；
    /// 画板侧 SelectShape 已有同款）。防止上一次只画了第一笔就中止导致 step 残留 1、
    /// 下一次第一笔抬笔被误判为完成分支。</summary>
    internal void ResetHyperbolaStep() => shapeMultiStepShapeCurrentStep = 0;

    private void ShapeDrawingMouseUp(InkCanvas canvas)
    {
        _shapeFinishing = true;
        shapeIsMouseDown = false;   // 提前复位（原在方法尾）：同步弹窗泵期间任何 MouseMove 立即失效
        try
        {
            if (ShapeDrawMode != 9 && ShapeDrawMode != 0 && ShapeDrawMode != 24 && ShapeDrawMode != 25)
            {
                if (!_viewModel.InkTool.IsShapeLongPressSelected)
                {
                    // 画完一次还原到笔模式（SetActiveTool 内部复位 ShapeDrawMode）
                    _viewModel.InkTool.SetActiveTool(InkToolMode.Pen);
                }
            }
            if (ShapeDrawMode == 9)
            {
                if (shapeIsFirstTouchCuboid)
                {
                    shapeCuboidStrokeCollection ??= new StrokeCollection();
                    shapeIsFirstTouchCuboid = false;
                    Point newIniP = new Point(Math.Min(shapeCuboidFrontRectIniP.X, shapeCuboidFrontRectEndP.X), Math.Min(shapeCuboidFrontRectIniP.Y, shapeCuboidFrontRectEndP.Y));
                    Point newEndP = new Point(Math.Max(shapeCuboidFrontRectIniP.X, shapeCuboidFrontRectEndP.X), Math.Max(shapeCuboidFrontRectIniP.Y, shapeCuboidFrontRectEndP.Y));
                    shapeCuboidFrontRectIniP = newIniP;
                    shapeCuboidFrontRectEndP = newEndP;
                    shapeCuboidStrokeCollection.Add(shapeLastTempStrokeCollection);
                }
                else
                {
                    // 画完还原到笔模式
                    _viewModel.InkTool.SetActiveTool(InkToolMode.Pen);

                    if (_currentCommitType == CommitReason.ShapeDrawing)
                    {
                        // 集合为 null 说明本"第二笔"没有对应的第一笔（上手正方体未走完/状态被中断）：
                        // 跳过提交，避免对 null 集合 Add 抛 NullReferenceException
                        if (shapeCuboidStrokeCollection != null)
                        {
                            shapeCuboidStrokeCollection.Add(shapeLastTempStrokeCollection);
                            _viewModel.InkTool.TimeMachine.CommitStrokeUserInputHistory(shapeCuboidStrokeCollection, canvas.Strokes);
                            shapeCuboidStrokeCollection = null;
                        }
                        _currentCommitType = CommitReason.UserInput;   // 提交门无论是否提交都复位
                    }
                    // 关键：复位为"第一笔"状态，下一个正方体从正面矩形重新开始。
                    // 原实现画完不复位（shapeIsFirstTouchCuboid 恒为 false），第二次绘制会直接进本分支并对 null 集合 Add → 崩溃。
                    shapeIsFirstTouchCuboid = true;
                }
            }
            if (ShapeDrawMode == 24 || ShapeDrawMode == 25)
            {
                if (shapeMultiStepShapeCurrentStep == 0)
                {
                    shapeMultiStepShapeCurrentStep = 1;
                }
                else
                {
                    shapeMultiStepShapeCurrentStep = 0;
                    var curve = shapeLastTempStrokeCollection;   // 第二笔曲线集合快照（入口处仍在）
                    ConsoleLog.Write("双曲线绘制", $"完成: 曲线笔画={curve?.Count ?? 0}, 渐近线笔画={shapeMultiStepShapeSpecialStrokeCollection?.Count ?? -1}, 画布笔画={canvas.Strokes.Count}");
                    // 通用设置（hyperbolaAsymptoteOption）：0=保留 / 1=移除 / 2=询问（iuwm ContentDialog 同步泵）
                    bool keep = true;
                    switch (_viewModel.Settings.HyperbolaAsymptoteOption)
                    {
                        case 1: keep = false; break;
                        case 2: keep = AskKeepAsymptote(); break;
                    }
                    if (!keep)
                        canvas.Strokes.Remove(shapeMultiStepShapeSpecialStrokeCollection);
                    // 自愈：保留/移除决定后曲线若已不在画布（根因未明的移除路径），提交历史前补回
                    bool healed = false;
                    if (curve is { Count: > 0 } && !canvas.Strokes.Contains(curve[0]))
                    {
                        canvas.Strokes.Add(curve);
                        healed = true;
                    }
                    ConsoleLog.Write("双曲线绘制", $"结果: 渐近线={(keep ? "保留" : "移除")}, 自愈={healed}, 画布笔画={canvas.Strokes.Count}");
                    // 画完还原到笔模式
                    _viewModel.InkTool.SetActiveTool(InkToolMode.Pen);
                }
            }
            if (_currentCommitType == CommitReason.ShapeDrawing && ShapeDrawMode != 9)
            {
                _currentCommitType = CommitReason.UserInput;
                StrokeCollection? collection = null;
                if (shapeLastTempStrokeCollection != null && shapeLastTempStrokeCollection.Count > 0)
                {
                    collection = shapeLastTempStrokeCollection;
                }
                else if (shapeLastTempStroke != null)
                {
                    collection = new StrokeCollection() { shapeLastTempStroke };
                }
                if (collection != null)
                {
                    _viewModel.InkTool.TimeMachine.CommitStrokeUserInputHistory(collection, canvas.Strokes);
                }
            }
            shapeLastTempStroke = null;
            shapeLastTempStrokeCollection = null;
        }
        finally
        {
            _shapeFinishing = false;
        }
    }

    // ═══ 形状绘制主函数（ICA MW_ShapeDrawing.cs:502-1280 原样移植；inkCanvas→页面画布、
    //     DefaultDrawingAttributes→InkTool.DrawingAttributes、drawingShapeMode→ShapeDrawMode）═══

    private void ShapeMouseTouchMove(InkCanvas canvas, Point endP)
    {
        if (_shapeFinishing) return;   // 完成流程（含同步弹窗泵）期间屏蔽一切绘制帧
        var drawingShapeMode = ShapeDrawMode;
        // 形状笔画必须关闭墨迹平滑：FitToCurve 会把矩形等少量点的闭合几何拟合成圆角曲线（矩形只剩 5 点）
        var defaultAttributes = _viewModel.InkTool.DrawingAttributes.Clone();
        defaultAttributes.FitToCurve = false;
        List<Point> pointList;
        StylusPointCollection point;
        Stroke stroke;
        StrokeCollection strokes = new StrokeCollection();
        Point newIniP = shapeIniP;
        switch (drawingShapeMode)
        {
            case 1:
                _currentCommitType = CommitReason.ShapeDrawing;
                pointList = new List<Point>{
                    new Point(shapeIniP.X, shapeIniP.Y),
                    new Point(endP.X, endP.Y)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStroke);
                }
                catch { }
                shapeLastTempStroke = stroke;
                canvas.Strokes.Add(stroke);
                break;
            case 8:
                _currentCommitType = CommitReason.ShapeDrawing;
                strokes.Add(ShapeGenerateDashedLineStrokeCollection(defaultAttributes, shapeIniP, endP));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);
                break;
            case 18:
                _currentCommitType = CommitReason.ShapeDrawing;
                strokes.Add(ShapeGenerateDotLineStrokeCollection(defaultAttributes, shapeIniP, endP));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);
                break;
            case 2:
                _currentCommitType = CommitReason.ShapeDrawing;
                double w = 30, h = 10;
                double theta = Math.Atan2(shapeIniP.Y - endP.Y, shapeIniP.X - endP.X);
                double sint = Math.Sin(theta);
                double cost = Math.Cos(theta);

                pointList = new List<Point>
                {
                    new Point(shapeIniP.X, shapeIniP.Y),
                    new Point(endP.X , endP.Y),
                    new Point(endP.X + (w * cost - h * sint), endP.Y + (w * sint + h * cost)),
                    new Point(endP.X,endP.Y),
                    new Point(endP.X + (w * cost + h * sint), endP.Y - (h * cost - w * sint))
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStroke);
                }
                catch { }
                shapeLastTempStroke = stroke;
                canvas.Strokes.Add(stroke);

                break;
            case 15:
                _currentCommitType = CommitReason.ShapeDrawing;
                double d = GetDistance(shapeIniP, endP);
                if (d == 0) return;
                double sinTheta = (shapeIniP.Y - endP.Y) / d;
                double cosTheta = (endP.X - shapeIniP.X) / d;
                double tanTheta = Math.Abs(sinTheta / cosTheta);
                double x = 25;
                if (Math.Abs(tanTheta) < 1.0 / 12)
                {
                    sinTheta = 0;
                    cosTheta = 1;
                    endP.Y = shapeIniP.Y;
                }
                if (tanTheta < 0.63 && tanTheta > 0.52) //30
                {
                    sinTheta = sinTheta / Math.Abs(sinTheta) * 0.5;
                    cosTheta = cosTheta / Math.Abs(cosTheta) * 0.866;
                    endP.Y = shapeIniP.Y - d * sinTheta;
                    endP.X = shapeIniP.X + d * cosTheta;
                }
                if (tanTheta < 1.08 && tanTheta > 0.92) //45
                {
                    sinTheta = sinTheta / Math.Abs(sinTheta) * 0.707;
                    cosTheta = cosTheta / Math.Abs(cosTheta) * 0.707;
                    endP.Y = shapeIniP.Y - d * sinTheta;
                    endP.X = shapeIniP.X + d * cosTheta;
                }
                if (tanTheta < 1.95 && tanTheta > 1.63) //60
                {
                    sinTheta = sinTheta / Math.Abs(sinTheta) * 0.866;
                    cosTheta = cosTheta / Math.Abs(cosTheta) * 0.5;
                    endP.Y = shapeIniP.Y - d * sinTheta;
                    endP.X = shapeIniP.X + d * cosTheta;
                }
                if (Math.Abs(cosTheta / sinTheta) < 1.0 / 12)
                {
                    endP.X = shapeIniP.X;
                    sinTheta = 1;
                    cosTheta = 0;
                }
                strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(shapeIniP.X - 3 * x * sinTheta, shapeIniP.Y - 3 * x * cosTheta), new Point(endP.X - 3 * x * sinTheta, endP.Y - 3 * x * cosTheta)));
                strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(shapeIniP.X - x * sinTheta, shapeIniP.Y - x * cosTheta), new Point(endP.X - x * sinTheta, endP.Y - x * cosTheta)));
                strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(shapeIniP.X + x * sinTheta, shapeIniP.Y + x * cosTheta), new Point(endP.X + x * sinTheta, endP.Y + x * cosTheta)));
                strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(shapeIniP.X + 3 * x * sinTheta, shapeIniP.Y + 3 * x * cosTheta), new Point(endP.X + 3 * x * sinTheta, endP.Y + 3 * x * cosTheta)));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 11:
                _currentCommitType = CommitReason.ShapeDrawing;
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(2 * shapeIniP.X - (endP.X - 20), shapeIniP.Y), new Point(endP.X, shapeIniP.Y)));
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X, 2 * shapeIniP.Y - (endP.Y + 20)), new Point(shapeIniP.X, endP.Y)));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 12:
                _currentCommitType = CommitReason.ShapeDrawing;
                if (Math.Abs(shapeIniP.X - endP.X) < 0.01) return;
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X + (shapeIniP.X - endP.X) / Math.Abs(shapeIniP.X - endP.X) * 25, shapeIniP.Y), new Point(endP.X, shapeIniP.Y)));
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X, 2 * shapeIniP.Y - (endP.Y + 20)), new Point(shapeIniP.X, endP.Y)));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 13:
                _currentCommitType = CommitReason.ShapeDrawing;
                if (Math.Abs(shapeIniP.Y - endP.Y) < 0.01) return;
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(2 * shapeIniP.X - (endP.X - 20), shapeIniP.Y), new Point(endP.X, shapeIniP.Y)));
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X, shapeIniP.Y + (shapeIniP.Y - endP.Y) / Math.Abs(shapeIniP.Y - endP.Y) * 25), new Point(shapeIniP.X, endP.Y)));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 14:
                _currentCommitType = CommitReason.ShapeDrawing;
                if (Math.Abs(shapeIniP.X - endP.X) < 0.01 || Math.Abs(shapeIniP.Y - endP.Y) < 0.01) return;
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X + (shapeIniP.X - endP.X) / Math.Abs(shapeIniP.X - endP.X) * 25, shapeIniP.Y), new Point(endP.X, shapeIniP.Y)));
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X, shapeIniP.Y + (shapeIniP.Y - endP.Y) / Math.Abs(shapeIniP.Y - endP.Y) * 25), new Point(shapeIniP.X, endP.Y)));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 17:
                _currentCommitType = CommitReason.ShapeDrawing;
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X, shapeIniP.Y), new Point(shapeIniP.X + Math.Abs(endP.X - shapeIniP.X), shapeIniP.Y)));
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X, shapeIniP.Y), new Point(shapeIniP.X, shapeIniP.Y - Math.Abs(endP.Y - shapeIniP.Y))));
                d = (Math.Abs(shapeIniP.X - endP.X) + Math.Abs(shapeIniP.Y - endP.Y)) / 2;
                strokes.Add(ShapeGenerateArrowLineStroke(defaultAttributes, new Point(shapeIniP.X, shapeIniP.Y), new Point(shapeIniP.X - d / 1.76, shapeIniP.Y + d / 1.76)));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 3:
                _currentCommitType = CommitReason.ShapeDrawing;
                pointList = new List<Point>{
                    new Point(shapeIniP.X, shapeIniP.Y),
                    new Point(shapeIniP.X, endP.Y),
                    new Point(endP.X, endP.Y),
                    new Point(endP.X, shapeIniP.Y),
                    new Point(shapeIniP.X, shapeIniP.Y)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStroke);
                }
                catch { }
                shapeLastTempStroke = stroke;
                canvas.Strokes.Add(stroke);

                break;
            case 19:
                _currentCommitType = CommitReason.ShapeDrawing;
                double a = shapeIniP.X - endP.X;
                double b = shapeIniP.Y - endP.Y;
                pointList = new List<Point>{
                    new Point(shapeIniP.X - a, shapeIniP.Y - b),
                    new Point(shapeIniP.X - a, shapeIniP.Y + b),
                    new Point(shapeIniP.X + a, shapeIniP.Y + b),
                    new Point(shapeIniP.X + a, shapeIniP.Y - b),
                    new Point(shapeIniP.X - a, shapeIniP.Y - b)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStroke);
                }
                catch { }
                shapeLastTempStroke = stroke;
                canvas.Strokes.Add(stroke);

                break;
            case 4:
                _currentCommitType = CommitReason.ShapeDrawing;
                pointList = ShapeGenerateEllipseGeometry(shapeIniP, endP);
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStroke);
                }
                catch { }
                shapeLastTempStroke = stroke;
                canvas.Strokes.Add(stroke);

                break;
            case 5:
                _currentCommitType = CommitReason.ShapeDrawing;
                double R = GetDistance(shapeIniP, endP);
                pointList = ShapeGenerateEllipseGeometry(new Point(shapeIniP.X - R, shapeIniP.Y - R), new Point(shapeIniP.X + R, shapeIniP.Y + R));
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStroke);
                }
                catch { }
                shapeLastTempStroke = stroke;
                canvas.Strokes.Add(stroke);

                break;
            case 16:
                _currentCommitType = CommitReason.ShapeDrawing;
                double halfA = endP.X - shapeIniP.X;
                double halfB = endP.Y - shapeIniP.Y;
                pointList = ShapeGenerateEllipseGeometry(new Point(shapeIniP.X - halfA, shapeIniP.Y - halfB), new Point(shapeIniP.X + halfA, shapeIniP.Y + halfB));
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStroke);
                }
                catch { }
                shapeLastTempStroke = stroke;
                canvas.Strokes.Add(stroke);

                break;
            case 23:
                _currentCommitType = CommitReason.ShapeDrawing;
                a = Math.Abs(endP.X - shapeIniP.X);
                b = Math.Abs(endP.Y - shapeIniP.Y);
                pointList = ShapeGenerateEllipseGeometry(new Point(shapeIniP.X - a, shapeIniP.Y - b), new Point(shapeIniP.X + a, shapeIniP.Y + b));
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke);
                double c = Math.Sqrt(Math.Abs(a * a - b * b));
                StylusPoint stylusPoint;
                if (a > b)
                {
                    stylusPoint = new StylusPoint(shapeIniP.X + c, shapeIniP.Y, (float)1.0);
                    point = new StylusPointCollection();
                    point.Add(stylusPoint);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = defaultAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    stylusPoint = new StylusPoint(shapeIniP.X - c, shapeIniP.Y, (float)1.0);
                    point = new StylusPointCollection();
                    point.Add(stylusPoint);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = defaultAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }
                else if (a < b)
                {
                    stylusPoint = new StylusPoint(shapeIniP.X, shapeIniP.Y - c, (float)1.0);
                    point = new StylusPointCollection();
                    point.Add(stylusPoint);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = defaultAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                    stylusPoint = new StylusPoint(shapeIniP.X, shapeIniP.Y + c, (float)1.0);
                    point = new StylusPointCollection();
                    point.Add(stylusPoint);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = defaultAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 10:
                _currentCommitType = CommitReason.ShapeDrawing;
                R = GetDistance(shapeIniP, endP);
                strokes = ShapeGenerateDashedLineEllipseStrokeCollection(defaultAttributes, new Point(shapeIniP.X - R, shapeIniP.Y - R), new Point(shapeIniP.X + R, shapeIniP.Y + R));
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 24:
            case 25:
                _currentCommitType = CommitReason.ShapeDrawing;
                //双曲线 x^2/a^2 - y^2/b^2 = 1
                if (Math.Abs(shapeIniP.X - endP.X) < 0.01 || Math.Abs(shapeIniP.Y - endP.Y) < 0.01) return;
                var pointList2 = new List<Point>();
                var pointList3 = new List<Point>();
                var pointList4 = new List<Point>();
                if (shapeMultiStepShapeCurrentStep == 0)
                {
                    //第一笔：画渐近线
                    double k = Math.Abs((endP.Y - shapeIniP.Y) / (endP.X - shapeIniP.X));
                    strokes.Add(ShapeGenerateDashedLineStrokeCollection(defaultAttributes, new Point(2 * shapeIniP.X - endP.X, 2 * shapeIniP.Y - endP.Y), endP));
                    strokes.Add(ShapeGenerateDashedLineStrokeCollection(defaultAttributes, new Point(2 * shapeIniP.X - endP.X, endP.Y), new Point(endP.X, 2 * shapeIniP.Y - endP.Y)));
                    shapeMultiStepShapeSpecialParameter3 = k;
                    shapeMultiStepShapeSpecialStrokeCollection = strokes;
                }
                else
                {
                    //第二笔：画双曲线
                    double k = shapeMultiStepShapeSpecialParameter3;
                    bool isHyperbolaFocalPointOnXAxis = Math.Abs((endP.Y - shapeIniP.Y) / (endP.X - shapeIniP.X)) < k;
                    if (isHyperbolaFocalPointOnXAxis)
                    { // 焦点在 x 轴上
                        a = Math.Sqrt(Math.Abs((endP.X - shapeIniP.X) * (endP.X - shapeIniP.X) - (endP.Y - shapeIniP.Y) * (endP.Y - shapeIniP.Y) / (k * k)));
                        b = a * k;
                        pointList = new List<Point>();
                        for (double i = a; i <= Math.Abs(endP.X - shapeIniP.X); i += 0.5)
                        {
                            double rY = Math.Sqrt(Math.Abs(k * k * i * i - b * b));
                            pointList.Add(new Point(shapeIniP.X + i, shapeIniP.Y - rY));
                            pointList2.Add(new Point(shapeIniP.X + i, shapeIniP.Y + rY));
                            pointList3.Add(new Point(shapeIniP.X - i, shapeIniP.Y - rY));
                            pointList4.Add(new Point(shapeIniP.X - i, shapeIniP.Y + rY));
                        }
                    }
                    else
                    { // 焦点在 y 轴上
                        a = Math.Sqrt(Math.Abs((endP.Y - shapeIniP.Y) * (endP.Y - shapeIniP.Y) - (endP.X - shapeIniP.X) * (endP.X - shapeIniP.X) * (k * k)));
                        b = a / k;
                        pointList = new List<Point>();
                        for (double i = a; i <= Math.Abs(endP.Y - shapeIniP.Y); i += 0.5)
                        {
                            double rX = Math.Sqrt(Math.Abs(i * i / k / k - b * b));
                            pointList.Add(new Point(shapeIniP.X - rX, shapeIniP.Y + i));
                            pointList2.Add(new Point(shapeIniP.X + rX, shapeIniP.Y + i));
                            pointList3.Add(new Point(shapeIniP.X - rX, shapeIniP.Y - i));
                            pointList4.Add(new Point(shapeIniP.X + rX, shapeIniP.Y - i));
                        }
                    }
                    try
                    {
                        point = new StylusPointCollection(pointList);
                        stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        point = new StylusPointCollection(pointList2);
                        stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        point = new StylusPointCollection(pointList3);
                        stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        point = new StylusPointCollection(pointList4);
                        stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                        strokes.Add(stroke.Clone());
                        if (drawingShapeMode == 25)
                        {
                            //画焦点
                            c = Math.Sqrt(a * a + b * b);
                            stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(shapeIniP.X + c, shapeIniP.Y, (float)1.0) : new StylusPoint(shapeIniP.X, shapeIniP.Y + c, (float)1.0);
                            point = new StylusPointCollection();
                            point.Add(stylusPoint);
                            stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                            stylusPoint = isHyperbolaFocalPointOnXAxis ? new StylusPoint(shapeIniP.X - c, shapeIniP.Y, (float)1.0) : new StylusPoint(shapeIniP.X, shapeIniP.Y - c, (float)1.0);
                            point = new StylusPointCollection();
                            point.Add(stylusPoint);
                            stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                            strokes.Add(stroke.Clone());
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log("Hyperbola.Curve", ex);   // 生成异常只进 error.log（log 只存错误）
                        return;
                    }
                }
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 20:
                _currentCommitType = CommitReason.ShapeDrawing;
                //抛物线 y=ax^2
                if (Math.Abs(shapeIniP.X - endP.X) < 0.01 || Math.Abs(shapeIniP.Y - endP.Y) < 0.01) return;
                a = (shapeIniP.Y - endP.Y) / ((shapeIniP.X - endP.X) * (shapeIniP.X - endP.X));
                pointList = new List<Point>();
                pointList2 = new List<Point>();
                for (double i = 0.0; i <= Math.Abs(endP.X - shapeIniP.X); i += 0.5)
                {
                    pointList.Add(new Point(shapeIniP.X + i, shapeIniP.Y - a * i * i));
                    pointList2.Add(new Point(shapeIniP.X - i, shapeIniP.Y - a * i * i));
                }
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                point = new StylusPointCollection(pointList2);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 21:
                _currentCommitType = CommitReason.ShapeDrawing;
                //抛物线 y^2=ax
                if (Math.Abs(shapeIniP.X - endP.X) < 0.01 || Math.Abs(shapeIniP.Y - endP.Y) < 0.01) return;
                a = (shapeIniP.X - endP.X) / ((shapeIniP.Y - endP.Y) * (shapeIniP.Y - endP.Y));
                pointList = new List<Point>();
                pointList2 = new List<Point>();
                for (double i = 0.0; i <= Math.Abs(endP.Y - shapeIniP.Y); i += 0.5)
                {
                    pointList.Add(new Point(shapeIniP.X - a * i * i, shapeIniP.Y + i));
                    pointList2.Add(new Point(shapeIniP.X - a * i * i, shapeIniP.Y - i));
                }
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                point = new StylusPointCollection(pointList2);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 22:
                _currentCommitType = CommitReason.ShapeDrawing;
                //抛物线 y^2=ax, 含焦点
                if (Math.Abs(shapeIniP.X - endP.X) < 0.01 || Math.Abs(shapeIniP.Y - endP.Y) < 0.01) return;
                double p = (shapeIniP.Y - endP.Y) * (shapeIniP.Y - endP.Y) / (2 * (shapeIniP.X - endP.X));
                a = 0.5 / p;
                pointList = new List<Point>();
                pointList2 = new List<Point>();
                for (double i = 0.0; i <= Math.Abs(endP.Y - shapeIniP.Y); i += 0.5)
                {
                    pointList.Add(new Point(shapeIniP.X - a * i * i, shapeIniP.Y + i));
                    pointList2.Add(new Point(shapeIniP.X - a * i * i, shapeIniP.Y - i));
                }
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                point = new StylusPointCollection(pointList2);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                stylusPoint = new StylusPoint(shapeIniP.X - p / 2, shapeIniP.Y, (float)1.0);
                point = new StylusPointCollection();
                point.Add(stylusPoint);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 6:
                _currentCommitType = CommitReason.ShapeDrawing;
                newIniP = shapeIniP;
                if (shapeIniP.Y > endP.Y)
                {
                    newIniP = new Point(shapeIniP.X, endP.Y);
                    endP = new Point(endP.X, shapeIniP.Y);
                }
                double topA = Math.Abs(newIniP.X - endP.X);
                double topB = topA / 2.646;
                //顶部椭圆
                pointList = ShapeGenerateEllipseGeometry(new Point(newIniP.X, newIniP.Y - topB / 2), new Point(endP.X, newIniP.Y + topB / 2));
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                //底部椭圆
                pointList = ShapeGenerateEllipseGeometry(new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), false, true);
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                strokes.Add(ShapeGenerateDashedLineEllipseStrokeCollection(defaultAttributes, new Point(newIniP.X, endP.Y - topB / 2), new Point(endP.X, endP.Y + topB / 2), true, false));
                //左侧
                pointList = new List<Point>{
                    new Point(newIniP.X, newIniP.Y),
                    new Point(newIniP.X, endP.Y)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                //右侧
                pointList = new List<Point>{
                    new Point(endP.X, newIniP.Y),
                    new Point(endP.X, endP.Y)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 7:
                _currentCommitType = CommitReason.ShapeDrawing;
                if (shapeIniP.Y > endP.Y)
                {
                    newIniP = new Point(shapeIniP.X, endP.Y);
                    endP = new Point(endP.X, shapeIniP.Y);
                }
                double bottomA = Math.Abs(newIniP.X - endP.X);
                double bottomB = bottomA / 2.646;
                //底部椭圆
                pointList = ShapeGenerateEllipseGeometry(new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), false, true);
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                strokes.Add(ShapeGenerateDashedLineEllipseStrokeCollection(defaultAttributes, new Point(newIniP.X, endP.Y - bottomB / 2), new Point(endP.X, endP.Y + bottomB / 2), true, false));
                //左侧
                pointList = new List<Point>{
                    new Point((newIniP.X + endP.X) / 2, newIniP.Y),
                    new Point(newIniP.X, endP.Y)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                //右侧
                pointList = new List<Point>{
                    new Point((newIniP.X + endP.X) / 2, newIniP.Y),
                    new Point(endP.X, endP.Y)
                };
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = defaultAttributes.Clone()
                };
                strokes.Add(stroke.Clone());
                try
                {
                    canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                }
                catch { }
                shapeLastTempStrokeCollection = strokes;
                canvas.Strokes.Add(strokes);

                break;
            case 9:
                _currentCommitType = CommitReason.ShapeDrawing;
                if (shapeIsFirstTouchCuboid)
                {
                    //分开画线条方便后期单独擦除某一条棱
                    strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(shapeIniP.X, shapeIniP.Y), new Point(shapeIniP.X, endP.Y)));
                    strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(shapeIniP.X, endP.Y), new Point(endP.X, endP.Y)));
                    strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(endP.X, endP.Y), new Point(endP.X, shapeIniP.Y)));
                    strokes.Add(ShapeGenerateLineStroke(defaultAttributes, new Point(shapeIniP.X, shapeIniP.Y), new Point(endP.X, shapeIniP.Y)));
                    try
                    {
                        canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                    }
                    catch { }
                    shapeLastTempStrokeCollection = strokes;
                    canvas.Strokes.Add(strokes);
                    shapeCuboidFrontRectIniP = shapeIniP;
                    shapeCuboidFrontRectEndP = endP;
                }
                else
                {
                    d = shapeCuboidFrontRectIniP.Y - endP.Y;
                    if (d < 0) d = -d;
                    a = shapeCuboidFrontRectEndP.X - shapeCuboidFrontRectIniP.X; //正面矩形长
                    b = shapeCuboidFrontRectEndP.Y - shapeCuboidFrontRectIniP.Y; //正面矩形宽

                    //横上
                    Point newLineIniP = new Point(shapeCuboidFrontRectIniP.X + d, shapeCuboidFrontRectIniP.Y - d);
                    Point newLineEndP = new Point(shapeCuboidFrontRectEndP.X + d, shapeCuboidFrontRectIniP.Y - d);
                    pointList = new List<Point> { newLineIniP, newLineEndP };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                    strokes.Add(stroke.Clone());
                    //横下 (虚线)
                    newLineIniP = new Point(shapeCuboidFrontRectIniP.X + d, shapeCuboidFrontRectEndP.Y - d);
                    newLineEndP = new Point(shapeCuboidFrontRectEndP.X + d, shapeCuboidFrontRectEndP.Y - d);
                    strokes.Add(ShapeGenerateDashedLineStrokeCollection(defaultAttributes, newLineIniP, newLineEndP));
                    //斜左上
                    newLineIniP = new Point(shapeCuboidFrontRectIniP.X, shapeCuboidFrontRectIniP.Y);
                    newLineEndP = new Point(shapeCuboidFrontRectIniP.X + d, shapeCuboidFrontRectIniP.Y - d);
                    pointList = new List<Point> { newLineIniP, newLineEndP };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                    strokes.Add(stroke.Clone());
                    //斜右上
                    newLineIniP = new Point(shapeCuboidFrontRectEndP.X, shapeCuboidFrontRectIniP.Y);
                    newLineEndP = new Point(shapeCuboidFrontRectEndP.X + d, shapeCuboidFrontRectIniP.Y - d);
                    pointList = new List<Point> { newLineIniP, newLineEndP };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                    strokes.Add(stroke.Clone());
                    //斜左下 (虚线)
                    newLineIniP = new Point(shapeCuboidFrontRectIniP.X, shapeCuboidFrontRectEndP.Y);
                    newLineEndP = new Point(shapeCuboidFrontRectIniP.X + d, shapeCuboidFrontRectEndP.Y - d);
                    strokes.Add(ShapeGenerateDashedLineStrokeCollection(defaultAttributes, newLineIniP, newLineEndP));
                    //斜右下
                    newLineIniP = new Point(shapeCuboidFrontRectEndP.X, shapeCuboidFrontRectEndP.Y);
                    newLineEndP = new Point(shapeCuboidFrontRectEndP.X + d, shapeCuboidFrontRectEndP.Y - d);
                    pointList = new List<Point> { newLineIniP, newLineEndP };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                    strokes.Add(stroke.Clone());
                    //竖左 (虚线)
                    newLineIniP = new Point(shapeCuboidFrontRectIniP.X + d, shapeCuboidFrontRectIniP.Y - d);
                    newLineEndP = new Point(shapeCuboidFrontRectIniP.X + d, shapeCuboidFrontRectEndP.Y - d);
                    strokes.Add(ShapeGenerateDashedLineStrokeCollection(defaultAttributes, newLineIniP, newLineEndP));
                    //竖右
                    newLineIniP = new Point(shapeCuboidFrontRectEndP.X + d, shapeCuboidFrontRectIniP.Y - d);
                    newLineEndP = new Point(shapeCuboidFrontRectEndP.X + d, shapeCuboidFrontRectEndP.Y - d);
                    pointList = new List<Point> { newLineIniP, newLineEndP };
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point) { DrawingAttributes = defaultAttributes.Clone() };
                    strokes.Add(stroke.Clone());

                    try
                    {
                        canvas.Strokes.Remove(shapeLastTempStrokeCollection);
                    }
                    catch { }
                    shapeLastTempStrokeCollection = strokes;
                    canvas.Strokes.Add(strokes);
                }

                break;
        }
    }

    // ═══ 笔画生成辅助（ICA MW_ShapeDrawing.cs:1297-1467 移植；attrs 显式传参）═══

    private static List<Point> ShapeGenerateEllipseGeometry(Point st, Point ed, bool isDrawTop = true, bool isDrawBottom = true)
    {
        double a = 0.5 * (ed.X - st.X);
        double b = 0.5 * (ed.Y - st.Y);
        List<Point> pointList = new List<Point>();
        if (isDrawTop && isDrawBottom)
        {
            for (double r = 0; r <= 2 * Math.PI; r = r + 0.01)
            {
                pointList.Add(new Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
            }
        }
        else
        {
            if (isDrawBottom)
            {
                for (double r = 0; r <= Math.PI; r = r + 0.01)
                {
                    pointList.Add(new Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                }
            }
            if (isDrawTop)
            {
                for (double r = Math.PI; r <= 2 * Math.PI; r = r + 0.01)
                {
                    pointList.Add(new Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                }
            }
        }
        return pointList;
    }

    private static StrokeCollection ShapeGenerateDashedLineEllipseStrokeCollection(DrawingAttributes attrs, Point st, Point ed, bool isDrawTop = true, bool isDrawBottom = true)
    {
        double a = 0.5 * (ed.X - st.X);
        double b = 0.5 * (ed.Y - st.Y);
        double step = 0.05;
        List<Point> pointList;
        StylusPointCollection point;
        Stroke stroke;
        StrokeCollection strokes = new StrokeCollection();
        if (isDrawBottom)
        {
            for (double i = 0.0; i < 1.0; i += step * 1.66)
            {
                pointList = new List<Point>();
                for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                {
                    pointList.Add(new Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                }
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = attrs.Clone()
                };
                strokes.Add(stroke.Clone());
            }
        }
        if (isDrawTop)
        {
            for (double i = 1.0; i < 2.0; i += step * 1.66)
            {
                pointList = new List<Point>();
                for (double r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                {
                    pointList.Add(new Point(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                }
                point = new StylusPointCollection(pointList);
                stroke = new Stroke(point)
                {
                    DrawingAttributes = attrs.Clone()
                };
                strokes.Add(stroke.Clone());
            }
        }
        return strokes;
    }

    private static Stroke ShapeGenerateLineStroke(DrawingAttributes attrs, Point st, Point ed)
    {
        List<Point> pointList = new List<Point>{
            new Point(st.X, st.Y),
            new Point(ed.X, ed.Y)
        };
        var point = new StylusPointCollection(pointList);
        return new Stroke(point)
        {
            DrawingAttributes = attrs.Clone()
        };
    }

    private static Stroke ShapeGenerateArrowLineStroke(DrawingAttributes attrs, Point st, Point ed)
    {
        double w = 20, h = 7;
        double theta = Math.Atan2(st.Y - ed.Y, st.X - ed.X);
        double sint = Math.Sin(theta);
        double cost = Math.Cos(theta);

        List<Point> pointList = new List<Point>
        {
            new Point(st.X, st.Y),
            new Point(ed.X , ed.Y),
            new Point(ed.X + (w * cost - h * sint), ed.Y + (w * sint + h * cost)),
            new Point(ed.X,ed.Y),
            new Point(ed.X + (w * cost + h * sint), ed.Y - (h * cost - w * sint))
        };
        var point = new StylusPointCollection(pointList);
        return new Stroke(point)
        {
            DrawingAttributes = attrs.Clone()
        };
    }

    private static StrokeCollection ShapeGenerateDashedLineStrokeCollection(DrawingAttributes attrs, Point st, Point ed)
    {
        double step = 5;
        List<Point> pointList;
        StylusPointCollection point;
        Stroke stroke;
        StrokeCollection strokes = new StrokeCollection();
        double d = Math.Sqrt((st.X - ed.X) * (st.X - ed.X) + (st.Y - ed.Y) * (st.Y - ed.Y));
        double sinTheta = (ed.Y - st.Y) / d;
        double cosTheta = (ed.X - st.X) / d;
        for (double i = 0.0; i < d; i += step * 2.76)
        {
            pointList = new List<Point>{
                new Point(st.X + i * cosTheta, st.Y + i * sinTheta),
                new Point(st.X + Math.Min(i + step, d) * cosTheta, st.Y + Math.Min(i + step, d) * sinTheta)
            };
            point = new StylusPointCollection(pointList);
            stroke = new Stroke(point)
            {
                DrawingAttributes = attrs.Clone()
            };
            strokes.Add(stroke.Clone());
        }
        return strokes;
    }

    private static StrokeCollection ShapeGenerateDotLineStrokeCollection(DrawingAttributes attrs, Point st, Point ed)
    {
        double step = 3;
        StylusPointCollection point;
        Stroke stroke;
        StrokeCollection strokes = new StrokeCollection();
        double d = Math.Sqrt((st.X - ed.X) * (st.X - ed.X) + (st.Y - ed.Y) * (st.Y - ed.Y));
        double sinTheta = (ed.Y - st.Y) / d;
        double cosTheta = (ed.X - st.X) / d;
        for (double i = 0.0; i < d; i += step * 2.76)
        {
            var stylusPoint = new StylusPoint(st.X + i * cosTheta, st.Y + i * sinTheta, (float)0.8);
            point = new StylusPointCollection();
            point.Add(stylusPoint);
            stroke = new Stroke(point)
            {
                DrawingAttributes = attrs.Clone()
            };
            strokes.Add(stroke.Clone());
        }
        return strokes;
    }
}

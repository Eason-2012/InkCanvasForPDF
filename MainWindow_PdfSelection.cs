#nullable disable warnings

using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.ViewModels;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace InkCanvasForPDF;

/// <summary>PDF 批注"选择"（ICA 屏幕批注 MW_SelectionGestures.cs 移植）：套索选中笔画后弹出选择浮动工具栏，
/// 支持鼠标拖移、双指旋转/缩放/平移、克隆模式、衍至画板、粗细、翻转、旋转、改色、存为图片、删除。
/// 适配点：ICA 单画布全窗 → PDF 多页画布。inkCanvas 等价物是 <see cref="_selectionInkCanvas"/>（最近触发
/// SelectionChanged 的页面画布）；选择遮罩逐页放置（必须与页面画布同坐标系），浮动工具栏为窗口级单实例
/// （页面处于整层 DisplayScale 缩放层内，页内放置会随缩放变形），定位经 TransformToVisual 换算窗口坐标。</summary>
public partial class MainWindow
{
    // ═══ 选择状态（ICA MW_SelectionGestures.cs 同名字段等价；inkCanvas → _selectionInkCanvas）═══

    private InkCanvas? _selectionInkCanvas;   // 最近有选区的页面画布（ICA inkCanvas 等价）

    bool isStrokeSelectionCloneOn;

    private void BorderStrokeSelectionClone_Click(object sender, RoutedEventArgs e)
    {
        if (isStrokeSelectionCloneOn)
        {
            IconStrokeSelectionClone.SetResourceReference(TextBlock.ForegroundProperty, "FloatBarForeground");
            isStrokeSelectionCloneOn = false;
        }
        else
        {
            IconStrokeSelectionClone.SetResourceReference(TextBlock.ForegroundProperty, "FloatBarBackground");
            isStrokeSelectionCloneOn = true;
        }
    }

    private void BorderStrokeSelectionCloneToBoardOrNewPage_Click(object sender, RoutedEventArgs e)
    {
        // ICA 屏幕批注（currentMode == 0）分支：衍至画板 = 克隆选中笔画 → 进入画板 → 粘贴（PDF 恒走此分支）
        if (_selectionInkCanvas == null)
            return;
        var strokes = _selectionInkCanvas.GetSelectedStrokes();
        _selectionInkCanvas.Select(new StrokeCollection());
        strokes = strokes.Clone();
        SetBoardMode(true);
        Board.inkCanvas.Strokes.Add(strokes);
    }

    private void GridPenWidthDecrease_Click(object sender, RoutedEventArgs e)
    {
        ChangeSelectedStrokeThickness(0.8);
    }

    private void GridPenWidthIncrease_Click(object sender, RoutedEventArgs e)
    {
        ChangeSelectedStrokeThickness(1.25);
    }

    private void GridPenWidthRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_selectionInkCanvas == null) return;
        foreach (Stroke stroke in _selectionInkCanvas.GetSelectedStrokes())
        {
            stroke.DrawingAttributes.Width = _selectionInkCanvas.DefaultDrawingAttributes.Width;
            stroke.DrawingAttributes.Height = _selectionInkCanvas.DefaultDrawingAttributes.Height;
        }
    }

    private void BorderStrokeSelectionDelete_Click(object sender, RoutedEventArgs e)
    {
        // ICA SymbolIconDelete_MouseUp 选中分支（选择工具栏仅在有选区时可见，无整页清空语义）
        if (_selectionInkCanvas == null) return;
        if (_selectionInkCanvas.GetSelectedStrokes().Count > 0)
        {
            _selectionInkCanvas.Strokes.Remove(_selectionInkCanvas.GetSelectedStrokes());
            HidePageSelectionCover(_selectionInkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnStrokeSelectionSaveToImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectionInkCanvas == null) return;
        StrokeCollection selectedStrokes = _selectionInkCanvas.GetSelectedStrokes();

        if (selectedStrokes.Count > 0)
        {
            Rect bounds = selectedStrokes.GetBounds();

            double width = bounds.Width + 10;
            double height = bounds.Height + 10;
            RenderTargetBitmap renderTarget = new RenderTargetBitmap(
                (int)Math.Ceiling(width), (int)Math.Ceiling(height),
                96, 96, PixelFormats.Pbgra32);

            DrawingVisual drawingVisual = new DrawingVisual();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));

                foreach (Stroke stroke in selectedStrokes)
                {
                    stroke.Draw(drawingContext);
                }
            }

            renderTarget.Render(drawingVisual);

            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "PNG Images|*.png";
            saveFileDialog.Title = "Save Selected Ink as PNG";
            saveFileDialog.FileName = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff");

            if (saveFileDialog.ShowDialog() == true)
            {
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderTarget));

                using (FileStream fileStream = new FileStream(saveFileDialog.FileName, FileMode.Create))
                {
                    encoder.Save(fileStream);
                }
            }
        }
    }

    private void ChangeSelectedStrokeThickness(double multipler)
    {
        if (_selectionInkCanvas == null) return;
        foreach (Stroke stroke in _selectionInkCanvas.GetSelectedStrokes())
        {
            var newWidth = stroke.DrawingAttributes.Width * multipler;
            var newHeight = stroke.DrawingAttributes.Height * multipler;
            if (newWidth >= DrawingAttributes.MinWidth && newWidth <= DrawingAttributes.MaxWidth
                && newHeight >= DrawingAttributes.MinHeight && newHeight <= DrawingAttributes.MaxHeight)
            {
                stroke.DrawingAttributes.Width = newWidth;
                stroke.DrawingAttributes.Height = newHeight;
            }
        }
        CommitDrawingAttributesHistoryIfAny();
    }

    private void MatrixTransform(int type)
    {
        if (_selectionInkCanvas == null) return;
        Matrix m = new Matrix();
        Point center = new Point(_selectionInkCanvas.GetSelectionBounds().Left + _selectionInkCanvas.GetSelectionBounds().Width / 2,
            _selectionInkCanvas.GetSelectionBounds().Top + _selectionInkCanvas.GetSelectionBounds().Height / 2);

        switch (type)
        {
            case 1: // Flip Horizontal
                m.ScaleAt(-1, 1, center.X, center.Y);
                break;
            case 2: // Flip Vertical
                m.ScaleAt(1, -1, center.X, center.Y);
                break;
            default: // Rotate
                m.RotateAt(type, center.X, center.Y);
                break;
        }

        StrokeCollection targetStrokes = _selectionInkCanvas.GetSelectedStrokes();
        foreach (Stroke stroke in targetStrokes)
        {
            stroke.Transform(m, false);
        }

        List<Image> selectedImages = InkCanvasImageHelper.GetSelectedImages(_selectionInkCanvas);
        foreach (Image image in selectedImages)
        {
            ApplyImageMatrixTransform(image, m, center);
        }

        CommitDrawingAttributesHistoryIfAny();
    }

    private void ApplyImageMatrixTransform(Image image, Matrix matrix, Point center, double transX = 0, double transY = 0)
    {
        // handle Translate:
        InkCanvas.SetLeft(image, InkCanvas.GetLeft(image) + transX);
        InkCanvas.SetTop(image, InkCanvas.GetTop(image) + transY);
        // handle Rotate and Scale:
        TransformGroup transformGroup = GetOrCreateTransformGroup(image);
        TransformGroup centeredTransformGroup = new TransformGroup();
        // move to the center point
        double dx = center.X - image.ActualWidth / 2;
        double dy = center.Y - image.ActualHeight / 2;
        centeredTransformGroup.Children.Add(new TranslateTransform(dx, dy));
        // tranform
        centeredTransformGroup.Children.Add(new MatrixTransform(matrix));
        // move back
        centeredTransformGroup.Children.Add(new TranslateTransform(-dx, -dy));
        transformGroup.Children.Add(centeredTransformGroup);
    }

    private TransformGroup GetOrCreateTransformGroup(Image image)
    {
        TransformGroup? transformGroup = image.RenderTransform as TransformGroup;
        if (transformGroup == null)
        {
            transformGroup = new TransformGroup();
            image.RenderTransform = transformGroup;
        }
        return transformGroup;
    }

    private void BtnFlipHorizontal_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(1);
    }

    private void BtnFlipVertical_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(2);
    }

    private void BtnAnticlockwiseRotate15_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(-15);
    }

    private void BtnAnticlockwiseRotate45_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(-45);
    }

    private void BtnAnticlockwiseRotate90_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(-90);
    }

    private void BtnClockwiseRotate15_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(15);
    }

    private void BtnClockwiseRotate45_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(45);
    }

    private void BtnClockwiseRotate90_Click(object sender, RoutedEventArgs e)
    {
        MatrixTransform(90);
    }

    // ────────── 颜色按钮（ICA MW_PenColors.ColorSwitchCheck 选区分支：有选区只改选中笔画颜色）──────────

    private void BtnColorBlack_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(0);
    private void BtnColorWhite_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(5);
    private void BtnColorRed_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(1);
    private void BtnColorOrange_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(8);
    private void BtnColorYellow_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(4);
    private void BtnColorGreen_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(2);
    private void BtnColorTeal_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(7);
    private void BtnColorBlue_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(3);
    private void BtnColorPink_Click(object sender, RoutedEventArgs e) => ApplyColorToSelectedStrokes(6);

    /// <summary>改选中笔画颜色（ICA ColorSwitchCheck L54-65 选区分支）：只改 Color，不退出选择模式。</summary>
    private void ApplyColorToSelectedStrokes(int colorIndex)
    {
        if (_selectionInkCanvas == null) return;
        var map = _viewModel.InkTool.IsUselightThemeColor ? InkToolViewModel.PenLightColorMap : InkToolViewModel.PenDarkColorMap;
        if (!map.TryGetValue(colorIndex, out var color))
            return;

        StrokeCollection strokes = _selectionInkCanvas.GetSelectedStrokes();
        if (strokes.Count != 0)
        {
            foreach (Stroke stroke in strokes)
            {
                try
                {
                    stroke.DrawingAttributes.Color = color;
                }
                catch { }
            }
        }
        CommitDrawingAttributesHistoryIfAny();
    }

    // ────────── 选择遮罩手势（ICA L270-362、L445-596）──────────

    bool isGridInkCanvasSelectionCoverMouseDown = false;
    private Point lastMousePoint;

    private void GridInkCanvasSelectionCover_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectionInkCanvas == null) return;
        lastMousePoint = e.GetPosition(_selectionInkCanvas);
        isGridInkCanvasSelectionCoverMouseDown = true;
        if (isStrokeSelectionCloneOn)
        {
            StrokeCollection strokes = _selectionInkCanvas.GetSelectedStrokes();
            List<Image> imageList = InkCanvasImageHelper.GetSelectedImages(_selectionInkCanvas);
            isProgramChangeStrokeSelection = true;
            ImagesSelectionClone = InkCanvasImageHelper.CloneSelectedImages(_selectionInkCanvas);
            _selectionInkCanvas.Select(new StrokeCollection());
            StrokesSelectionClone = strokes.Clone();
            _selectionInkCanvas.Strokes.Add(StrokesSelectionClone);
            _selectionInkCanvas.Select(strokes, imageList);
            isProgramChangeStrokeSelection = false;
        }
        else if (lastMousePoint.X < _selectionInkCanvas.GetSelectionBounds().Left ||
        lastMousePoint.Y < _selectionInkCanvas.GetSelectionBounds().Top ||
        lastMousePoint.X > _selectionInkCanvas.GetSelectionBounds().Right ||
        lastMousePoint.Y > _selectionInkCanvas.GetSelectionBounds().Bottom)
        {
            isGridInkCanvasSelectionCoverMouseDown = false;
            _selectionInkCanvas.Select(new StrokeCollection());
            StrokesSelectionClone = new StrokeCollection();
            ImagesSelectionClone = new List<Image>();
        }
    }

    private void GridInkCanvasSelectionCover_MouseMove(object sender, MouseEventArgs e)
    {
        if (isGridInkCanvasSelectionCoverMouseDown == false || _selectionInkCanvas == null) return;
        Point mousePoint = e.GetPosition(_selectionInkCanvas);
        Vector trans = new Vector(mousePoint.X - lastMousePoint.X, mousePoint.Y - lastMousePoint.Y);
        lastMousePoint = mousePoint;
        Rect bounds = _selectionInkCanvas.GetSelectionBounds();
        Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        Matrix m = new Matrix();
        // handle images
        List<Image> images = new List<Image>();
        if (ImagesSelectionClone.Count != 0)
        {
            images = ImagesSelectionClone;
        }
        else
        {
            images = InkCanvasImageHelper.GetSelectedImages(_selectionInkCanvas);
        }
        foreach (Image image in images)
        {
            ApplyImageMatrixTransform(image, m, center, trans.X, trans.Y);
        }
        // add Translate
        m.Translate(trans.X, trans.Y);
        // handle strokes
        StrokeCollection strokes = _selectionInkCanvas.GetSelectedStrokes();
        if (StrokesSelectionClone.Count != 0)
        {
            strokes = StrokesSelectionClone;
        }
        foreach (Stroke stroke in strokes)
        {
            stroke.Transform(m, false);
        }
        updateBorderStrokeSelectionControlLocation();
    }

    private void GridInkCanvasSelectionCover_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionInkCanvas == null) return;
        isGridInkCanvasSelectionCoverMouseDown = false;
        if (InkCanvasImageHelper.IsNotCanvasElementSelected(_selectionInkCanvas))
        {
            HidePageSelectionCover(_selectionInkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Collapsed;
            StrokesSelectionClone = new StrokeCollection();
            ImagesSelectionClone = new List<Image>();
        }
        else
        {
            // PDF 侧恒为"衍至画板"（ICA currentMode==0 分支文案）
            TextSelectionCloneToNewBoard.Text = "衍至画板";
            ShowPageSelectionCover(_selectionInkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Visible;
            StrokesSelectionClone = new StrokeCollection();
            ImagesSelectionClone = new List<Image>();
        }
    }

    double BorderStrokeSelectionControlWidth = 490.0;
    double BorderStrokeSelectionControlHeight = 80.0;
    bool isProgramChangeStrokeSelection = false;

    /// <summary>页面画布选区变化（ICA inkCanvas_SelectionChanged L407-430 等价）。</summary>
    private void PageInkCanvas_SelectionChanged(object sender, EventArgs e)
    {
        if (sender is not InkCanvas inkCanvas)
            return;
        if (_viewModel.InkTool.ActiveTool != InkToolMode.Select)
        {
            // 非选择工具下的选区事件一律按"应清除"处理：堵死模式切换竞态下的一切重显路径
            // （不更新 _selectionInkCanvas：非 Select 工具下该字段应保持 null）
            HidePageSelectionCover(inkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Collapsed;
            return;
        }
        _selectionInkCanvas = inkCanvas;
        if (isProgramChangeStrokeSelection) return;
        if (InkCanvasImageHelper.IsNotCanvasElementSelected(inkCanvas))
        {
            HidePageSelectionCover(inkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Collapsed;
        }
        else
        {
            TextSelectionCloneToNewBoard.Text = "衍至画板";
            ShowPageSelectionCover(inkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Visible;
            IconStrokeSelectionClone.SetResourceReference(TextBlock.ForegroundProperty, "FloatBarForeground");
            ToggleButtonStrokeSelectionClone.IsChecked = false;
            isStrokeSelectionCloneOn = false;
            updateBorderStrokeSelectionControlLocation();
        }
    }

    /// <summary>工具栏定位（ICA updateBorderStrokeSelectionControlLocation L432-443 等价）：
    /// 原版公式与边界 clamp 保留，仅把选区_bounds 从页面坐标经 TransformToVisual 换算成窗口坐标
    /// （页面处于整层 DisplayScale 缩放层，换算自动完成），clamp 用窗口尺寸。</summary>
    private void updateBorderStrokeSelectionControlLocation()
    {
        if (_selectionInkCanvas == null) return;
        Rect bounds = _selectionInkCanvas.GetSelectionBounds();
        Point topLeft = _selectionInkCanvas.TransformToVisual(this).Transform(new Point(bounds.Left, bounds.Top));
        Point bottomRight = _selectionInkCanvas.TransformToVisual(this).Transform(new Point(bounds.Right, bounds.Bottom));

        double borderLeft = (topLeft.X + bottomRight.X - BorderStrokeSelectionControlWidth) / 2;
        double borderTop = bottomRight.Y + 15;
        if (borderLeft < 0) borderLeft = 0;
        if (borderTop < 0) borderTop = 0;
        if (ActualWidth - borderLeft < BorderStrokeSelectionControlWidth || double.IsNaN(borderLeft)) borderLeft = ActualWidth - BorderStrokeSelectionControlWidth;
        if (ActualHeight - borderTop < BorderStrokeSelectionControlHeight || double.IsNaN(borderTop)) borderTop = ActualHeight - BorderStrokeSelectionControlHeight;

        if (borderTop > 60) borderTop -= 60;
        BorderStrokeSelectionControl.Margin = new Thickness(borderLeft, borderTop, 0, 0);
    }

    private void GridInkCanvasSelectionCover_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        e.Mode = ManipulationModes.All;
    }

    private void GridInkCanvasSelectionCover_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
    {
        // ICA L450-470：双指结束后提交选择变换历史（移动/旋转/缩放可撤销）
        if (StrokeManipulationHistory?.Count > 0)
        {
            _viewModel.InkTool.TimeMachine.CommitStrokeManipulationHistory(StrokeManipulationHistory);
            foreach (var item in StrokeManipulationHistory)
            {
                StrokeInitialHistory[item.Key] = item.Value.Item2;
            }
            StrokeManipulationHistory = null;
        }
        CommitDrawingAttributesHistoryIfAny();
    }

    StrokeCollection StrokesSelectionClone = new StrokeCollection();
    List<Image> ImagesSelectionClone = new List<Image>();

    /// <summary>ICA Settings.Gesture.IsEnableTwoFingerRotationOnSelection 默认值（PDF 暂无对应设置项，取默认 false）。</summary>
    private static readonly bool IsEnableTwoFingerRotationOnSelection = false;

    private void GridInkCanvasSelectionCover_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        try
        {
            if (dec.Count >= 1 && _selectionInkCanvas != null)
            {
                ManipulationDelta md = e.DeltaManipulation;
                Vector trans = md.Translation;
                double rotate = md.Rotation;
                Vector scale = md.Scale;
                Rect bounds = _selectionInkCanvas.GetSelectionBounds();
                Point center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                Matrix m = new Matrix();
                // add Scale
                m.ScaleAt(scale.X, scale.Y, center.X, center.Y);
                StrokeCollection strokes = _selectionInkCanvas.GetSelectedStrokes();
                if (StrokesSelectionClone.Count != 0)
                {
                    strokes = StrokesSelectionClone;
                }
                else if (IsEnableTwoFingerRotationOnSelection)
                {
                    // add Rotate
                    m.RotateAt(rotate, center.X, center.Y);
                }
                List<Image> images = new List<Image>();
                if (ImagesSelectionClone.Count != 0)
                {
                    images = ImagesSelectionClone;
                }
                else
                {
                    images = InkCanvasImageHelper.GetSelectedImages(_selectionInkCanvas);
                }
                // handle images
                foreach (Image image in images)
                {
                    ApplyImageMatrixTransform(image, m, center, trans.X, trans.Y);
                }
                // add Translate
                m.Translate(trans.X, trans.Y);
                // handle strokes
                foreach (Stroke stroke in strokes)
                {
                    stroke.Transform(m, false);
                    try
                    {
                        stroke.DrawingAttributes.Width *= md.Scale.X;
                        stroke.DrawingAttributes.Height *= md.Scale.Y;
                    }
                    catch { }
                }
                updateBorderStrokeSelectionControlLocation();
            }
        }
        catch { }
    }

    Point lastTouchPointOnGridInkCanvasCover = new Point(0, 0);
    private void GridInkCanvasSelectionCover_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (_selectionInkCanvas == null) return;
        dec.Add(e.TouchDevice.Id);
        //设备1个的时候，记录中心点
        if (dec.Count == 1)
        {
            TouchPoint touchPoint = e.GetTouchPoint(null);
            centerPoint = touchPoint.Position;
            lastTouchPointOnGridInkCanvasCover = touchPoint.Position;

            if (isStrokeSelectionCloneOn)
            {
                StrokeCollection strokes = _selectionInkCanvas.GetSelectedStrokes();
                List<Image> imageList = InkCanvasImageHelper.GetSelectedImages(_selectionInkCanvas);
                isProgramChangeStrokeSelection = true;
                ImagesSelectionClone = InkCanvasImageHelper.CloneSelectedImages(_selectionInkCanvas);
                _selectionInkCanvas.Select(new StrokeCollection());
                StrokesSelectionClone = strokes.Clone();
                _selectionInkCanvas.Strokes.Add(StrokesSelectionClone);
                _selectionInkCanvas.Select(strokes, imageList);
                isProgramChangeStrokeSelection = false;
            }
        }
    }

    private void GridInkCanvasSelectionCover_PreviewTouchUp(object sender, TouchEventArgs e)
    {
        if (_selectionInkCanvas == null) return;
        dec.Remove(e.TouchDevice.Id);
        if (dec.Count >= 1) return;
        isProgramChangeStrokeSelection = false;
        if (lastTouchPointOnGridInkCanvasCover == e.GetTouchPoint(null).Position)
        {
            if (lastTouchPointOnGridInkCanvasCover.X < _selectionInkCanvas.GetSelectionBounds().Left ||
                lastTouchPointOnGridInkCanvasCover.Y < _selectionInkCanvas.GetSelectionBounds().Top ||
                lastTouchPointOnGridInkCanvasCover.X > _selectionInkCanvas.GetSelectionBounds().Right ||
                lastTouchPointOnGridInkCanvasCover.Y > _selectionInkCanvas.GetSelectionBounds().Bottom)
            {
                _selectionInkCanvas.Select(new StrokeCollection());
                StrokesSelectionClone = new StrokeCollection();
                ImagesSelectionClone = new List<Image>();
            }
        }
        else if (InkCanvasImageHelper.IsNotCanvasElementSelected(_selectionInkCanvas))
        {
            HidePageSelectionCover(_selectionInkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Collapsed;
            StrokesSelectionClone = new StrokeCollection();
            ImagesSelectionClone = new List<Image>();
        }
        else
        {
            TextSelectionCloneToNewBoard.Text = "衍至画板";
            ShowPageSelectionCover(_selectionInkCanvas);
            BorderStrokeSelectionControl.Visibility = Visibility.Visible;
            StrokesSelectionClone = new StrokeCollection();
            ImagesSelectionClone = new List<Image>();
        }
    }

    // ────────── 选择撤销历史（ICA MW_TimeMachine.cs L220-340 移植）──────────

    /// <summary>触摸设备 id 列表（ICA MW_TouchEvents.cs dec 同名字段；选择遮罩双指变换用）。</summary>
    private List<int> dec = new List<int>();
    private System.Windows.Point centerPoint;
    private Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>>? StrokeManipulationHistory;
    private Dictionary<Stroke, StylusPointCollection> StrokeInitialHistory = new Dictionary<Stroke, StylusPointCollection>();
    private Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>> DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
    private Dictionary<Guid, List<Stroke>> DrawingAttributesHistoryFlag = new Dictionary<Guid, List<Stroke>>()
    {
        { DrawingAttributeIds.Color, new List<Stroke>() },
        { DrawingAttributeIds.DrawingFlags, new List<Stroke>() },
        { DrawingAttributeIds.IsHighlighter, new List<Stroke>() },
        { DrawingAttributeIds.StylusHeight, new List<Stroke>() },
        { DrawingAttributeIds.StylusTip, new List<Stroke>() },
        { DrawingAttributeIds.StylusTipTransform, new List<Stroke>() },
        { DrawingAttributeIds.StylusWidth, new List<Stroke>() }
    };

    /// <summary>笔画属性变化追踪（ICA Stroke_DrawingAttributesChanged 原样）：粗细/改色先入累加器，操作结束统一提交。</summary>
    private void Stroke_DrawingAttributesChanged(object sender, PropertyDataChangedEventArgs e)
    {
        var key = sender as Stroke;
        if (key == null) return;
        if (_strokePage.TryGetValue(key, out var attrPage)) MarkPdfDirty(attrPage);   // 改色/粗细不走 StrokesChanged，不标就丢（D3）
        // 克隆手势中的克隆笔画不记属性历史：撤销语义 = 整体删除克隆，不单步回退
        if (StrokesSelectionClone.Count != 0 && StrokesSelectionClone.Contains(key)) return;
        var currentValue = key.DrawingAttributes.Clone();
        DrawingAttributesHistory.TryGetValue(key, out var previousTuple);
        var previousValue = previousTuple?.Item1 ?? currentValue.Clone();
        var needUpdateValue = !DrawingAttributesHistoryFlag[e.PropertyGuid].Contains(key);
        if (needUpdateValue)
        {
            DrawingAttributesHistoryFlag[e.PropertyGuid].Add(key);
        }
        if (e.PropertyGuid == DrawingAttributeIds.Color && needUpdateValue)
        {
            previousValue.Color = (Color)e.PreviousValue;
        }
        if (e.PropertyGuid == DrawingAttributeIds.IsHighlighter && needUpdateValue)
        {
            previousValue.IsHighlighter = (bool)e.PreviousValue;
        }
        if (e.PropertyGuid == DrawingAttributeIds.StylusHeight && needUpdateValue)
        {
            previousValue.Height = (double)e.PreviousValue;
        }
        if (e.PropertyGuid == DrawingAttributeIds.StylusWidth && needUpdateValue)
        {
            previousValue.Width = (double)e.PreviousValue;
        }
        if (e.PropertyGuid == DrawingAttributeIds.StylusTip && needUpdateValue)
        {
            previousValue.StylusTip = (StylusTip)e.PreviousValue;
        }
        if (e.PropertyGuid == DrawingAttributeIds.StylusTipTransform && needUpdateValue)
        {
            previousValue.StylusTipTransform = (Matrix)e.PreviousValue;
        }
        if (e.PropertyGuid == DrawingAttributeIds.DrawingFlags && needUpdateValue)
        {
            previousValue.IgnorePressure = (bool)e.PreviousValue;
        }
        DrawingAttributesHistory[key] = new Tuple<DrawingAttributes, DrawingAttributes>(previousValue, currentValue);
    }

    /// <summary>选择变换点集追踪（ICA Stroke_StylusPointsChanged 原样，inkCanvas → _selectionInkCanvas）：
    /// 变换中的笔画数达到选区/全页笔画数且无触摸按住时提交一条 Manipulation 历史。</summary>
    private void Stroke_StylusPointsChanged(object? sender, EventArgs e)
    {
        if (!(sender is Stroke stroke)) return;
        if (_strokePage.TryGetValue(stroke, out var dragPage)) MarkPdfDirty(dragPage);   // 选中拖移/变换改点集，不走 StrokesChanged（D3）
        // 克隆笔画的拖移不进 Manipulation 累加器（逐帧 Transform 会刷屏提交，盖住"新增克隆"条目）；
        // 基线跟上当前位置，避免手势结束后首次移动该克隆撤销回退到拖前位置
        if (StrokesSelectionClone.Count != 0 && StrokesSelectionClone.Contains(stroke))
        {
            StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
            return;
        }
        var selectedStrokes = _selectionInkCanvas?.GetSelectedStrokes();
        var count = selectedStrokes?.Count ?? 0;
        if (count == 0 && _selectionInkCanvas != null) count = _selectionInkCanvas.Strokes.Count;
        if (StrokeManipulationHistory == null)
        {
            StrokeManipulationHistory = new Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>>();
        }
        if (!StrokeInitialHistory.TryGetValue(stroke, out var initial))
            return;
        StrokeManipulationHistory[stroke] =
            new Tuple<StylusPointCollection, StylusPointCollection>(initial, stroke.StylusPoints.Clone());
        if ((StrokeManipulationHistory.Count == count || sender == null) && dec.Count == 0)
        {
            _viewModel.InkTool.TimeMachine.CommitStrokeManipulationHistory(StrokeManipulationHistory);
            foreach (var item in StrokeManipulationHistory)
            {
                StrokeInitialHistory[item.Key] = item.Value.Item2;
            }
            StrokeManipulationHistory = null;
        }
    }

    private void Stroke_StylusPointsReplaced(object sender, StylusPointsReplacedEventArgs e)
    {
        if (sender is Stroke stroke)
        {
            if (_strokePage.TryGetValue(stroke, out var replacedPage)) MarkPdfDirty(replacedPage);   // 同上（D3）
            StrokeInitialHistory[stroke] = e.NewStylusPoints.Clone();
        }
    }

    /// <summary>有待提交的笔画属性历史时统一提交并复位标志（ICA DrawingAttributesHistory 提交段等价）。</summary>
    private void CommitDrawingAttributesHistoryIfAny()
    {
        if (DrawingAttributesHistory.Count > 0)
        {
            _viewModel.InkTool.TimeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
            DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
            foreach (var item in DrawingAttributesHistoryFlag)
            {
                item.Value.Clear();
            }
        }
    }

    // ────────── 页面遮罩定位辅助（PDF 多页适配）──────────

    /// <summary>取页面画布所在页 Grid 内的选择遮罩（遮罩在 DataTemplate 内逐页实例化，按 Name 匹配）。</summary>
    private static Grid? GetPageSelectionCover(InkCanvas inkCanvas)
        => inkCanvas.Parent is Panel host
            ? host.Children.OfType<Grid>().FirstOrDefault(c => c.Name == "GridInkCanvasSelectionCover")
            : null;

    private static void ShowPageSelectionCover(InkCanvas inkCanvas)
    {
        if (GetPageSelectionCover(inkCanvas) is { } cover)
            cover.Visibility = Visibility.Visible;
    }

    private static void HidePageSelectionCover(InkCanvas inkCanvas)
    {
        if (GetPageSelectionCover(inkCanvas) is { } cover)
            cover.Visibility = Visibility.Collapsed;
    }

    /// <summary>是否任意页面有选区遮罩可见（捏合缩放守卫用：双指让位给选区变换，对齐 ICA）。</summary>
    private bool IsAnySelectionCoverVisible
        => _pageInkCanvases.Any(c => GetPageSelectionCover(c)?.Visibility == Visibility.Visible);

    /// <summary>按笔画反查所属页笔画集合（撤销回放用：Manipulation/DrawingAttributes 条目不带 TargetStrokes）。</summary>
    private StrokeCollection? ResolveStrokeCollection(Stroke stroke)
        => _trackedStrokePages.Keys.FirstOrDefault(k => k.Contains(stroke));

    /// <summary>清空所有页面选区并隐藏选择 UI，复位全部选择状态（切出选择工具时调用）。</summary>
    private void ClearPdfSelection()
    {
        foreach (var canvas in _pageInkCanvases.ToList())
        {
            try
            {
                canvas.Select(new StrokeCollection());
            }
            catch
            {
                // 已卸载/分离画布可能抛异常：选区清理失败也不得中断后续遮罩隐藏
            }
            HidePageSelectionCover(canvas);
        }
        _selectionInkCanvas = null;
        BorderStrokeSelectionControl.Visibility = Visibility.Collapsed;
        StrokeManipulationHistory = null;
        // 克隆模式与手势状态全套复位（ICA 切工具即退出选择语义）
        isStrokeSelectionCloneOn = false;
        ToggleButtonStrokeSelectionClone.IsChecked = false;
        IconStrokeSelectionClone.SetResourceReference(TextBlock.ForegroundProperty, "FloatBarForeground");
        StrokesSelectionClone = new StrokeCollection();
        ImagesSelectionClone = new List<Image>();
        dec.Clear();
        isGridInkCanvasSelectionCoverMouseDown = false;
        isProgramChangeStrokeSelection = false;
        lastTouchPointOnGridInkCanvasCover = new Point(0, 0);
    }

    /// <summary>强制同步所有页面画布的 EditingMode（工具切换确定性兜底，不依赖绑定更新时序）：
    /// 值不一致的画布按 CommitPendingPdfStroke 同款"清绑定→赋值→还原绑定"手法处理。</summary>
    private void SyncPageEditingModes()
    {
        var desired = _viewModel.InkTool.EditingMode;
        var modeProperty = InkCanvas.EditingModeProperty;
        foreach (var canvas in _pageInkCanvases)
        {
            if (canvas.EditingMode == desired)
                continue;
            var binding = BindingOperations.GetBindingExpression(canvas, modeProperty);
            if (binding != null)
                BindingOperations.ClearBinding(canvas, modeProperty);
            canvas.EditingMode = desired;
            if (binding != null)
                BindingOperations.SetBinding(canvas, modeProperty, binding.ParentBinding);
        }
    }
}

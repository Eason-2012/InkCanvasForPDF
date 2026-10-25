#nullable disable warnings

using InkCanvasForPDF.Controls;
using InkCanvasForPDF.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;

namespace InkCanvasForPDF.Board
{
    /// <summary>画板工具栏处理器（移植自 ICA MW_FloatBarIcons.cs / MW_Settings.cs，
    /// 浮动栏与 PPT 引用按画板独立运行适配；未移植功能的入口收敛为隐藏面板）。</summary>
    public partial class BoardView
    {
        // ═══ 撤销/重做/选择（ICA MW_FloatBarIcons.cs:241-303）═══

        private void SymbolIconUndo_Click(object sender, RoutedEventArgs e)
        {
            if (!BoardUndo.IsEnabled) return;
            BtnUndo_Click(null, null);
            HideSubPanels();
        }

        private void SymbolIconRedo_Click(object sender, RoutedEventArgs e)
        {
            if (!BoardRedo.IsEnabled) return;
            BtnRedo_Click(null, null);
            HideSubPanels();
        }

        private void SymbolIconSelect_Click(object sender, RoutedEventArgs e)
        {
            BtnSelect_Click(null, null);
            HideSubPanels("select");
        }

        // ═══ 工具面板/截屏：工具按钮及其面板已按用户要求整体下线（暂时不移植），
        //     SymbolIconTools_Click / SymbolIconScreenshot_Click / MW_SaveAndOpenStrokes.cs 一并移除 ═══

        // ═══ 手势面板开合（改为 WPF Popup，对齐墨迹菜单：Custom 回调居中悬于按钮上方 + FlyIn；未移植功能的入口已随 XAML 一并删除）═══

        private void TwoFingerGestureBorder_Click(object sender, RoutedEventArgs e)
        {
            if (BoardGesturePopup.IsOpen && !PopupFlyAnimator.IsClosing(BoardGesturePopup))
            {
                PopupFlyAnimator.FlyOut(BoardGesturePopup, BoardTwoFingerGestureBorder);
            }
            else
            {
                // 滑出动画进行中再点按钮：中止滑出立即关闭后重开（避免 150ms 内点击无反应的死点）
                if (PopupFlyAnimator.IsClosing(BoardGesturePopup))
                    PopupFlyAnimator.AbortFlyOut(BoardGesturePopup);
                // Popup 脱离中间条 Viewbox，不再继承画板栏缩放档；旧实现在 Viewbox 内还被拉伸
                // （149×135 内容 → 约 190 宽，倍率 190/149≈1.275），此处补回两层，保持与改造前一致
                double s = _boardBarScale * (190.0 / 149.0);
                BoardTwoFingerGestureBorder.LayoutTransform = new ScaleTransform(s, s);
                // 打开前预设动画初态（纯滑入：整块位于终点下方，150ms EaseOut 由快到慢滑入）：
                // Popup 首帧呈现时即为动画起点，不闪终态；Opacity 恒 1 无淡入；
                // FlyIn 由 BoardGesturePopup.Opened 经 Dispatcher 启动（见 BoardView 构造器）
                PopupFlyAnimator.EnsureMeasured(BoardTwoFingerGestureBorder);   // 首开 ActualHeight=0：先同步布局取真实高度
                BoardTwoFingerGestureBorder.RenderTransform = new TranslateTransform(0, BoardTwoFingerGestureBorder.ActualHeight);
                BoardTwoFingerGestureBorder.RenderTransformOrigin = new Point(0.5, 1);
                BoardGesturePopup.IsOpen = true;
            }
        }

        // ═══ 退出画板（ICA ImageBlackboard_Click:313-427，进入分支由 MainWindow.EnterBoard 承担，
        //     退出分支保留自动截图；通知 MainWindow 退出画板模式）═══

        private void ImageBlackboard_Click(object sender, RoutedEventArgs e)
        {
            if (isDisplayingOrHidingBlackboard) return;
            isDisplayingOrHidingBlackboard = true;

            if (inkCanvas.EditingMode == InkCanvasEditingMode.Select) PenIcon_Click(null, null);

            ExitRequested?.Invoke(this, EventArgs.Empty);

            new System.Threading.Thread(new System.Threading.ThreadStart(() =>
            {
                System.Threading.Thread.Sleep(200);
                Application.Current.Dispatcher.Invoke(() => isDisplayingOrHidingBlackboard = false);
            })).Start();
        }

        // ═══ 画笔（ICA PenIcon_Click:721-755，画板元素版）═══

        private void PenIcon_Click(object sender, RoutedEventArgs e)
        {
            // 形状面板打开时：点批注按钮=退出形状并关面板（Pin 固定状态下也关，用户规格）
            if (BoardShapePalette.IsOpen)
            {
                CollapseBorderDrawShape();
                drawingShapeMode = 0;
                isLongPressSelected = false;
            }

            // ICA 画板约定（MW_BoardIcons.cs BoardEraserIcon_Click）：Opacity!=1（0.99）=已激活。
            // 不可用浮动栏的 Background==null 判据——画板元素背景永远非空，恒为假会导致永远只弹面板。
            if (BoardPen.Opacity != 1)
            {
                // 对齐 PDF PenIcon_Click：WPF Popup + Custom 回调定位（居中于墨迹按钮上方 8px），开关即切换，
                // 打开时播放纯滑入动画（150ms EaseOut 由快到慢，整块从下方滑入）
                if (BoardPenPalettePopup.IsOpen && !PopupFlyAnimator.IsClosing(BoardPenPalettePopup))
                {
                    PopupFlyAnimator.FlyOut(BoardPenPalettePopup, BoardPenPalette);
                }
                else
                {
                    // 滑出动画进行中再点按钮：中止滑出立即关闭后重开（避免 150ms 内点击无反应的死点）
                    if (PopupFlyAnimator.IsClosing(BoardPenPalettePopup))
                        PopupFlyAnimator.AbortFlyOut(BoardPenPalettePopup);
                    // 互斥：打开墨迹菜单时关掉橡皮菜单（避免双菜单叠加，照 PDF PenIcon_Click）
                    if (BoardEraserPalette.IsOpen)
                        PopupFlyAnimator.FlyOut(BoardEraserPalette, BoardEraserPaletteRoot);
                    // 打开前预设动画初态（纯滑入：整块位于终点下方，150ms EaseOut 由快到慢滑入）：
                    // Popup 首帧呈现时即为动画起点，不闪终态；Opacity 恒 1 无淡入；
                    // FlyIn 由 BoardPenPalettePopup.Opened 经 Dispatcher 启动（见 BoardView 构造器）
                    PopupFlyAnimator.EnsureMeasured(BoardPenPalette);   // 首开 ActualHeight=0：先同步布局取真实高度
                    BoardPenPalette.RenderTransform = new TranslateTransform(0, BoardPenPalette.ActualHeight);
                    BoardPenPalette.RenderTransformOrigin = new Point(0.5, 1);
                    BoardPenPalettePopup.IsOpen = true;
                }
            }
            else
            {
                drawingShapeMode = 0;   // ICA BtnPen_Click:107：切回画笔复位形状模式
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;

                Main_Grid.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));

                inkCanvas.IsHitTestVisible = true;
                inkCanvas.Visibility = Visibility.Visible;

                GridBackgroundCoverHolder.Visibility = Visibility.Visible;

                GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                ColorSwitchCheck();
                HideSubPanels("pen", true);
            }
        }

        // ═══ 主题切换（ICA ColorThemeSwitch_MouseUp:757-765）═══

        private void ColorThemeSwitch_MouseUp(object sender, MouseButtonEventArgs e)
        {
            isUselightThemeColor = !isUselightThemeColor;
            if (currentMode == 0)
            {
                isDesktopUselightThemeColor = isUselightThemeColor;
            }
            CheckColorTheme();
        }

        // ═══ 橡皮（ICA EraserIcon_Click:767-795 / EraserIconByStrokes_Click:797-810，画板元素版）═══

        private void EraserIcon_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            forcePointEraser = true;
            // 直径取 InkCanvasForPDF 设置（EraserSizeSetting 经 15+3.5x 换算，由 MainWindow 注入），与 PDF 批注侧一致；黑板擦 38:56 矩形
            inkCanvas.EraserShape = new RectangleStylusShape(EraserDiameter, InkCircleCursor.EraserBoardHeight(EraserDiameter));
            inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            drawingShapeMode = 0;

            inkCanvas_EditingModeChanged(inkCanvas, null);
            CancelSingleFingerDragMode();

            HideSubPanels("eraser");
        }

        private void EraserIconByStrokes_Click(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            forcePointEraser = false;

            inkCanvas.EraserShape = new EllipseStylusShape(5, 5);
            inkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            drawingShapeMode = 0;

            inkCanvas_EditingModeChanged(inkCanvas, null);
            CancelSingleFingerDragMode();

            HideSubPanels("eraserByStrokes");
        }

        // ═══ 选择（ICA SelectIcon_MouseUp:818-839）═══

        private void SelectIcon_MouseUp(object sender, RoutedEventArgs e)
        {
            forceEraser = true;
            drawingShapeMode = 0;
            inkCanvas.IsManipulationEnabled = false;
            if (inkCanvas.EditingMode == InkCanvasEditingMode.Select)
            {
                StrokeCollection selectedStrokes = new StrokeCollection();
                foreach (Stroke stroke in inkCanvas.Strokes)
                {
                    if (stroke.GetBounds().Width > 0 && stroke.GetBounds().Height > 0)
                    {
                        selectedStrokes.Add(stroke);
                    }
                }
                inkCanvas.Select(selectedStrokes);
            }
            else
            {
                inkCanvas.EditingMode = InkCanvasEditingMode.Select;
            }
        }

        // ═══ 画板工具面板开关（ICA MW_Settings.cs 移植，画板命名单实例）═══

        /// <summary>停顿纠正开关（硬笔/软笔两个页签，全局共享）：镜像另一个页签并回流主设置（MainWindow 落盘）→ 不关墨迹菜单（点击开关保持菜单打开）。</summary>
        private void ToggleSwitchEnablePauseInkToShape_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            if (sender is ToggleSwitch src)
            {
                if (ToggleSwitchEnablePauseInkToShape is { } hard) hard.IsOn = src.IsOn;
                if (ToggleSwitchEnablePauseInkToShapeSoft is { } soft) soft.IsOn = src.IsOn;
                RaisePauseInkToShapeChanged(src.IsOn);
            }
        }

        /// <summary>墨迹纠正开关（硬笔/软笔两个页签，全局共享）：镜像另一个页签并回流主设置（MainWindow 落盘）→ 不关墨迹菜单（与停顿纠正同款，保持菜单打开）。</summary>
        private void ToggleSwitchEnableInkToShape_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            if (sender is ToggleSwitch src)
            {
                if (ToggleSwitchEnableInkToShape is { } hard) hard.IsOn = src.IsOn;
                if (ToggleSwitchEnableInkToShapeSoft is { } soft) soft.IsOn = src.IsOn;
                RaiseInkToShapeChanged(src.IsOn);
            }
        }

        private void ToggleSwitchEnableMultiTouchMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;   // 构造器回填 IsOn 时不触运行时（由 BoardView 构造器尾部统一恢复，与旁系处理器一致）
            if (ToggleSwitchEnableMultiTouchMode.IsOn)
            {
                if (!isInMultiTouchMode) BorderMultiTouchMode_MouseUp(null, null);
            }
            else
            {
                if (isInMultiTouchMode) BorderMultiTouchMode_MouseUp(null, null);
            }
            Settings.Gesture.IsEnableMultiTouchMode = ToggleSwitchEnableMultiTouchMode.IsOn;
            SaveSettingsToFile();
            CheckEnableTwoFingerGestureBtnColorPrompt();   // ICA MW_Settings.cs:550-608 同款调用
        }

        private void ToggleSwitchEnableTwoFingerZoom_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Gesture.IsEnableTwoFingerZoom = ToggleSwitchEnableTwoFingerZoom.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerTranslate_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Gesture.IsEnableTwoFingerTranslate = ToggleSwitchEnableTwoFingerTranslate.IsOn;
            SaveSettingsToFile();
        }

        private void ToggleSwitchEnableTwoFingerRotation_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Gesture.IsEnableTwoFingerRotation = ToggleSwitchEnableTwoFingerRotation.IsOn;
            SaveSettingsToFile();
        }

        // 墨迹识别开关已随 XAML 删除（未移植功能），ToggleSwitchEnableInkToShape_Toggled 一并移除
    }
}

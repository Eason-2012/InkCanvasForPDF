#nullable disable warnings

using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.Controls;
using InkCanvasForPDF.Services;
using InkCanvasForPDF.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Diagnostics;
using Application = System.Windows.Application;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        private void BoardChangeBackgroundColorBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            Settings.Canvas.UsingWhiteboard = !Settings.Canvas.UsingWhiteboard;
            SaveSettingsToFile();
            if (Settings.Canvas.UsingWhiteboard)
            {
                if (inkColor == 5) lastBoardInkColor = 0;
            }
            else
            {
                if (inkColor == 0) lastBoardInkColor = 5;
            }
            ComboBoxTheme_SelectionChanged(null, null);
            CheckColorTheme(true);
            // 画布黑白互换后同步硬/软笔记忆色（黑底存白 5、白底存黑 0；荧光笔色不受画布影响）
            if (boardInkType <= 1)
            {
                SaveActiveColorToStore();
                SaveSettingsToFile();
            }
            if (BoardPen.Opacity == 1)
            {
                BoardPen.Background = BoardBarBackgroundBrush;
            }
            if (BoardEraser.Opacity == 1)
            {
                BoardEraser.Background = BoardBarBackgroundBrush;
            }
            if (BoardSelect.Opacity == 1)
            {
                BoardSelect.Background = BoardBarBackgroundBrush;
            }
        }

        /// <summary>橡皮按钮（PDF EraserIcon_Click 两段式）：未激活→只切橡皮工具（按持久化的面积擦/墨迹擦模式，不弹菜单）；
        /// 已激活→切换橡皮选项 Popup 开关（菜单与 PDF EraserPalette 一模一样，只套画板栏缩放档、不套 0.65）。</summary>
        private void BoardEraserIcon_Click(object sender, RoutedEventArgs e)
        {
            if (BoardEraser.Opacity == 1)   // 未激活（高亮态=0.99，见 HideSubPanels）→ 只切橡皮，不弹菜单
            {
                ApplyBoardEraserMode();
                HideSubPanels("eraser");
            }
            else if (BoardEraserPalette.IsOpen && !PopupFlyAnimator.IsClosing(BoardEraserPalette))
            {
                PopupFlyAnimator.FlyOut(BoardEraserPalette, BoardEraserPaletteRoot);
            }
            else
            {
                // 滑出动画进行中再点按钮：中止滑出立即关闭后重开（避免 150ms 内点击无反应的死点）
                if (PopupFlyAnimator.IsClosing(BoardEraserPalette))
                    PopupFlyAnimator.AbortFlyOut(BoardEraserPalette);
                // 互斥：打开橡皮菜单时关掉墨迹菜单（避免双菜单叠加，照 PDF PenIcon_Click）
                if (BoardPenPalettePopup.IsOpen)
                    PopupFlyAnimator.FlyOut(BoardPenPalettePopup, BoardPenPalette);
                // 面板跟随画板栏缩放档（Popup 不在 Viewbox 内，需自行应用；不套 PDF 的 0.65）
                BoardEraserPaletteRoot.LayoutTransform = new ScaleTransform(_boardBarScale, _boardBarScale);
                BoardEraserPalette.PlacementTarget = BoardEraser;
                // 打开前回填 UI：Pivot 页签（持久化的面积擦/墨迹擦）、粗细滑块（值=SliderFromEraserSize(EraserSize)）、
                // 手掌擦除/笔尖模式开关（PDF 橡皮菜单共用同一设置，两侧一致）
                if (Vm is { } vm)
                {
                    BoardPivotEraser.SelectedIndex = vm.InkTool.IsStrokeEraser ? 1 : 0;
                    BoardEraserSizeSlider.Value = InkToolViewModel.SliderFromEraserSize(vm.InkTool.EraserSize);
                    _syncingBoardEraserToggles = true;
                    BoardToggleSwitchPalmEraseArea.IsOn = BoardToggleSwitchPalmEraseStroke.IsOn = vm.Settings.IsPalmEraseEnabled;
                    BoardToggleSwitchNibModeArea.IsOn = BoardToggleSwitchNibModeStroke.IsOn = vm.Settings.IsNibModeEnabled;
                    BoardToggleSwitchNibModeArea.IsEnabled = BoardToggleSwitchNibModeStroke.IsEnabled = vm.Settings.IsPalmEraseEnabled;
                    _syncingBoardEraserToggles = false;
                }
                ResetBoardClearInkSlider();
                // 打开前预设动画初态（纯滑入：整块位于终点下方，150ms EaseOut 由快到慢滑入）
                PopupFlyAnimator.EnsureMeasured(BoardEraserPaletteRoot);   // 首开 ActualHeight=0：先同步布局取真实高度
                BoardEraserPaletteRoot.RenderTransform = new TranslateTransform(0, BoardEraserPaletteRoot.ActualHeight);
                BoardEraserPaletteRoot.RenderTransformOrigin = new Point(0.5, 1);
                BoardEraserPalette.IsOpen = true;   // FlyIn 由 Opened 事件经 Dispatcher 启动（见 BoardView 构造器）
            }
        }

        /// <summary>按当前模式（forcePointEraser：true=面积擦，false=墨迹擦）建立橡皮 EraserShape/EditingMode。
        /// 直径取 InkCanvasForPDF 设置（EraserSizeSetting 经 15+3.5x 换算，由 MainWindow 注入），与 PDF 批注侧一致；黑板擦 38:56 矩形。</summary>
        private void ApplyBoardEraserMode()
        {
            forceEraser = true;
            drawingShapeMode = 0;
            // 单一真源 = 共享 VM 的 IsStrokeEraser：应用前重新对齐 forcePointEraser，
            // 保证与 Pivot 页签/UI 一致（与 PDF 侧"VM 唯一真源"语义相同）
            if (Vm is { } v)
                forcePointEraser = !v.InkTool.IsStrokeEraser;
            if (forcePointEraser)
                inkCanvas.EraserShape = new RectangleStylusShape(EraserDiameter, InkCircleCursor.EraserBoardHeight(EraserDiameter));
            else
                inkCanvas.EraserShape = new EllipseStylusShape(5, 5);
            inkCanvas.EditingMode = forcePointEraser
                ? InkCanvasEditingMode.EraseByPoint
                : InkCanvasEditingMode.EraseByStroke;

            inkCanvas_EditingModeChanged(inkCanvas, null);
            CancelSingleFingerDragMode();
        }

        /// <summary>Pivot 页签切换 → 面积擦/墨迹擦：与 PDF 侧共用 InkTool.IsStrokeEraser 与持久化设置；
        /// 橡皮激活时即时生效（菜单仅在激活时可打开；Loaded 回填页签时未激活不应用，避免误切画布模式）。</summary>
        private void BoardPivotEraser_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            bool byStroke = BoardPivotEraser.SelectedIndex == 1;
            if (Vm is { } vm && vm.InkTool.IsStrokeEraser != byStroke)
            {
                vm.InkTool.IsStrokeEraser = byStroke;   // PDF 批注侧实时同步（EditingMode 绑定随之刷新）
                vm.Settings.EraserModeByStrokeSetting = byStroke;
                SettingsService.Save(vm.Settings);
            }
            forcePointEraser = !byStroke;
            if (BoardEraser.Opacity != 1)   // 已激活（0.99）：即时切换 EraserShape/EditingMode，不关菜单；
                ApplyBoardEraserMode();     // 未激活（Loaded 回填页签）不应用，避免误切画布模式
        }

        /// <summary>橡皮粗细滑块（1-30）→ 实际 EraserSize=15+3.5x（MainWindow 监听统一刷两侧 EraserShape/光标/画板直径），
        /// 并回写 EraserSizeSetting 持久化（同值守卫：菜单打开回填滑块不重复存盘）。</summary>
        private void BoardEraserSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Vm is not { } vm) return;
            vm.InkTool.EraserSize = InkToolViewModel.EraserSizeFromSlider(e.NewValue);
            if (vm.Settings.EraserSizeSetting != (int)e.NewValue)
            {
                vm.Settings.EraserSizeSetting = (int)e.NewValue;
                SettingsService.Save(vm.Settings);
            }
        }

        // ────────── 手掌擦除 / 笔尖模式（画板橡皮菜单两页签各一份开关；逻辑与 PDF InkToolBar 同名处理器逐字一致）──────────

        /// <summary>回填/互同步重入守卫（菜单打开回填与页签互同步时不写回设置）。</summary>
        private bool _syncingBoardEraserToggles;

        /// <summary>手掌擦除开关（面积擦/墨迹擦两页签共享）：写设置 → 持久化 → 互同步两开关 →
        /// 联动笔尖模式开关的可操作性（手掌擦除关则笔尖模式不可点；值不清零，重开后恢复原选择）。</summary>
        private void BoardToggleSwitchPalmErase_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingBoardEraserToggles) return;
            if (sender is not iNKORE.UI.WPF.Modern.Controls.ToggleSwitch ts || Vm is not { } vm) return;

            if (vm.Settings.IsPalmEraseEnabled != ts.IsOn)
            {
                vm.Settings.IsPalmEraseEnabled = ts.IsOn;
                SettingsService.Save(vm.Settings);
            }

            _syncingBoardEraserToggles = true;
            if (BoardToggleSwitchPalmEraseArea is { } pa) pa.IsOn = ts.IsOn;
            if (BoardToggleSwitchPalmEraseStroke is { } ps) ps.IsOn = ts.IsOn;
            if (BoardToggleSwitchNibModeArea is { } na) na.IsEnabled = ts.IsOn;
            if (BoardToggleSwitchNibModeStroke is { } ns) ns.IsEnabled = ts.IsOn;
            _syncingBoardEraserToggles = false;
        }

        /// <summary>笔尖模式开关（面积擦/墨迹擦两页签共享）：写设置 → 持久化 → 互同步两开关。
        /// 只改手掌识别阈值（笔尖档/手指档），手掌擦除关闭时该开关被禁用、不会走到这里。</summary>
        private void BoardToggleSwitchNibMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingBoardEraserToggles) return;
            if (sender is not iNKORE.UI.WPF.Modern.Controls.ToggleSwitch ts || Vm is not { } vm) return;

            if (vm.Settings.IsNibModeEnabled != ts.IsOn)
            {
                vm.Settings.IsNibModeEnabled = ts.IsOn;
                SettingsService.Save(vm.Settings);
            }

            _syncingBoardEraserToggles = true;
            if (BoardToggleSwitchNibModeArea is { } na) na.IsOn = ts.IsOn;
            if (BoardToggleSwitchNibModeStroke is { } ns) ns.IsOn = ts.IsOn;
            _syncingBoardEraserToggles = false;
        }

        // ────────── 滑动清空滑块（照抄 PDF InkToolBar 完整确认样式：拖到底松手清空当前画板后直接回左）──────────

        /// <summary>滑块行程：(轨道160 - 边框2) 内部158 - 滑块40 - 左右内边距各4 = 110（抵到右端时右侧间隙与左侧 4px 相等）。</summary>
        private const double BoardClearInkMaxX = 110;

        private double _boardClearInkX;                                   // 当前位移
        private readonly TranslateTransform _boardClearInkTransform = new();   // 拖动/回弹统一作用的变换

        private void BoardClearInkThumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            // 回弹动画中途抓取：DP 读回动画当前值 → 摘动画 → 基值对齐（避免跳变）
            _boardClearInkX = Math.Clamp(_boardClearInkTransform.X, 0, BoardClearInkMaxX);
            BoardClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
            _boardClearInkTransform.X = _boardClearInkX;
        }

        private void BoardClearInkThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            _boardClearInkX = Math.Clamp(_boardClearInkX + e.HorizontalChange, 0, BoardClearInkMaxX);
            _boardClearInkTransform.X = _boardClearInkX;
            // 文字：滑块一动即快速淡出（12px 内隐没）
            BoardClearInkLabel.Opacity = Math.Max(0, 1 - _boardClearInkX / 12);
        }

        private void BoardClearInkThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_boardClearInkX >= BoardClearInkMaxX * 0.95)
                BoardCompleteClearInk();
            else
                AnimateBoardClearInkBack();
        }

        /// <summary>键盘可达：方向键步进（行程/10）、Home 归零、End 直达即清空。</summary>
        private void BoardClearInkThumb_KeyDown(object sender, KeyEventArgs e)
        {
            double step = BoardClearInkMaxX / 10;
            switch (e.Key)
            {
                case Key.Right:
                case Key.Up:
                    _boardClearInkX = Math.Min(BoardClearInkMaxX, _boardClearInkX + step);
                    break;
                case Key.Left:
                case Key.Down:
                    _boardClearInkX = Math.Max(0, _boardClearInkX - step);
                    break;
                case Key.Home:
                    _boardClearInkX = 0;
                    break;
                case Key.End:
                    _boardClearInkX = BoardClearInkMaxX;
                    break;
                default:
                    return;
            }
            e.Handled = true;
            BoardClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
            _boardClearInkTransform.X = _boardClearInkX;
            BoardClearInkLabel.Opacity = Math.Max(0, 1 - _boardClearInkX / 12);
            if (_boardClearInkX >= BoardClearInkMaxX)
                BoardCompleteClearInk();
        }

        /// <summary>滑到底：清空当前画板墨迹（画板清屏统一入口：选区删除 / 自动截图分支 + BtnClear_Click），
        /// 随即回左；不切回笔模式、不关菜单（与 PDF CompleteClearInk 一致）。</summary>
        private void BoardCompleteClearInk()
        {
            SymbolIconDelete_MouseUp(null, null);
            AnimateBoardClearInkBack();
        }

        /// <summary>回弹到初始位：文字立即复原，滑块 160ms EaseOut 滑回（无淡入）。</summary>
        private void AnimateBoardClearInkBack()
        {
            double from = _boardClearInkX;
            _boardClearInkX = 0;
            BoardClearInkLabel.Opacity = 1;
            if (from <= 0)
            {
                _boardClearInkTransform.X = 0;
                return;
            }
            BoardClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
            _boardClearInkTransform.X = 0;   // 基值归零：动画结束后回落正确
            var anim = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (_, _) => BoardClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
            BoardClearInkThumb.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        /// <summary>打开菜单前复位滑块（拖动中途关菜单的兜底）。</summary>
        private void ResetBoardClearInkSlider()
        {
            _boardClearInkX = 0;
            BoardClearInkThumb.BeginAnimation(TranslateTransform.XProperty, null);
            _boardClearInkTransform.X = 0;
            BoardClearInkLabel.Opacity = 1;
        }
    }
}

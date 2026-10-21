#nullable disable warnings

using InkCanvasForPDF.Board.Helpers;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        int inkColor = 1;

        // 墨迹菜单当前页签（0=硬笔 1=软笔 2=荧光笔）；记忆值直接存 Settings.Canvas（LastPenKind/Pen*/Highlighter*，硬/软笔共用 Pen*）
        int boardInkType = 0;

        private void InkWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isLoaded) return;
            BoardInkWidthSlider.Value = ((Slider)sender).Value;
            Settings.Canvas.InkWidth = ((Slider)sender).Value / 2;
            SaveActiveWidthToStore();
            if (inkColor > 100)
            {
                drawingAttributes.Height = 30 + ((Slider)sender).Value;
                drawingAttributes.Width = 30 + ((Slider)sender).Value;
            }
            else
            {
                drawingAttributes.Height = ((Slider)sender).Value / 2;
                drawingAttributes.Width = ((Slider)sender).Value / 2;
            }
            SaveSettingsToFile();
        }

        private void ColorSwitchCheck()
        {
            forceEraser = false;
            HideSubPanels("color");
            if (Main_Grid.Background == Brushes.Transparent)
            {
                if (currentMode == 1)
                {
                    currentMode = 0;
                    GridBackgroundCover.Visibility = Visibility.Collapsed;
                    AnimationsHelper.HideWithSlideAndFade(BlackboardLeftSide);
                    AnimationsHelper.HideWithSlideAndFade(BlackboardCenterSide);
                    AnimationsHelper.HideWithSlideAndFade(BlackboardRightSide);
                }
                BtnHideInkCanvas_Click(null, null);
            }

            StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
            if (strokes.Count != 0)
            {
                foreach (Stroke stroke in strokes)
                {
                    try
                    {
                        stroke.DrawingAttributes.Color = inkCanvas.DefaultDrawingAttributes.Color;
                    }
                    catch { }
                }
            }
            if (DrawingAttributesHistory.Count > 0)
            {
                timeMachine.CommitStrokeDrawingAttributesHistory(DrawingAttributesHistory);
                DrawingAttributesHistory = new Dictionary<Stroke, Tuple<DrawingAttributes, DrawingAttributes>>();
                foreach (var item in DrawingAttributesHistoryFlag)
                {
                    item.Value.Clear();
                }
            }
            else
            {
                inkCanvas.IsManipulationEnabled = true;
                drawingShapeMode = 0;
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                CancelSingleFingerDragMode();
                forceEraser = false;
                CheckColorTheme();
            }

            isLongPressSelected = false;
        }

        bool isUselightThemeColor = false, isDesktopUselightThemeColor = false;
        int lastDesktopInkColor = 1, lastBoardInkColor = 5;
        Dictionary<int, Color> inkColorLightThemeMapping = new Dictionary<int, Color>
        {
            { 0, Colors.Black }, // Black
            { 1, Color.FromRgb(239, 68, 68) }, // Red
            { 2, Color.FromRgb(34, 197, 94) }, // Green
            { 3, Color.FromRgb(59, 130, 246) }, // Blue
            { 4, Color.FromRgb(250, 204, 21) }, // Yellow
            { 5, Colors.White }, // White
            { 6, Color.FromRgb(236, 72, 153) }, // Pink
            { 7, Color.FromRgb(20, 184, 166) }, // Teal
            { 8, Color.FromRgb(249, 115, 22) }, // Orange
        };
        Dictionary<int, Color> inkColorDarkThemeMapping = new Dictionary<int, Color>
        {
            { 0, Colors.Black }, // Black
            { 1, Color.FromRgb(220, 38, 38) }, // Red
            { 2, Color.FromRgb(22, 163, 74) }, // Green
            { 3, Color.FromRgb(37, 99, 235) }, // Blue
            { 4, Color.FromRgb(234, 179, 8) }, // Yellow
            { 5, Colors.White }, // White
            { 6, Color.FromRgb(147, 51, 234) }, // Pink (Purple)
            { 7, Color.FromRgb(13, 148, 136) }, // Teal
            { 8, Color.FromRgb(234, 88, 12) }  // Orange
        };

        private void CheckColorTheme(bool changeColorTheme = false)
        {
            if (changeColorTheme)
            {
                if (currentMode != 0)
                {
                    if (Settings.Canvas.UsingWhiteboard)
                    {
                        GridBackgroundCover.Background = new SolidColorBrush(StringToColor("#FFF2F2F2"));
                        isUselightThemeColor = false;
                    }
                    else
                    {
                        GridBackgroundCover.Background = new SolidColorBrush(StringToColor("#FF1F1F1F"));
                        isUselightThemeColor = true;
                    }
                }
            }

            if (currentMode == 0)
            {
                isUselightThemeColor = isDesktopUselightThemeColor;
                inkColor = lastDesktopInkColor;
            }
            else
            {
                inkColor = lastBoardInkColor;
            }

            if (inkColor == 101)
            { // Highlighter Red
                inkCanvas.DefaultDrawingAttributes.Color = Color.FromRgb(220, 38, 38);
            }
            else if (inkColor == 102)
            { // Highlighter Orange
                inkCanvas.DefaultDrawingAttributes.Color = Color.FromRgb(234, 88, 12);
            }
            else if (inkColor == 103)
            { // Highlighter Yellow
                inkCanvas.DefaultDrawingAttributes.Color = Color.FromRgb(234, 179, 8);
            }
            else if (inkColor == 104)
            { // Highlighter Teal
                inkCanvas.DefaultDrawingAttributes.Color = Color.FromRgb(13, 148, 136);
            }
            else if (inkColor == 105)
            { // Highlighter Blue
                inkCanvas.DefaultDrawingAttributes.Color = Color.FromRgb(37, 99, 235);
            }
            else if (inkColor == 106)
            { // Highlighter Purple
                inkCanvas.DefaultDrawingAttributes.Color = Color.FromRgb(147, 51, 234);
            }
            else if (isUselightThemeColor)
            {
                inkCanvas.DefaultDrawingAttributes.Color = inkColorLightThemeMapping[inkColor];
            }
            else
            {
                inkCanvas.DefaultDrawingAttributes.Color = inkColorDarkThemeMapping[inkColor];
            }
            if (isUselightThemeColor)
            { // 亮系
                // Red
                BoardBorderPenColorRed.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                BoardBorderPenColorRed.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                // Green
                BoardBorderPenColorGreen.Background = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                BoardBorderPenColorGreen.Background = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                // Blue
                BoardBorderPenColorBlue.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                BoardBorderPenColorBlue.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                // Yellow
                BoardBorderPenColorYellow.Background = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                BoardBorderPenColorYellow.Background = new SolidColorBrush(Color.FromRgb(250, 204, 21));
                // Pink ( Purple )
                BoardBorderPenColorPink.Background = new SolidColorBrush(Color.FromRgb(236, 72, 153));
                BoardBorderPenColorPink.Background = new SolidColorBrush(Color.FromRgb(236, 72, 153));
                // Teal
                BoardBorderPenColorTeal.Background = new SolidColorBrush(Color.FromRgb(20, 184, 166));
                BoardBorderPenColorTeal.Background = new SolidColorBrush(Color.FromRgb(20, 184, 166));
                // Orange
                BoardBorderPenColorOrange.Background = new SolidColorBrush(Color.FromRgb(249, 115, 22));

                BoardColorThemeSwitchIcon.Glyph = "\uE708";
                BoardColorThemeSwitchTextBlock.Text = "暗系";
            }
            else
            { // 暗系
                // Red
                BoardBorderPenColorRed.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                BoardBorderPenColorRed.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                // Green
                BoardBorderPenColorGreen.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                BoardBorderPenColorGreen.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                // Blue
                BoardBorderPenColorBlue.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                BoardBorderPenColorBlue.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                // Yellow
                BoardBorderPenColorYellow.Background = new SolidColorBrush(Color.FromRgb(234, 179, 8));
                BoardBorderPenColorYellow.Background = new SolidColorBrush(Color.FromRgb(234, 179, 8));
                // Pink ( Purple )
                BoardBorderPenColorPink.Background = new SolidColorBrush(Color.FromRgb(147, 51, 234));
                BoardBorderPenColorPink.Background = new SolidColorBrush(Color.FromRgb(147, 51, 234));
                // Teal
                BoardBorderPenColorTeal.Background = new SolidColorBrush(Color.FromRgb(13, 148, 136));
                BoardBorderPenColorTeal.Background = new SolidColorBrush(Color.FromRgb(13, 148, 136));
                // Orange
                BoardBorderPenColorOrange.Background = new SolidColorBrush(Color.FromRgb(234, 88, 12));
                BoardBorderPenColorOrange.Background = new SolidColorBrush(Color.FromRgb(234, 88, 12));


                BoardColorThemeSwitchIcon.Glyph = "\uE706";
                BoardColorThemeSwitchTextBlock.Text = "亮系";
            }

            // 软笔页签色板/亮暗切换钮同步重绘（与硬笔共用亮暗系映射）
            foreach (var kv in new Dictionary<int, Border?>
            {
                { 0, BoardBorderPenColorBlackSoft }, { 5, BoardBorderPenColorWhiteSoft }, { 1, BoardBorderPenColorRedSoft },
                { 6, BoardBorderPenColorPinkSoft }, { 8, BoardBorderPenColorOrangeSoft }, { 4, BoardBorderPenColorYellowSoft },
                { 2, BoardBorderPenColorGreenSoft }, { 7, BoardBorderPenColorTealSoft }, { 3, BoardBorderPenColorBlueSoft },
            })
            {
                if (kv.Value != null)
                {
                    kv.Value.Background = new SolidColorBrush((isUselightThemeColor ? inkColorLightThemeMapping : inkColorDarkThemeMapping)[kv.Key]);
                }
            }
            BoardColorThemeSwitchIconSoft.Glyph = BoardColorThemeSwitchIcon.Glyph;
            BoardColorThemeSwitchTextBlockSoft.Text = BoardColorThemeSwitchTextBlock.Text;

            if (drawingAttributes != null && isLoaded)
            {
                if (inkColor > 100)
                {
                    drawingAttributes.Height = 30 + Settings.Canvas.InkWidth * 2;   // 对齐 ICA：实际宽 = 30 + 滑块值（滑块值 = InkWidth×2）
                    drawingAttributes.Width = 30 + Settings.Canvas.InkWidth * 2;
                    byte NowR = drawingAttributes.Color.R;
                    byte NowG = drawingAttributes.Color.G;
                    byte NowB = drawingAttributes.Color.B;
                    drawingAttributes.Color = Color.FromArgb((byte)Settings.Canvas.InkAlpha, NowR, NowG, NowB);
                    drawingAttributes.IsHighlighter = true;
                    drawingAttributes.IgnorePressure = true;   // 荧光笔屏蔽压感（等粗绘制）
                }
                else
                {
                    drawingAttributes.Height = Settings.Canvas.InkWidth;
                    drawingAttributes.Width = Settings.Canvas.InkWidth;
                    drawingAttributes.IsHighlighter = false;
                    drawingAttributes.IgnorePressure = false;  // 复位为 WPF 默认（跟随压感）
                }
                RefreshTimePressureArmed();   // 颜色/荧光笔档位切换影响时间笔锋武装状态（inkColor>100 豁免）
            }

            // 改变选中提示
            BoardViewboxBtnColorBlackContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorBlueContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorGreenContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorRedContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorYellowContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorWhiteContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorPinkContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorTealContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorOrangeContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorBlackContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorBlueContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorGreenContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorRedContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorYellowContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorWhiteContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorPinkContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorTealContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnColorOrangeContent.Visibility = Visibility.Collapsed;

            BoardViewboxBtnHighlighterColorRedContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorRedContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorOrangeContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorOrangeContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorYellowContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorYellowContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorTealContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorTealContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorBlueContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorBlueContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorPurpleContent.Visibility = Visibility.Collapsed;
            BoardViewboxBtnHighlighterColorPurpleContent.Visibility = Visibility.Collapsed;
            switch (inkColor)
            {
                case 0:
                    BoardViewboxBtnColorBlackContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorBlackContent.Visibility = Visibility.Visible;
                    break;
                case 1:
                    BoardViewboxBtnColorRedContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorRedContent.Visibility = Visibility.Visible;
                    break;
                case 2:
                    BoardViewboxBtnColorGreenContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorGreenContent.Visibility = Visibility.Visible;
                    break;
                case 3:
                    BoardViewboxBtnColorBlueContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorBlueContent.Visibility = Visibility.Visible;
                    break;
                case 4:
                    BoardViewboxBtnColorYellowContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorYellowContent.Visibility = Visibility.Visible;
                    break;
                case 5:
                    BoardViewboxBtnColorWhiteContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorWhiteContent.Visibility = Visibility.Visible;
                    break;
                case 6:
                    BoardViewboxBtnColorPinkContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorPinkContent.Visibility = Visibility.Visible;
                    break;
                case 7:
                    BoardViewboxBtnColorTealContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorTealContent.Visibility = Visibility.Visible;
                    break;
                case 8:
                    BoardViewboxBtnColorOrangeContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnColorOrangeContent.Visibility = Visibility.Visible;
                    break;
                case 101:
                    BoardViewboxBtnHighlighterColorRedContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnHighlighterColorRedContent.Visibility = Visibility.Visible;
                    break;
                case 102:
                    BoardViewboxBtnHighlighterColorOrangeContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnHighlighterColorOrangeContent.Visibility = Visibility.Visible;
                    break;
                case 103:
                    BoardViewboxBtnHighlighterColorYellowContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnHighlighterColorYellowContent.Visibility = Visibility.Visible;
                    break;
                case 104:
                    BoardViewboxBtnHighlighterColorTealContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnHighlighterColorTealContent.Visibility = Visibility.Visible;
                    break;
                case 105:
                    BoardViewboxBtnHighlighterColorBlueContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnHighlighterColorBlueContent.Visibility = Visibility.Visible;
                    break;
                case 106:
                    BoardViewboxBtnHighlighterColorPurpleContent.Visibility = Visibility.Visible;
                    BoardViewboxBtnHighlighterColorPurpleContent.Visibility = Visibility.Visible;
                    break;
            }

            // 软笔页签勾标同步（画笔色与硬笔页签同色种；荧光笔仅硬笔 switch 处理、软签无荧光笔色块）
            if (inkColor <= 100)
            {
                BoardViewboxBtnColorBlackSoftContent.Visibility = BoardViewboxBtnColorBlackContent.Visibility;
                BoardViewboxBtnColorWhiteSoftContent.Visibility = BoardViewboxBtnColorWhiteContent.Visibility;
                BoardViewboxBtnColorRedSoftContent.Visibility = BoardViewboxBtnColorRedContent.Visibility;
                BoardViewboxBtnColorPinkSoftContent.Visibility = BoardViewboxBtnColorPinkContent.Visibility;
                BoardViewboxBtnColorOrangeSoftContent.Visibility = BoardViewboxBtnColorOrangeContent.Visibility;
                BoardViewboxBtnColorYellowSoftContent.Visibility = BoardViewboxBtnColorYellowContent.Visibility;
                BoardViewboxBtnColorGreenSoftContent.Visibility = BoardViewboxBtnColorGreenContent.Visibility;
                BoardViewboxBtnColorTealSoftContent.Visibility = BoardViewboxBtnColorTealContent.Visibility;
                BoardViewboxBtnColorBlueSoftContent.Visibility = BoardViewboxBtnColorBlueContent.Visibility;
            }
        }

        private void CheckLastColor(int inkColor)
        {
            StrokeCollection strokes = inkCanvas.GetSelectedStrokes();
            if (strokes.Count > 0)
            {
                Color targetedColor = inkColorLightThemeMapping[inkColor];
                if (!isUselightThemeColor)
                {
                    inkCanvas.DefaultDrawingAttributes.Color = inkColorDarkThemeMapping[inkColor];
                }
                foreach (Stroke stroke in strokes)
                {
                    stroke.DrawingAttributes.Color = targetedColor;
                }
            }
            else
            {
                if (currentMode == 0)
                {
                    lastDesktopInkColor = inkColor;
                }
                else
                {
                    lastBoardInkColor = inkColor;
                }
                SaveActiveColorToStore(inkColor);   // 按当前墨迹类型写入记忆色（参数遮蔽字段，显式传新色）
                ColorSwitchCheck();
                SaveSettingsToFile();
            }
        }

        private void BtnColorBlack_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(0);
        }

        private void BtnColorRed_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(1);
        }

        private void BtnColorGreen_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(2);
        }

        private void BtnColorBlue_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(3);
        }

        private void BtnColorYellow_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(4);
        }

        private void BtnColorWhite_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(5);
        }

        private void BtnColorPink_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(6);
        }

        private void BtnColorTeal_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(7);
        }

        private void BtnColorOrange_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(8);
        }

        // ────────── 墨迹菜单页签（硬笔/软笔/荧光笔）与按类型记忆 ──────────

        /// <summary>把颜色写入当前墨迹类型的记忆存储（CheckLastColor 传参避免参数遮蔽字段；硬/软笔共用 PenColor）。</summary>
        private void SaveActiveColorToStore(int? color = null)
        {
            int c = color ?? inkColor;
            switch (boardInkType)
            {
                case 0:
                case 1: Settings.Canvas.PenColor = c; break;
                case 2: Settings.Canvas.HighlighterColor = c; break;
            }
        }

        /// <summary>把当前粗细（InkWidth，滑块值一半）写入当前墨迹类型的记忆存储（硬/软笔共用 PenWidth）。</summary>
        private void SaveActiveWidthToStore()
        {
            switch (boardInkType)
            {
                case 0:
                case 1: Settings.Canvas.PenWidth = Settings.Canvas.InkWidth; break;
                case 2: Settings.Canvas.HighlighterWidth = Settings.Canvas.InkWidth; break;
            }
        }

        /// <summary>切换画板墨迹类型（0=硬笔 1=软笔 2=荧光笔）：装入该类型记忆的粗细（硬/软笔共用）；
        /// 硬/软笔颜色装入共享记忆色（PenColor）；按画布黑白覆盖（白底→黑 0、黑底→白 5）只发生在
        /// 进入画板（EnterBoard）与存储值非法时（canvasColorOverride），切页签不覆盖；
        /// 荧光笔装入记忆色；压感随硬/软笔切换（荧光笔保持上次值，其本身豁免时间笔锋武装）。调用方随后 CheckColorTheme 刷新 UI。</summary>
        private void ApplyBoardInkType(int type, bool canvasColorOverride)
        {
            boardInkType = Math.Clamp(type, 0, 2);
            switch (boardInkType)
            {
                case 0:
                case 1: Settings.Canvas.InkWidth = Math.Clamp(Settings.Canvas.PenWidth, 1, 15); break;
                case 2: Settings.Canvas.InkWidth = Math.Clamp(Settings.Canvas.HighlighterWidth, 1, 15); break;
            }
            if (boardInkType == 2)
            {
                int hc = Settings.Canvas.HighlighterColor;
                inkColor = hc >= 101 && hc <= 106 ? hc : 103;
                lastBoardInkColor = inkColor;
            }
            else
            {
                int stored = Settings.Canvas.PenColor;
                if (canvasColorOverride || stored < 0 || stored > 8)
                    inkColor = Settings.Canvas.UsingWhiteboard ? 0 : 5;   // 白底黑笔、黑底白笔
                else
                    inkColor = stored;
                lastBoardInkColor = inkColor;
            }
            if (boardInkType == 0) _pressureEnabled = false;
            else if (boardInkType == 1) _pressureEnabled = true;
            RefreshTimePressureArmed();
        }

        /// <summary>墨迹菜单页签切换：旧类型活动值回写记忆 → 装入新类型（LastPenKind 只记硬/软笔）→
        /// 回填滑块/下拉框 → CheckColorTheme 刷新色板勾标与画笔属性 → 落盘。
        /// 不按画布覆盖颜色：硬/软笔共用 PenColor，切页签保留当前颜色（黑白换色只在进入画板/画布切换时发生）。</summary>
        private void BoardPivotInk_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            int target = BoardPivotInk.SelectedIndex;
            if (target < 0 || target == boardInkType) return;
            SaveActiveColorToStore();
            SaveActiveWidthToStore();
            ApplyBoardInkType(target, canvasColorOverride: false);
            if (boardInkType <= 1) Settings.Canvas.LastPenKind = boardInkType;   // 荧光笔不记录为"上次启用的画笔"
            SyncBoardInkControls();
            CheckColorTheme();
            SaveSettingsToFile();
        }

        /// <summary>把当前活动值回填到墨迹菜单控件（两页签粗细滑块、荧光笔粗细/不透明度）。</summary>
        private void SyncBoardInkControls()
        {
            double sliderVal = Math.Clamp(Settings.Canvas.InkWidth * 2, 1, 30);
            BoardInkWidthSlider.Value = sliderVal;
            BoardInkWidthSliderSoft.Value = sliderVal;
            BoardInkHighlighterWidthSlider.Value = sliderVal;
            BoardInkAlphaSlider.Value = Math.Clamp(Settings.Canvas.InkAlpha, 20, 240);
        }

        /// <summary>荧光笔"粗细"滑块：写活动值与荧光笔记忆，重算画笔属性（30 + InkWidth）。</summary>
        private void InkHighlighterWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isLoaded) return;
            Settings.Canvas.InkWidth = ((Slider)sender).Value / 2;
            Settings.Canvas.HighlighterWidth = Settings.Canvas.InkWidth;
            if (inkColor > 100)
                CheckColorTheme();
            SaveSettingsToFile();
        }

        private Color StringToColor(string colorStr)
        {
            Byte[] argb = new Byte[4];
            for (int i = 0; i < 4; i++)
            {
                char[] charArray = colorStr.Substring(i * 2 + 1, 2).ToCharArray();
                Byte b1 = toByte(charArray[0]);
                Byte b2 = toByte(charArray[1]);
                argb[i] = (Byte)(b2 | (b1 << 4));
            }
            return Color.FromArgb(argb[0], argb[1], argb[2], argb[3]);//#FFFFFFFF
        }

        private static byte toByte(char c)
        {
            byte b = (byte)"0123456789ABCDEF".IndexOf(c);
            return b;
        }
    }
}
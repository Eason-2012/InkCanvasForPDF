using System.Windows;
using System.Windows.Controls;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        private void InkAlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isLoaded) return;
            BoardInkAlphaSlider.Value = ((Slider)sender).Value;
            drawingAttributes.Height = 20;
            drawingAttributes.Width = 5;
            Settings.Canvas.InkAlpha = (int)((Slider)sender).Value;
            SaveSettingsToFile();
            CheckColorTheme();
        }

        private void BtnHighlighterColorRed_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(101);
        }

        private void BtnHighlighterColorOrange_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(102);
        }

        private void BtnHighlighterColorYellow_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(103);
        }

        private void BtnHighlighterColorTeal_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(104);
        }
        private void BtnHighlighterColorBlue_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(105);
        }

        private void BtnHighlighterColorPurple_Click(object sender, RoutedEventArgs e)
        {
            CheckLastColor(106);
        }
    }
}
#nullable disable warnings

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        private void SaveScreenshot(bool isHideNotification, string fileName = null)
        {
            var bitmap = GetScreenshotBitmap();
            string savePath = Settings.Automation.AutoSavedStrokesLocation + @"\Auto Saved - Screenshots";
            if (fileName == null) fileName = DateTime.Now.ToString("u").Replace(":", "-");
            if (Settings.Automation.IsSaveScreenshotsInDateFolders)
            {
                savePath += @"\" + DateTime.Now.ToString("yyyy-MM-dd");
            }
            savePath += @"\" + fileName + ".png";
            string? dir = Path.GetDirectoryName(savePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            bitmap.Save(savePath, ImageFormat.Png);
            if (Settings.Automation.IsAutoSaveStrokesAtScreenshot)
            {
                SaveInkCanvasStrokes(false, false);
            }
            if (!isHideNotification)
            {
                ShowNotificationAsync("截图成功保存至 " + savePath);
            }
        }

        private void SaveScreenShotToDesktop()
        {
            var bitmap = GetScreenshotBitmap();
            string savePath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            bitmap.Save(savePath + @"\" + DateTime.Now.ToString("u").Replace(':', '-') + ".png", ImageFormat.Png);
            ShowNotificationAsync("截图成功保存至【桌面" + @"\" + DateTime.Now.ToString("u").Replace(':', '-') + ".png】");
            if (Settings.Automation.IsAutoSaveStrokesAtScreenshot) SaveInkCanvasStrokes(false, false);
        }

        private void SavePPTScreenshot(string fileName)
        {
            var bitmap = GetScreenshotBitmap();
            string savePath = Settings.Automation.AutoSavedStrokesLocation + @"\Auto Saved - PPT Screenshots";
            if (Settings.Automation.IsSaveScreenshotsInDateFolders)
            {
                savePath += @"\" + DateTime.Now.ToString("yyyy-MM-dd");
            }
            if (fileName == null) fileName = DateTime.Now.ToString("u").Replace(":", "-");
            savePath += @"\" + fileName + ".png";
            string? dir = Path.GetDirectoryName(savePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            bitmap.Save(savePath, ImageFormat.Png);
            if (Settings.Automation.IsAutoSaveStrokesAtScreenshot)
            {
                SaveInkCanvasStrokes(false, false);
            }
        }

        private Bitmap GetScreenshotBitmap()
        {
            Rectangle rc = System.Windows.Forms.SystemInformation.VirtualScreen;
            var bitmap = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppArgb);
            using (Graphics memoryGrahics = Graphics.FromImage(bitmap))
            {
                memoryGrahics.CopyFromScreen(rc.X, rc.Y, 0, 0, rc.Size, CopyPixelOperation.SourceCopy);
            }
            return bitmap;
        }
    }
}

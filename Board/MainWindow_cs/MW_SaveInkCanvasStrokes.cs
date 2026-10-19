using InkCanvasForPDF.Board.Helpers;
using System;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using File = System.IO.File;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        // ICA MW_Save&OpenStrokes.cs 的 SaveInkCanvasStrokes（MW_Screenshot.cs 自动截图依赖）。
        // SymbolIconSaveStrokes_Click / SymbolIconOpenStrokes_Click 已随工具面板下线（暂时不移植），
        // 需要时从 ICA 4.9.4 原件直接复制回来。

        private void SaveInkCanvasStrokes(bool newNotice = true, bool saveByUser = false)
        {
            try
            {
                string savePath = Settings.Automation.AutoSavedStrokesLocation
                    + (saveByUser ? @"\User Saved - " : @"\Auto Saved - ")
                    + (currentMode == 0 ? "Annotation Strokes" : "BlackBoard Strokes");
                if (!Directory.Exists(savePath))
                {
                    Directory.CreateDirectory(savePath);
                }
                string savePathWithName;
                if (currentMode != 0)
                { // 黑板模式下
                    savePathWithName = savePath + @"\" + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff") + " Page-" + CurrentWhiteboardIndex + " StrokesCount-" + inkCanvas.Strokes.Count + ".icstk";
                }
                else
                {
                    savePathWithName = savePath + @"\" + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff") + ".icstk";
                }
                FileStream fs = new FileStream(savePathWithName, FileMode.Create);
                inkCanvas.Strokes.Save(fs);
                if (newNotice)
                {
                    ShowNotificationAsync("墨迹成功保存至 " + savePathWithName);
                }
            }
            catch (Exception Ex)
            {
                ShowNotificationAsync("墨迹保存失败");
                LogHelper.WriteLogToFile("墨迹保存失败 | " + Ex.ToString(), LogHelper.LogType.Error);
            }
        }
    }
}

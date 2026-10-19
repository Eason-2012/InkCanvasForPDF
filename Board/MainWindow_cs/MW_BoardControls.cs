using InkCanvasForPDF.Board.Helpers;
using InkCanvasForPDF.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        int CurrentWhiteboardIndex = 1, WhiteboardTotalCount = 1;
        TimeMachineHistory[][] TimeMachineHistories = new TimeMachineHistory[101][]; //最多99页，0用来存储非白板时的墨迹以便还原

        // ══ 墨迹持久化（MainWindow 注入）══
        /// <summary>立即持久化 (槽位, 墨迹)：调用方保证传入时画布仍是该槽位的内容（D4，同步序列化）。</summary>
        public Action<int, StrokeCollection>? PersistSink;
        /// <summary>画板内墨迹/变换发生（由 MainWindow 套 300ms 去抖）。</summary>
        public Action? InkChanged;
        /// <summary>画板中间插页：给 newCurrent 让位——库内槽位整体 +1（D9 的中间加页分支；末尾加页无位移不需要）。</summary>
        public Action<int>? BoardSlotInserted;
        /// <summary>画板删页：删除 removed 行并将后续行整体 -1（D9；末尾删除由关闭时 TrimBoardSlots 兜底）。</summary>
        public Action<int>? BoardSlotDeleted;

        /// <summary>把当前槽位的当前画布立即写入持久化（currentMode!=1 时 no-op）。去抖 tick / 关闭 flush 调用。</summary>
        public void PersistCurrentSlot()
        {
            if (currentMode != 1) return;
            PersistSink?.Invoke(CurrentWhiteboardIndex, inkCanvas.Strokes);   // 当前槽位↔当前画布配对恒正确
        }

        /// <summary>关闭时取页签状态供写 view_state。</summary>
        public (int Current, int Total) GetBoardPageState() => (CurrentWhiteboardIndex, WhiteboardTotalCount);

        /// <summary>文档打开时装入持久化的画板槽位与页签（每槽单条历史：进画板时 RestoreStrokes 自然回放；D7）。</summary>
        public void LoadPersistedBoard(IReadOnlyDictionary<int, byte[]> slots,
            IReadOnlyDictionary<string, string> viewState)
        {
            // 页签：total 缺省 = 有墨迹的最大槽位，再缺省 1；current 缺省 1（均夹取）
            int total = 1;
            if (viewState.TryGetValue("board_total", out var tStr) && int.TryParse(tStr, out var t))
                total = t;
            else
                foreach (var key in slots.Keys)
                    total = Math.Max(total, key);
            total = Math.Clamp(total, 1, 99);
            int current = 1;
            if (viewState.TryGetValue("board_current", out var cStr) && int.TryParse(cStr, out var c))
                current = c;
            current = Math.Clamp(current, 1, total);

            Array.Clear(TimeMachineHistories);   // 换文档必须清上一个文档的槽位
            foreach (var (slot, bytes) in slots)
            {
                if (slot < 1 || slot > total)
                    continue;
                try
                {
                    var sc = InkStoreService.Deserialize(bytes);   // UI 线程（DocumentOpened 处理器内）
                    TimeMachineHistories[slot] = new[]
                    {
                        new TimeMachineHistory(sc, TimeMachineHistoryType.UserInput, false)   // TargetStrokes=null → 回退 inkCanvas.Strokes
                    };
                }
                catch (Exception ex)
                {
                    App.Log($"LoadPersistedBoard slot{slot}", ex);   // 单槽 BLOB 损坏：跳过该槽，页签照常恢复
                }
            }
            // 防上一个文档的画布残留（正常退出画板已清，此处兜底）
            if (inkCanvas.Strokes.Count > 0)
                ClearStrokes(true);
            CurrentWhiteboardIndex = current;
            WhiteboardTotalCount = total;
            UpdateIndexInfoDisplay();   // 刷新 "n / m" 与按钮态
        }

        private void SaveStrokes(bool isBackupMain = false)
        {
            if (isBackupMain)
            {
                var timeMachineHistory = timeMachine.ExportTimeMachineHistory();
                TimeMachineHistories[0] = timeMachineHistory;
                timeMachine.ClearStrokeHistory();

            }
            else
            {
                var timeMachineHistory = timeMachine.ExportTimeMachineHistory();
                TimeMachineHistories[CurrentWhiteboardIndex] = timeMachineHistory;
                timeMachine.ClearStrokeHistory();
                // 立即持久化本槽位（必须在调用方 ClearStrokes 之前：画布此刻仍是本槽位内容，等不了去抖；D4）
                PersistSink?.Invoke(CurrentWhiteboardIndex, inkCanvas.Strokes);
            }
        }

        private void ClearStrokes(bool isErasedByCode)
        {

            _currentCommitType = CommitReason.ClearingCanvas;
            if (isErasedByCode) _currentCommitType = CommitReason.CodeInput;
            inkCanvas.Controller.AbortStroke();   // 时间笔锋叠加层随清屏/翻页/退出一并清空（单一咽喉）
            inkCanvas.Strokes.Clear();
            _currentCommitType = CommitReason.UserInput;
        }

        private void RestoreStrokes(bool isBackupMain = false)
        {
            try
            {
                if (TimeMachineHistories[CurrentWhiteboardIndex] == null) return; //防止白板打开后不居中
                if (isBackupMain)
                {
                    timeMachine.ImportTimeMachineHistory(TimeMachineHistories[0]);
                    foreach (var item in TimeMachineHistories[0])
                    {
                        ApplyHistoryToCanvas(item);
                    }
                }
                else
                {
                    timeMachine.ImportTimeMachineHistory(TimeMachineHistories[CurrentWhiteboardIndex]);
                    foreach (var item in TimeMachineHistories[CurrentWhiteboardIndex])
                    {
                        ApplyHistoryToCanvas(item);
                    }
                }
            }
            catch { }
        }

        private void BtnWhiteBoardSwitchPrevious_Click(object sender, EventArgs e)
        {
            if (CurrentWhiteboardIndex <= 1) return;
            SaveStrokes();
            ClearStrokes(true);
            CurrentWhiteboardIndex--;
            RestoreStrokes();
            UpdateIndexInfoDisplay();
        }

        private void BtnWhiteBoardSwitchNext_Click(object sender, EventArgs e)
        {
            if (Settings.Automation.IsAutoSaveStrokesAtClear && inkCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber)
            {
                SaveScreenshot(true);
            }
            if (CurrentWhiteboardIndex >= WhiteboardTotalCount)
            {
                BtnWhiteBoardAdd_Click(sender, e);
                return;
            }
            SaveStrokes();
            ClearStrokes(true);
            CurrentWhiteboardIndex++;
            RestoreStrokes();
            UpdateIndexInfoDisplay();
        }

        private void BtnWhiteBoardAdd_Click(object sender, EventArgs e)
        {
            if (WhiteboardTotalCount >= 99) return;
            if (Settings.Automation.IsAutoSaveStrokesAtClear && inkCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber)
            {
                SaveScreenshot(true);
            }
            SaveStrokes();
            ClearStrokes(true);
            WhiteboardTotalCount++;
            CurrentWhiteboardIndex++;
            if (CurrentWhiteboardIndex != WhiteboardTotalCount)
            {
                // 位移源 = H[新页签..旧末页] 整体 +1（对齐内存循环 i>CurrentWhiteboardIndex 的源范围），给新页让位
                int newCurrent = CurrentWhiteboardIndex;
                for (int i = WhiteboardTotalCount; i > CurrentWhiteboardIndex; i--)
                {
                    TimeMachineHistories[i] = TimeMachineHistories[i - 1];
                }
                BoardSlotInserted?.Invoke(newCurrent);   // 库内 >= newCurrent 的行整体 +1（SaveStrokes 的写已入队，保序；D9）
            }
            UpdateIndexInfoDisplay();
        }

        private void BtnWhiteBoardDelete_Click(object sender, RoutedEventArgs e)
        {
            int removed = CurrentWhiteboardIndex;   // 被删页签（ClearStrokes 之后内容即丢弃，无需存）
            ClearStrokes(true);
            if (CurrentWhiteboardIndex != WhiteboardTotalCount)
            {
                for (int i = CurrentWhiteboardIndex; i <= WhiteboardTotalCount; i++)
                {
                    TimeMachineHistories[i] = TimeMachineHistories[i + 1];
                }
            }
            else
            {
                CurrentWhiteboardIndex--;
            }
            WhiteboardTotalCount--;
            // 库内删 removed 行并把 >= removed+1 的行 -1（与内存位移对齐；末尾删除无位移行，等价纯删；D9）
            BoardSlotDeleted?.Invoke(removed);
            RestoreStrokes();
            UpdateIndexInfoDisplay();
        }

        private void UpdateIndexInfoDisplay()
        {
            TextBlockWhiteBoardIndexInfo.Text = string.Format("{0} / {1}", CurrentWhiteboardIndex, WhiteboardTotalCount);

            if (CurrentWhiteboardIndex == WhiteboardTotalCount)
            {
                BoardLeftPannelNextPage1.Width = 26;
                BoardLeftPannelNextPage2.Width = 0;
                BoardLeftPannelNextPageTextBlock.Text = "加页";
            }
            else
            {
                BoardLeftPannelNextPage1.Width = 0;
                BoardLeftPannelNextPage2.Width = 26;
                BoardLeftPannelNextPageTextBlock.Text = "下一页";
            }

            if (CurrentWhiteboardIndex == 1)
            {
                BtnWhiteBoardSwitchPrevious.IsEnabled = false;
            }
            else
            {
                BtnWhiteBoardSwitchPrevious.IsEnabled = true;
            }

            if (CurrentWhiteboardIndex == 99)
            {
                BoardLeftPannelNextPage1.IsEnabled = false;
            }
            else
            {
                BoardLeftPannelNextPage1.IsEnabled = true;
            }

            if (WhiteboardTotalCount == 99)
            {
                BtnBoardAddPage.IsEnabled = false;
            }
            else
            {
                BtnBoardAddPage.IsEnabled = true;
            }
            /*
            if (WhiteboardTotalCount == 1)
            {
                //BtnWhiteBoardDelete.IsEnabled = false;
            }
            else
            {
                //BtnWhiteBoardDelete.IsEnabled = true;
            }
            */
        }
    }
}
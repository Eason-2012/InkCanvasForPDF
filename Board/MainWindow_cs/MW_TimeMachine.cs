#nullable disable warnings

using InkCanvasForPDF.Board.Helpers;
using System.Collections.Generic;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Media;

namespace InkCanvasForPDF.Board
{
    public partial class BoardView
    {
        private enum CommitReason
        {
            UserInput,
            CodeInput,
            ShapeDrawing,
            ShapeRecognition,
            ClearingCanvas,
            Manipulation,
            ImageInsert
        }

        private CommitReason _currentCommitType = CommitReason.UserInput;
        private bool IsEraseByPoint => inkCanvas.EditingMode == InkCanvasEditingMode.EraseByPoint;
        private StrokeCollection ReplacedStroke;
        private StrokeCollection AddedStroke;
        private Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>> StrokeManipulationHistory;
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
        private TimeMachine timeMachine = new TimeMachine();

        private void ApplyHistoryToCanvas(TimeMachineHistory item)
        {
            _currentCommitType = CommitReason.CodeInput;
            if (item.CommitType == TimeMachineHistoryType.UserInput)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (!inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Add(strokes);
                    }
                }
                else
                {
                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Remove(strokes);
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.ShapeRecognition)
            {
                if (item.StrokeHasBeenCleared)
                {

                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Remove(strokes);
                    }
                    if (item.ReplacedStroke != null)
                        foreach (var strokes in item.ReplacedStroke)
                        {
                            if (!inkCanvas.Strokes.Contains(strokes))
                                inkCanvas.Strokes.Add(strokes);
                        }
                }
                else
                {
                    foreach (var strokes in item.CurrentStroke)
                    {
                        if (!inkCanvas.Strokes.Contains(strokes))
                            inkCanvas.Strokes.Add(strokes);
                    }
                    if (item.ReplacedStroke != null)
                        foreach (var strokes in item.ReplacedStroke)
                        {
                            if (inkCanvas.Strokes.Contains(strokes))
                                inkCanvas.Strokes.Remove(strokes);
                        }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.Manipulation)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    foreach (var currentStroke in item.StylusPointDictionary)
                    {
                        if (inkCanvas.Strokes.Contains(currentStroke.Key))
                        {
                            currentStroke.Key.StylusPoints = currentStroke.Value.Item2;
                        }
                    }
                }
                else
                {
                    foreach (var currentStroke in item.StylusPointDictionary)
                    {
                        if (inkCanvas.Strokes.Contains(currentStroke.Key))
                        {
                            currentStroke.Key.StylusPoints = currentStroke.Value.Item1;
                        }
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.DrawingAttributes)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    foreach (var currentStroke in item.DrawingAttributes)
                    {
                        if (inkCanvas.Strokes.Contains(currentStroke.Key))
                        {
                            currentStroke.Key.DrawingAttributes = currentStroke.Value.Item2;
                        }
                    }
                }
                else
                {
                    foreach (var currentStroke in item.DrawingAttributes)
                    {
                        if (inkCanvas.Strokes.Contains(currentStroke.Key))
                        {
                            currentStroke.Key.DrawingAttributes = currentStroke.Value.Item1;
                        }
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.Clear)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    if (item.CurrentStroke != null)
                    {
                        foreach (var currentStroke in item.CurrentStroke)
                        {
                            if (!inkCanvas.Strokes.Contains(currentStroke)) inkCanvas.Strokes.Add(currentStroke);
                        }

                    }
                    if (item.ReplacedStroke != null)
                    {
                        foreach (var replacedStroke in item.ReplacedStroke)
                        {
                            if (inkCanvas.Strokes.Contains(replacedStroke)) inkCanvas.Strokes.Remove(replacedStroke);
                        }
                    }

                }
                else
                {
                    if (item.ReplacedStroke != null)
                    {
                        foreach (var replacedStroke in item.ReplacedStroke)
                        {
                            if (!inkCanvas.Strokes.Contains(replacedStroke)) inkCanvas.Strokes.Add(replacedStroke);
                        }
                    }
                    if (item.CurrentStroke != null)
                    {
                        foreach (var currentStroke in item.CurrentStroke)
                        {
                            if (inkCanvas.Strokes.Contains(currentStroke)) inkCanvas.Strokes.Remove(currentStroke);
                        }
                    }
                }
            }
            else if (item.CommitType == TimeMachineHistoryType.ImageInsert)
            {
                if (!item.StrokeHasBeenCleared)
                {
                    inkCanvas.Children.Add(item.ImageElement);
                }
                else
                {
                    inkCanvas.Children.Remove(item.ImageElement);
                }
            }
            _currentCommitType = CommitReason.UserInput;
        }

        private void TimeMachine_OnUndoStateChanged(bool status)
        {
            BoardUndo.IsEnabled = status;
        }

        private void TimeMachine_OnRedoStateChanged(bool status)
        {
            BoardRedo.IsEnabled = status;
        }

        private void StrokesOnStrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            if (!isHidingSubPanelsWhenInking)
            {
                isHidingSubPanelsWhenInking = true;
                HideSubPanels(); // 书写时自动隐藏二级菜单
            }

            foreach (var stroke in e.Removed)
            {
                stroke.StylusPointsChanged -= Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced -= Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged -= Stroke_DrawingAttributesChanged;
                StrokeInitialHistory.Remove(stroke);
            }
            foreach (var stroke in e.Added)
            {
                stroke.StylusPointsChanged += Stroke_StylusPointsChanged;
                stroke.StylusPointsReplaced += Stroke_StylusPointsReplaced;
                stroke.DrawingAttributesChanged += Stroke_DrawingAttributesChanged;
                StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
            }
            if (_currentCommitType == CommitReason.CodeInput || _currentCommitType == CommitReason.ShapeDrawing) return;
            if (currentMode == 1) InkChanged?.Invoke();   // 画板内抬笔/擦除标脏（回放门之后；去抖吸收面积擦中间态，D4）
            // 面积擦恒进批处理；墨迹擦在橡皮工具下也进批处理（逐触摸提交，双指手势成立时可整体回滚），清屏除外
            bool erasingByTool = IsEraseByPoint
                || (forceEraser && inkCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke
                    && _currentCommitType != CommitReason.ClearingCanvas);
            if ((e.Added.Count != 0 || e.Removed.Count != 0) && erasingByTool)
            {
                if (AddedStroke == null) AddedStroke = new StrokeCollection();
                if (ReplacedStroke == null) ReplacedStroke = new StrokeCollection();
                AddedStroke.Add(e.Added);
                ReplacedStroke.Add(e.Removed);
                return;
            }
            if (e.Added.Count != 0)
            {
                if (_currentCommitType == CommitReason.ShapeRecognition)
                {
                    timeMachine.CommitStrokeShapeHistory(ReplacedStroke, e.Added);
                    ReplacedStroke = null;
                    return;
                }
                else
                {
                    timeMachine.CommitStrokeUserInputHistory(e.Added);
                    return;
                }
            }

            if (e.Removed.Count != 0)
            {
                if (_currentCommitType == CommitReason.ShapeRecognition)
                {
                    ReplacedStroke = e.Removed;
                    return;
                }
                else if (!IsEraseByPoint || _currentCommitType == CommitReason.ClearingCanvas)
                {
                    timeMachine.CommitStrokeEraseHistory(e.Removed);
                    return;
                }
            }
        }

        private void Stroke_DrawingAttributesChanged(object sender, PropertyDataChangedEventArgs e)
        {
            var key = sender as Stroke;
            if (key == null) return;
            if (currentMode == 1) InkChanged?.Invoke();   // 改色/粗细不走 StrokesChanged，不标就丢（D4）
            // 克隆手势中的克隆笔画不记属性历史：撤销语义 = 整体删除克隆，不单步回退
            if (StrokesSelectionClone.Count != 0 && StrokesSelectionClone.Contains(key)) return;
            var currentValue = key.DrawingAttributes.Clone();
            DrawingAttributesHistory.TryGetValue(key, out var previousTuple);
            var previousValue = previousTuple?.Item1 ?? currentValue.Clone();
            var needUpdateValue = !DrawingAttributesHistoryFlag[e.PropertyGuid].Contains(key);
            if (needUpdateValue)
            {
                DrawingAttributesHistoryFlag[e.PropertyGuid].Add(key);
                Debug.Write(e.PreviousValue.ToString());
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

        private void Stroke_StylusPointsReplaced(object sender, StylusPointsReplacedEventArgs e)
        {
            if (sender is Stroke stroke)
            {
                if (currentMode == 1) InkChanged?.Invoke();   // 点集替换（变换定格）不走 StrokesChanged，不标就丢（D4）
                StrokeInitialHistory[stroke] = e.NewStylusPoints.Clone();
            }
        }

        private void Stroke_StylusPointsChanged(object sender, EventArgs e)
        {
            if (!(sender is Stroke stroke)) return;
            if (currentMode == 1) InkChanged?.Invoke();   // 双指平移/缩放/选中拖移改点集，不走 StrokesChanged（D4）
            // 克隆笔画的拖移不进 Manipulation 累加器（逐帧 Transform 会刷屏提交，盖住"新增克隆"条目）；
            // 基线跟上当前位置，避免手势结束后首次移动该克隆撤销回退到拖前位置
            if (StrokesSelectionClone.Count != 0 && StrokesSelectionClone.Contains(stroke))
            {
                StrokeInitialHistory[stroke] = stroke.StylusPoints.Clone();
                return;
            }
            var selectedStrokes = inkCanvas.GetSelectedStrokes();
            var count = selectedStrokes.Count;
            if (count == 0) count = inkCanvas.Strokes.Count;
            if (StrokeManipulationHistory == null)
            {
                StrokeManipulationHistory = new Dictionary<Stroke, Tuple<StylusPointCollection, StylusPointCollection>>();
            }
            StrokeManipulationHistory[stroke] =
                new Tuple<StylusPointCollection, StylusPointCollection>(StrokeInitialHistory[stroke], stroke.StylusPoints.Clone());
            if ((StrokeManipulationHistory.Count == count || sender == null) && dec.Count == 0)
            {
                timeMachine.CommitStrokeManipulationHistory(StrokeManipulationHistory);
                foreach (var item in StrokeManipulationHistory)
                {
                    StrokeInitialHistory[item.Key] = item.Value.Item2;
                }
                StrokeManipulationHistory = null;
            }
        }
    }
}

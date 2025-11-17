using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace PhotoMax
{
    public class UndoRedoManager
    {
        private readonly Stack<UndoRedoState> _undoStack = new Stack<UndoRedoState>();
        private readonly Stack<UndoRedoState> _redoStack = new Stack<UndoRedoState>();
        private readonly int _maxHistorySize;

        public UndoRedoManager(int maxHistorySize = 50)
        {
            _maxHistorySize = maxHistorySize;
        }

        public bool CanUndo => _undoStack.Count > 0;

        public bool CanRedo => _redoStack.Count > 0;

        public void SaveState(Mat imageMat, string description = "")
        {
            if (imageMat == null || imageMat.Empty()) return;

            var snapshot = imageMat.Clone();
            var state = new UndoRedoState(snapshot, description);

            _undoStack.Push(state);

            if (_undoStack.Count > _maxHistorySize)
            {
                var oldest = _undoStack.ToArray().Last();
                oldest.Dispose();
                var temp = _undoStack.ToArray().Take(_maxHistorySize).Reverse();
                _undoStack.Clear();
                foreach (var s in temp) _undoStack.Push(s);
            }

            ClearRedo();
        }

        public void SaveCurrentStateForRedo(Mat currentState, string description)
        {
            if (currentState == null || currentState.Empty()) return;
            var state = new UndoRedoState(currentState, description);
            _redoStack.Push(state);

            if (_redoStack.Count > _maxHistorySize)
            {
                var oldest = _redoStack.ToArray().Last();
                oldest.Dispose();
                var temp = _redoStack.ToArray().Take(_maxHistorySize).Reverse();
                _redoStack.Clear();
                foreach (var s in temp) _redoStack.Push(s);
            }
        }

        public void SaveCurrentStateForUndo(Mat currentState, string description)
        {
            if (currentState == null || currentState.Empty()) return;
            var state = new UndoRedoState(currentState, description);
            _undoStack.Push(state);

            if (_undoStack.Count > _maxHistorySize)
            {
                var oldest = _undoStack.ToArray().Last();
                oldest.Dispose();
                var temp = _undoStack.ToArray().Take(_maxHistorySize).Reverse();
                _undoStack.Clear();
                foreach (var s in temp) _undoStack.Push(s);
            }
        }

        public UndoRedoState? Undo()
        {
            if (!CanUndo) return null;

            var state = _undoStack.Pop();
            return state;
        }

        public UndoRedoState? Redo()
        {
            if (!CanRedo) return null;

            var state = _redoStack.Pop();
            return state;
        }

        public void ClearRedo()
        {
            while (_redoStack.Count > 0)
            {
                _redoStack.Pop().Dispose();
            }
        }

        public void Clear()
        {
            while (_undoStack.Count > 0)
            {
                _undoStack.Pop().Dispose();
            }

            ClearRedo();
        }

        public string GetUndoDescription()
        {
            return CanUndo ? _undoStack.Peek().Description : "";
        }

        public string GetRedoDescription()
        {
            return CanRedo ? _redoStack.Peek().Description : "";
        }
    }

    public class UndoRedoState : IDisposable
    {
        public Mat ImageSnapshot { get; }
        public string Description { get; }

        public UndoRedoState(Mat imageSnapshot, string description = "")
        {
            ImageSnapshot = imageSnapshot;
            Description = description;
        }

        public void Dispose()
        {
            ImageSnapshot?.Dispose();
        }
    }
}
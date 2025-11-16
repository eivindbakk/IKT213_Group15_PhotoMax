using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace PhotoMax
{
    /// <summary>
    /// Manages undo/redo operations by storing snapshots of image state.
    /// </summary>
    public class UndoRedoManager
    {
        private readonly Stack<UndoRedoState> _undoStack = new Stack<UndoRedoState>();
        private readonly Stack<UndoRedoState> _redoStack = new Stack<UndoRedoState>();
        private readonly int _maxHistorySize;

        public UndoRedoManager(int maxHistorySize = 50)
        {
            _maxHistorySize = maxHistorySize;
        }

        /// <summary>
        /// Returns true if undo is available.
        /// </summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>
        /// Returns true if redo is available.
        /// </summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// Saves the current state before an operation.
        /// </summary>
        public void SaveState(Mat imageMat, string description = "")
        {
            if (imageMat == null || imageMat.Empty()) return;

            // Clone the Mat to create a snapshot
            var snapshot = imageMat.Clone();
            var state = new UndoRedoState(snapshot, description);

            _undoStack.Push(state);
            
            // Limit stack size
            if (_undoStack.Count > _maxHistorySize)
            {
                var oldest = _undoStack.ToArray().Last();
                oldest.Dispose();
                var temp = _undoStack.ToArray().Take(_maxHistorySize).Reverse();
                _undoStack.Clear();
                foreach (var s in temp) _undoStack.Push(s);
            }

            // Clear redo stack when new operation is performed
            ClearRedo();
        }

        /// <summary>
        /// Saves the current state to the redo stack (called before undoing).
        /// </summary>
        public void SaveCurrentStateForRedo(Mat currentState, string description)
        {
            if (currentState == null || currentState.Empty()) return;
            var state = new UndoRedoState(currentState, description);
            _redoStack.Push(state);
            
            // Limit redo stack size
            if (_redoStack.Count > _maxHistorySize)
            {
                var oldest = _redoStack.ToArray().Last();
                oldest.Dispose();
                var temp = _redoStack.ToArray().Take(_maxHistorySize).Reverse();
                _redoStack.Clear();
                foreach (var s in temp) _redoStack.Push(s);
            }
        }

        /// <summary>
        /// Saves the current state to the undo stack (called before redoing).
        /// </summary>
        public void SaveCurrentStateForUndo(Mat currentState, string description)
        {
            if (currentState == null || currentState.Empty()) return;
            var state = new UndoRedoState(currentState, description);
            _undoStack.Push(state);
            
            // Limit undo stack size
            if (_undoStack.Count > _maxHistorySize)
            {
                var oldest = _undoStack.ToArray().Last();
                oldest.Dispose();
                var temp = _undoStack.ToArray().Take(_maxHistorySize).Reverse();
                _undoStack.Clear();
                foreach (var s in temp) _undoStack.Push(s);
            }
        }

        /// <summary>
        /// Undoes the last operation and returns the previous state.
        /// </summary>
        public UndoRedoState? Undo()
        {
            if (!CanUndo) return null;

            var state = _undoStack.Pop();
            return state;
        }

        /// <summary>
        /// Redoes the last undone operation and returns the state.
        /// </summary>
        public UndoRedoState? Redo()
        {
            if (!CanRedo) return null;

            var state = _redoStack.Pop();
            return state;
        }

        /// <summary>
        /// Clears the redo stack (called when a new operation is performed).
        /// </summary>
        public void ClearRedo()
        {
            while (_redoStack.Count > 0)
            {
                _redoStack.Pop().Dispose();
            }
        }

        /// <summary>
        /// Clears all undo/redo history.
        /// </summary>
        public void Clear()
        {
            while (_undoStack.Count > 0)
            {
                _undoStack.Pop().Dispose();
            }
            ClearRedo();
        }

        /// <summary>
        /// Gets a description of the next undo operation.
        /// </summary>
        public string GetUndoDescription()
        {
            return CanUndo ? _undoStack.Peek().Description : "";
        }

        /// <summary>
        /// Gets a description of the next redo operation.
        /// </summary>
        public string GetRedoDescription()
        {
            return CanRedo ? _redoStack.Peek().Description : "";
        }
    }

    /// <summary>
    /// Represents a snapshot of the application state at a point in time.
    /// </summary>
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


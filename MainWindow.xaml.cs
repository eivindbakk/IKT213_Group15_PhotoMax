using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PhotoMax
{
    public partial class MainWindow : Window
    {
        // ---- Brush state used by Tools.cs (partial) ----
        internal readonly int[] _brushSizes = new[] { 2, 4, 8, 12, 16, 24, 36 };
        internal int _brushIndex = 2;
        internal Color _brushColor = Colors.Black;
        internal bool _eraseMode = false;

        // ---- Zoom/pan state ----
        internal double _zoom = 1.0;
        internal const double ZoomStep = 1.25;
        internal const double MinZoom = 0.05;
        internal const double MaxZoom = 8.0;

        // Panning (>100%)
        private bool _isSpaceDown = false;
        private bool _isPanning = false;
        private Point _panStartMouse;
        private double _panStartH, _panStartV;

        // Grid state
        internal bool _gridEnabled = true;
        internal Color _gridColor = Color.FromArgb(0x22, 0x00, 0x00, 0x00);
        internal double _gridSpacing = 32.0;

        // Image features controller (defined in MenuHandlers/Image.cs)
        private ImageController? _img;

        // Current file
        private string? _currentFilePath = null;
        private bool _hasUnsavedChanges = false;

        // Undo/Redo manager
        private UndoRedoManager? _undoRedoManager;

        public MainWindow()
        {
            InitializeComponent();
            ConfigureBrush();

            Loaded += (_, __) =>
            {
                _img = new ImageController(ImageView, Artboard, StatusText, PaintCanvas);
                _undoRedoManager = new UndoRedoManager();
                
                // Set callback for ImageController to save undo state
                _img.SaveUndoStateCallback = (description) => SaveUndoState(description);

                RenderOptions.SetBitmapScalingMode(PaintCanvas, BitmapScalingMode.NearestNeighbor);
                RenderOptions.SetEdgeMode(PaintCanvas, EdgeMode.Aliased);
                PaintCanvas.SnapsToDevicePixels = true;

                _img.ImageChanged += () => ConfigureBrush();
                Artboard.SizeChanged += (_, __) => ConfigureBrush();
                SizeChanged += Window_SizeChanged;

                UpdateGridBrush();
                SetZoom(1.0, new Point(Scroller.ActualWidth / 2, Scroller.ActualHeight / 2));
                StatusText.Content = "Ctrl+Wheel: zoom • Space/Middle: pan";
                
                UpdateUndoRedoMenuItems();
            };

            // Track changes when drawing/editing
            PaintCanvas.StrokeCollected += (_, __) => _hasUnsavedChanges = true;
            PaintCanvas.StrokeErased += (_, __) => _hasUnsavedChanges = true;
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
             if (!CanvasExceedsViewport())
             {
                SetZoom(_zoom, new Point(Scroller.ActualWidth / 2, Scroller.ActualHeight / 2), false);
             }
        }

        /* -------------------- GRID -------------------- */
        private DrawingBrush BuildGridBrush(Color color, double spacing)
        {
            var penBrush = new SolidColorBrush(color);
            if (penBrush.CanFreeze) penBrush.Freeze();
            var pen = new Pen(penBrush, 1);
            var group = new GeometryGroup();
            group.Children.Add(new LineGeometry(new Point(0, 0), new Point(spacing, 0)));
            group.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, spacing)));
            var drawing = new GeometryDrawing { Brush = Brushes.Transparent, Pen = pen, Geometry = group };
            var brush = new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, spacing, spacing),
                ViewportUnits = BrushMappingMode.Absolute
            };
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private void UpdateGridBrush()
        {
            GridOverlay.Fill = BuildGridBrush(_gridColor, _gridSpacing);
            GridOverlay.Visibility = _gridEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        /* -------------------- UNIFIED ZOOM CORE -------------------- */
        private void SetZoom(double newZoom, Point mousePosition, bool updateZoomValue = true)
        {
            var oldZoom = _zoom;
            if (updateZoomValue)
            {
                 _zoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
            }

            RenderScale.ScaleX = 1;
            RenderScale.ScaleY = 1;
            LayoutScale.ScaleX = _zoom;
            LayoutScale.ScaleY = _zoom;

            var targetX = Scroller.HorizontalOffset + mousePosition.X;
            var targetY = Scroller.VerticalOffset + mousePosition.Y;

            var newOffsetX = (targetX * (_zoom / oldZoom)) - mousePosition.X;
            var newOffsetY = (targetY * (_zoom / oldZoom)) - mousePosition.Y;
            
            if (Artboard.ActualWidth * _zoom < Scroller.ViewportWidth)
            {
                newOffsetX = (Artboard.ActualWidth * _zoom - Scroller.ViewportWidth) / 2;
            }
            if (Artboard.ActualHeight * _zoom < Scroller.ViewportHeight)
            {
                newOffsetY = (Artboard.ActualHeight * _zoom - Scroller.ViewportHeight) / 2;
            }

            Scroller.ScrollToHorizontalOffset(newOffsetX);
            Scroller.ScrollToVerticalOffset(newOffsetY);

            StatusText.Content = $"Zoom: {(int)Math.Round(_zoom * 100)}%";
            OnZoomChanged_UpdateBrushPreview();
        }

        /* -------------------- WHEEL ZOOM -------------------- */
        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            var mousePos = e.GetPosition(Scroller);
            var nextZoom = _zoom * (e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep);
            
            SetZoom(nextZoom, mousePos);
            
            e.Handled = true;
        }

        /* -------------------- Backward Compatibility Stubs (for Tools.cs) -------------------- */
        internal static bool AtOrBelowOne(double z) => z <= 1.0 + 1e-9;
        internal bool CanvasExceedsViewport() => Artboard.ActualWidth * _zoom > Scroller.ViewportWidth || Artboard.ActualHeight * _zoom > Scroller.ViewportHeight;
        internal void ApplyZoom_NoScroll(double newZoom) => SetZoom(newZoom, new Point(Scroller.ActualWidth / 2, Scroller.ActualHeight / 2));
        internal void SetZoomCentered(double newZoom) => SetZoom(newZoom, new Point(Scroller.ActualWidth / 2, Scroller.ActualHeight / 2));
        internal void SetZoomToCursor(double newZoom, Point mouseViewport, Point mouseContentBefore) => SetZoom(newZoom, mouseViewport);
        internal Point ViewportPointToWorkspaceBeforeZoom(Point viewportPoint)
        {
            Point inContent = new Point(Scroller.HorizontalOffset + viewportPoint.X, Scroller.VerticalOffset + viewportPoint.Y);
            return Root.TransformToVisual(Workspace).Transform(inContent);
        }

        internal void OnZoomChanged_UpdateBrushPreview()
        {
            SyncBrushPreviewStrokeToZoom();
        }

        /* -------------------- PANNING -------------------- */
        private void Scroller_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Space) _isSpaceDown = true; }
        private void Scroller_PreviewKeyUp(object sender, KeyEventArgs e) { if (e.Key == Key.Space) _isSpaceDown = false; }
        private void Scroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if ((_isSpaceDown || e.MiddleButton == MouseButtonState.Pressed) && CanvasExceedsViewport())
            {
                _isPanning = true;
                _panStartMouse = e.GetPosition(this);
                _panStartH = Scroller.HorizontalOffset;
                _panStartV = Scroller.VerticalOffset;
                Cursor = Cursors.Hand;
                Scroller.CaptureMouse();
                e.Handled = true;
            }
        }
        private void Scroller_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var cur = e.GetPosition(this);
                Scroller.ScrollToHorizontalOffset(_panStartH - (cur.X - _panStartMouse.X));
                Scroller.ScrollToVerticalOffset(_panStartV - (cur.Y - _panStartMouse.Y));
                e.Handled = true;
            }
        }
        private void Scroller_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                Cursor = Cursors.Arrow;
                Scroller.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        /* -------------------- ARTBOARD RESIZE -------------------- */
        private void SetArtboardSize(double width, double height)
        {
            Artboard.Width = width; Artboard.Height = height;
            ImageView.Width = width; ImageView.Height = height;
            PaintCanvas.Width = width; PaintCanvas.Height = height;
            Dispatcher.BeginInvoke(new Action(() => {
                SetZoom(_zoom, new Point(Scroller.ActualWidth/2, Scroller.ActualHeight/2), false);
            }), DispatcherPriority.Loaded);
            ConfigureBrush();
        }

        /* -------------------- BRUSH WRAPPER -------------------- */
        private void ConfigureBrush() { ApplyInkBrushAttributes(); }
        private void ImageView_Drop(object sender, DragEventArgs e) => MessageBox.Show("TODO: Drag-and-drop open.");

        /* -------------------- UNDO/REDO -------------------- */
        private void Edit_Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoRedoManager == null || _img == null) return;
            if (!_undoRedoManager.CanUndo) return;
            
            // Save current state to redo stack before undoing
            var currentMat = _img.Mat;
            if (currentMat != null && !currentMat.Empty())
            {
                // Get the description of what we're undoing (this will be the description of the state we're restoring)
                var undoDesc = _undoRedoManager.GetUndoDescription();
                
                // Save current state to redo stack with the description of the operation we're undoing
                var currentSnapshot = currentMat.Clone();
                _undoRedoManager.SaveCurrentStateForRedo(currentSnapshot, undoDesc);
            }
            
            // Now pop and restore the previous state
            var state = _undoRedoManager.Undo();
            if (state != null && state.ImageSnapshot != null && !state.ImageSnapshot.Empty())
            {
                // Restore the image state
                _img.RestoreImageState(state.ImageSnapshot);
                _hasUnsavedChanges = true;
                UpdateUndoRedoMenuItems();
                StatusText.Content = $"Undo: {state.Description}";
            }
        }

        private void Edit_Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoRedoManager == null || _img == null) return;
            if (!_undoRedoManager.CanRedo) return;
            
            // Save current state to undo stack before redoing
            var currentMat = _img.Mat;
            if (currentMat != null && !currentMat.Empty())
            {
                // Get the description of what we're redoing (this will be the description of the state we're restoring)
                var redoDesc = _undoRedoManager.GetRedoDescription();
                
                // Save current state to undo stack with the description of the operation we're redoing
                var currentSnapshot = currentMat.Clone();
                _undoRedoManager.SaveCurrentStateForUndo(currentSnapshot, redoDesc);
            }
            
            // Now pop and restore the next state
            var state = _undoRedoManager.Redo();
            if (state != null && state.ImageSnapshot != null && !state.ImageSnapshot.Empty())
            {
                // Restore the image state
                _img.RestoreImageState(state.ImageSnapshot);
                _hasUnsavedChanges = true;
                UpdateUndoRedoMenuItems();
                StatusText.Content = $"Redo: {state.Description}";
            }
        }

        private void UpdateUndoRedoMenuItems()
        {
            if (_undoRedoManager == null) return;
            
            UndoMenuItem.IsEnabled = _undoRedoManager.CanUndo;
            RedoMenuItem.IsEnabled = _undoRedoManager.CanRedo;
            
            var undoDesc = _undoRedoManager.GetUndoDescription();
            var redoDesc = _undoRedoManager.GetRedoDescription();
            
            UndoMenuItem.Header = string.IsNullOrEmpty(undoDesc) ? "_Undo" : $"_Undo: {undoDesc}";
            RedoMenuItem.Header = string.IsNullOrEmpty(redoDesc) ? "_Redo" : $"_Redo: {redoDesc}";
        }

        // Saves the current state before an operation. Call this before modifying the image.
        internal void SaveUndoState(string description = "")
        {
            if (_undoRedoManager == null || _img == null) return;
            var mat = _img.Mat;
            if (mat != null && !mat.Empty())
            {
                _undoRedoManager.SaveState(mat, description);
                UpdateUndoRedoMenuItems();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            
            // Handle Ctrl+Z for undo
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    // Ctrl+Shift+Z is redo
                    Edit_Redo_Click(this, e);
                }
                else
                {
                    // Ctrl+Z is undo
                    Edit_Undo_Click(this, e);
                }
                e.Handled = true;
            }
            // Handle Ctrl+Y for redo
            else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Edit_Redo_Click(this, e);
                e.Handled = true;
            }
        }
    }
}
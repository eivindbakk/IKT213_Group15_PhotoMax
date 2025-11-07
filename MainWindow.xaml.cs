using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PhotoMax
{
    public partial class MainWindow : Window
    {
        private readonly int[] _brushSizes = new[] { 2, 4, 8, 12, 16, 24, 36 };
        private int _brushIndex = 2;
        private Color _brushColor = Colors.Black;
        private bool _eraseMode = false;

        private double _zoom = 1.0;
        private const double ZoomStep = 1.25;
        private const double MinZoom = 0.05;
        private const double MaxZoom = 8.0;

        // Panning (>100%)
        private bool _isSpaceDown = false;
        private bool _isPanning = false;
        private Point _panStartMouse;
        private double _panStartH, _panStartV;

        // Grid state
        private bool _gridEnabled = true;
        private Color _gridColor = Color.FromArgb(0x22, 0x00, 0x00, 0x00); // default alpha ≈ 13%
        private double _gridSpacing = 32.0;

        // Current file
        private string? _currentFilePath = null;
        private bool _hasUnsavedChanges = false;

        public MainWindow()
        {
            InitializeComponent();
            ConfigureBrush();

            Loaded += (_, __) =>
            {
                UpdateGridBrush();
                SetZoomCentered(1.0);
                StatusText.Content = "Ctrl+Wheel: zoom (center ≤100%, to-cursor >100%) • Space/Middle: pan";
            };

            SizeChanged += (_, __) =>
            {
                if (_zoom <= 1.0 + 1e-9) SetZoomCentered(_zoom);
            };

            // Track changes when drawing/editing
            PaintCanvas.StrokeCollected += (_, __) => _hasUnsavedChanges = true;
            PaintCanvas.StrokeErased += (_, __) => _hasUnsavedChanges = true;
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

            var drawing = new GeometryDrawing
            {
                Brush = Brushes.Transparent,
                Pen = pen,
                Geometry = group
            };

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


        /* -------------------- ZOOM CORE -------------------- */
        private static bool AtOrBelowOne(double z) => z <= 1.0 + 1e-9;

        private void ApplyZoom_NoScroll(double newZoom)
        {
            _zoom = Math.Clamp(newZoom, MinZoom, MaxZoom);

            LayoutScale.ScaleX = 1.0;
            LayoutScale.ScaleY = 1.0;
            RenderScale.ScaleX = _zoom;
            RenderScale.ScaleY = _zoom;

            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Scroller.VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled;

            Workspace.UpdateLayout();
            Scroller.ScrollToHorizontalOffset(0);
            Scroller.ScrollToVerticalOffset(0);

            StatusText.Content = $"Zoom: {(int)Math.Round(_zoom * 100)}%";
        }

        private void ApplyZoom_WithScroll(double newZoom)
        {
            _zoom = Math.Clamp(newZoom, MinZoom, MaxZoom);

            RenderScale.ScaleX = 1.0;
            RenderScale.ScaleY = 1.0;
            LayoutScale.ScaleX = _zoom;
            LayoutScale.ScaleY = _zoom;

            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.VerticalScrollBarVisibility   = ScrollBarVisibility.Auto;

            Workspace.UpdateLayout();

            StatusText.Content = $"Zoom: {(int)Math.Round(_zoom * 100)}%";
        }

        private void SetZoomCentered(double newZoom)
        {
            if (AtOrBelowOne(newZoom))
            {
                ApplyZoom_NoScroll(newZoom);
            }
            else
            {
                ApplyZoom_WithScroll(newZoom);
                var centerContent = new Point(
                    Math.Max(1, Workspace.ActualWidth)  / 2.0,
                    Math.Max(1, Workspace.ActualHeight) / 2.0
                );
                CenterOnContentPoint(centerContent);
            }
        }

        private void SetZoomToCursor(double newZoom, Point mouseViewport, Point mouseContentBefore)
        {
            ApplyZoom_WithScroll(newZoom);

            Point topLeftAfter = Workspace.TransformToVisual(Root).Transform(new Point(0, 0));

            double h = topLeftAfter.X + mouseContentBefore.X * _zoom - mouseViewport.X;
            double v = topLeftAfter.Y + mouseContentBefore.Y * _zoom - mouseViewport.Y;

            double maxH = Math.Max(0, Scroller.ExtentWidth  - Scroller.ViewportWidth);
            double maxV = Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight);

            Scroller.ScrollToHorizontalOffset(Math.Clamp(h, 0, maxH));
            Scroller.ScrollToVerticalOffset(Math.Clamp(v, 0, maxV));
        }

        private void CenterOnContentPoint(Point contentPoint)
        {
            Point topLeft = Workspace.TransformToVisual(Root).Transform(new Point(0, 0));

            double cx = topLeft.X + contentPoint.X * _zoom;
            double cy = topLeft.Y + contentPoint.Y * _zoom;

            double targetH = cx - Scroller.ViewportWidth  / 2.0;
            double targetV = cy - Scroller.ViewportHeight / 2.0;

            double maxH = Math.Max(0, Scroller.ExtentWidth  - Scroller.ViewportWidth);
            double maxV = Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight);

            Scroller.ScrollToHorizontalOffset(Math.Clamp(targetH, 0, maxH));
            Scroller.ScrollToVerticalOffset(Math.Clamp(targetV, 0, maxV));
        }

        /* -------------------- WHEEL ZOOM -------------------- */
        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            double next = _zoom * (e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep);

            if (AtOrBelowOne(next))
            {
                ApplyZoom_NoScroll(next);
            }
            else
            {
                Point pBefore = e.GetPosition(Workspace);
                Point mViewport = e.GetPosition(Scroller);
                SetZoomToCursor(next, mViewport, pBefore);
            }

            e.Handled = true;
        }

        /* -------------------- ZOOM BUTTONS -------------------- */

        private Point ViewportPointToWorkspaceBeforeZoom(Point viewportPoint)
        {
            Point inContent = new Point(Scroller.HorizontalOffset + viewportPoint.X,
                                        Scroller.VerticalOffset   + viewportPoint.Y);
            GeneralTransform toWorkspace = Root.TransformToVisual(Workspace);
            return toWorkspace.Transform(inContent);
        }


        /* -------------------- PANNING -------------------- */
        private void Scroller_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) _isSpaceDown = true;
        }

        private void Scroller_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) _isSpaceDown = false;
        }

        private void Scroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isSpaceDown || e.MiddleButton == MouseButtonState.Pressed)
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
            Artboard.Width = width;
            Artboard.Height = height;
            ImageView.Width = width;
            ImageView.Height = height;
            PaintCanvas.Width = width;
            PaintCanvas.Height = height;

            if (_zoom <= 1.0 + 1e-9) SetZoomCentered(_zoom);
        }

        /* -------------------- PAINTING -------------------- */
        private void ConfigureBrush()
        {
            var da = PaintCanvas.DefaultDrawingAttributes;
            da.Color = _brushColor;
            da.IgnorePressure = true;
            da.FitToCurve = false;
            da.Width = _brushSizes[_brushIndex];
            da.Height = _brushSizes[_brushIndex];
            PaintCanvas.EditingMode = _eraseMode
                ? InkCanvasEditingMode.EraseByPoint
                : InkCanvasEditingMode.Ink;

            StatusText.Content = _eraseMode
                ? $"Eraser ON • Size: {_brushSizes[_brushIndex]} px"
                : $"Brush • Color: #{_brushColor.R:X2}{_brushColor.G:X2}{_brushColor.B:X2} • Size: {_brushSizes[_brushIndex]} px";
        }

        /* -------------------- DRAG AND DROP -------------------- */
        private void ImageView_Drop(object sender, DragEventArgs e) => MessageBox.Show("TODO: Drag-and-drop open.");
    }
}

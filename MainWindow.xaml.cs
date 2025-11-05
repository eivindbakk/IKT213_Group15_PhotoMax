using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using WF = System.Windows.Forms;

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

        private void View_ToggleGrid_Click(object sender, RoutedEventArgs e)
        {
            _gridEnabled = !_gridEnabled;
            GridOverlay.Visibility = _gridEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private void View_GridSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new GridSettingsWindow(_gridColor) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _gridColor = dlg.GridColor; // includes new alpha
                UpdateGridBrush();
            }
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
        private void Zoom_In_Click(object sender, RoutedEventArgs e)
        {
            double next = _zoom * ZoomStep;
            if (AtOrBelowOne(next))
            {
                ApplyZoom_NoScroll(next);
            }
            else
            {
                var m = new Point(Scroller.ViewportWidth / 2.0, Scroller.ViewportHeight / 2.0);
                var p = ViewportPointToWorkspaceBeforeZoom(m);
                SetZoomToCursor(next, m, p);
            }
        }

        private void Zoom_Out_Click(object sender, RoutedEventArgs e)
        {
            double next = _zoom / ZoomStep;
            if (AtOrBelowOne(next))
            {
                ApplyZoom_NoScroll(next);
            }
            else
            {
                var m = new Point(Scroller.ViewportWidth / 2.0, Scroller.ViewportHeight / 2.0);
                var p = ViewportPointToWorkspaceBeforeZoom(m);
                SetZoomToCursor(next, m, p);
            }
        }

        private Point ViewportPointToWorkspaceBeforeZoom(Point viewportPoint)
        {
            Point inContent = new Point(Scroller.HorizontalOffset + viewportPoint.X,
                                        Scroller.VerticalOffset   + viewportPoint.Y);
            GeneralTransform toWorkspace = Root.TransformToVisual(Workspace);
            return toWorkspace.Transform(inContent);
        }

        private void Zoom_100_Click(object? sender, RoutedEventArgs e) => SetZoomCentered(1.0);

        private void Zoom_Fit_Click(object? sender, RoutedEventArgs e)
        {
            var margin = 16.0;
            var vw = Math.Max(1, Scroller.ViewportWidth  - margin);
            var vh = Math.Max(1, Scroller.ViewportHeight - margin);

            var cw = Math.Max(1, Artboard.ActualWidth);
            var ch = Math.Max(1, Artboard.ActualHeight);

            var fit = Math.Max(MinZoom, Math.Min(vw / cw, vh / ch));
            SetZoomCentered(fit);
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

        private void Tool_Brushes_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = false;
            PaintCanvas.EditingMode = InkCanvasEditingMode.Ink;
            ConfigureBrush();
        }

        private void Tool_Erase_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = !_eraseMode;
            PaintCanvas.EditingMode = _eraseMode ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink;
            ConfigureBrush();
        }

        private void Colors_BrushSize_Click(object sender, RoutedEventArgs e)
        {
            _brushIndex = (_brushIndex + 1) % _brushSizes.Length;
            ConfigureBrush();
        }

        private void Tool_ColorPicker_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WF.ColorDialog { AllowFullOpen = true, FullOpen = true };
            if (dlg.ShowDialog() == WF.DialogResult.OK)
            {
                _brushColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _eraseMode = false;
                ConfigureBrush();
            }
        }

        private void Tool_TextBox_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("TODO: Text box tool.");
        }

        /* -------------------- PLACEHOLDERS -------------------- */
        private void File_New_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: New document (create empty Mat + set artboard).");
        private void File_Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                MessageBox.Show($"TODO: Open image: {dlg.FileName} then SetArtboardSize(imageW, imageH)");
        }
        private void File_Save_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Save merged Image + Strokes.");
        private void File_SaveAs_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Save As...");
        private void File_Properties_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Show image properties.");
        private void File_Quit_Click(object sender, RoutedEventArgs e) => Close();
        private void Clipboard_Copy_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Copy selection.");
        private void Clipboard_Paste_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Paste.");
        private void Clipboard_Cut_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Cut.");
        private void Select_Rect_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Rectangular selection.");
        private void Select_Lasso_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Lasso selection.");
        private void Select_Polygon_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Polygon selection.");
        private void Image_Crop_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Crop.");
        private void Image_Resize_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Resize.");
        private void Rotate_Right_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Rotate Right 90°.");
        private void Rotate_Left_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Rotate Left 90°.");
        private void Flip_Vert_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Flip Vertical.");
        private void Flip_Horiz_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Flip Horizontal.");
        private void Filter_Gaussian_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Gaussian (OpenCvSharp).");
        private void Filter_Sobel_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Sobel (OpenCvSharp).");
        private void Filter_Binary_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Binary threshold.");
        private void Filter_HistogramThreshold_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Histogram threshold.");
        private void Shapes_List_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Shapes list.");
        private void Shapes_OutlineColor_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Outline color.");
        private void Shapes_FillColor_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Fill color.");
        private void Layers_New_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: New layer.");
        private void Layers_Load_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Load layer.");
        private void Layers_Edit_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Edit layer.");
        private void Layers_Select_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Select layer.");
        private void Layers_Delete_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Delete layer.");
        private void Layers_Rename_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Rename layer.");
        private void Fun_Filters_Click(object sender, RoutedEventArgs e) => MessageBox.Show("TODO: Fun filters 😉");
        private void ImageView_Drop(object sender, DragEventArgs e) => MessageBox.Show("TODO: Drag-and-drop open.");
    }
}

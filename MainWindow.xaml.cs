using System;
using System.Windows;
using System.Windows.Controls;          // InkCanvasEditingMode, ScrollViewer
using System.Windows.Ink;
using System.Windows.Input;             // Keyboard, Key, ModifierKeys
using System.Windows.Media;             // ScaleTransform, Color
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace PhotoMax
{
    public partial class MainWindow : Window
    {
        private readonly int[] _brushSizes = new[] { 2, 4, 8, 12, 16, 24, 36 };
        private int _brushIndex = 2; // default 8px
        private Color _brushColor = Colors.Black;
        private bool _eraseMode = false;

        private double _zoom = 1.0;
        private const double ZoomStep = 1.25;
        private const double MinZoom = 0.05;
        private const double MaxZoom = 8.0;

        public MainWindow()
        {
            InitializeComponent();
            ConfigureBrush();
            UpdateZoom(1.0, center:true);
            StatusText.Content = "Painting ready. Use Tools ▸ Paint Brushes. Ctrl + Mouse Wheel to zoom.";
            
        }

        /* -------------------- ZOOM SYSTEM -------------------- */

        private void UpdateZoom(double newZoom, bool center = false)
        {
            _zoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
            WorkspaceScale.ScaleX = _zoom;
            WorkspaceScale.ScaleY = _zoom;
            StatusText.Content = $"Zoom: {(int)Math.Round(_zoom * 100)}%";

            if (center)
            {
                // Center the scroller after big zoom jumps (e.g., Fit/100%)
                Scroller.ScrollToHorizontalOffset(Math.Max(0, (Workspace.ActualWidth * _zoom - Scroller.ViewportWidth) / 2));
                Scroller.ScrollToVerticalOffset(Math.Max(0, (Workspace.ActualHeight * _zoom - Scroller.ViewportHeight) / 2));
            }
        }

        private void Zoom_In_Click(object sender, RoutedEventArgs e)
            => UpdateZoom(_zoom * ZoomStep);

        private void Zoom_Out_Click(object sender, RoutedEventArgs e)
            => UpdateZoom(_zoom / ZoomStep);

        // Ctrl + Mouse Wheel zoom (common UX)
        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta > 0) UpdateZoom(_zoom * ZoomStep);
                else UpdateZoom(_zoom / ZoomStep);
                e.Handled = true;
            }
        }

        // Optional helpers: call these from menu/toolbar later
        private void Zoom_100_Click(object? sender, RoutedEventArgs e) => UpdateZoom(1.0, center: true);

        private void Zoom_Fit_Click(object? sender, RoutedEventArgs e)
        {
            // Fit the Artboard into the Scroller viewport (minus a tiny margin)
            var margin = 16.0;
            var vw = Math.Max(1, Scroller.ViewportWidth - margin);
            var vh = Math.Max(1, Scroller.ViewportHeight - margin);
            var aw = Artboard.Width;
            var ah = Artboard.Height;
            var sx = vw / aw;
            var sy = vh / ah;
            UpdateZoom(Math.Max(MinZoom, Math.Min(sx, sy)), center: true);
        }
        
        private void Tool_TextBox_Click(object sender, RoutedEventArgs e)
        {
            // TODO: place a TextBox on the Artboard at current mouse position
            MessageBox.Show("TODO: Text box tool.");
        }

        private void Colors_Palette_Click(object sender, RoutedEventArgs e)
        {
            // Reuse the same color picker as Tools ▸ Color Picker
            Tool_ColorPicker_Click(sender, e);
        }


        /* -------------------- ARTBOARD RESIZE READY -------------------- */

        // Call this later when you open an image to match canvas to image size.
        private void SetArtboardSize(double width, double height)
        {
            Artboard.Width = width;
            Artboard.Height = height;
            ImageView.Width = width;
            ImageView.Height = height;
            PaintCanvas.Width = width;
            PaintCanvas.Height = height;
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

        /* -------------------- PLACEHOLDERS (unchanged) -------------------- */

        private void File_New_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: New document (create empty Mat + set artboard).");

        private void File_Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                MessageBox.Show($"TODO: Open image: {dlg.FileName} then SetArtboardSize(imageW, imageH)");
            }
        }

        private void File_Save_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: Save merged Image + Strokes.");

        private void File_SaveAs_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: Save As...");

        private void File_Properties_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: Show image properties.");

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

        private void ImageView_Drop(object sender, DragEventArgs e) =>
            MessageBox.Show("TODO: Drag-and-drop open.");
    }
}

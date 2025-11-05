using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using WF = System.Windows.Forms;

namespace PhotoMax
{
    public partial class GridSettingsWindow : Window
    {
        public Color GridColor { get; private set; }
        private double _gridSpacing = 32.0;

        public GridSettingsWindow(Color initialColor)
        {
            InitializeComponent();
            GridColor = initialColor;

            // Init slider from alpha
            OpacitySlider.Value = Math.Round(GridColor.A * 100.0 / 255.0);
            OpacityLabel.Text = $"{(int)OpacitySlider.Value}%";

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            PreviewGrid.Fill = BuildGridBrush(GridColor, _gridSpacing);
        }

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

        private void ChangeColor_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WF.ColorDialog { AllowFullOpen = true, FullOpen = true, Color = System.Drawing.Color.FromArgb(GridColor.R, GridColor.G, GridColor.B) };
            if (dlg.ShowDialog() == WF.DialogResult.OK)
            {
                // Keep the current alpha, update RGB
                GridColor = Color.FromArgb(GridColor.A, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                UpdatePreview();
            }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            byte a = (byte)Math.Max(0, Math.Min(255, Math.Round(255.0 * e.NewValue / 100.0)));
            GridColor = Color.FromArgb(a, GridColor.R, GridColor.G, GridColor.B);
            OpacityLabel.Text = $"{(int)e.NewValue}%";
            UpdatePreview();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}

using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WF = System.Windows.Forms;

namespace PhotoMax
{
    public partial class GridSettingsWindow : Window
    {
        public Color GridColor { get; private set; }
        private double _gridSpacing = 32.0;
        private bool _updatingFromSlider = false;
        private bool _updatingFromTextBox = false;

        public GridSettingsWindow(Color initialColor)
        {
            InitializeComponent();
            GridColor = initialColor;

            // Init slider and textbox from alpha
            double opacityPercent = Math.Round(GridColor.A * 100.0 / 255.0);
            OpacitySlider.Value = opacityPercent;
            OpacityTextBox.Text = $"{(int)opacityPercent}%";
            OpacityLabel.Text = GridColor.A == 0 ? "Transparent (hidden)" : 
                               GridColor.A == 255 ? "Fully opaque" : 
                               $"{(int)opacityPercent}% opacity";

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
            using var dlg = new WF.ColorDialog 
            { 
                AllowFullOpen = true, 
                FullOpen = true, 
                Color = System.Drawing.Color.FromArgb(GridColor.R, GridColor.G, GridColor.B) 
            };
            
            if (dlg.ShowDialog() == WF.DialogResult.OK)
            {
                // Keep the current alpha, update RGB
                GridColor = Color.FromArgb(GridColor.A, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                UpdatePreview();
            }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingFromTextBox) return;
            _updatingFromSlider = true;

            byte a = (byte)Math.Max(0, Math.Min(255, Math.Round(255.0 * e.NewValue / 100.0)));
            GridColor = Color.FromArgb(a, GridColor.R, GridColor.G, GridColor.B);
            
            int percent = (int)e.NewValue;
            OpacityTextBox.Text = $"{percent}%";
            OpacityLabel.Text = a == 0 ? "Transparent (hidden)" : 
                               a == 255 ? "Fully opaque" : 
                               $"{percent}% opacity";
            
            UpdatePreview();
            _updatingFromSlider = false;
        }

        private void OpacityTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Only allow digits and % symbol
            e.Handled = !Regex.IsMatch(e.Text, @"^[0-9%]+$");
        }

        private void OpacityTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_updatingFromSlider) return;
            _updatingFromTextBox = true;

            string text = OpacityTextBox.Text.Replace("%", "").Trim();
            
            if (int.TryParse(text, out int value))
            {
                value = Math.Clamp(value, 0, 100);
                OpacitySlider.Value = value;
                
                // Update display
                byte a = (byte)Math.Round(255.0 * value / 100.0);
                GridColor = Color.FromArgb(a, GridColor.R, GridColor.G, GridColor.B);
                OpacityLabel.Text = a == 0 ? "Transparent (hidden)" : 
                                   a == 255 ? "Fully opaque" : 
                                   $"{value}% opacity";
                UpdatePreview();
            }

            _updatingFromTextBox = false;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
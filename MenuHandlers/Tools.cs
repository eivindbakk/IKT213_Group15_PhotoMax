using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using WF = System.Windows.Forms;

namespace PhotoMax
{
    public partial class MainWindow
    {
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

        private void Tool_Erase_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = !_eraseMode;
            PaintCanvas.EditingMode = _eraseMode ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink;
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

        private void Tool_Brushes_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = false;
            PaintCanvas.EditingMode = InkCanvasEditingMode.Ink;
            ConfigureBrush();
        }

        private void Tool_TextBox_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("TODO: Text box tool.");
        }

        private void Filter_Gaussian_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Gaussian (OpenCvSharp).");

        private void Filter_Sobel_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Sobel (OpenCvSharp).");

        private void Filter_Binary_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Binary threshold.");

        private void Filter_HistogramThreshold_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Histogram threshold.");
    }
}


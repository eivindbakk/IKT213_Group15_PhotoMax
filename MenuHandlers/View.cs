using System.Windows;

namespace PhotoMax
{
    public partial class MainWindow
    {
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
    }
}
using System.Windows;

namespace PhotoMax
{
    public partial class MainWindow
    {
        // Color Palette uses the same handler as Tool_ColorPicker_Click (in Tools.cs)
        
        private void Colors_BrushSize_Click(object sender, RoutedEventArgs e)
        {
            _brushIndex = (_brushIndex + 1) % _brushSizes.Length;
            ConfigureBrush();
        }
    }
}


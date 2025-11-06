using System.Windows;
using Microsoft.Win32;

namespace PhotoMax
{
    public partial class MainWindow
    {
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

        private void File_Save_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Save merged Image + Strokes.");

        private void File_SaveAs_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Save As...");

        private void File_Properties_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Show image properties.");

        private void File_Quit_Click(object sender, RoutedEventArgs e) => Close();
    }
}


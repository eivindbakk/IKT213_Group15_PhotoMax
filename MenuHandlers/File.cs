using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        private void File_Save_Click(object sender, RoutedEventArgs e) 
        {
            var dlg = new SaveFileDialog
            {
                FileName = "Image",
                DefaultExt = ".png",
                Filter = "PNG Image (.png)|*.png|JPEG Image (.jpg)|*.jpg;*.jpeg|Bitmap Image (.bmp)|*.bmp|TIFF Image (.tif)|*.tif;*.tiff",
                Title = "Save Image As"
            };

            if (dlg.ShowDialog() == true)
            {
                try 
                {
                    SaveCanvasToFile(dlg.FileName);
                    MessageBox.Show("Image saved successfully!", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) 
                {
                    MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveCanvasToFile(string filename)
        {
            // Get the size of the canvas
            double width = Artboard.ActualWidth;
            double height = Artboard.ActualHeight;

            // Create a render target bitmap to capture the canvas (image + strokes)
            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(
                (int)width, (int)height, 96, 96, PixelFormats.Pbgra32);

            // Render the Artboard (which includes ImageView and PaintCanvas)
            renderBitmap.Render(Artboard);

            // Choose encoder based on file extension
            BitmapEncoder encoder;
            string ext = System.IO.Path.GetExtension(filename).ToLower();
            
            encoder = ext switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".bmp" => new BmpBitmapEncoder(),
                ".tif" or ".tiff" => new TiffBitmapEncoder(),
                _ => new PngBitmapEncoder()
            };

            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

            // Save to file
            using (FileStream stream = new FileStream(filename, FileMode.Create))
            {
                encoder.Save(stream);
            }
        } 
            

        private void File_SaveAs_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Save As...");

        private void File_Properties_Click(object sender, RoutedEventArgs e) => 
            MessageBox.Show("TODO: Show image properties.");

        private void File_Quit_Click(object sender, RoutedEventArgs e) => Close();
    }
}


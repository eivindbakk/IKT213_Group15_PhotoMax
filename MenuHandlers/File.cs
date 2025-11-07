using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace PhotoMax
{
    public partial class MainWindow
    {
        private void File_New_Click(object sender, RoutedEventArgs e)
        {
            // Check for unsaved changes before creating new document
            if (_hasUnsavedChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save before creating a new document?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    File_Save_Click(sender, e);
                    // If user cancelled the save dialog, don't proceed
                    if (_hasUnsavedChanges) return;
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    return; // Don't create new document
                }
            }

            // Clear the canvas
            ResetCanvas();
            
            // Reset file tracking
            _currentFilePath = null;
            _hasUnsavedChanges = false;
            
            StatusText.Content = "New document created";
        }

        private void ResetCanvas()
        {
			// Reset underlying document to a blank 1280x720 BGRA image and refresh view
			try
			{
				if (_img != null)
				{
					using var blank = new OpenCvSharp.Mat(new OpenCvSharp.Size(1280, 720), OpenCvSharp.MatType.CV_8UC4, new OpenCvSharp.Scalar(255, 255, 255, 255));
					_img.Doc.ReplaceWith(blank.Clone());
					_img.ForceRefreshView();
				}
				else
				{
					ImageView.Source = null;
					SetArtboardSize(1280, 720);
				}
			}
			finally
			{
				// Clear all ink strokes
				PaintCanvas.Strokes.Clear();
			}
        }

        private void File_Open_Click(object sender, RoutedEventArgs e)
        {
            // Check for unsaved changes
            if (_hasUnsavedChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save before opening a new image?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    File_Save_Click(sender, e);
                    if (_hasUnsavedChanges) return; // User cancelled save
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    return; // Don't open new image
                }
            }

            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*",
                Title = "Open Image"
            };
            
			if (dlg.ShowDialog() == true)
            {
                try
                {
					// Load image using OpenCvSharp and ensure BGRA format for the document
					using (Mat src = Cv2.ImRead(dlg.FileName, ImreadModes.Unchanged))
					{
						if (src.Empty())
						{
							MessageBox.Show("Failed to load image.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
							return;
						}

						Mat bgra;
						if (src.Channels() == 1)
						{
							bgra = new Mat();
							Cv2.CvtColor(src, bgra, ColorConversionCodes.GRAY2BGRA);
						}
						else if (src.Channels() == 3)
						{
							bgra = new Mat();
							Cv2.CvtColor(src, bgra, ColorConversionCodes.BGR2BGRA);
						}
						else if (src.Channels() == 4)
						{
							bgra = src.Clone();
						}
						else
						{
							MessageBox.Show($"Unsupported image with {src.Channels()} channels.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
							return;
						}

						using (bgra)
						{
							// Clear existing strokes
							PaintCanvas.Strokes.Clear();

							// Update centralized document so all tools/filters operate on the same image
							if (_img != null)
							{
								_img.Doc.ReplaceWith(bgra.Clone());
								_img.ForceRefreshView();
							}
							else
							{
								// Fallback for safety
								ImageView.Source = bgra.ToBitmapSource();
								SetArtboardSize(bgra.Width, bgra.Height);
							}

							// Update tracking
							_currentFilePath = dlg.FileName;
							_hasUnsavedChanges = false;
							StatusText.Content = $"Opened: {Path.GetFileName(dlg.FileName)} ({bgra.Width}x{bgra.Height})";
						}
					}
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void File_Save_Click(object sender, RoutedEventArgs e) 
        {
            // If no current file, prompt for location (like Save As)
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                File_SaveAs_Click(sender, e);
                return;
            }

            // Save to existing file without prompting
            try 
            {
                SaveCanvasToFile(_currentFilePath);
                _hasUnsavedChanges = false; // Clear unsaved changes flag
                StatusText.Content = $"Saved: {Path.GetFileName(_currentFilePath)}";
            }
            catch (Exception ex) 
            {
                MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            

        private void File_SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = string.IsNullOrEmpty(_currentFilePath) 
                    ? "Image" 
                    : Path.GetFileNameWithoutExtension(_currentFilePath),
                DefaultExt = ".png",
                Filter = "PNG Image (.png)|*.png|JPEG Image (.jpg)|*.jpg;*.jpeg|Bitmap Image (.bmp)|*.bmp|TIFF Image (.tif)|*.tif;*.tiff",
                Title = "Save Image As"
            };

            if (dlg.ShowDialog() == true)
            {
                try 
                {
                    SaveCanvasToFile(dlg.FileName);
                    _currentFilePath = dlg.FileName; // Update current file path
                    _hasUnsavedChanges = false; // Clear unsaved changes flag
                    StatusText.Content = $"Saved: {Path.GetFileName(_currentFilePath)}";
                }
                catch (Exception ex) 
                {
                    MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void File_Properties_Click(object sender, RoutedEventArgs e)
        {
            // Check if there's an image loaded
            if (ImageView.Source == null)
            {
                MessageBox.Show("No image loaded.", "Properties", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                StringBuilder props = new StringBuilder();
                
                // Get image dimensions from ImageView
                BitmapSource? bitmapSource = ImageView.Source as BitmapSource;
                if (bitmapSource != null)
                {
                    props.AppendLine($"Dimensions: {bitmapSource.PixelWidth} x {bitmapSource.PixelHeight} pixels");
                    props.AppendLine($"DPI: {bitmapSource.DpiX} x {bitmapSource.DpiY}");
                    props.AppendLine($"Format: {bitmapSource.Format}");
                    props.AppendLine($"Bits per pixel: {bitmapSource.Format.BitsPerPixel}");
                }

                // If we have a current file, get additional info using OpenCvSharp
                if (!string.IsNullOrEmpty(_currentFilePath) && File.Exists(_currentFilePath))
                {
                    props.AppendLine();
                    props.AppendLine($"File: {Path.GetFileName(_currentFilePath)}");
                    props.AppendLine($"Path: {Path.GetDirectoryName(_currentFilePath)}");
                    
                    FileInfo fileInfo = new FileInfo(_currentFilePath);
                    props.AppendLine($"Size: {FormatFileSize(fileInfo.Length)}");
                    props.AppendLine($"Modified: {fileInfo.LastWriteTime}");

                    // Use OpenCvSharp to get color information
                    using (Mat mat = Cv2.ImRead(_currentFilePath, ImreadModes.Unchanged))
                    {
                        if (!mat.Empty())
                        {
                            props.AppendLine();
                            props.AppendLine($"Channels: {mat.Channels()}");
                            props.AppendLine($"Depth: {mat.Depth()}");
                            props.AppendLine($"Type: {mat.Type()}");
                            
                            // Determine color space
                            string colorSpace = mat.Channels() switch
                            {
                                1 => "Grayscale",
                                3 => "BGR (Color)",
                                4 => "BGRA (Color + Alpha)",
                                _ => $"{mat.Channels()} channels"
                            };
                            props.AppendLine($"Color Space: {colorSpace}");
                        }
                    }
                }
                else
                {
                    props.AppendLine();
                    props.AppendLine("File: [Unsaved]");
                }

                // Show stroke information
                props.AppendLine();
                props.AppendLine($"Ink Strokes: {PaintCanvas.Strokes.Count}");
                props.AppendLine($"Modified: {(_hasUnsavedChanges ? "Yes" : "No")}");

                MessageBox.Show(props.ToString(), "Image Properties", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading properties: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void File_Quit_Click(object sender, RoutedEventArgs e) 
        {
            if (_hasUnsavedChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save before quitting?", 
                    "Unsaved Changes", 
                    MessageBoxButton.YesNoCancel, 
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    File_Save_Click(sender, e);
                    // Only close if save was successful (changes were cleared)
                    if (!_hasUnsavedChanges)
                    {
                        Close();
                    }
                }
                else if (result == MessageBoxResult.No)
                {
                    Close();
                }
                // Cancel = do nothing, stay in the app
            }
            else
            {
                Close();
            }
        }
    }
}


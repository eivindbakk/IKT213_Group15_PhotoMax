// File: MenuHandlers/Layers.cs
// Minimal raster layer system for PhotoMax.
// - Normal blend only
// - Active layer editing (brush, shapes, text all go to active layer via ImageController.Mat)
// - Composite shown in ImageView via ImageController.RefreshView()
// - Transforms (rotate/flip/resize/crop) apply to all layers

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;  // **ADD THIS**
using Microsoft.Win32;
using OpenCvSharp;

// Alias to disambiguate from OpenCvSharp.Window
using WWindow = System.Windows.Window;

namespace PhotoMax
{
    public sealed class Layer : IDisposable
    {
        // internal setter so the stack can replace Mats on transforms/crop/resize.
        public Mat Mat { get; internal set; }    // CV_8UC4 (BGRA)
        public string Name { get; set; }
        public bool Visible { get; set; } = true;

        public int Width  => Mat?.Cols ?? 0;
        public int Height => Mat?.Rows ?? 0;

        public Layer(int w, int h, string name, bool opaqueWhite = false)
        {
            Mat = new Mat(h, w, MatType.CV_8UC4);
            if (opaqueWhite) Mat.SetTo(new Scalar(255, 255, 255, 255));
            else             Mat.SetTo(new Scalar(0, 0, 0, 0)); // transparent
            Name = name;
        }

        public Layer(Mat src, string name)
        {
            Mat = EnsureBGRA(src).Clone();
            Name = name;
        }

        public void Dispose()
        {
            Mat?.Dispose();
            Mat = null!;
        }

        internal static Mat EnsureBGRA(Mat src)
        {
            if (src.Empty()) return new Mat();
            if (src.Type() == MatType.CV_8UC4) return src;
            if (src.Type() == MatType.CV_8UC3)
            {
                var d = new Mat();
                Cv2.CvtColor(src, d, ColorConversionCodes.BGR2BGRA);
                return d;
            }
            if (src.Type() == MatType.CV_8UC1)
            {
                var d = new Mat();
                Cv2.CvtColor(src, d, ColorConversionCodes.GRAY2BGRA);
                return d;
            }
            throw new NotSupportedException($"Unsupported Mat type: {src.Type()}");
        }
    }

    public sealed class LayerStack : IDisposable
    {
        public List<Layer> Layers { get; } = new();
        public int ActiveIndex { get; private set; } = 0;

        public Layer Active => Layers[Math.Clamp(ActiveIndex, 0, Math.Max(0, Layers.Count - 1))];
        public Mat ActiveMat => Active.Mat;

        public int Width  => Layers.Count == 0 ? 0 : Layers[0].Width;
        public int Height => Layers.Count == 0 ? 0 : Layers[0].Height;

        public void Dispose()
        {
            foreach (var l in Layers) l.Dispose();
            Layers.Clear();
        }

        public void InitNew(int w, int h)
        {
            Dispose();
            Layers.Add(new Layer(w, h, "Background", opaqueWhite: true));
            ActiveIndex = 0;
        }

        public void SetSingleFromMat(Mat src, string? name = null)
        {
            Dispose();
            var bgra = Layer.EnsureBGRA(src);
            Layers.Add(new Layer(bgra, name ?? "Background"));
            ActiveIndex = 0;
        }

        public void AddBlank(string? name = null)
        {
            if (Layers.Count == 0) throw new InvalidOperationException("Initialize document first.");
            Layers.Add(new Layer(Width, Height, name ?? $"Layer {Layers.Count}"));
            ActiveIndex = Layers.Count - 1;
        }

        public void AddFromMat(Mat src, string? name = null, bool resizeToDoc = true)
        {
            if (Layers.Count == 0) throw new InvalidOperationException("Initialize document first.");
            var bgra = Layer.EnsureBGRA(src);
            Mat mat = bgra;
            if (resizeToDoc && (bgra.Cols != Width || bgra.Rows != Height))
            {
                mat = new Mat();
                var mode = (bgra.Cols > Width || bgra.Rows > Height) ? InterpolationFlags.Area : InterpolationFlags.Nearest;
                Cv2.Resize(bgra, mat, new OpenCvSharp.Size(Width, Height), 0, 0, mode);
            }
            Layers.Add(new Layer(mat, name ?? $"Layer {Layers.Count}"));
            ActiveIndex = Layers.Count - 1;
        }

        public void DeleteActive()
        {
            if (Layers.Count <= 1) return; // keep background
            Active.Mat.Dispose();
            Layers.RemoveAt(ActiveIndex);
            ActiveIndex = Math.Clamp(ActiveIndex, 0, Layers.Count - 1);
        }

        public void RenameActive(string newName) => Active.Name = newName;
        public void Select(int index) => ActiveIndex = Math.Clamp(index, 0, Layers.Count - 1);
        public void ToggleActiveVisibility() => Active.Visible = !Active.Visible;

        // ----- transforms on all layers -----
        public void RotateRight90()
        {
            foreach (var l in Layers)
            {
                var d = new Mat();
                Cv2.Rotate(l.Mat, d, RotateFlags.Rotate90Clockwise);
                l.Mat.Dispose();
                l.Mat = d;
            }
        }
        public void RotateLeft90()
        {
            foreach (var l in Layers)
            {
                var d = new Mat();
                Cv2.Rotate(l.Mat, d, RotateFlags.Rotate90Counterclockwise);
                l.Mat.Dispose();
                l.Mat = d;
            }
        }
        public void FlipHorizontal()
        {
            foreach (var l in Layers)
            {
                var d = new Mat();
                Cv2.Flip(l.Mat, d, FlipMode.Y);
                l.Mat.Dispose();
                l.Mat = d;
            }
        }
        public void FlipVertical()
        {
            foreach (var l in Layers)
            {
                var d = new Mat();
                Cv2.Flip(l.Mat, d, FlipMode.X);
                l.Mat.Dispose();
                l.Mat = d;
            }
        }
        public void ResizeTo(int newW, int newH, InterpolationFlags mode)
        {
            foreach (var l in Layers)
            {
                var d = new Mat();
                Cv2.Resize(l.Mat, d, new OpenCvSharp.Size(newW, newH), 0, 0, mode);
                l.Mat.Dispose();
                l.Mat = d;
            }
        }
        public void Crop(int x, int y, int width, int height)
        {
            var rect = new OpenCvSharp.Rect(
                Math.Clamp(x, 0, Math.Max(0, Width - 1)),
                Math.Clamp(y, 0, Math.Max(0, Height - 1)),
                Math.Clamp(width,  1, Math.Max(1, Width  - x)),
                Math.Clamp(height, 1, Math.Max(1, Height - y)));
            foreach (var l in Layers)
            {
                using var roi = new Mat(l.Mat, rect);
                var copy = roi.Clone();
                l.Mat.Dispose();
                l.Mat = copy;
            }
        }

        // ----- composite (normal blend in stack order) -----
        // ----- composite (normal blend in stack order) -----
        public Mat Composite()
        {
            if (Layers.Count == 0) return new Mat();
            var dst = new Mat(Height, Width, MatType.CV_8UC4);
    
            // **FIX: Start with white background instead of black**
            dst.SetTo(new Scalar(255, 255, 255, 255)); // White opaque background
    
            foreach (var l in Layers)
                if (l.Visible) AlphaBlendOver(dst, l.Mat);
    
            MakeOpaque(dst); // keep final view opaque (matches previous behaviour)
            return dst;
        }

        private static void AlphaBlendOver(Mat dst, Mat src)
        {
            int w = Math.Min(dst.Cols, src.Cols), h = Math.Min(dst.Rows, src.Rows);
            int dStep = (int)dst.Step(), sStep = (int)src.Step();
            var dArr = new byte[dStep * dst.Rows];
            var sArr = new byte[sStep * src.Rows];
            System.Runtime.InteropServices.Marshal.Copy(dst.Data, dArr, 0, dArr.Length);
            System.Runtime.InteropServices.Marshal.Copy(src.Data, sArr, 0, sArr.Length);
            for (int y = 0; y < h; y++)
            {
                int dRow = y * dStep, sRow = y * sStep;
                for (int x = 0; x < w; x++)
                {
                    int di = dRow + x * 4, si = sRow + x * 4;
                    byte sb = sArr[si + 0], sg = sArr[si + 1], sr = sArr[si + 2], sa = sArr[si + 3];
                    if (sa == 0) continue;
                    double a = sa / 255.0;
                    dArr[di + 0] = (byte)Math.Clamp(sb * a + dArr[di + 0] * (1 - a), 0, 255);
                    dArr[di + 1] = (byte)Math.Clamp(sg * a + dArr[di + 1] * (1 - a), 0, 255);
                    dArr[di + 2] = (byte)Math.Clamp(sr * a + dArr[di + 2] * (1 - a), 0, 255);
                    dArr[di + 3] = 255;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(dArr, 0, dst.Data, dArr.Length);
        }

        private static void MakeOpaque(Mat m)
        {
            int step = (int)m.Step();
            var data = new byte[step * m.Rows];
            System.Runtime.InteropServices.Marshal.Copy(m.Data, data, 0, data.Length);
            for (int y = 0; y < m.Rows; y++)
            {
                int row = y * step;
                for (int x = 0; x < m.Cols; x++) data[row + x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(data, 0, m.Data, data.Length);
        }
    }

    public partial class MainWindow
    {
        // ----- Menu handlers (wired in your XAML) -----

        private void Layers_New_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) { MessageBox.Show("Open or create an image first."); return; }
            _img.Layers_AddBlank();
            StatusText.Content = $"Added layer '{_img.Layers_ActiveName}'";
        }

        private void Layers_Load_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) { MessageBox.Show("Open or create an image first."); return; }
            var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" };
            if (dlg.ShowDialog() == true)
            {
                _img.Layers_AddFromFile(dlg.FileName);
                StatusText.Content = $"Loaded layer '{_img.Layers_ActiveName}'";
            }
        }

        // “Edit Layer” dialog: rename + optional “toggle visibility now”
        private void Layers_Edit_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;

            var w = new WWindow
            {
                Title = "Layer properties",
                Width = 320,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            namePanel.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Width = 70 });
            var nameBox = new TextBox { Text = _img.Layers_ActiveName, MinWidth = 180 };
            namePanel.Children.Add(nameBox);

            // Stateless checkbox: applies a single toggle if checked.
            var toggleNow = new CheckBox { Content = "Toggle visibility now", Margin = new Thickness(0, 0, 0, 8) };

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            btnPanel.Children.Add(ok);
            btnPanel.Children.Add(cancel);

            Grid.SetRow(namePanel, 0);
            Grid.SetRow(toggleNow, 1);
            Grid.SetRow(btnPanel, 2);
            root.Children.Add(namePanel);
            root.Children.Add(toggleNow);
            root.Children.Add(btnPanel);

            w.Content = root;

            ok.Click += (_, __) =>
            {
                var newName = nameBox.Text?.Trim();
                if (!string.IsNullOrEmpty(newName) && newName != _img.Layers_ActiveName)
                    _img.Layers_RenameActive(newName);

                if (toggleNow.IsChecked == true)
                    _img.Layers_ToggleActiveVisibility();

                w.DialogResult = true;
                w.Close();
            };

            w.ShowDialog();
            StatusText.Content = $"Edited layer '{_img.Layers_ActiveName}'";
        }

        private void Layers_Select_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
            var names = _img.Layers_AllNames;
            if (names.Count == 0) return;

            var dlg = new LayerSelectWindow(_img.Layers_GetActiveIndex(), names)
            {
                Owner = this
            };

            if (dlg.ShowDialog() == true)
            {
                _img.Layers_Select(dlg.SelectedIndex);
                StatusText.Content = $"Active layer: [{dlg.SelectedIndex}] '{_img.Layers_ActiveName}'";
            }
        }

        private void Layers_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
            _img.Layers_DeleteActive();
            StatusText.Content = "Deleted layer (if not background).";
        }

        private void Layers_Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
    
            var dlg = new LayerRenameWindow(_img.Layers_ActiveName)
            {
                Owner = this
            };

            if (dlg.ShowDialog() == true)
            {
                _img.Layers_RenameActive(dlg.NewName);
                StatusText.Content = $"Renamed to '{_img.Layers_ActiveName}'";
            }
        }
    }
    // ========== LAYER SELECT WINDOW ==========
internal sealed class LayerSelectWindow : WWindow
{
    public int SelectedIndex { get; private set; }
    
    private readonly ListBox _layerList;

    public LayerSelectWindow(int currentIndex, List<string> layerNames)
    {
        SelectedIndex = currentIndex;

        Title = "Select Layer";
        Width = 400;
        Height = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        
        Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
        Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 243, 243));

        var mainGrid = new Grid { Margin = new Thickness(20) };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new TextBlock
        {
            Text = "LAYERS",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(header, 0);

        // Layer list
        _layerList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 45, 45, 45)),
            Foreground = Foreground,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 63, 63, 70)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            FontSize = 13,
            SelectionMode = SelectionMode.Single
        };

        // Populate with layer items
        for (int i = layerNames.Count - 1; i >= 0; i--) // Top to bottom (reverse order)
        {
            var layer = layerNames[i];
            var item = new LayerListItem(i, layer, i == currentIndex);
            _layerList.Items.Add(item);
            
            if (i == currentIndex)
            {
                _layerList.SelectedItem = item;
            }
        }

        _layerList.SelectionChanged += (s, e) =>
        {
            if (_layerList.SelectedItem is LayerListItem item)
            {
                SelectedIndex = item.Index;
            }
        };

        // Double-click to confirm
        _layerList.MouseDoubleClick += (s, e) =>
        {
            if (_layerList.SelectedItem != null)
            {
                DialogResult = true;
                Close();
            }
        };

        Grid.SetRow(_layerList, 1);

        // Info text
        var infoText = new TextBlock
        {
            Text = "Double-click to select, or click OK",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
            Margin = new Thickness(0, 12, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(infoText, 1);
        infoText.VerticalAlignment = VerticalAlignment.Bottom;

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        var okBtn = new Button
        {
            Content = "OK",
            Width = 80,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okBtn.Click += (s, e) => { DialogResult = true; Close(); };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true
        };

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);
        Grid.SetRow(buttonPanel, 2);

        mainGrid.Children.Add(header);
        mainGrid.Children.Add(_layerList);
        mainGrid.Children.Add(infoText);
        mainGrid.Children.Add(buttonPanel);

        Content = mainGrid;
        
        // Focus the list for keyboard navigation
        Loaded += (s, e) => _layerList.Focus();
    }

    // Custom item for rich display
    private class LayerListItem
    {
        public int Index { get; }
        public string Name { get; }
        public bool IsActive { get; }

        public LayerListItem(int index, string name, bool isActive)
        {
            Index = index;
            Name = name;
            IsActive = isActive;
        }

        public override string ToString()
        {
            var active = IsActive ? " ★" : "";
            return $"[{Index}] {Name}{active}";
        }
    }
}
// ========== LAYER RENAME WINDOW ==========
internal sealed class LayerRenameWindow : WWindow
{
    public string NewName { get; private set; }

    public LayerRenameWindow(string currentName)
    {
        NewName = currentName;

        Title = "Rename Layer";
        Width = 320;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        // **DARK THEME - matching Edit dialog**
        Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        
        var label = new TextBlock 
        { 
            Text = "Name:", 
            VerticalAlignment = VerticalAlignment.Center, 
            Width = 70,
            Foreground = Brushes.White  // **WHITE TEXT**
        };
        namePanel.Children.Add(label);
        
        var nameBox = new TextBox 
        { 
            Text = currentName, 
            MinWidth = 180,
            Background = new SolidColorBrush(Color.FromArgb(255, 50, 50, 50)),  // **DARK INPUT**
            Foreground = Brushes.White,  // **WHITE TEXT**
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70)),
            CaretBrush = Brushes.White
        };
        namePanel.Children.Add(nameBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        
        var ok = new Button 
        { 
            Content = "OK", 
            Width = 70, 
            Margin = new Thickness(0, 0, 8, 0), 
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromArgb(255, 50, 50, 50)),  // **DARK BUTTON**
            Foreground = Brushes.White
        };
        
        var cancel = new Button 
        { 
            Content = "Cancel", 
            Width = 70, 
            IsCancel = true,
            Background = new SolidColorBrush(Color.FromArgb(255, 50, 50, 50)),  // **DARK BUTTON**
            Foreground = Brushes.White
        };
        
        btnPanel.Children.Add(ok);
        btnPanel.Children.Add(cancel);

        Grid.SetRow(namePanel, 0);
        Grid.SetRow(btnPanel, 1);
        root.Children.Add(namePanel);
        root.Children.Add(btnPanel);

        Content = root;

        ok.Click += (_, __) =>
        {
            var trimmed = nameBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                NewName = trimmed;
                DialogResult = true;
                Close();
            }
        };

        // Focus and select all for easy editing
        Loaded += (s, e) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };
    }
}
}

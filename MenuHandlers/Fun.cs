// File: MenuHandlers/Fun.cs
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoMax
{
    public partial class MainWindow
    {
        // ===== Canvas filter session state =====
        private Image? _filterOverlay;           // preview/result bitmap layer
        private Border? _filterToolbar;          // small control panel
        private WriteableBitmap? _filterWB;      // destination bitmap
        private byte[]? _filterSrc;              // canvas snapshot (Pbgra32)
        private byte[]? _filterDst;              // working buffer
        private int _fw, _fh, _fstride;

        private string _filterMode = "Grayscale"; // Grayscale | Invert | Sepia | Posterize | Pixelate
        private double _filterAmount = 1.0;       // 0..1
        private int _posterizeLevels = 4;         // 2..8
        private int _pixelBlock = 8;              // 2..40
        private bool _filterActive = false;

        // Smoothing: throttle preview while dragging sliders
        private DispatcherTimer? _filterTimer;
        private bool _filterDirty;

        // UI refs
        private ComboBox? _modeCombo;
        private Slider? _amtSlider;
        private Slider? _lvlSlider;
        private Slider? _blkSlider;

        // Entry from menu: Fun → Filters
        private void Fun_Filters_Click(object sender, RoutedEventArgs e)
        {
            if (_filterActive)
            {
                MessageBox.Show("A filter session is already active.\nPress Enter to apply or Esc to cancel.",
                    "PhotoMax", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var board = GetArtboardOrNull();
            if (board == null || board.ActualWidth < 1 || board.ActualHeight < 1)
            {
                MessageBox.Show("Canvas isn’t ready yet. Try again once it’s visible.",
                    "PhotoMax", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StartCanvasFilterSession(board);
        }

        // ---- session lifecycle -------------------------------------------------

        private void StartCanvasFilterSession(Canvas board)
        {
            (_filterSrc, _fw, _fh, _fstride) = SnapshotCanvas(board);
            if (_filterSrc == null || _filterSrc.Length == 0 || _fw == 0 || _fh == 0)
                return;

            _filterDst = new byte[_filterSrc.Length];
            _filterWB  = new WriteableBitmap(_fw, _fh, 96, 96, PixelFormats.Pbgra32, null);

            // overlay for live preview (and final result after Apply)
            _filterOverlay = new Image
            {
                Source = _filterWB,
                IsHitTestVisible = false // never block interactions
            };

            // Place the overlay BELOW interaction layers (InkCanvas / PaintCanvas)
            AddOverlayUnderInteractionLayer(board, _filterOverlay);

            // toolbar UI (topmost while editing)
            _filterToolbar = BuildFilterToolbar(board);
            Canvas.SetLeft(_filterToolbar, 8);
            Canvas.SetTop(_filterToolbar, 8);
            Panel.SetZIndex(_filterToolbar, int.MaxValue);
            board.Children.Add(_filterToolbar);

            // start throttle timer (~30 fps). It only renders when _filterDirty is true.
            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _filterTimer.Tick += (_, __) =>
            {
                if (_filterDirty)
                {
                    _filterDirty = false;
                    ApplyFilterAndUpdate();
                }
            };
            _filterTimer.Start();

            _filterActive = true;
            this.PreviewKeyDown += OnFilter_PreviewKeyDown;

            // First render immediately so user sees something
            ApplyFilterAndUpdate();
        }

        private void EndCanvasFilterSession(bool keepOverlay)
        {
            var board = GetArtboardOrNull();
            if (board == null) return;

            _filterTimer?.Stop();
            _filterTimer = null;

            if (!keepOverlay && _filterOverlay != null)
                board.Children.Remove(_filterOverlay);

            if (_filterToolbar != null)
                board.Children.Remove(_filterToolbar);

            // If we keep the overlay, ensure it remains under interaction layers
            if (keepOverlay && _filterOverlay != null)
                AddOverlayUnderInteractionLayer(board, _filterOverlay);

            _filterOverlay = null;
            _filterToolbar = null;
            _filterWB = null;
            _filterSrc = null;
            _filterDst = null;
            _modeCombo = null;
            _amtSlider = null;
            _lvlSlider = null;
            _blkSlider = null;
            _filterActive = false;
            _filterDirty = false;

            this.PreviewKeyDown -= OnFilter_PreviewKeyDown;
        }

        private void OnFilter_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_filterActive) return;
            if (e.Key == Key.Enter)
            {
                // keep overlay as a static bitmap layer (still under InkCanvas)
                EndCanvasFilterSession(keepOverlay: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndCanvasFilterSession(keepOverlay: false);
                e.Handled = true;
            }
        }

        // ---- toolbar -----------------------------------------------------------

        private Border BuildFilterToolbar(Canvas _)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 25, 25, 25)),
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8)
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(2) };

            var title = new TextBlock
            {
                Text = "Canvas Filter",
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(title);

            _modeCombo = new ComboBox
            {
                ItemsSource = new[] { "Grayscale", "Invert", "Sepia", "Posterize", "Pixelate" },
                SelectedItem = _filterMode,
                Width = 220,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _modeCombo.SelectionChanged += (s, e) =>
            {
                _filterMode = (string)_modeCombo.SelectedItem!;
                UpdateExtrasVisibility();
                _filterDirty = true; // defer heavy work to timer
            };
            stack.Children.Add(_modeCombo);

            _amtSlider = MakeLabeledSlider(stack, "Amount", 0, 1, _filterAmount, 0.01, v =>
            {
                _filterAmount = v;
                _filterDirty = true; // throttle updates for smooth dragging
            });

            _lvlSlider = MakeLabeledSlider(stack, "Levels", 2, 8, _posterizeLevels, 1, v =>
            {
                _posterizeLevels = (int)Math.Round(v);
                _filterDirty = true;
            });

            _blkSlider = MakeLabeledSlider(stack, "Block", 2, 40, _pixelBlock, 1, v =>
            {
                _pixelBlock = (int)Math.Round(v);
                _filterDirty = true;
            });

            UpdateExtrasVisibility();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var apply = new Button { Content = "Apply (Enter)", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
            apply.Click += (s, e) => EndCanvasFilterSession(keepOverlay: true);

            var cancel = new Button { Content = "Cancel (Esc)", Padding = new Thickness(10, 4, 10, 4) };
            cancel.Click += (s, e) => EndCanvasFilterSession(keepOverlay: false);

            row.Children.Add(apply);
            row.Children.Add(cancel);
            stack.Children.Add(row);

            border.Child = stack;
            return border;

            // local helper
            static Slider MakeLabeledSlider(Panel parent, string label, double min, double max, double val, double step, Action<double> onChange)
            {
                var labelBlock = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.Gainsboro,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                parent.Children.Add(labelBlock);

                var s = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    Value = val,
                    TickFrequency = step,
                    // Smoothness helpers:
                    IsSnapToTickEnabled = false, // free movement
                    IsMoveToPointEnabled = true, // click to set directly
                    SmallChange = step,
                    LargeChange = Math.Max(step * 10, 0.1),
                    Width = 220
                };
                s.ValueChanged += (_, e) => onChange(e.NewValue);
                parent.Children.Add(s);
                return s;
            }
        }

        private void UpdateExtrasVisibility()
        {
            if (_lvlSlider != null)
                _lvlSlider.Visibility = (_filterMode == "Posterize") ? Visibility.Visible : Visibility.Collapsed;
            if (_blkSlider != null)
                _blkSlider.Visibility = (_filterMode == "Pixelate") ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- rendering ---------------------------------------------------------

        private void ApplyFilterAndUpdate()
        {
            if (_filterSrc == null || _filterDst == null || _filterWB == null) return;

            switch (_filterMode)
            {
                case "Invert":     Filter_Invert();     break;
                case "Sepia":      Filter_Sepia();      break;
                case "Posterize":  Filter_Posterize();  break;
                case "Pixelate":   Filter_Pixelate();   break;
                default:           Filter_Grayscale();  break;
            }

            var rect = new Int32Rect(0, 0, _fw, _fh);
            _filterWB.WritePixels(rect, _filterDst, _fstride, 0);
        }

        private void Filter_Grayscale()
        {
            var amt = _filterAmount;
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte b = src[i + 0], g = src[i + 1], r = src[i + 2], a = src[i + 3];
                byte y = (byte)Math.Clamp((int)Math.Round(0.114 * b + 0.587 * g + 0.299 * r), 0, 255);
                dst[i + 0] = LerpByte(b, y, amt);
                dst[i + 1] = LerpByte(g, y, amt);
                dst[i + 2] = LerpByte(r, y, amt);
                dst[i + 3] = a;
            }
        }

        private void Filter_Invert()
        {
            var amt = _filterAmount;
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte b = src[i + 0], g = src[i + 1], r = src[i + 2], a = src[i + 3];
                dst[i + 0] = LerpByte(b, (byte)(255 - b), amt);
                dst[i + 1] = LerpByte(g, (byte)(255 - g), amt);
                dst[i + 2] = LerpByte(r, (byte)(255 - r), amt);
                dst[i + 3] = a;
            }
        }

        private void Filter_Sepia()
        {
            var amt = _filterAmount;
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte b = src[i + 0], g = src[i + 1], r = src[i + 2], a = src[i + 3];
                byte y = (byte)Math.Clamp((int)Math.Round(0.114 * b + 0.587 * g + 0.299 * r), 0, 255);
                int sr = Math.Min(255, (int)(y * 1.07));
                int sg = Math.Min(255, (int)(y * 0.87));
                int sb = Math.Min(255, (int)(y * 0.55));
                dst[i + 0] = LerpByte(b, (byte)sb, amt);
                dst[i + 1] = LerpByte(g, (byte)sg, amt);
                dst[i + 2] = LerpByte(r, (byte)sr, amt);
                dst[i + 3] = a;
            }
        }

        private void Filter_Posterize()
        {
            var amt = _filterAmount;
            int levels = Math.Max(2, Math.Min(8, _posterizeLevels));
            double step = 255.0 / (levels - 1);

            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte b = src[i + 0], g = src[i + 1], r = src[i + 2], a = src[i + 3];

                // Quantize each channel to the nearest "step"
                byte qb = (byte)Math.Clamp((int)Math.Round(Math.Round(b / step) * step), 0, 255);
                byte qg = (byte)Math.Clamp((int)Math.Round(Math.Round(g / step) * step), 0, 255);
                byte qr = (byte)Math.Clamp((int)Math.Round(Math.Round(r / step) * step), 0, 255);

                dst[i + 0] = LerpByte(b, qb, amt);
                dst[i + 1] = LerpByte(g, qg, amt);
                dst[i + 2] = LerpByte(r, qr, amt);
                dst[i + 3] = a;
            }
        }

        private void Filter_Pixelate()
        {
            var amt = _filterAmount;
            int block = Math.Max(2, Math.Min(40, _pixelBlock));

            var src = _filterSrc!;
            var dst = _filterDst!;
            Array.Copy(src, dst, src.Length);

            for (int y = 0; y < _fh; y += block)
            {
                int bh = Math.Min(block, _fh - y);
                for (int x = 0; x < _fw; x += block)
                {
                    int bw = Math.Min(block, _fw - x);
                    int cx = x + bw / 2;
                    int cy = y + bh / 2;
                    int cidx = cy * _fstride + cx * 4;

                    byte sb = src[cidx + 0];
                    byte sg = src[cidx + 1];
                    byte sr = src[cidx + 2];

                    for (int yy = 0; yy < bh; yy++)
                    {
                        int row = (y + yy) * _fstride + x * 4;
                        for (int xx = 0; xx < bw; xx++)
                        {
                            int i = row + xx * 4;
                            byte ob = dst[i + 0], og = dst[i + 1], orr = dst[i + 2];
                            dst[i + 0] = LerpByte(ob, sb, amt);
                            dst[i + 1] = LerpByte(og, sg, amt);
                            dst[i + 2] = LerpByte(orr, sr, amt);
                            // alpha unchanged
                        }
                    }
                }
            }
        }

        private static byte LerpByte(byte a, byte b, double t)
        {
            int v = (int)Math.Round(a + (b - a) * t);
            if (v < 0) v = 0; else if (v > 255) v = 255;
            return (byte)v;
        }

        // ---- helpers -----------------------------------------------------------

        private (byte[] buf, int w, int h, int stride) SnapshotCanvas(Canvas board)
        {
            int w = (int)Math.Ceiling(board.ActualWidth);
            int h = (int)Math.Ceiling(board.ActualHeight);
            if (w <= 0 || h <= 0) return (Array.Empty<byte>(), 0, 0, 0);

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(board);

            int stride = w * 4;
            var buf = new byte[h * stride];
            rtb.CopyPixels(buf, stride, 0);
            return (buf, w, h, stride);
        }

        private Canvas? GetArtboardOrNull()
        {
            // 1) XAML name lookup
            var byName = this.FindName("Artboard") as Canvas;
            if (byName != null) return byName;

            // 2) Search by name in visual tree
            var namedArtboard = FindDescendantByName<Canvas>(this, "Artboard");
            if (namedArtboard != null) return namedArtboard;

            // 3) If there’s an InkCanvas (often named PaintCanvas), use its parent Canvas
            var ink = (this.FindName("PaintCanvas") as InkCanvas) ?? FindDescendant<InkCanvas>(this);
            if (ink?.Parent is Canvas parentCanvas) return parentCanvas;

            // 4) Fallback: first Canvas anywhere under the Window
            return FindDescendant<Canvas>(this);
        }

        // Insert overlay just below interaction layers (InkCanvas / element named "PaintCanvas")
        private static void AddOverlayUnderInteractionLayer(Canvas board, UIElement overlay)
        {
            // If overlay is already inside some panel, remove it first
            var oldParent = VisualTreeHelper.GetParent(overlay) as Panel;
            if (oldParent != null)
                oldParent.Children.Remove(overlay);

            // Find the first "interaction layer"
            int interactionIndex = -1;
            for (int i = 0; i < board.Children.Count; i++)
            {
                var ch = board.Children[i];
                if (ch is InkCanvas) { interactionIndex = i; break; }
                if ((ch as FrameworkElement)?.Name == "PaintCanvas") { interactionIndex = i; break; }
            }

            if (interactionIndex >= 0)
            {
                // Insert just before the interaction layer and set Z below it
                var neighbor = board.Children[interactionIndex];
                int neighborZ = Panel.GetZIndex(neighbor);
                board.Children.Insert(interactionIndex, overlay);
                Panel.SetZIndex(overlay, neighborZ - 1);
            }
            else
            {
                // No explicit interaction layer; add far back
                board.Children.Add(overlay);
                Panel.SetZIndex(overlay, -100);
            }
        }

        private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root is T t && t.Name == name) return t;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindDescendantByName<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T t) return t;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindDescendant<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}

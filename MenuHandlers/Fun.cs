// File: MenuHandlers/Fun.cs
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;

namespace PhotoMax
{
    public partial class MainWindow
    {
        // ===== Layer-specific filter session (live preview + apply in-place) =====
        private Image? _filterOverlay;           // preview bitmap (Pbgra32) drawn under PaintCanvas
        private Border? _filterToolbar;          // tiny control panel
        private WriteableBitmap? _filterWB;      // preview surface (Pbgra32)
        private byte[]? _filterSrc;              // source snapshot (Pbgra32) — from ACTIVE LAYER
        private byte[]? _filterDst;              // working buffer (Pbgra32)
        private int _fw, _fh, _fstride;

        // backup of the ACTIVE layer (BGRA straight) so we can hide/restore during preview
        private byte[]? _activeBackup;
        private int _activeW, _activeH, _activeStride;

        private string _filterMode = "Grayscale"; // Grayscale | Invert | Sepia | Posterize | Pixelate
        private double _filterAmount = 1.0;       // 0..1
        private int _posterizeLevels = 4;         // 2..8
        private int _pixelBlock = 8;              // 2..40
        private bool _filterActive = false;

        private DispatcherTimer? _filterTimer;    // throttled live preview (~30 fps)
        private bool _filterDirty;

        // UI refs
        private ComboBox? _modeCombo;
        private Slider? _amtSlider;
        private Slider? _lvlSlider;
        private Slider? _blkSlider;

        // === Public helper so Tools/Layers can safely tear down a running preview ===
        internal void Filters_CancelIfActive()
        {
            if (_filterActive)
                EndLayerFilterSession(apply: false);
        }

        // ===== Entry from menu: Fun → Filters =====
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

            StartLayerFilterSession(board);
        }

        // ---- session lifecycle -------------------------------------------------

        private void StartLayerFilterSession(Canvas board)
        {
            // 1) Snapshot ACTIVE LAYER (BGRA straight) and keep a backup
            (var srcBGRA, _activeW, _activeH, _activeStride) = SnapshotActiveLayer();
            if (srcBGRA.Length == 0 || _activeW == 0 || _activeH == 0) return;
            _activeBackup = srcBGRA; // keep original to restore on cancel

            // 2) Build PBGRA source for preview filters
            _fw = _activeW; _fh = _activeH; _fstride = _fw * 4;
            _filterSrc = new byte[_fstride * _fh];
            _filterDst = new byte[_fstride * _fh];
            BGRA_to_PBGRA(srcBGRA, _fw, _fh, _activeStride, _filterSrc);

            // 3) Hide the ACTIVE layer underneath so we don't see "double image" during preview
            HideActiveLayerPixels();       // hide by zeroing RGBA
            _img?.ForceRefreshView();

            // 4) Create overlay for live preview (shows only the processed result)
            _filterWB  = new WriteableBitmap(_fw, _fh, 96, 96, PixelFormats.Pbgra32, null);
            _filterOverlay = new Image
            {
                Source = _filterWB,
                IsHitTestVisible = false,
                Opacity = 1.0
            };
            AddOverlayUnderInteractionLayer(board, _filterOverlay);

            // 5) Toolbar
            _filterToolbar = BuildFilterToolbar(board);
            Canvas.SetLeft(_filterToolbar, 8);
            Canvas.SetTop(_filterToolbar, 8);
            Panel.SetZIndex(_filterToolbar, int.MaxValue);
            board.Children.Add(_filterToolbar);

            // 6) Throttle timer (~30 fps). Renders only when _filterDirty is true.
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

        private void EndLayerFilterSession(bool apply)
        {
            var board = GetArtboardOrNull();
            if (board == null) return;

            _filterTimer?.Stop();
            _filterTimer = null;

            if (apply)
            {
                // Write the current preview (_filterDst PBGRA) INTO the active layer (BGRA straight)
                ApplyPreviewBufferToActiveLayer();
                _img?.ForceRefreshView();
                _hasUnsavedChanges = true;
                StatusText.Content = $"Applied {_filterMode} to active layer.";
            }
            else
            {
                // Restore original active layer pixels
                if (_activeBackup != null) RestoreActiveLayerPixels(_activeBackup, _activeW, _activeH, _activeStride);
                _img?.ForceRefreshView();
                StatusText.Content = "Filter canceled.";
            }

            // Remove overlay + toolbar
            if (_filterOverlay != null) board.Children.Remove(_filterOverlay);
            if (_filterToolbar != null) board.Children.Remove(_filterToolbar);

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

            // Make sure active layer is visible again (either modified or restored)
            if (_activeBackup != null) { _activeBackup = null; }
            this.PreviewKeyDown -= OnFilter_PreviewKeyDown;
        }

        private void OnFilter_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_filterActive) return;
            if (e.Key == Key.Enter)
            {
                EndLayerFilterSession(apply: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndLayerFilterSession(apply: false);
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
                Text = "Layer Filter (live)",
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
                _filterDirty = true;
            };
            stack.Children.Add(_modeCombo);

            _amtSlider = MakeLabeledSlider(stack, "Amount", 0, 1, _filterAmount, 0.01, v =>
            {
                _filterAmount = v;
                _filterDirty = true;
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
            apply.Click += (s, e) => EndLayerFilterSession(apply: true);
            var cancel = new Button { Content = "Cancel (Esc)", Padding = new Thickness(10, 4, 10, 4) };
            cancel.Click += (s, e) => EndLayerFilterSession(apply: false);

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
                    IsSnapToTickEnabled = false,
                    IsMoveToPointEnabled = true,
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
                case "Invert":     Filter_Invert_PremulSafe();   break;
                case "Sepia":      Filter_Sepia_PremulSafe();     break;
                case "Posterize":  Filter_Posterize_PremulSafe(); break;
                case "Pixelate":   Filter_Pixelate();             break; // premul-safe
                default:           Filter_Grayscale_PremulSafe(); break;
            }

            var rect = new Int32Rect(0, 0, _fw, _fh);
            _filterWB.WritePixels(rect, _filterDst, _fstride, 0);
        }

        // === Filters that respect premultiplied alpha ==================================

        private void Filter_Grayscale_PremulSafe()
        {
            var amt = _filterAmount;
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }

                // un-premultiply
                int b = (bp * 255 + (a >> 1)) / a;
                int g = (gp * 255 + (a >> 1)) / a;
                int r = (rp * 255 + (a >> 1)) / a;

                int y = (int)Math.Round(0.114 * b + 0.587 * g + 0.299 * r);
                y = Math.Clamp(y, 0, 255);

                // lerp in straight space, then re-premultiply
                int lb = (int)Math.Round(b + (y - b) * amt);
                int lg = (int)Math.Round(g + (y - g) * amt);
                int lr = (int)Math.Round(r + (y - r) * amt);

                dst[i + 0] = (byte)((lb * a + 127) / 255);
                dst[i + 1] = (byte)((lg * a + 127) / 255);
                dst[i + 2] = (byte)((lr * a + 127) / 255);
                dst[i + 3] = a;
            }
        }

        private void Filter_Invert_PremulSafe()
        {
            var amt = _filterAmount;
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }

                // In premultiplied space, perfect invert is: c' = a - c
                byte tb = (byte)(a - bp);
                byte tg = (byte)(a - gp);
                byte tr = (byte)(a - rp);

                dst[i + 0] = LerpByte(bp, tb, amt);
                dst[i + 1] = LerpByte(gp, tg, amt);
                dst[i + 2] = LerpByte(rp, tr, amt);
                dst[i + 3] = a;
            }
        }

        private void Filter_Sepia_PremulSafe()
        {
            var amt = _filterAmount;
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }

                // un-premultiply
                int b = (bp * 255 + (a >> 1)) / a;
                int g = (gp * 255 + (a >> 1)) / a;
                int r = (rp * 255 + (a >> 1)) / a;

                int y = (int)Math.Round(0.114 * b + 0.587 * g + 0.299 * r);
                int sr = Math.Min(255, (int)(y * 1.07));
                int sg = Math.Min(255, (int)(y * 0.87));
                int sb = Math.Min(255, (int)(y * 0.55));

                int lr = (int)Math.Round(r + (sr - r) * amt);
                int lg = (int)Math.Round(g + (sg - g) * amt);
                int lb = (int)Math.Round(b + (sb - b) * amt);

                dst[i + 0] = (byte)((lb * a + 127) / 255);
                dst[i + 1] = (byte)((lg * a + 127) / 255);
                dst[i + 2] = (byte)((lr * a + 127) / 255);
                dst[i + 3] = a;
            }
        }

        private void Filter_Posterize_PremulSafe()
        {
            var amt = _filterAmount;
            int levels = Math.Max(2, Math.Min(8, _posterizeLevels));
            double step = 255.0 / (levels - 1);

            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }

                int b = (bp * 255 + (a >> 1)) / a;
                int g = (gp * 255 + (a >> 1)) / a;
                int r = (rp * 255 + (a >> 1)) / a;

                byte qb = (byte)Math.Clamp((int)Math.Round(Math.Round(b / step) * step), 0, 255);
                byte qg = (byte)Math.Clamp((int)Math.Round(Math.Round(g / step) * step), 0, 255);
                byte qr = (byte)Math.Clamp((int)Math.Round(Math.Round(r / step) * step), 0, 255);

                int lb = (int)Math.Round(b + (qb - b) * amt);
                int lg = (int)Math.Round(g + (qg - g) * amt);
                int lr = (int)Math.Round(r + (qr - r) * amt);

                dst[i + 0] = (byte)((lb * a + 127) / 255);
                dst[i + 1] = (byte)((lg * a + 127) / 255);
                dst[i + 2] = (byte)((lr * a + 127) / 255);
                dst[i + 3] = a;
            }
        }

        // === PIXELATE — uses your center-sample algorithm (premul-safe, amount-aware) ===
        private void Filter_Pixelate()
        {
            var amt = _filterAmount;
            int block = Math.Max(2, Math.Min(40, _pixelBlock));

            var src = _filterSrc!;   // PBGRA
            var dst = _filterDst!;
            Array.Copy(src, dst, src.Length); // start from src

            bool hard = amt >= 0.999;

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
                    byte sa = src[cidx + 3];

                    for (int yy = 0; yy < bh; yy++)
                    {
                        int row = (y + yy) * _fstride + x * 4;
                        for (int xx = 0; xx < bw; xx++)
                        {
                            int i = row + xx * 4;
                            if (hard)
                            {
                                dst[i + 0] = sb;
                                dst[i + 1] = sg;
                                dst[i + 2] = sr;
                                dst[i + 3] = sa;
                            }
                            else
                            {
                                dst[i + 0] = LerpByte(dst[i + 0], sb, amt);
                                dst[i + 1] = LerpByte(dst[i + 1], sg, amt);
                                dst[i + 2] = LerpByte(dst[i + 2], sr, amt);
                                dst[i + 3] = LerpByte(dst[i + 3], sa, amt);
                            }
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

        private static int GetLuma(int b, int g, int r) => (int)Math.Round(0.114 * b + 0.587 * g + 0.299 * r);

        // ---- helpers -----------------------------------------------------------

        // Snapshot ACTIVE LAYER (BGRA straight, tightly packed)
        private (byte[] buf, int w, int h, int stride) SnapshotActiveLayer()
        {
            if (_img?.Mat == null || _img.Mat.Empty())
                return (Array.Empty<byte>(), 0, 0, 0);

            int w = _img.Mat.Cols;
            int h = _img.Mat.Rows;
            int stride = w * 4;
            var buf = new byte[h * stride];

            int step = (int)_img.Mat.Step();
            var row = new byte[step];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(_img.Mat.Data + y * step, row, 0, step);
                Buffer.BlockCopy(row, 0, buf, y * stride, stride); // drop padding, keep tight
            }
            return (buf, w, h, stride);
        }

        // TEMP: hide active layer (zero RGBA) while preview runs
        private void HideActiveLayerPixels()
        {
            if (_img?.Mat == null || _img.Mat.Empty()) return;
            _img.Mat.SetTo(new Scalar(0, 0, 0, 0)); // safer than manual Marshal.Copy
        }

        private void RestoreActiveLayerPixels(byte[] srcBGRA, int w, int h, int stride)
        {
            if (_img?.Mat == null || _img.Mat.Empty()) return;
            int step = (int)_img.Mat.Step();
            var row = new byte[step];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(srcBGRA, y * stride, row, 0, stride);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, _img.Mat.Data + y * step, step);
            }
        }

        // Apply current preview buffer (PBGRA) into active layer (BGRA straight)
        private void ApplyPreviewBufferToActiveLayer()
        {
            if (_filterDst == null || _img?.Mat == null || _img.Mat.Empty()) return;
            int w = _fw, h = _fh;
            int stride = w * 4;

            // Convert PBGRA -> BGRA
            var bgra = new byte[_filterDst.Length];
            PBGRA_to_BGRA(_filterDst, w, h, stride, bgra);

            // Copy into Mat row by row (including padding)
            int step = (int)_img.Mat.Step();
            var row = new byte[step];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(bgra, y * stride, row, 0, stride);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, _img.Mat.Data + y * step, step);
            }
        }

        private static void BGRA_to_PBGRA(byte[] bgra, int w, int h, int stride, byte[] pbgraOut)
        {
            int len = stride * h;
            for (int i = 0; i < len; i += 4)
            {
                byte B = bgra[i + 0], G = bgra[i + 1], R = bgra[i + 2], A = bgra[i + 3];
                pbgraOut[i + 3] = A;
                if (A == 0) { pbgraOut[i + 0] = 0; pbgraOut[i + 1] = 0; pbgraOut[i + 2] = 0; continue; }
                pbgraOut[i + 0] = (byte)((B * A + 127) / 255);
                pbgraOut[i + 1] = (byte)((G * A + 127) / 255);
                pbgraOut[i + 2] = (byte)((R * A + 127) / 255);
            }
        }

        private static void PBGRA_to_BGRA(byte[] pbgra, int w, int h, int stride, byte[] bgraOut)
        {
            int len = stride * h;
            for (int i = 0; i < len; i += 4)
            {
                byte Bp = pbgra[i + 0], Gp = pbgra[i + 1], Rp = pbgra[i + 2], A = pbgra[i + 3];
                bgraOut[i + 3] = A;
                if (A == 0) { bgraOut[i + 0] = 0; bgraOut[i + 1] = 0; bgraOut[i + 2] = 0; continue; }
                bgraOut[i + 0] = (byte)Math.Min(255, (Bp * 255 + (A >> 1)) / A);
                bgraOut[i + 1] = (byte)Math.Min(255, (Gp * 255 + (A >> 1)) / A);
                bgraOut[i + 2] = (byte)Math.Min(255, (Rp * 255 + (A >> 1)) / A);
            }
        }

        // ---- canvas helpers ----------------------------------------------------

        private Canvas? GetArtboardOrNull()
        {
            var byName = this.FindName("Artboard") as Canvas;
            if (byName != null) return byName;

            var namedArtboard = FindDescendantByName<Canvas>(this, "Artboard");
            if (namedArtboard != null) return namedArtboard;

            var ink = (this.FindName("PaintCanvas") as InkCanvas) ?? FindDescendant<InkCanvas>(this);
            if (ink?.Parent is Canvas parentCanvas) return parentCanvas;

            return FindDescendant<Canvas>(this);
        }

        // Insert overlay just below interaction layers (InkCanvas / element named "PaintCanvas")
        private static void AddOverlayUnderInteractionLayer(Canvas board, UIElement overlay)
        {
            var oldParent = VisualTreeHelper.GetParent(overlay) as Panel;
            if (oldParent != null)
                oldParent.Children.Remove(overlay);

            int interactionIndex = -1;
            for (int i = 0; i < board.Children.Count; i++)
            {
                var ch = board.Children[i];
                if (ch is InkCanvas) { interactionIndex = i; break; }
                if ((ch as FrameworkElement)?.Name == "PaintCanvas") { interactionIndex = i; break; }
            }

            if (interactionIndex >= 0)
            {
                var neighbor = board.Children[interactionIndex];
                int neighborZ = Panel.GetZIndex(neighbor);
                board.Children.Insert(interactionIndex, overlay);
                // SAME ZIndex as InkCanvas so overlay sits just underneath it
                Panel.SetZIndex(overlay, neighborZ);
            }
            else
            {
                board.Children.Add(overlay);
                Panel.SetZIndex(overlay, 1);
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

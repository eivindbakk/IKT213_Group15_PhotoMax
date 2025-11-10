// File: MenuHandlers/Fun.cs
// Fixed: Better crown placement - overlaps with top of face instead of floating above

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.Dnn;

using WpfPoint = System.Windows.Point;
using WpfRect  = System.Windows.Rect;
using CvPoint  = OpenCvSharp.Point;
using CvRect   = OpenCvSharp.Rect;
using CvSize   = OpenCvSharp.Size;
using HaarFlags = OpenCvSharp.HaarDetectionTypes;
using WpfWindow = System.Windows.Window;
using static System.Math;

namespace PhotoMax
{
    public partial class MainWindow
    {
        // Live preview session
        private Image? _filterOverlay;
        private Border? _filterToolbar;
        private WriteableBitmap? _filterWB;
        private byte[]? _filterSrc;
        private byte[]? _filterDst;
        private int _fw, _fh, _fstride;

        private byte[]? _activeBackup;
        private int _activeW, _activeH, _activeStride;

        // Filter parameters
        private string _filterMode = "Grayscale";
        private double _filterAmount = 1.0;
        private int _posterizeLevels = 4;
        private int _pixelBlock = 8;

        // Background edit
        private Mat? _bgImage;
        private double _bgBlurRadius = 25;
        private double _bgSatMul = 1.0;
        private double _bgValMul = 1.0;

        // Person mask cache
        private Mat? _cachedPersonMask;
        private bool _maskDirty = true;
        private int _marginPercent = 3;
        private int _iterations = 3;
        private int _erosion = 2;

        // Crown sticker
        private Mat? _sticker;
        private bool _stickerTriedLoad = false;

        // Session control
        private bool _filterActive = false;
        private DispatcherTimer? _filterTimer;
        private bool _filterDirty;

        // Cascades
        private CascadeClassifier? _faceClassifier;
        private CascadeClassifier? _eyeClassifier;
        private bool _triedLoadModels = false;

        // UI refs - store panels directly
        private StackPanel? _basicFilterPanel;
        private StackPanel? _bgPanel;
        private StackPanel? _maskPanel;
        private ComboBox? _modeCombo;
        private Slider? _amtSlider, _lvlSlider, _blkSlider;
        private Button? _btnPickBg;
        private Slider? _bgSigmaSlider, _bgSatSlider, _bgValSlider;
        private Slider? _marginSlider, _iterSlider, _erosionSlider;

        internal void Filters_CancelIfActive()
        {
            if (_filterActive) EndLayerFilterSession(apply: false);
        }

        public void Fun_Filters_Click(object sender, RoutedEventArgs e)
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
                MessageBox.Show("Canvas isn't ready yet. Try again once it's visible.",
                    "PhotoMax", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StartLayerFilterSession(board);
        }

        private async void StartLayerFilterSession(Canvas board)
        {
            (var srcBGRA, _activeW, _activeH, _activeStride) = SnapshotActiveLayer();
            if (_activeW == 0 || _activeH == 0) return;
            _activeBackup = srcBGRA;

            StatusText.Content = "Loading models...";
            await EnsureModelsLoadedAsync();
            StatusText.Content = "Ready";

            _fw = _activeW; _fh = _activeH; _fstride = _fw * 4;
            _filterSrc = new byte[_fstride * _fh];
            _filterDst = new byte[_fstride * _fh];
            BGRA_to_PBGRA(srcBGRA, _fw, _fh, _activeStride, _filterSrc);

            HideActiveLayerPixels();
            _img?.ForceRefreshView();

            _filterWB = new WriteableBitmap(_fw, _fh, 96, 96, PixelFormats.Pbgra32, null);
            _filterOverlay = new Image { Source = _filterWB, IsHitTestVisible = false, Opacity = 1.0 };
            AddOverlayUnderInteractionLayer(board, _filterOverlay);

            _filterToolbar = BuildFilterToolbar(board);
            Canvas.SetLeft(_filterToolbar, 8);
            Canvas.SetTop(_filterToolbar, 8);
            Panel.SetZIndex(_filterToolbar, int.MaxValue);
            board.Children.Add(_filterToolbar);

            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _filterTimer.Tick += (_, __) => { if (_filterDirty) { _filterDirty = false; ApplyFilterAndUpdate(); } };
            _filterTimer.Start();

            _filterActive = true;
            this.PreviewKeyDown += OnFilter_PreviewKeyDown;

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
                ApplyPreviewBufferToActiveLayer();
                _img?.ForceRefreshView();
                _hasUnsavedChanges = true;
                StatusText.Content = $"Applied {_filterMode}";
            }
            else
            {
                if (_activeBackup != null) RestoreActiveLayerPixels(_activeBackup, _activeW, _activeH, _activeStride);
                _img?.ForceRefreshView();
                StatusText.Content = "Filter canceled";
            }

            if (_filterOverlay != null) board.Children.Remove(_filterOverlay);
            if (_filterToolbar != null) board.Children.Remove(_filterToolbar);

            _filterOverlay = null; _filterToolbar = null; _filterWB = null;
            _filterSrc = null; _filterDst = null; _activeBackup = null;

            _basicFilterPanel = null; _bgPanel = null; _maskPanel = null;
            _btnPickBg = null; _bgSigmaSlider = null; _bgSatSlider = null; _bgValSlider = null;
            _marginSlider = null; _iterSlider = null; _erosionSlider = null;

            _bgImage?.Dispose(); _bgImage = null;
            _sticker?.Dispose(); _sticker = null; _stickerTriedLoad = false;
            _cachedPersonMask?.Dispose(); _cachedPersonMask = null;
            _maskDirty = true;

            _faceClassifier?.Dispose(); _faceClassifier = null;
            _eyeClassifier?.Dispose(); _eyeClassifier = null;
            _triedLoadModels = false;

            _filterActive = false; _filterDirty = false;
            this.PreviewKeyDown -= OnFilter_PreviewKeyDown;
        }

        private void OnFilter_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_filterActive) return;
            if (e.Key == Key.Enter) { EndLayerFilterSession(apply: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndLayerFilterSession(apply: false); e.Handled = true; }
        }

        private Border BuildFilterToolbar(Canvas _)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 315,
                    ShadowDepth = 4,
                    BlurRadius = 12,
                    Opacity = 0.5
                }
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };

            // Header
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(new TextBlock 
            { 
                Text = "✨ Filters", 
                FontSize = 16, 
                FontWeight = FontWeights.Bold, 
                Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock 
            { 
                Text = "  •  Press Enter to apply, Esc to cancel", 
                FontSize = 10, 
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            stack.Children.Add(header);

            // Filter selector
            _modeCombo = new ComboBox
            {
                ItemsSource = new[] { "Grayscale", "Invert", "Sepia", "Posterize", "Pixelate", "BackgroundBlur", "BackgroundReplace", "AddCrown" },
                SelectedItem = _filterMode,
                Width = 360,
                Margin = new Thickness(0, 0, 0, 8),
                Height = 28
            };
            _modeCombo.SelectionChanged += (s, e) =>
            {
                _filterMode = (string)_modeCombo.SelectedItem!;
                UpdateExtrasVisibility();
                _maskDirty = true;
                _filterDirty = true;
            };
            stack.Children.Add(_modeCombo);

            // Basic filter controls (in own panel)
            _basicFilterPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
            _amtSlider = MakeSlider(_basicFilterPanel, "💫 Amount", 0, 1, _filterAmount, v => { _filterAmount = v; _filterDirty = true; });
            _lvlSlider = MakeSlider(_basicFilterPanel, "🎨 Levels", 2, 8, _posterizeLevels, v => { _posterizeLevels = (int)Round(v); _filterDirty = true; });
            _blkSlider = MakeSlider(_basicFilterPanel, "🔲 Block Size", 2, 40, _pixelBlock, v => { _pixelBlock = (int)Round(v); _filterDirty = true; });
            stack.Children.Add(_basicFilterPanel);

            // BG controls (in own panel)
            _bgPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            
            _btnPickBg = new Button 
            { 
                Content = "📁 Choose Background Image", 
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 6, 10, 6),
                Width = 360,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            _btnPickBg.Click += (_, __) =>
            {
                var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" };
                if (ofd.ShowDialog() == true)
                {
                    _bgImage?.Dispose();
                    var tmp = Cv2.ImRead(ofd.FileName, ImreadModes.Unchanged);
                    if (!tmp.Empty())
                    {
                        if (tmp.Channels() == 3) Cv2.CvtColor(tmp, tmp, ColorConversionCodes.BGR2BGRA);
                        _bgImage = tmp;
                        _filterDirty = true;
                    }
                }
            };
            _bgPanel.Children.Add(_btnPickBg);

            _bgSigmaSlider = MakeSlider(_bgPanel, "🌫️ Blur Radius", 1, 80, _bgBlurRadius, v => { _bgBlurRadius = v; _filterDirty = true; });
            _bgSatSlider = MakeSlider(_bgPanel, "🎨 Saturation", 0, 2, _bgSatMul, v => { _bgSatMul = v; _filterDirty = true; });
            _bgValSlider = MakeSlider(_bgPanel, "☀️ Brightness", 0, 2, _bgValMul, v => { _bgValMul = v; _filterDirty = true; });
            
            stack.Children.Add(_bgPanel);

            // Mask controls (in own panel)
            _maskPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var maskTitle = new TextBlock 
            { 
                Text = "🎯 Person Detection Settings", 
                Foreground = new SolidColorBrush(Color.FromRgb(150, 200, 255)),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _maskPanel.Children.Add(maskTitle);

            _marginSlider = MakeSlider(_maskPanel, "   Margin %", 1, 10, _marginPercent, v => { _marginPercent = (int)Round(v); _maskDirty = true; _filterDirty = true; });
            _iterSlider = MakeSlider(_maskPanel, "   Iterations", 1, 8, _iterations, v => { _iterations = (int)Round(v); _maskDirty = true; _filterDirty = true; });
            _erosionSlider = MakeSlider(_maskPanel, "   Edge Cleanup", 0, 5, _erosion, v => { _erosion = (int)Round(v); _maskDirty = true; _filterDirty = true; });
            
            stack.Children.Add(_maskPanel);

            UpdateExtrasVisibility();

            // Action buttons
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            
            var applyBtn = new Button 
            { 
                Content = "✓ Apply", 
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(40, 120, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            applyBtn.Click += (s, e) => EndLayerFilterSession(apply: true);
            
            var cancelBtn = new Button 
            { 
                Content = "✕ Cancel", 
                Padding = new Thickness(20, 8, 20, 8),
                Background = new SolidColorBrush(Color.FromRgb(100, 40, 40)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            cancelBtn.Click += (s, e) => EndLayerFilterSession(apply: false);
            
            btnPanel.Children.Add(applyBtn);
            btnPanel.Children.Add(cancelBtn);
            stack.Children.Add(btnPanel);

            border.Child = stack;
            return border;
        }

        private Slider MakeSlider(Panel parent, string label, double min, double max, double val, Action<double> onChange)
        {
            parent.Children.Add(new TextBlock 
            { 
                Text = label, 
                Foreground = Brushes.Gainsboro, 
                Margin = new Thickness(0, 6, 0, 2),
                FontSize = 11
            });
            
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                Width = 360,
                Margin = new Thickness(0, 0, 0, 4)
            };
            slider.ValueChanged += (_, e) => onChange(e.NewValue);
            parent.Children.Add(slider);
            
            return slider;
        }

        private void UpdateExtrasVisibility()
        {
            bool isGrayscale = _filterMode == "Grayscale";
            bool isInvert = _filterMode == "Invert";
            bool isSepia = _filterMode == "Sepia";
            bool isPosterize = _filterMode == "Posterize";
            bool isPixelate = _filterMode == "Pixelate";
            bool bgBlur = _filterMode == "BackgroundBlur";
            bool bgRepl = _filterMode == "BackgroundReplace";
            bool bgAny = bgBlur || bgRepl;

            // Show/hide basic filter panel items
            if (_amtSlider != null)
            {
                var amtVis = (isGrayscale || isInvert || isSepia) ? Visibility.Visible : Visibility.Collapsed;
                _amtSlider.Visibility = amtVis;
                int idx = _basicFilterPanel!.Children.IndexOf(_amtSlider);
                if (idx > 0 && _basicFilterPanel.Children[idx - 1] is TextBlock)
                    _basicFilterPanel.Children[idx - 1].Visibility = amtVis;
            }

            if (_lvlSlider != null)
            {
                var lvlVis = isPosterize ? Visibility.Visible : Visibility.Collapsed;
                _lvlSlider.Visibility = lvlVis;
                int idx = _basicFilterPanel!.Children.IndexOf(_lvlSlider);
                if (idx > 0 && _basicFilterPanel.Children[idx - 1] is TextBlock)
                    _basicFilterPanel.Children[idx - 1].Visibility = lvlVis;
            }

            if (_blkSlider != null)
            {
                var blkVis = isPixelate ? Visibility.Visible : Visibility.Collapsed;
                _blkSlider.Visibility = blkVis;
                int idx = _basicFilterPanel!.Children.IndexOf(_blkSlider);
                if (idx > 0 && _basicFilterPanel.Children[idx - 1] is TextBlock)
                    _basicFilterPanel.Children[idx - 1].Visibility = blkVis;
            }

            if (_bgPanel != null) _bgPanel.Visibility = bgAny ? Visibility.Visible : Visibility.Collapsed;
            if (_btnPickBg != null) _btnPickBg.Visibility = bgRepl ? Visibility.Visible : Visibility.Collapsed;

            if (_bgSigmaSlider != null)
            {
                var blurVis = bgBlur ? Visibility.Visible : Visibility.Collapsed;
                _bgSigmaSlider.Visibility = blurVis;
                int idx = _bgPanel!.Children.IndexOf(_bgSigmaSlider);
                if (idx > 0 && _bgPanel.Children[idx - 1] is TextBlock)
                    _bgPanel.Children[idx - 1].Visibility = blurVis;
            }

            if (_maskPanel != null) _maskPanel.Visibility = bgAny ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyFilterAndUpdate()
        {
            if (_filterSrc == null || _filterDst == null || _filterWB == null) return;

            try
            {
                switch (_filterMode)
                {
                    case "Invert":            Filter_Invert_PremulSafe();      break;
                    case "Sepia":             Filter_Sepia_PremulSafe();        break;
                    case "Posterize":         Filter_Posterize_PremulSafe();    break;
                    case "Pixelate":          Filter_Pixelate();                break;
                    case "BackgroundBlur":    Filter_BackgroundEdit(blurOnly: true);  break;
                    case "BackgroundReplace": Filter_BackgroundEdit(blurOnly: false); break;
                    case "AddCrown":          Filter_AddCrown();                break;
                    default:                  Filter_Grayscale_PremulSafe();    break;
                }
            }
            catch (Exception ex)
            {
                Array.Copy(_filterSrc, _filterDst, _filterSrc.Length);
                StatusText.Content = $"Error: {ex.Message}";
            }

            var rect = new Int32Rect(0, 0, _fw, _fh);
            _filterWB.WritePixels(rect, _filterDst, _fstride, 0);
        }

        // ===== Classic filters =====
        private void Filter_Grayscale_PremulSafe()
        {
            var amt = _filterAmount; var src = _filterSrc!; var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }
                int b = (bp * 255 + (a >> 1)) / a, g = (gp * 255 + (a >> 1)) / a, r = (rp * 255 + (a >> 1)) / a;
                int y = (int)Round(0.114 * b + 0.587 * g + 0.299 * r);
                int lb = (int)Round(b + (y - b) * amt), lg = (int)Round(g + (y - g) * amt), lr = (int)Round(r + (y - r) * amt);
                dst[i + 0] = (byte)((lb * a + 127) / 255); dst[i + 1] = (byte)((lg * a + 127) / 255); dst[i + 2] = (byte)((lr * a + 127) / 255); dst[i + 3] = a;
            }
        }

        private void Filter_Invert_PremulSafe()
        {
            var amt = _filterAmount; var src = _filterSrc!; var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }
                byte tb = (byte)(a - bp), tg = (byte)(a - gp), tr = (byte)(a - rp);
                dst[i + 0] = LerpByte(bp, tb, amt); dst[i + 1] = LerpByte(gp, tg, amt); dst[i + 2] = LerpByte(rp, tr, amt); dst[i + 3] = a;
            }
        }

        private void Filter_Sepia_PremulSafe()
        {
            var amt = _filterAmount; var src = _filterSrc!; var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }
                int b = (bp * 255 + (a >> 1)) / a, g = (gp * 255 + (a >> 1)) / a, r = (rp * 255 + (a >> 1)) / a;
                int y = (int)Round(0.114 * b + 0.587 * g + 0.299 * r);
                int sr = Min(255, (int)(y * 1.07)), sg = Min(255, (int)(y * 0.87)), sb = Min(255, (int)(y * 0.55));
                int lr = (int)Round(r + (sr - r) * amt), lg = (int)Round(g + (sg - g) * amt), lb = (int)Round(b + (sb - b) * amt);
                dst[i + 0] = (byte)((lb * a + 127) / 255); dst[i + 1] = (byte)((lg * a + 127) / 255); dst[i + 2] = (byte)((lr * a + 127) / 255); dst[i + 3] = a;
            }
        }

        private void Filter_Posterize_PremulSafe()
        {
            var amt = _filterAmount; int levels = Math.Clamp(_posterizeLevels, 2, 8);
            double step = 255.0 / (levels - 1); var src = _filterSrc!; var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0) { dst[i + 0] = dst[i + 1] = dst[i + 2] = 0; dst[i + 3] = 0; continue; }
                int b = (bp * 255 + (a >> 1)) / a, g = (gp * 255 + (a >> 1)) / a, r = (rp * 255 + (a >> 1)) / a;
                byte qb = (byte)Math.Clamp((int)Round(Round(b / step) * step), 0, 255);
                byte qg = (byte)Math.Clamp((int)Round(Round(g / step) * step), 0, 255);
                byte qr = (byte)Math.Clamp((int)Round(Round(r / step) * step), 0, 255);
                int lb = (int)Round(b + (qb - b) * amt), lg = (int)Round(g + (qg - g) * amt), lr = (int)Round(r + (qr - r) * amt);
                dst[i + 0] = (byte)((lb * a + 127) / 255); dst[i + 1] = (byte)((lg * a + 127) / 255); dst[i + 2] = (byte)((lr * a + 127) / 255); dst[i + 3] = a;
            }
        }

        private void Filter_Pixelate()
        {
            var amt = _filterAmount; int block = Math.Clamp(_pixelBlock, 2, 40);
            var src = _filterSrc!; var dst = _filterDst!;
            Array.Copy(src, dst, src.Length);
            bool hard = amt >= 0.999;
            for (int y = 0; y < _fh; y += block)
            {
                int bh = Math.Min(block, _fh - y);
                for (int x = 0; x < _fw; x += block)
                {
                    int bw = Math.Min(block, _fw - x);
                    int cx = x + bw / 2, cy = y + bh / 2;
                    int cidx = cy * _fstride + cx * 4;
                    byte sb = src[cidx + 0], sg = src[cidx + 1], sr = src[cidx + 2], sa = src[cidx + 3];
                    for (int yy = 0; yy < bh; yy++)
                    {
                        int row = (y + yy) * _fstride + x * 4;
                        for (int xx = 0; xx < bw; xx++)
                        {
                            int i = row + xx * 4;
                            if (hard) { dst[i + 0] = sb; dst[i + 1] = sg; dst[i + 2] = sr; dst[i + 3] = sa; }
                            else { dst[i + 0] = LerpByte(dst[i + 0], sb, amt); dst[i + 1] = LerpByte(dst[i + 1], sg, amt); dst[i + 2] = LerpByte(dst[i + 2], sr, amt); dst[i + 3] = LerpByte(dst[i + 3], sa, amt); }
                        }
                    }
                }
            }
        }

        // ===== Background edit =====
        private void Filter_BackgroundEdit(bool blurOnly)
        {
            var mat = MakeMatFromBGRA(_activeBackup!, _fw, _fh, _activeStride);

            if (_maskDirty || _cachedPersonMask == null || _cachedPersonMask.Empty())
            {
                _cachedPersonMask?.Dispose();
                _cachedPersonMask = CreateForegroundMask(mat, _marginPercent, _iterations, _erosion);
                _maskDirty = false;
            }

            Mat bg;
            if (!blurOnly && _bgImage != null && !_bgImage.Empty())
            {
                bg = new Mat(_fh, _fw, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
                using var fitted = new Mat();
                Cv2.Resize(_bgImage, fitted, new CvSize(_fw, _fh), 0, 0, InterpolationFlags.Lanczos4);
                fitted.CopyTo(bg);
            }
            else
            {
                bg = mat.Clone();
                int k = Math.Max(1, (int)Round(_bgBlurRadius)); if (k % 2 == 0) k++;
                Cv2.GaussianBlur(bg, bg, new CvSize(k, k), _bgBlurRadius);
            }

            AdjustSatVal_BGRA(bg, _bgSatMul, _bgValMul);
            var comp = CompositeByMask_BGRA(foregroundBGRA: mat, backgroundBGRA: bg, mask255: _cachedPersonMask!);
            var outBGRA = MatToTightBGRA(comp);
            BGRA_to_PBGRA(outBGRA, _fw, _fh, _fw * 4, _filterDst!);

            mat.Dispose(); bg.Dispose(); comp.Dispose();
        }

        // ===== Add Crown (improved placement) =====
        private void Filter_AddCrown()
        {
            var baseMat = MakeMatFromBGRA(_activeBackup!, _fw, _fh, _activeStride);
            EnsureCrownLoaded();

            if (_sticker == null || _sticker.Empty())
            {
                var passThru = MatToTightBGRA(baseMat);
                BGRA_to_PBGRA(passThru, _fw, _fh, _fw * 4, _filterDst!);
                baseMat.Dispose();
                StatusText.Content = "Crown sticker not loaded.";
                return;
            }

            var faces = DetectAllFaces_Haar(baseMat);
            if (faces.Count == 0)
            {
                var passThru = MatToTightBGRA(baseMat);
                BGRA_to_PBGRA(passThru, _fw, _fh, _fw * 4, _filterDst!);
                baseMat.Dispose();
                StatusText.Content = "No faces detected for crown placement.";
                return;
            }

            var face = faces[0].Rect;
            
            // Crown is 120% of face width
            int crownW = (int)(face.Width * 1.2);
            int crownH = (int)(_sticker.Height * (crownW / (double)_sticker.Width));
            
            // Center horizontally on face
            int crownX = face.X + (face.Width - crownW) / 2;
            
            // IMPROVED: Instead of placing entirely above (face.Y - crownH),
            // overlap with the top of the face by placing it at the forehead area
            // This makes it sit ON the head rather than floating above
            int crownY = face.Y - (int)(crownH * 0.65);  // Overlap by 35% of crown height

            using var crownScaled = new Mat();
            Cv2.Resize(_sticker, crownScaled, new CvSize(crownW, crownH), 0, 0, InterpolationFlags.Lanczos4);

            OverlayPng_BGRA(baseMat, crownScaled, new CvPoint(crownX, crownY));
            
            var finalBGRA = MatToTightBGRA(baseMat);
            BGRA_to_PBGRA(finalBGRA, _fw, _fh, _fw * 4, _filterDst!);
            baseMat.Dispose();
            StatusText.Content = $"Crown placed at y={crownY} (face.Y={face.Y}, overlap={(int)(crownH * 0.35)}px)";
        }

        private static byte LerpByte(byte a, byte b, double t) => (byte)Math.Clamp((int)Round(a + (b - a) * t), 0, 255);

        // ===== GrabCut mask =====
        private static Mat CreateForegroundMask(Mat source, int marginPercent = 3, int iterations = 3, int erosion = 2)
        {
            try
            {
                Mat bgr;
                if (source.Channels() == 4) { bgr = new Mat(); Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR); }
                else if (source.Channels() == 1) { bgr = new Mat(); Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR); }
                else { bgr = source.Clone(); }

                int marginX = source.Width * marginPercent / 100;
                int marginY = source.Height * marginPercent / 100;
                var rect = new CvRect(marginX, marginY, source.Width - 2 * marginX, source.Height - 2 * marginY);

                var mask = new Mat(source.Size(), MatType.CV_8UC1, Scalar.All(0));
                var bgModel = new Mat(); var fgModel = new Mat();
                Cv2.GrabCut(bgr, mask, rect, bgModel, fgModel, iterations, GrabCutModes.InitWithRect);

                var binaryMask = new Mat(source.Size(), MatType.CV_8UC1);
                for (int y = 0; y < mask.Height; y++)
                    for (int x = 0; x < mask.Width; x++)
                    {
                        byte val = mask.At<byte>(y, x);
                        binaryMask.Set(y, x, (val == 1 || val == 3) ? (byte)255 : (byte)0);
                    }

                using var openKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new CvSize(3, 3));
                Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Open, openKernel, iterations: 2);
                using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new CvSize(15, 15));
                Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Close, closeKernel, iterations: 3);
                Cv2.GaussianBlur(binaryMask, binaryMask, new CvSize(5, 5), 2);
                Cv2.Threshold(binaryMask, binaryMask, 127, 255, ThresholdTypes.Binary);
                if (erosion > 0)
                {
                    using var erodeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new CvSize(3, 3));
                    Cv2.Erode(binaryMask, binaryMask, erodeKernel, iterations: erosion);
                }

                bgr.Dispose(); mask.Dispose(); bgModel.Dispose(); fgModel.Dispose();
                return binaryMask;
            }
            catch { return new Mat(source.Size(), MatType.CV_8UC1, Scalar.All(255)); }
        }

        // ===== Helpers =====
        private (byte[] buf, int w, int h, int stride) SnapshotActiveLayer()
        {
            if (_img?.Mat == null || _img.Mat.Empty()) return (Array.Empty<byte>(), 0, 0, 0);
            int w = _img.Mat.Cols, h = _img.Mat.Rows, stride = w * 4;
            var buf = new byte[h * stride];
            Marshal.Copy(_img.Mat.Data, buf, 0, buf.Length);
            return (buf, w, h, stride);
        }

        private void HideActiveLayerPixels() { if (_img?.Mat != null && !_img.Mat.Empty()) _img.Mat.SetTo(Scalar.All(0)); }
        private void RestoreActiveLayerPixels(byte[] src, int w, int h, int stride) { if (_img?.Mat != null && !_img.Mat.Empty()) Marshal.Copy(src, 0, _img.Mat.Data, src.Length); }
        
        private void ApplyPreviewBufferToActiveLayer()
        {
            if (_filterDst == null || _img?.Mat == null || _img.Mat.Empty()) return;
            var bgra = new byte[_filterDst.Length];
            PBGRA_to_BGRA(_filterDst, _fw, _fh, _fstride, bgra);
            Marshal.Copy(bgra, 0, _img.Mat.Data, bgra.Length);
        }

        private static void BGRA_to_PBGRA(byte[] bgra, int w, int h, int stride, byte[] pOut)
        {
            for (int i = 0; i < bgra.Length; i += 4)
            {
                byte B = bgra[i], G = bgra[i + 1], R = bgra[i + 2], A = bgra[i + 3];
                pOut[i + 3] = A;
                if (A == 0) { pOut[i] = pOut[i + 1] = pOut[i + 2] = 0; }
                else { pOut[i] = (byte)((B * A + 127) / 255); pOut[i + 1] = (byte)((G * A + 127) / 255); pOut[i + 2] = (byte)((R * A + 127) / 255); }
            }
        }

        private static void PBGRA_to_BGRA(byte[] pbgra, int w, int h, int stride, byte[] bgraOut)
        {
            for (int i = 0; i < pbgra.Length; i += 4)
            {
                byte Bp = pbgra[i], Gp = pbgra[i + 1], Rp = pbgra[i + 2], A = pbgra[i + 3];
                bgraOut[i + 3] = A;
                if (A == 0) { bgraOut[i] = bgraOut[i + 1] = bgraOut[i + 2] = 0; }
                else { bgraOut[i] = (byte)Min(255, (Bp * 255 + (A >> 1)) / A); bgraOut[i + 1] = (byte)Min(255, (Gp * 255 + (A >> 1)) / A); bgraOut[i + 2] = (byte)Min(255, (Rp * 255 + (A >> 1)) / A); }
            }
        }

        private static Mat MakeMatFromBGRA(byte[] bgra, int w, int h, int stride) { var mat = new Mat(h, w, MatType.CV_8UC4); Marshal.Copy(bgra, 0, mat.Data, bgra.Length); return mat; }

        private static byte[] MatToTightBGRA(Mat mat)
        {
            Mat converted; bool dispose = false;
            if (mat.Channels() == 3) { converted = new Mat(); Cv2.CvtColor(mat, converted, ColorConversionCodes.BGR2BGRA); dispose = true; }
            else if (mat.Channels() == 1) { converted = new Mat(); Cv2.CvtColor(mat, converted, ColorConversionCodes.GRAY2BGRA); dispose = true; }
            else { converted = mat; }
            var buf = new byte[converted.Total() * converted.ElemSize()];
            Marshal.Copy(converted.Data, buf, 0, buf.Length);
            if (dispose) converted.Dispose();
            return buf;
        }

        private void EnsureCrownLoaded()
        {
            if (_sticker != null && !_sticker.Empty()) return;
            if (_stickerTriedLoad) return;
            _stickerTriedLoad = true;
            string[] candidates = BuildSearchPathsForAsset("filter-assets", new[] { "crown.png" });
            foreach (var p in candidates)
            {
                try { if (!File.Exists(p)) continue; var tmp = Cv2.ImRead(p, ImreadModes.Unchanged); if (tmp.Empty()) continue; if (tmp.Channels() == 3) Cv2.CvtColor(tmp, tmp, ColorConversionCodes.BGR2BGRA); _sticker = tmp; StatusText.Content = $"Loaded crown: {Path.GetFileName(p)}"; return; }
                catch { }
            }
            StatusText.Content = "Crown asset missing (filter-assets/crown.png).";
        }

        private async Task EnsureModelsLoadedAsync()
        {
            if (_triedLoadModels) return;
            _triedLoadModels = true;
            var modelDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "filter-assets", "models");
            Directory.CreateDirectory(modelDir);
            var models = new Dictionary<string, (string name, string url)>
            {
                { "face", ("haarcascade_frontalface_default.xml", "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml") },
                { "eye", ("haarcascade_eye.xml", "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_eye.xml") },
            };
            using var client = new HttpClient();
            foreach (var m in models)
            {
                var path = Path.Combine(modelDir, m.Value.name);
                if (File.Exists(path)) continue;
                try { StatusText.Content = $"Downloading {m.Value.name}..."; var data = await client.GetByteArrayAsync(m.Value.url); File.WriteAllBytes(path, data); }
                catch (Exception ex) { StatusText.Content = $"Download failed for {m.Value.name}: {ex.Message}"; }
            }

            StatusText.Content = "Loading models...";
            try
            {
                var facePath = Path.Combine(modelDir, models["face"].name);
                if (File.Exists(facePath)) _faceClassifier = new CascadeClassifier(facePath);

                var eyePath = Path.Combine(modelDir, models["eye"].name);
                if (File.Exists(eyePath)) _eyeClassifier = new CascadeClassifier(eyePath);

                if (_faceClassifier == null) StatusText.Content = "Warning: Face classifier missing.";
                else StatusText.Content = "Models loaded successfully.";
            }
            catch (Exception ex)
            {
                _faceClassifier = null; _eyeClassifier = null;
                StatusText.Content = $"Model load error: {ex.Message}";
            }
        }

        private static string[] BuildSearchPathsForAsset(string folder, string[] files)
        {
            var list = new List<string>();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 5 && dir != null; i++) { var f = Path.Combine(dir.FullName, folder); foreach (var name in files) list.Add(Path.Combine(f, name)); dir = dir.Parent; }
            return list.ToArray();
        }

        public record FaceInfo(CvRect Rect, Point2f[] Landmarks);

        private List<FaceInfo> DetectAllFaces_Haar(Mat imgBGRA)
        {
            var faceList = new List<FaceInfo>();
            if (_faceClassifier == null) return faceList;

            using var gray = new Mat();
            Cv2.CvtColor(imgBGRA, gray, ColorConversionCodes.BGRA2GRAY);
            
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new CvSize(8, 8));
            clahe.Apply(gray, gray);

            var faces = _faceClassifier.DetectMultiScale(
                gray, 
                scaleFactor: 1.05,
                minNeighbors: 3,
                flags: HaarFlags.ScaleImage, 
                minSize: new CvSize(30, 30)
            );
            
            foreach (var face in faces)
            {
                faceList.Add(new FaceInfo(face, Array.Empty<Point2f>()));
            }

            faceList.Sort((a, b) => (b.Rect.Width * b.Rect.Height).CompareTo(a.Rect.Width * a.Rect.Height));
            return faceList;
        }

        private static void OverlayPng_BGRA(Mat dstBGRA, Mat stickerBGRA, CvPoint topLeft)
        {
            int x0 = Math.Max(0, topLeft.X), y0 = Math.Max(0, topLeft.Y);
            int x1 = Math.Min(dstBGRA.Cols, topLeft.X + stickerBGRA.Cols);
            int y1 = Math.Min(dstBGRA.Rows, topLeft.Y + stickerBGRA.Rows);
            if (x1 <= x0 || y1 <= y0) return;

            using var roi = dstBGRA.SubMat(new CvRect(x0, y0, x1 - x0, y1 - y0));
            using var src = stickerBGRA.SubMat(new CvRect(x0 - topLeft.X, y0 - topLeft.Y, x1 - x0, y1 - y0));
            var channels = Cv2.Split(src);
            if (channels.Length < 4) { foreach (var c in channels) c.Dispose(); return; }
            using var alpha = channels[3];
            for (int y = 0; y < roi.Rows; y++)
            {
                for (int x = 0; x < roi.Cols; x++)
                {
                    byte aVal = alpha.At<byte>(y, x);
                    if (aVal == 0) continue;
                    Vec4b srcPixel = src.At<Vec4b>(y, x);
                    Vec4b dstPixel = roi.At<Vec4b>(y, x);
                    int invA = 255 - aVal;
                    byte b = (byte)((srcPixel.Item0 * aVal + dstPixel.Item0 * invA + 127) / 255);
                    byte g = (byte)((srcPixel.Item1 * aVal + dstPixel.Item1 * invA + 127) / 255);
                    byte r = (byte)((srcPixel.Item2 * aVal + dstPixel.Item2 * invA + 127) / 255);
                    byte a = (byte)Max(srcPixel.Item3, dstPixel.Item3);
                    roi.Set(y, x, new Vec4b(b, g, r, a));
                }
            }
            foreach (var c in channels) c.Dispose();
        }

        private static Mat CompositeByMask_BGRA(Mat foregroundBGRA, Mat backgroundBGRA, Mat mask255)
        {
            var fg = new Mat(); var bg = new Mat();
            if (foregroundBGRA.Channels() == 4) Cv2.CvtColor(foregroundBGRA, fg, ColorConversionCodes.BGRA2BGR); else fg = foregroundBGRA.Clone();
            if (backgroundBGRA.Channels() == 4) Cv2.CvtColor(backgroundBGRA, bg, ColorConversionCodes.BGRA2BGR); else bg = backgroundBGRA.Clone();
            fg.ConvertTo(fg, MatType.CV_32FC3, 1.0 / 255); bg.ConvertTo(bg, MatType.CV_32FC3, 1.0 / 255);
            var alpha = new Mat(); mask255.ConvertTo(alpha, MatType.CV_32FC1, 1.0 / 255.0);
            var alpha3 = new Mat(); Cv2.Merge(new[] { alpha, alpha, alpha }, alpha3);
            var ones = new Mat(alpha3.Size(), MatType.CV_32FC3, new Scalar(1.0, 1.0, 1.0)); var invAlpha3 = new Mat(); Cv2.Subtract(ones, alpha3, invAlpha3);
            var fgMul = new Mat(); var bgMul = new Mat(); Cv2.Multiply(fg, alpha3, fgMul); Cv2.Multiply(bg, invAlpha3, bgMul);
            var comp = new Mat(); Cv2.Add(fgMul, bgMul, comp); comp.ConvertTo(comp, MatType.CV_8UC3, 255); Cv2.CvtColor(comp, comp, ColorConversionCodes.BGR2BGRA);
            alpha.Dispose(); alpha3.Dispose(); ones.Dispose(); invAlpha3.Dispose(); fgMul.Dispose(); bgMul.Dispose(); fg.Dispose(); bg.Dispose();
            return comp;
        }

        private static void AdjustSatVal_BGRA(Mat bgra, double satMul, double valMul)
        {
            var bgr = new Mat(); Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR); var hsv = new Mat(); Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            Mat[] ch = Cv2.Split(hsv); if (satMul != 1.0) ch[1].ConvertTo(ch[1], ch[1].Type(), satMul, 0); if (valMul != 1.0) ch[2].ConvertTo(ch[2], ch[2].Type(), valMul, 0);
            Cv2.Merge(ch, hsv); Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR); Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
            foreach (var m in ch) m.Dispose(); hsv.Dispose(); bgr.Dispose();
        }

        private Canvas? GetArtboardOrNull()
        {
            var byName = this.FindName("Artboard") as Canvas; if (byName != null) return byName;
            var namedArtboard = FindDescendantByName<Canvas>(this, "Artboard"); if (namedArtboard != null) return namedArtboard;
            var ink = (this.FindName("PaintCanvas") as InkCanvas) ?? FindDescendant<InkCanvas>(this);
            if (ink?.Parent is Canvas parentCanvas) return parentCanvas;
            return FindDescendant<Canvas>(this);
        }

        private static void AddOverlayUnderInteractionLayer(Canvas board, UIElement overlay)
        {
            if (VisualTreeHelper.GetParent(overlay) is Panel old) old.Children.Remove(overlay);
            int idx = -1, z = 1;
            for (int i = 0; i < board.Children.Count; i++) { var ch = board.Children[i]; if (ch is InkCanvas || (ch as FrameworkElement)?.Name == "PaintCanvas") { idx = i; z = Panel.GetZIndex(ch); break; } }
            if (idx >= 0) { board.Children.Insert(idx, overlay); Panel.SetZIndex(overlay, z); }
            else { board.Children.Add(overlay); Panel.SetZIndex(overlay, 1); }
        }

        private static T? FindDescendantByName<T>(DependencyObject r, string n) where T : FrameworkElement
        {
            if (r is T t && t.Name == n) return t;
            int count = VisualTreeHelper.GetChildrenCount(r);
            for (int i = 0; i < count; i++) { var c = VisualTreeHelper.GetChild(r, i); var f = FindDescendantByName<T>(c, n); if (f != null) return f; }
            return null;
        }

        private static T? FindDescendant<T>(DependencyObject r) where T : DependencyObject
        {
            if (r is T t) return t;
            int count = VisualTreeHelper.GetChildrenCount(r);
            for (int i = 0; i < count; i++) { var c = VisualTreeHelper.GetChild(r, i); var f = FindDescendant<T>(c); if (f != null) return f; }
            return null;
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
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
using WpfRect = System.Windows.Rect;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;
using HaarFlags = OpenCvSharp.HaarDetectionTypes;
using WpfWindow = System.Windows.Window;
using static System.Math;

namespace PhotoMax
{
    public partial class MainWindow
    {
        internal CancellationTokenSource? _filterCancellation;
        internal bool _filterProcessing = false;

        internal Image? _filterOverlay;
        internal Border? _filterToolbar;
        internal WriteableBitmap? _filterWB;
        internal byte[]? _filterSrc;
        internal byte[]? _filterDst;
        internal int _fw, _fh, _fstride;

        internal byte[]? _activeBackup;
        internal int _activeW, _activeH, _activeStride;

        internal string _filterMode = "Grayscale";
        internal double _filterAmount = 1.0;
        internal int _posterizeLevels = 4;
        internal int _pixelBlock = 8;

        internal Mat? _bgImage;
        internal double _bgBlurRadius = 25;
        internal double _bgSatMul = 1.0;
        internal double _bgValMul = 1.0;

        internal Mat? _cachedPersonMask;
        internal bool _maskDirty = true;
        internal int _marginPercent = 3;
        internal int _iterations = 3;
        internal int _erosion = 2;

        internal Mat? _sticker;
        internal bool _stickerTriedLoad = false;

        internal bool _filterActive = false;
        internal DispatcherTimer? _filterTimer;
        internal bool _filterDirty;

        internal CascadeClassifier? _faceClassifier;
        internal CascadeClassifier? _eyeClassifier;
        internal bool _triedLoadModels = false;

        internal StackPanel? _basicFilterPanel;
        internal StackPanel? _bgPanel;
        internal StackPanel? _maskPanel;
        internal ComboBox? _modeCombo;
        internal Slider? _amtSlider, _lvlSlider, _blkSlider;
        internal Button? _btnPickBg;
        internal Slider? _bgSigmaSlider, _bgSatSlider, _bgValSlider;
        internal Slider? _marginSlider, _iterSlider, _erosionSlider;

        internal int _maskQuality = 1024;

        public void Fun_Filters_Click(object sender, RoutedEventArgs e)
        {
            if (ImageController == null || ImageController.Mat == null || ImageController.Mat.Empty())
            {
                MessageBox.Show("Open an image first.", "PhotoMax", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new FilterWindow(this)
            {
                Owner = this
            };

            dlg.ShowDialog();
        }

        internal async void StartLayerFilterSession(Canvas board)
        {
            (var srcBGRA, _activeW, _activeH, _activeStride) = SnapshotActiveLayer();
            if (_activeW == 0 || _activeH == 0) return;
            _activeBackup = srcBGRA;

            _fw = _activeW;
            _fh = _activeH;
            _fstride = _fw * 4;
            _filterSrc = new byte[_fstride * _fh];
            _filterDst = new byte[_fstride * _fh];
            BGRA_to_PBGRA(srcBGRA, _fw, _fh, _activeStride, _filterSrc);

            HideActiveLayerPixels();
            _img?.ForceRefreshView();

            _filterWB = new WriteableBitmap(_fw, _fh, 96, 96, PixelFormats.Pbgra32, null);
            _filterOverlay = new Image { Source = _filterWB, IsHitTestVisible = false, Opacity = 1.0 };
            AddOverlayUnderInteractionLayer(board, _filterOverlay);

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

            ApplyFilterAndUpdate();

            StatusText.Content = "Loading models...";
            await EnsureModelsLoadedAsync();
            StatusText.Content = "Ready";
        }


        internal void EndLayerFilterSession(bool apply)
        {
            _filterCancellation?.Cancel();
            _filterCancellation?.Dispose();
            _filterCancellation = null;

            var board = GetArtboardOrNull();
            if (board == null) return;

            _filterTimer?.Stop();
            _filterTimer = null;

            if (apply && !_filterProcessing)
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
                StatusText.Content = _filterProcessing ? "Canceled during processing" : "Filter canceled";
            }

            if (_filterOverlay != null) board.Children.Remove(_filterOverlay);

            _filterOverlay = null;
            _filterToolbar = null;
            _filterWB = null;
            _filterSrc = null;
            _filterDst = null;
            _activeBackup = null;

            _basicFilterPanel = null;
            _bgPanel = null;
            _maskPanel = null;
            _btnPickBg = null;
            _bgSigmaSlider = null;
            _bgSatSlider = null;
            _bgValSlider = null;
            _marginSlider = null;
            _iterSlider = null;
            _erosionSlider = null;

            _bgImage?.Dispose();
            _bgImage = null;
            _sticker?.Dispose();
            _sticker = null;
            _stickerTriedLoad = false;
            _cachedPersonMask?.Dispose();
            _cachedPersonMask = null;
            _maskDirty = true;

            _faceClassifier?.Dispose();
            _faceClassifier = null;
            _eyeClassifier?.Dispose();
            _eyeClassifier = null;
            _triedLoadModels = false;

            _filterActive = false;
            _filterDirty = false;
            _filterProcessing = false;
        }

        private void ApplyFilterAndUpdate()
        {
            if (_filterSrc == null || _filterDst == null || _filterWB == null) return;

            try
            {
                switch (_filterMode)
                {
                    case "Invert": Filter_Invert_PremulSafe(); break;
                    case "Sepia": Filter_Sepia_PremulSafe(); break;
                    case "Posterize": Filter_Posterize_PremulSafe(); break;
                    case "Pixelate": Filter_Pixelate(); break;
                    case "BackgroundBlur": Filter_BackgroundEdit(blurOnly: true); break;
                    case "BackgroundReplace": Filter_BackgroundEdit(blurOnly: false); break;
                    case "AddCrown": Filter_AddCrown(); break;
                    default: Filter_Grayscale_PremulSafe(); break;
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

        private void Filter_Grayscale_PremulSafe()
        {
            var amt = _filterAmount;
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0)
                {
                    dst[i + 0] = dst[i + 1] = dst[i + 2] = 0;
                    dst[i + 3] = 0;
                    continue;
                }

                int b = (bp * 255 + (a >> 1)) / a, g = (gp * 255 + (a >> 1)) / a, r = (rp * 255 + (a >> 1)) / a;
                int y = (int)Round(0.114 * b + 0.587 * g + 0.299 * r);
                int lb = (int)Round(b + (y - b) * amt),
                    lg = (int)Round(g + (y - g) * amt),
                    lr = (int)Round(r + (y - r) * amt);
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
                if (a == 0)
                {
                    dst[i + 0] = dst[i + 1] = dst[i + 2] = 0;
                    dst[i + 3] = 0;
                    continue;
                }

                byte tb = (byte)(a - bp), tg = (byte)(a - gp), tr = (byte)(a - rp);
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
                if (a == 0)
                {
                    dst[i + 0] = dst[i + 1] = dst[i + 2] = 0;
                    dst[i + 3] = 0;
                    continue;
                }

                int b = (bp * 255 + (a >> 1)) / a, g = (gp * 255 + (a >> 1)) / a, r = (rp * 255 + (a >> 1)) / a;
                int y = (int)Round(0.114 * b + 0.587 * g + 0.299 * r);
                int sr = Min(255, (int)(y * 1.07)), sg = Min(255, (int)(y * 0.87)), sb = Min(255, (int)(y * 0.55));
                int lr = (int)Round(r + (sr - r) * amt),
                    lg = (int)Round(g + (sg - g) * amt),
                    lb = (int)Round(b + (sb - b) * amt);
                dst[i + 0] = (byte)((lb * a + 127) / 255);
                dst[i + 1] = (byte)((lg * a + 127) / 255);
                dst[i + 2] = (byte)((lr * a + 127) / 255);
                dst[i + 3] = a;
            }
        }

        private void Filter_Posterize_PremulSafe()
        {
            var amt = _filterAmount;
            int levels = Math.Clamp(_posterizeLevels, 2, 8);
            double step = 255.0 / (levels - 1);
            var src = _filterSrc!;
            var dst = _filterDst!;
            for (int i = 0; i < src.Length; i += 4)
            {
                byte bp = src[i + 0], gp = src[i + 1], rp = src[i + 2], a = src[i + 3];
                if (a == 0)
                {
                    dst[i + 0] = dst[i + 1] = dst[i + 2] = 0;
                    dst[i + 3] = 0;
                    continue;
                }

                int b = (bp * 255 + (a >> 1)) / a, g = (gp * 255 + (a >> 1)) / a, r = (rp * 255 + (a >> 1)) / a;
                byte qb = (byte)Math.Clamp((int)Round(Round(b / step) * step), 0, 255);
                byte qg = (byte)Math.Clamp((int)Round(Round(g / step) * step), 0, 255);
                byte qr = (byte)Math.Clamp((int)Round(Round(r / step) * step), 0, 255);
                int lb = (int)Round(b + (qb - b) * amt),
                    lg = (int)Round(g + (qg - g) * amt),
                    lr = (int)Round(r + (qr - r) * amt);
                dst[i + 0] = (byte)((lb * a + 127) / 255);
                dst[i + 1] = (byte)((lg * a + 127) / 255);
                dst[i + 2] = (byte)((lr * a + 127) / 255);
                dst[i + 3] = a;
            }
        }

        private void Filter_Pixelate()
        {
            var amt = _filterAmount;
            int block = Math.Clamp(_pixelBlock, 2, 40);
            var src = _filterSrc!;
            var dst = _filterDst!;
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

        private void Filter_BackgroundEdit(bool blurOnly)
        {
            if (!blurOnly && (_bgImage == null || _bgImage.Empty()))
            {
                Array.Copy(_filterSrc!, _filterDst!, _filterSrc!.Length);
                StatusText.Content = "Select a background image first";
                return;
            }

            var mat = MakeMatFromBGRA(_activeBackup!, _fw, _fh, _activeStride);

            if (_maskDirty || _cachedPersonMask == null || _cachedPersonMask.Empty())
            {
                StatusText.Content = "Computing person mask...";
                Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                _cachedPersonMask?.Dispose();
                _cachedPersonMask =
                    CreateForegroundMask(mat, _marginPercent, _iterations, _erosion,
                        _maskQuality);
                _maskDirty = false;

                StatusText.Content = "Applying effect...";
                Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            }

            Mat bg;
            if (!blurOnly && _bgImage != null && !_bgImage.Empty())
            {
                bg = new Mat(_fh, _fw, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
                using var fitted = new Mat();
                Cv2.Resize(_bgImage, fitted, new CvSize(_fw, _fh), 0, 0,
                    InterpolationFlags.Linear);
                fitted.CopyTo(bg);
            }
            else
            {
                bg = mat.Clone();
                int k = Math.Max(1, (int)Round(_bgBlurRadius));
                if (k % 2 == 0) k++;

                int maxKernel = 31;
                if (k > maxKernel) k = maxKernel;

                Cv2.GaussianBlur(bg, bg, new CvSize(k, k), _bgBlurRadius * 0.3);
            }

            AdjustSatVal_BGRA(bg, _bgSatMul, _bgValMul);
            var comp = CompositeByMask_BGRA(foregroundBGRA: mat, backgroundBGRA: bg, mask255: _cachedPersonMask!);
            var outBGRA = MatToTightBGRA(comp);
            BGRA_to_PBGRA(outBGRA, _fw, _fh, _fw * 4, _filterDst!);

            mat.Dispose();
            bg.Dispose();
            comp.Dispose();

            StatusText.Content = "Ready";
        }

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

            int crownW = (int)(face.Width * 1.2);
            int crownH = (int)(_sticker.Height * (crownW / (double)_sticker.Width));

            int crownX = face.X + (face.Width - crownW) / 2;

            int crownY = face.Y - (int)(crownH * 0.65);

            using var crownScaled = new Mat();
            Cv2.Resize(_sticker, crownScaled, new CvSize(crownW, crownH), 0, 0, InterpolationFlags.Lanczos4);

            OverlayPng_BGRA(baseMat, crownScaled, new CvPoint(crownX, crownY));

            var finalBGRA = MatToTightBGRA(baseMat);
            BGRA_to_PBGRA(finalBGRA, _fw, _fh, _fw * 4, _filterDst!);
            baseMat.Dispose();
            StatusText.Content = $"Crown placed at y={crownY} (face.Y={face.Y}, overlap={(int)(crownH * 0.35)}px)";
        }

        private static byte LerpByte(byte a, byte b, double t) => (byte)Math.Clamp((int)Round(a + (b - a) * t), 0, 255);

        private static Mat CreateForegroundMask(Mat source, int marginPercent = 3, int iterations = 3, int erosion = 2,
            int targetSize = 1024)
        {
            try
            {
                Mat bgr;
                if (source.Channels() == 4)
                {
                    bgr = new Mat();
                    Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                }
                else if (source.Channels() == 1)
                {
                    bgr = new Mat();
                    Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                }
                else
                {
                    bgr = source.Clone();
                }

                double scale = Math.Min(1.0, targetSize / (double)Math.Max(bgr.Width, bgr.Height));
                Mat resized = bgr;
                bool needsResize = scale < 0.99;

                if (needsResize)
                {
                    resized = new Mat();
                    Cv2.Resize(bgr, resized, new CvSize(), scale, scale, InterpolationFlags.Area);
                }

                int marginX = resized.Width * marginPercent / 100;
                int marginY = resized.Height * marginPercent / 100;
                var rect = new CvRect(marginX, marginY, resized.Width - 2 * marginX, resized.Height - 2 * marginY);

                var mask = new Mat(resized.Size(), MatType.CV_8UC1, Scalar.All(0));
                var bgModel = new Mat();
                var fgModel = new Mat();

                Cv2.GrabCut(resized, mask, rect, bgModel, fgModel, iterations, GrabCutModes.InitWithRect);

                var binaryMask = new Mat(resized.Size(), MatType.CV_8UC1);

                var maskIndexer = mask.GetGenericIndexer<byte>();
                var binaryIndexer = binaryMask.GetGenericIndexer<byte>();

                System.Threading.Tasks.Parallel.For(0, mask.Rows, y =>
                {
                    for (int x = 0; x < mask.Cols; x++)
                    {
                        byte val = maskIndexer[y, x];
                        binaryIndexer[y, x] = (val == 1 || val == 3) ? (byte)255 : (byte)0;
                    }
                });

                using var openKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new CvSize(3, 3));
                Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Open, openKernel, iterations: 2);

                using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new CvSize(15, 15));
                Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Close, closeKernel, iterations: 3);

                Cv2.GaussianBlur(binaryMask, binaryMask, new CvSize(7, 7), 2.5);
                Cv2.Threshold(binaryMask, binaryMask, 127, 255, ThresholdTypes.Binary);

                if (erosion > 0)
                {
                    using var erodeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new CvSize(3, 3));
                    Cv2.Erode(binaryMask, binaryMask, erodeKernel, iterations: erosion);
                }

                if (needsResize)
                {
                    var fullSizeMask = new Mat();
                    Cv2.Resize(binaryMask, fullSizeMask, source.Size(), 0, 0, InterpolationFlags.Cubic);
                    Cv2.GaussianBlur(fullSizeMask, fullSizeMask, new CvSize(5, 5), 1.5);
                    Cv2.Threshold(fullSizeMask, fullSizeMask, 127, 255, ThresholdTypes.Binary);

                    binaryMask.Dispose();
                    binaryMask = fullSizeMask;
                    resized.Dispose();
                }

                bgr.Dispose();
                mask.Dispose();
                bgModel.Dispose();
                fgModel.Dispose();
                return binaryMask;
            }
            catch
            {
                return new Mat(source.Size(), MatType.CV_8UC1, Scalar.All(255));
            }
        }

        private (byte[] buf, int w, int h, int stride) SnapshotActiveLayer()
        {
            if (_img?.Mat == null || _img.Mat.Empty()) return (Array.Empty<byte>(), 0, 0, 0);
            int w = _img.Mat.Cols, h = _img.Mat.Rows, stride = w * 4;
            var buf = new byte[h * stride];
            Marshal.Copy(_img.Mat.Data, buf, 0, buf.Length);
            return (buf, w, h, stride);
        }

        private void HideActiveLayerPixels()
        {
            if (_img?.Mat != null && !_img.Mat.Empty()) _img.Mat.SetTo(Scalar.All(0));
        }

        private void RestoreActiveLayerPixels(byte[] src, int w, int h, int stride)
        {
            if (_img?.Mat != null && !_img.Mat.Empty()) Marshal.Copy(src, 0, _img.Mat.Data, src.Length);
        }

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
                if (A == 0)
                {
                    pOut[i] = pOut[i + 1] = pOut[i + 2] = 0;
                }
                else
                {
                    pOut[i] = (byte)((B * A + 127) / 255);
                    pOut[i + 1] = (byte)((G * A + 127) / 255);
                    pOut[i + 2] = (byte)((R * A + 127) / 255);
                }
            }
        }

        private static void PBGRA_to_BGRA(byte[] pbgra, int w, int h, int stride, byte[] bgraOut)
        {
            for (int i = 0; i < pbgra.Length; i += 4)
            {
                byte Bp = pbgra[i], Gp = pbgra[i + 1], Rp = pbgra[i + 2], A = pbgra[i + 3];
                bgraOut[i + 3] = A;
                if (A == 0)
                {
                    bgraOut[i] = bgraOut[i + 1] = bgraOut[i + 2] = 0;
                }
                else
                {
                    bgraOut[i] = (byte)Min(255, (Bp * 255 + (A >> 1)) / A);
                    bgraOut[i + 1] = (byte)Min(255, (Gp * 255 + (A >> 1)) / A);
                    bgraOut[i + 2] = (byte)Min(255, (Rp * 255 + (A >> 1)) / A);
                }
            }
        }

        private static Mat MakeMatFromBGRA(byte[] bgra, int w, int h, int stride)
        {
            var mat = new Mat(h, w, MatType.CV_8UC4);
            Marshal.Copy(bgra, 0, mat.Data, bgra.Length);
            return mat;
        }

        private static byte[] MatToTightBGRA(Mat mat)
        {
            Mat converted;
            bool dispose = false;
            if (mat.Channels() == 3)
            {
                converted = new Mat();
                Cv2.CvtColor(mat, converted, ColorConversionCodes.BGR2BGRA);
                dispose = true;
            }
            else if (mat.Channels() == 1)
            {
                converted = new Mat();
                Cv2.CvtColor(mat, converted, ColorConversionCodes.GRAY2BGRA);
                dispose = true;
            }
            else
            {
                converted = mat;
            }

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
                try
                {
                    if (!File.Exists(p)) continue;
                    var tmp = Cv2.ImRead(p, ImreadModes.Unchanged);
                    if (tmp.Empty()) continue;
                    if (tmp.Channels() == 3) Cv2.CvtColor(tmp, tmp, ColorConversionCodes.BGR2BGRA);
                    _sticker = tmp;
                    StatusText.Content = $"Loaded crown: {Path.GetFileName(p)}";
                    return;
                }
                catch
                {
                }
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
                {
                    "face",
                    ("haarcascade_frontalface_default.xml",
                        "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml")
                },
                {
                    "eye",
                    ("haarcascade_eye.xml",
                        "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_eye.xml")
                },
            };
            using var client = new HttpClient();
            foreach (var m in models)
            {
                var path = Path.Combine(modelDir, m.Value.name);
                if (File.Exists(path)) continue;
                try
                {
                    StatusText.Content = $"Downloading {m.Value.name}...";
                    var data = await client.GetByteArrayAsync(m.Value.url);
                    File.WriteAllBytes(path, data);
                }
                catch (Exception ex)
                {
                    StatusText.Content = $"Download failed for {m.Value.name}: {ex.Message}";
                }
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
                _faceClassifier = null;
                _eyeClassifier = null;
                StatusText.Content = $"Model load error: {ex.Message}";
            }
        }

        private static string[] BuildSearchPathsForAsset(string folder, string[] files)
        {
            var list = new List<string>();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 5 && dir != null; i++)
            {
                var f = Path.Combine(dir.FullName, folder);
                foreach (var name in files) list.Add(Path.Combine(f, name));
                dir = dir.Parent;
            }

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
            if (channels.Length < 4)
            {
                foreach (var c in channels) c.Dispose();
                return;
            }

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
            var fg = new Mat();
            var bg = new Mat();
            if (foregroundBGRA.Channels() == 4) Cv2.CvtColor(foregroundBGRA, fg, ColorConversionCodes.BGRA2BGR);
            else fg = foregroundBGRA.Clone();
            if (backgroundBGRA.Channels() == 4) Cv2.CvtColor(backgroundBGRA, bg, ColorConversionCodes.BGRA2BGR);
            else bg = backgroundBGRA.Clone();
            fg.ConvertTo(fg, MatType.CV_32FC3, 1.0 / 255);
            bg.ConvertTo(bg, MatType.CV_32FC3, 1.0 / 255);
            var alpha = new Mat();
            mask255.ConvertTo(alpha, MatType.CV_32FC1, 1.0 / 255.0);
            var alpha3 = new Mat();
            Cv2.Merge(new[] { alpha, alpha, alpha }, alpha3);
            var ones = new Mat(alpha3.Size(), MatType.CV_32FC3, new Scalar(1.0, 1.0, 1.0));
            var invAlpha3 = new Mat();
            Cv2.Subtract(ones, alpha3, invAlpha3);
            var fgMul = new Mat();
            var bgMul = new Mat();
            Cv2.Multiply(fg, alpha3, fgMul);
            Cv2.Multiply(bg, invAlpha3, bgMul);
            var comp = new Mat();
            Cv2.Add(fgMul, bgMul, comp);
            comp.ConvertTo(comp, MatType.CV_8UC3, 255);
            Cv2.CvtColor(comp, comp, ColorConversionCodes.BGR2BGRA);
            alpha.Dispose();
            alpha3.Dispose();
            ones.Dispose();
            invAlpha3.Dispose();
            fgMul.Dispose();
            bgMul.Dispose();
            fg.Dispose();
            bg.Dispose();
            return comp;
        }

        private static void AdjustSatVal_BGRA(Mat bgra, double satMul, double valMul)
        {
            if (Math.Abs(satMul - 1.0) < 0.001 && Math.Abs(valMul - 1.0) < 0.001)
            {
                return;
            }

            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            var hsv = new Mat();
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            Mat[] ch = Cv2.Split(hsv);

            if (Math.Abs(satMul - 1.0) > 0.001)
                ch[1].ConvertTo(ch[1], ch[1].Type(), satMul, 0);
            if (Math.Abs(valMul - 1.0) > 0.001)
                ch[2].ConvertTo(ch[2], ch[2].Type(), valMul, 0);

            Cv2.Merge(ch, hsv);
            Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
            Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
            foreach (var m in ch) m.Dispose();
            hsv.Dispose();
            bgr.Dispose();
        }

        internal Canvas? GetArtboardOrNull()
        {
            var byName = this.FindName("Artboard") as Canvas;
            if (byName != null) return byName;
            var namedArtboard = FindDescendantByName<Canvas>(this, "Artboard");
            if (namedArtboard != null) return namedArtboard;
            var ink = (this.FindName("PaintCanvas") as InkCanvas) ?? FindDescendant<InkCanvas>(this);
            if (ink?.Parent is Canvas parentCanvas) return parentCanvas;
            return FindDescendant<Canvas>(this);
        }

        private static void AddOverlayUnderInteractionLayer(Canvas board, UIElement overlay)
        {
            if (VisualTreeHelper.GetParent(overlay) is Panel old) old.Children.Remove(overlay);
            int idx = -1, z = 1;
            for (int i = 0; i < board.Children.Count; i++)
            {
                var ch = board.Children[i];
                if (ch is InkCanvas || (ch as FrameworkElement)?.Name == "PaintCanvas")
                {
                    idx = i;
                    z = Panel.GetZIndex(ch);
                    break;
                }
            }

            if (idx >= 0)
            {
                board.Children.Insert(idx, overlay);
                Panel.SetZIndex(overlay, z);
            }
            else
            {
                board.Children.Add(overlay);
                Panel.SetZIndex(overlay, 1);
            }
        }

        private static T? FindDescendantByName<T>(DependencyObject r, string n) where T : FrameworkElement
        {
            if (r is T t && t.Name == n) return t;
            int count = VisualTreeHelper.GetChildrenCount(r);
            for (int i = 0; i < count; i++)
            {
                var c = VisualTreeHelper.GetChild(r, i);
                var f = FindDescendantByName<T>(c, n);
                if (f != null) return f;
            }

            return null;
        }

        private static T? FindDescendant<T>(DependencyObject r) where T : DependencyObject
        {
            if (r is T t) return t;
            int count = VisualTreeHelper.GetChildrenCount(r);
            for (int i = 0; i < count; i++)
            {
                var c = VisualTreeHelper.GetChild(r, i);
                var f = FindDescendant<T>(c);
                if (f != null) return f;
            }

            return null;
        }
    }

    internal sealed class FilterWindow : WpfWindow
    {
        private readonly MainWindow _main;
        private string _filterMode = "Grayscale";

        private ComboBox? _modeCombo;
        private StackPanel? _basicPanel, _bgPanel, _maskPanel;
        private Slider? _amtSlider, _lvlSlider, _blkSlider;
        private Slider? _bgSigmaSlider, _bgSatSlider, _bgValSlider;
        private Slider? _marginSlider, _iterSlider, _erosionSlider, _qualitySlider;
        private Button? _btnPickBg;

        public FilterWindow(MainWindow main)
        {
            _main = main;

            Title = "Filters & Effects";
            Width = 480;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 420;
            MinHeight = 500;

            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 243, 243));

            _filterMode = "Grayscale";
            _main._filterMode = "Grayscale";
            _main._filterAmount = 1.0;
            _main._posterizeLevels = 4;
            _main._pixelBlock = 8;
            _main._bgBlurRadius = 25;
            _main._bgSatMul = 1.0;
            _main._bgValMul = 1.0;
            _main._marginPercent = 3;
            _main._iterations = 3;
            _main._erosion = 2;
            _main._maskQuality = 1024;
            _main._bgImage?.Dispose();
            _main._bgImage = null;
            _main._maskDirty = true;

            Content = BuildControlsPanel();

            ContentRendered += async (s, e) =>
            {
                var board = GetArtboard();
                if (board != null)
                {
                    _main.StartLayerFilterSession(board);
                }
            };

            Closing += (s, e) =>
            {
                if (_main._filterActive)
                {
                    _main.EndLayerFilterSession(apply: false);
                }
            };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _main.EndLayerFilterSession(apply: true);
                    DialogResult = true;
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    _main.EndLayerFilterSession(apply: false);
                    DialogResult = false;
                    Close();
                    e.Handled = true;
                }
            };
        }

        private UIElement BuildControlsPanel()
        {
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(20)
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };

            stack.Children.Add(new TextBlock
            {
                Text = "FILTER SETTINGS",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 16)
            });

            stack.Children.Add(new TextBlock
            {
                Text =
                    "Live preview shows on canvas.\nPress Enter to apply, Esc to cancel.\n\nNote: High-res images may take time to process.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Effect Type:",
                Foreground = Foreground,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            });

            _modeCombo = new ComboBox
            {
                ItemsSource = new[]
                {
                    "Grayscale", "Invert", "Sepia", "Posterize", "Pixelate", "BackgroundBlur", "BackgroundReplace",
                    "AddCrown"
                },
                SelectedItem = _filterMode,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _modeCombo.SelectionChanged += (s, e) =>
            {
                _filterMode = (string)_modeCombo.SelectedItem!;
                _main._filterMode = _filterMode;
                UpdatePanelVisibility();
                _main._maskDirty = true;
                _main._filterDirty = true;
            };
            stack.Children.Add(_modeCombo);

            stack.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 16) });

            _basicPanel = new StackPanel();
            _amtSlider = MakeSliderWithValue(_basicPanel, "Amount", 0, 1, 1.0, v =>
            {
                _main._filterAmount = v;
                _main._filterDirty = true;
            }, integerOnly: false);
            _lvlSlider = MakeSliderWithValue(_basicPanel, "Levels", 2, 8, 4, v =>
            {
                _main._posterizeLevels = (int)Round(v);
                _main._filterDirty = true;
            }, integerOnly: true);
            _blkSlider = MakeSliderWithValue(_basicPanel, "Block Size", 2, 40, 8, v =>
            {
                _main._pixelBlock = (int)Round(v);
                _main._filterDirty = true;
            }, integerOnly: true);
            stack.Children.Add(_basicPanel);

            _bgPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            _bgPanel.Children.Add(new TextBlock
            {
                Text = "BACKGROUND",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 200, 255)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            _btnPickBg = new Button
            {
                Content = "Choose Image...",
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _btnPickBg.Click += (_, __) =>
            {
                var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" };
                if (ofd.ShowDialog() == true)
                {
                    _main._bgImage?.Dispose();
                    var tmp = Cv2.ImRead(ofd.FileName, ImreadModes.Unchanged);
                    if (!tmp.Empty())
                    {
                        if (tmp.Channels() == 3) Cv2.CvtColor(tmp, tmp, ColorConversionCodes.BGR2BGRA);
                        _main._bgImage = tmp;
                        _btnPickBg.Content = $"✓ {Path.GetFileName(ofd.FileName)}";
                        _main._filterDirty = true;
                    }
                }
            };
            _bgPanel.Children.Add(_btnPickBg);

            _bgSigmaSlider = MakeSliderWithValue(_bgPanel, "Blur Radius", 1, 80, 25, v =>
            {
                _main._bgBlurRadius = v;
                _main._filterDirty = true;
            }, integerOnly: false);
            _bgSatSlider = MakeSliderWithValue(_bgPanel, "Saturation", 0, 2, 1.0, v =>
            {
                _main._bgSatMul = v;
                _main._filterDirty = true;
            }, integerOnly: false);
            _bgValSlider = MakeSliderWithValue(_bgPanel, "Brightness", 0, 2, 1.0, v =>
            {
                _main._bgValMul = v;
                _main._filterDirty = true;
            }, integerOnly: false);
            stack.Children.Add(_bgPanel);

            _maskPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            _maskPanel.Children.Add(new TextBlock
            {
                Text = "DETECTION",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 200, 255)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            _qualitySlider = MakeSliderWithValue(_maskPanel, "Edge Quality", 512, 2048, 1024, v =>
            {
                _main._maskQuality = (int)Round(v);
                _main._maskDirty = true;
                _main._filterDirty = true;
            }, integerOnly: true);

            var qualityHint = new TextBlock
            {
                Text = "512=Fast/Blocky • 1024=Balanced • 2048=Slow/Smooth",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                Margin = new Thickness(0, -4, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            _maskPanel.Children.Add(qualityHint);

            _marginSlider = MakeSliderWithValue(_maskPanel, "Margin %", 1, 10, 3, v =>
            {
                _main._marginPercent = (int)Round(v);
                _main._maskDirty = true;
                _main._filterDirty = true;
            }, integerOnly: true);
            _iterSlider = MakeSliderWithValue(_maskPanel, "Iterations", 1, 8, 3, v =>
            {
                _main._iterations = (int)Round(v);
                _main._maskDirty = true;
                _main._filterDirty = true;
            }, integerOnly: true);
            _erosionSlider = MakeSliderWithValue(_maskPanel, "Edge Cleanup", 0, 5, 2, v =>
            {
                _main._erosion = (int)Round(v);
                _main._maskDirty = true;
                _main._filterDirty = true;
            }, integerOnly: true);
            stack.Children.Add(_maskPanel);

            UpdatePanelVisibility();

            stack.Children.Add(new Separator { Margin = new Thickness(0, 24, 0, 16) });

            var btnPanel = new StackPanel
                { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var applyBtn = new Button
            {
                Content = "Apply",
                Width = 80,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(40, 120, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            applyBtn.Click += (s, e) =>
            {
                _main.EndLayerFilterSession(apply: true);
                DialogResult = true;
                Close();
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 80,
                Padding = new Thickness(12, 6, 12, 6),
                IsCancel = true
            };
            cancelBtn.Click += (s, e) =>
            {
                _main.EndLayerFilterSession(apply: false);
                DialogResult = false;
                Close();
            };

            btnPanel.Children.Add(applyBtn);
            btnPanel.Children.Add(cancelBtn);
            stack.Children.Add(btnPanel);

            scrollViewer.Content = stack;
            return scrollViewer;
        }

        private Slider MakeSliderWithValue(Panel parent, string label, double min, double max, double val,
            Action<double> onChange, bool integerOnly = false)
        {
            parent.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 200, 200)),
                Margin = new Thickness(0, 8, 0, 4),
                FontSize = 11
            });

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                Margin = new Thickness(0, 0, 8, 0),
                IsSnapToTickEnabled = integerOnly,
                TickFrequency = integerOnly ? 1 : 0.01
            };
            Grid.SetColumn(slider, 0);

            var textBox = new TextBox
            {
                Text = integerOnly ? ((int)val).ToString() : val.ToString("F2"),
                Width = 60,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(255, 50, 50, 50)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70))
            };
            Grid.SetColumn(textBox, 1);

            slider.ValueChanged += (_, e) =>
            {
                double displayValue = integerOnly ? Math.Round(e.NewValue) : e.NewValue;
                textBox.Text = integerOnly ? ((int)displayValue).ToString() : displayValue.ToString("F2");
            };

            slider.PreviewMouseUp += (_, __) =>
            {
                double finalValue = integerOnly ? Math.Round(slider.Value) : slider.Value;
                if (integerOnly) slider.Value = finalValue;
                onChange(finalValue);
            };

            slider.PreviewKeyUp += (_, e) =>
            {
                if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
                {
                    double finalValue = integerOnly ? Math.Round(slider.Value) : slider.Value;
                    if (integerOnly) slider.Value = finalValue;
                    onChange(finalValue);
                }
            };

            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    if (double.TryParse(textBox.Text, out double newVal))
                    {
                        if (integerOnly) newVal = Math.Round(newVal);
                        if (newVal >= min && newVal <= max)
                        {
                            slider.Value = newVal;
                            onChange(newVal);
                        }
                    }
                }
            };

            grid.Children.Add(slider);
            grid.Children.Add(textBox);
            parent.Children.Add(grid);

            return slider;
        }

        private void UpdatePanelVisibility()
        {
            bool isGrayscale = _filterMode == "Grayscale";
            bool isInvert = _filterMode == "Invert";
            bool isSepia = _filterMode == "Sepia";
            bool isPosterize = _filterMode == "Posterize";
            bool isPixelate = _filterMode == "Pixelate";
            bool bgBlur = _filterMode == "BackgroundBlur";
            bool bgRepl = _filterMode == "BackgroundReplace";
            bool bgAny = bgBlur || bgRepl;

            if (_amtSlider != null) SetSliderVisibility(_basicPanel!, _amtSlider, isGrayscale || isInvert || isSepia);
            if (_lvlSlider != null) SetSliderVisibility(_basicPanel!, _lvlSlider, isPosterize);
            if (_blkSlider != null) SetSliderVisibility(_basicPanel!, _blkSlider, isPixelate);

            if (_bgPanel != null) _bgPanel.Visibility = bgAny ? Visibility.Visible : Visibility.Collapsed;

            if (_btnPickBg != null) _btnPickBg.Visibility = bgRepl ? Visibility.Visible : Visibility.Collapsed;

            if (_bgSigmaSlider != null) SetSliderVisibility(_bgPanel!, _bgSigmaSlider, bgBlur);

            if (_maskPanel != null) _maskPanel.Visibility = bgAny ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetSliderVisibility(StackPanel panel, Slider slider, bool visible)
        {
            UIElement? gridToHide = null;
            UIElement? labelToHide = null;

            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is Grid grid && grid.Children.Contains(slider))
                {
                    gridToHide = grid;
                    if (i > 0 && panel.Children[i - 1] is TextBlock)
                    {
                        labelToHide = panel.Children[i - 1];
                    }

                    break;
                }
            }

            if (gridToHide != null) gridToHide.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (labelToHide != null) labelToHide.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private Canvas? GetArtboard()
        {
            var byName = _main.FindName("Artboard") as Canvas;
            if (byName != null) return byName;

            var ink = _main.FindName("PaintCanvas") as InkCanvas;
            if (ink?.Parent is Canvas parentCanvas) return parentCanvas;

            return FindDescendant<Canvas>(_main);
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
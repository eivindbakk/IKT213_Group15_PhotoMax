// File: MenuHandlers/Tools.cs
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WF = System.Windows.Forms;

namespace PhotoMax
{
    public partial class MainWindow
    {
        // ===== Live blocky drawing state (strict bitmap overlay) =====
        private Image? _liveImage;                 // sits above InkCanvas
        private WriteableBitmap? _liveWB;          // BGRA32, same size as Doc
        private byte[]? _liveBuf;                  // backing buffer for _liveWB
        private bool _isDrawing = false;
        private readonly List<Point> _snappedPoints = new();

        // Cursor preview (zoom-aware outline of true stamp footprint)
        private Rectangle? _brushPreview;          // outline showing true brush footprint
        private bool _liveHooks = false;

        // ----------------- ZOOM -----------------
        private void Zoom_In_Click(object sender, RoutedEventArgs e)
        {
            double next = _zoom * ZoomStep;
            if (AtOrBelowOne(next))
            {
                ApplyZoom_NoScroll(next);
            }
            else
            {
                var m = new Point(Scroller.ViewportWidth / 2.0, Scroller.ViewportHeight / 2.0);
                var p = ViewportPointToWorkspaceBeforeZoom(m);
                SetZoomToCursor(next, m, p);
            }
        }

        private void Zoom_Out_Click(object sender, RoutedEventArgs e)
        {
            double next = _zoom / ZoomStep;
            if (AtOrBelowOne(next))
            {
                ApplyZoom_NoScroll(next);
            }
            else
            {
                var m = new Point(Scroller.ViewportWidth / 2.0, Scroller.ViewportHeight / 2.0);
                var p = ViewportPointToWorkspaceBeforeZoom(m);
                SetZoomToCursor(next, m, p);
            }
        }

        private void Zoom_100_Click(object? sender, RoutedEventArgs e) => SetZoomCentered(1.0);

        private void Zoom_Fit_Click(object? sender, RoutedEventArgs e)
        {
            var margin = 16.0;
            var vw = System.Math.Max(1, Scroller.ViewportWidth  - margin);
            var vh = System.Math.Max(1, Scroller.ViewportHeight - margin);

            var cw = System.Math.Max(1, Artboard.ActualWidth);
            var ch = System.Math.Max(1, Artboard.ActualHeight);

            var fit = System.Math.Max(MinZoom, System.Math.Min(vw / cw, vh / ch));
            SetZoomCentered(fit);
        }

        // ----------------- BRUSH CORE -----------------

        /// <summary>Effective brush size in image pixels, based on palette + image size.</summary>
        private int ComputeEffectiveBrushSizePx()
        {
            int imgMin = 0;
            if (_img != null && _img.Doc != null)
                imgMin = System.Math.Max(1, System.Math.Min(_img.Doc.Width, _img.Doc.Height));

            int baseSize = _brushSizes[_brushIndex];
            int minPx   = imgMin > 0 ? System.Math.Max(1, imgMin / 24) : 1; // tune divisor
            int eff     = System.Math.Max(baseSize, minPx);
            return System.Math.Clamp(eff, 1, 256);
        }

        /// <summary>Clamp the brush center so the whole eff×eff stamp fits inside the image.</summary>
        private static (int cx, int cy) ClampCenterToFit(int cx, int cy, int eff, int w, int h)
        {
            int half = eff / 2;
            int minCenX = half;
            int minCenY = half;
            int maxCenX = Math.Max(half, w - eff + half);
            int maxCenY = Math.Max(half, h - eff + half);
            cx = Math.Clamp(cx, minCenX, maxCenX);
            cy = Math.Clamp(cy, minCenY, maxCenY);
            return (cx, cy);
        }

        /// <summary>Called from MainWindow.ConfigureBrush(). Sets ink attributes and hooks live drawing/preview.</summary>
        internal void ApplyInkBrushAttributes()
        {
            // We render ourselves, so disable InkCanvas' own strokes/cursor to avoid any extra squares.
            PaintCanvas.EditingMode = InkCanvasEditingMode.None;
            PaintCanvas.Cursor = Cursors.None; // hide system cursor over canvas (we show our outline instead)

            // DA irrelevant while EditingMode=None
            var da = PaintCanvas.DefaultDrawingAttributes;
            da.IsHighlighter = false;
            da.IgnorePressure = true;
            da.FitToCurve = false;
            da.StylusTip = StylusTip.Rectangle;
            da.StylusTipTransform = Matrix.Identity;
            da.Color = _brushColor;
            da.Width = 1;
            da.Height = 1;

            // Rendering hints (keep aliasing)
            RenderOptions.SetBitmapScalingMode(PaintCanvas, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(PaintCanvas, EdgeMode.Aliased);
            PaintCanvas.SnapsToDevicePixels = true;

            // Strict bitmap overlay + input hooks
            EnsureLiveOverlayBitmap();
            HookLiveDrawingEvents();

            // Brush preview outline (zoom-aware)
            EnsureBrushPreview();
            UpdateBrushPreviewVisibility(true);
            SyncBrushPreviewStrokeToZoom();
            SyncBrushPreviewColor();

            int eff = ComputeEffectiveBrushSizePx();
            StatusText.Content = _eraseMode
                ? $"Eraser ON • Size: {eff}px"
                : $"Brush • Color: #{_brushColor.R:X2}{_brushColor.G:X2}{_brushColor.B:X2} • Size: {eff}px";
        }

        private void EnsureLiveOverlayBitmap()
        {
            if (_img == null || _img.Doc == null) return;

            int w = _img.Doc.Width, h = _img.Doc.Height;
            if (w < 1 || h < 1) return;

            bool need = _liveWB == null || _liveWB.PixelWidth != w || _liveWB.PixelHeight != h;
            if (need)
            {
                _liveWB = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _liveBuf = new byte[w * h * 4];
                if (_liveImage == null)
                {
                    _liveImage = new Image
                    {
                        Width = w,
                        Height = h,
                        IsHitTestVisible = false,
                        Source = _liveWB
                    };
                    Artboard.Children.Add(_liveImage);
                }
                else
                {
                    _liveImage.Source = _liveWB;
                    _liveImage.Width  = w;
                    _liveImage.Height = h;
                }

                RenderOptions.SetBitmapScalingMode(_liveImage, BitmapScalingMode.NearestNeighbor);
                _liveImage.SnapsToDevicePixels = true;

                Array.Clear(_liveBuf!);
                _liveWB.WritePixels(new Int32Rect(0, 0, w, h), _liveBuf, w * 4, 0);
            }
        }

        private void ClearLiveOverlay()
        {
            if (_liveWB == null || _liveBuf == null) return;
            int w = _liveWB.PixelWidth, h = _liveWB.PixelHeight;
            Array.Clear(_liveBuf);
            _liveWB.WritePixels(new Int32Rect(0, 0, w, h), _liveBuf, w * 4, 0);
        }

        private void HookLiveDrawingEvents()
        {
            if (_liveHooks) return;
            _liveHooks = true;

            PaintCanvas.PreviewMouseDown += PaintCanvas_PreviewMouseDown_BeginDraw;
            PaintCanvas.PreviewMouseMove += PaintCanvas_PreviewMouseMove_Draw;
            PaintCanvas.PreviewMouseUp   += PaintCanvas_PreviewMouseUp_EndDraw;

            // Preview rectangle follow + visibility lifecycle
            PaintCanvas.MouseMove  += PaintCanvas_MouseMove_UpdatePreview;
            PaintCanvas.MouseLeave += (_, __) =>
            {
                UpdateBrushPreviewVisibility(false);
                PaintCanvas.Cursor = Cursors.Arrow; // restore cursor when leaving
            };
            PaintCanvas.MouseEnter += (s, e) =>
            {
                PaintCanvas.Cursor = Cursors.None;  // hide cursor on enter
                UpdateBrushPreviewVisibility(true);
                UpdateBrushPreviewAt(e.GetPosition(PaintCanvas)); // position immediately
            };

            if (_img != null) _img.ImageChanged += EnsureLiveOverlayBitmap;
        }

        private void PaintCanvas_PreviewMouseDown_BeginDraw(object? sender, MouseButtonEventArgs e)
        {
            if (_img == null || _img.Doc == null) return;
            EnsureLiveOverlayBitmap();

            _isDrawing = true;
            _snappedPoints.Clear();

            var p = e.GetPosition(PaintCanvas);
            var s = SnapToImagePixel(p);

            int eff = ComputeEffectiveBrushSizePx();
            int w = _img.Doc.Width, h = _img.Doc.Height;

            // Clamp center so full stamp fits
            var fit = ClampCenterToFit((int)s.X, (int)s.Y, eff, w, h);
            s = new Point(fit.cx, fit.cy);
            _snappedPoints.Add(s);

            FillSquareIntoOverlay(fit.cx, fit.cy, eff, _eraseMode ? Colors.White : _brushColor);
            PushOverlay();

            PaintCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void PaintCanvas_PreviewMouseMove_Draw(object? sender, MouseEventArgs e)
        {
            if (!_isDrawing) return;
            if (_img == null || _img.Doc == null) return;

            var p = e.GetPosition(PaintCanvas);
            var s = SnapToImagePixel(p);

            int eff = ComputeEffectiveBrushSizePx();
            int w = _img.Doc.Width, h = _img.Doc.Height;

            // Clamp center so full stamp fits
            var fit = ClampCenterToFit((int)s.X, (int)s.Y, eff, w, h);
            s = new Point(fit.cx, fit.cy);

            var last = _snappedPoints[^1];
            if ((int)last.X != (int)s.X || (int)last.Y != (int)s.Y)
            {
                var col = _eraseMode ? Colors.White : _brushColor;
                foreach (var px in SupercoverLine((int)last.X, (int)last.Y, fit.cx, fit.cy))
                    FillSquareIntoOverlay(px.x, px.y, eff, col);

                _snappedPoints.Add(s);
                PushOverlay();
            }

            e.Handled = true;
        }

        private void PaintCanvas_PreviewMouseUp_EndDraw(object? sender, MouseButtonEventArgs e)
        {
            if (!_isDrawing) return;
            _isDrawing = false;
            PaintCanvas.ReleaseMouseCapture();

            CommitOverlayToImage();
            ClearLiveOverlay();
            _snappedPoints.Clear();
            e.Handled = true;
        }

        // Snap to integer pixel inside image bounds (center may still be adjusted later)
        private Point SnapToImagePixel(Point p)
        {
            int x = (int)System.Math.Round(p.X);
            int y = (int)System.Math.Round(p.Y);

            int w = _img?.Doc?.Width  ?? int.MaxValue;
            int h = _img?.Doc?.Height ?? int.MaxValue;

            if (w != int.MaxValue && h != int.MaxValue)
            {
                x = System.Math.Clamp(x, 0, System.Math.Max(0, w - 1));
                y = System.Math.Clamp(y, 0, System.Math.Max(0, h - 1));
            }
            return new Point(x, y);
        }

        // "Supercover" line: all grid cells a line passes through
        private IEnumerable<(int x, int y)> SupercoverLine(int x0, int y0, int x1, int y1)
        {
            int dx = x1 - x0, dy = y1 - y0;
            int sx = System.Math.Sign(dx), sy = System.Math.Sign(dy);
            int ax = System.Math.Abs(dx), ay = System.Math.Abs(dy);
            int x = x0, y = y0;

            if (ax >= ay)
            {
                int d = ay * 2 - ax;
                int incrE = ay * 2;
                int incrNE = (ay - ax) * 2;
                for (;;)
                {
                    yield return (x, y);
                    if (x == x1) break;
                    if (d <= 0) { d += incrE; x += sx; }
                    else { d += incrNE; x += sx; y += sy; yield return (x - sx, y); }
                }
            }
            else
            {
                int d = ax * 2 - ay;
                int incrN = ax * 2;
                int incrNE = (ax - ay) * 2;
                for (;;)
                {
                    yield return (x, y);
                    if (y == y1) break;
                    if (d <= 0) { d += incrN; y += sy; }
                    else { d += incrNE; y += sy; x += sx; yield return (x, y - sy); }
                }
            }
        }

        // Fill eff×eff block centered at (cx,cy) into overlay buffer
        private void FillSquareIntoOverlay(int cx, int cy, int eff, Color color)
        {
            if (_liveWB == null || _liveBuf == null) return;
            int w = _liveWB.PixelWidth, h = _liveWB.PixelHeight;
            int half = eff / 2;

            int left = cx - half;
            int top  = cy - half;
            if (left < 0) left = 0;
            if (top  < 0) top  = 0;
            int right  = System.Math.Min(w, left + eff);
            int bottom = System.Math.Min(h, top  + eff);
            if (right <= left || bottom <= top) return;

            byte B = color.B, G = color.G, R = color.R;
            for (int y = top; y < bottom; y++)
            {
                int row = y * w * 4;
                for (int x = left; x < right; x++)
                {
                    int i = row + x * 4;
                    _liveBuf[i + 0] = B;
                    _liveBuf[i + 1] = G;
                    _liveBuf[i + 2] = R;
                    _liveBuf[i + 3] = 255; // opaque
                }
            }
        }

        private void PushOverlay()
        {
            if (_liveWB == null || _liveBuf == null) return;
            int w = _liveWB.PixelWidth, h = _liveWB.PixelHeight;
            _liveWB.WritePixels(new Int32Rect(0, 0, w, h), _liveBuf, w * 4, 0);
        }

        private void CommitOverlayToImage()
        {
            if (_img == null || _img.Doc == null || _liveWB == null || _liveBuf == null) return;

            int w = _liveWB.PixelWidth, h = _liveWB.PixelHeight;
            int step = (int)_img.Doc.Image.Step(); // OpenCV row stride
            var dst = new byte[step * h];
            Marshal.Copy(_img.Doc.Image.Data, dst, 0, dst.Length);

            for (int y = 0; y < h; y++)
            {
                int rb = y * w * 4;  // overlay row base
                int rd = y * step;   // doc row base
                for (int x = 0; x < w; x++)
                {
                    int i = rb + x * 4;
                    byte a = _liveBuf[i + 3];
                    if (a == 0) continue;

                    dst[rd + x * 4 + 0] = _liveBuf[i + 0];
                    dst[rd + x * 4 + 1] = _liveBuf[i + 1];
                    dst[rd + x * 4 + 2] = _liveBuf[i + 2];
                    dst[rd + x * 4 + 3] = 255;
                }
            }

            Marshal.Copy(dst, 0, _img.Doc.Image.Data, dst.Length);
            _img.ForceRefreshView();
        }

        // ----------------- Brush preview (zoom-aware, snapped) -----------------
        private void EnsureBrushPreview()
        {
            if (_brushPreview != null) return;

            _brushPreview = new Rectangle
            {
                Stroke = new SolidColorBrush(_brushColor), // match chosen color
                Fill = Brushes.Transparent,                // outline-only
                StrokeThickness = 1,                       // normalized to screen px
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(_brushPreview, 900);
            PaintCanvas.Children.Add(_brushPreview);

            RenderOptions.SetEdgeMode(_brushPreview, EdgeMode.Aliased);
            _brushPreview.SnapsToDevicePixels = true;
        }

        private void UpdateBrushPreviewVisibility(bool visible)
        {
            if (_brushPreview == null) return;
            _brushPreview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateBrushPreviewAt(Point canvasPos)
        {
            if (_brushPreview == null) return;
            if (_img == null || _img.Doc == null) { UpdateBrushPreviewVisibility(false); return; }

            var s = SnapToImagePixel(canvasPos);
            int eff = ComputeEffectiveBrushSizePx();
            int w = _img.Doc.Width, h = _img.Doc.Height;

            // Force whole stamp to remain inside by adjusting center
            var fit = ClampCenterToFit((int)s.X, (int)s.Y, eff, w, h);

            int left = fit.cx - eff / 2;
            int top  = fit.cy - eff / 2;

            _brushPreview.Stroke = _eraseMode ? Brushes.Red : new SolidColorBrush(_brushColor);
            _brushPreview.Fill = Brushes.Transparent;

            InkCanvas.SetLeft(_brushPreview, left);
            InkCanvas.SetTop (_brushPreview, top);
            _brushPreview.Width  = eff;
            _brushPreview.Height = eff;

            SyncBrushPreviewStrokeToZoom();

            if (_brushPreview.Visibility != Visibility.Visible)
                _brushPreview.Visibility = Visibility.Visible;
        }

        private void PaintCanvas_MouseMove_UpdatePreview(object? sender, MouseEventArgs e)
        {
            UpdateBrushPreviewAt(e.GetPosition(PaintCanvas));
        }

        /// <summary>Keep the preview outline thickness ~1 *screen* pixel regardless of zoom.</summary>
        internal void SyncBrushPreviewStrokeToZoom()
        {
            if (_brushPreview == null) return;

            double thickness = 1.0 / Math.Max(0.0001, _zoom);
            if (thickness < 0.5) thickness = 0.5;
            if (thickness > 2.0) thickness = 2.0;

            _brushPreview.StrokeThickness = thickness;

            RenderOptions.SetEdgeMode(_brushPreview, EdgeMode.Aliased);
            _brushPreview.SnapsToDevicePixels = true;
        }

        // Keep preview color synced when brush color changes
        private void SyncBrushPreviewColor()
        {
            if (_brushPreview == null) return;
            if (_eraseMode) return;
            _brushPreview.Stroke = new SolidColorBrush(_brushColor);
        }

        // ----------------- Menu actions -----------------
        private void Tool_Erase_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = !_eraseMode;
            ApplyInkBrushAttributes();
        }

        private void Tool_ColorPicker_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WF.ColorDialog { AllowFullOpen = true, FullOpen = true };
            if (dlg.ShowDialog() == WF.DialogResult.OK)
            {
                _brushColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _eraseMode = false;
                ApplyInkBrushAttributes(); // updates preview color
            }
        }

        private void Tool_Brushes_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = false;
            ApplyInkBrushAttributes();
        }

        private void Tool_TextBox_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("TODO: Text box tool.");
        }

        private void Filter_Gaussian_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: Gaussian (OpenCvSharp).");

        private void Filter_Sobel_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: Sobel (OpenCvSharp).");

        private void Filter_Binary_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: Binary threshold.");

        private void Filter_HistogramThreshold_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show("TODO: Histogram threshold.");

        private void Colors_BrushSize_Click(object sender, RoutedEventArgs e)
        {
            _brushIndex = (_brushIndex + 1) % _brushSizes.Length;
            _eraseMode = false;
            ApplyInkBrushAttributes();
        }
    }
}

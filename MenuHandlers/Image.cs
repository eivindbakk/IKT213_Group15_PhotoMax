// File: MenuHandlers/Image.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// ---- Aliases to avoid clashes with OpenCvSharp ----
using WWindow = System.Windows.Window;
using WPoint  = System.Windows.Point;
using WRect   = System.Windows.Rect;
using OcvSize = OpenCvSharp.Size;
using OcvRect = OpenCvSharp.Rect;

using OpenCvSharp;

namespace PhotoMax
{
    /// <summary>
    /// OpenCvSharp-backed image (BGRA 8-bit for WPF interop).
    /// </summary>
    
    public partial class MainWindow
    {
        // ---- Image menu handlers (thin forwarders to ImageController) ----

        private void Select_Rect_Click(object sender, RoutedEventArgs e)
            => _img?.StartRectSelection();

        private void Select_Lasso_Click(object sender, RoutedEventArgs e)
            => _img?.StartLassoSelection();

        private void Select_Polygon_Click(object sender, RoutedEventArgs e)
            => _img?.StartPolygonSelection();

        private void Image_Crop_Click(object sender, RoutedEventArgs e)
            => _img?.CropCommand();

        private void Image_Resize_Click(object sender, RoutedEventArgs e)
            => _img?.ResizeWithDialog(this);

        private void Rotate_Right_Click(object sender, RoutedEventArgs e)
            => _img?.RotateRight90();

        private void Rotate_Left_Click(object sender, RoutedEventArgs e)
            => _img?.RotateLeft90();

        private void Flip_Vert_Click(object sender, RoutedEventArgs e)
            => _img?.FlipVertical();

        private void Flip_Horiz_Click(object sender, RoutedEventArgs e)
            => _img?.FlipHorizontal();
    }
    
    public sealed class ImageDoc : IDisposable
    {
        public Mat Image { get; private set; }          // CV_8UC4 (BGRA)
        public int Width  => Image?.Width  ?? 0;
        public int Height => Image?.Height ?? 0;

        public ImageDoc()
        {
            Image = new Mat(new OcvSize(1280, 720), MatType.CV_8UC4, new Scalar(255, 255, 255, 255));
        }

        public void Dispose()
        {
            Image?.Dispose();
            Image = null!;
        }

        public void ReplaceWith(Mat bgra)
        {
            var old = Image;
            Image = bgra;
            old?.Dispose();
        }

        /* ---------- WPF <-> Mat ---------- */

        public BitmapSource ToBitmapSource()
        {
            if (Image is null || Image.Empty()) throw new InvalidOperationException("Image is empty.");

            var src = Image;
            if (src.Type() != MatType.CV_8UC4)
            {
                var tmp = new Mat();
                Cv2.CvtColor(src, tmp, ColorConversionCodes.BGR2BGRA);
                src = tmp;
            }

            int w = src.Width, h = src.Height, stride = w * 4;
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, src.Data, stride * h, stride);
            bmp.Freeze();
            if (!ReferenceEquals(src, Image)) src.Dispose();
            return bmp;
        }

        public void FromBitmapSource(BitmapSource bmp)
        {
            var fmt = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;

            var data = new byte[stride * h];
            fmt.CopyPixels(data, stride, 0);

            var mat = new Mat(h, w, MatType.CV_8UC4);
            Marshal.Copy(data, 0, mat.Data, data.Length);
            ReplaceWith(mat);
        }

        /* ---------- Geometry ops ---------- */

        public void ResizeTo(int newWidth, int newHeight, InterpolationFlags mode)
        {
            var outMat = new Mat();
            Cv2.Resize(Image, outMat, new OcvSize(newWidth, newHeight), 0, 0, mode);
            ReplaceWith(outMat);
        }

        public void RotateRight90()
        {
            var outMat = new Mat();
            Cv2.Rotate(Image, outMat, RotateFlags.Rotate90Clockwise);
            ReplaceWith(outMat);
        }

        public void RotateLeft90()
        {
            var outMat = new Mat();
            Cv2.Rotate(Image, outMat, RotateFlags.Rotate90Counterclockwise);
            ReplaceWith(outMat);
        }

        public void FlipHorizontal()
        {
            var outMat = new Mat();
            Cv2.Flip(Image, outMat, FlipMode.Y);
            ReplaceWith(outMat);
        }

        public void FlipVertical()
        {
            var outMat = new Mat();
            Cv2.Flip(Image, outMat, FlipMode.X);
            ReplaceWith(outMat);
        }

        public void Crop(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            var rect = new OcvRect(
                Math.Clamp(x, 0, Math.Max(0, Width - 1)),
                Math.Clamp(y, 0, Math.Max(0, Height - 1)),
                Math.Clamp(width,  1, Width  - Math.Clamp(x, 0, Width  - 1)),
                Math.Clamp(height, 1, Height - Math.Clamp(y, 0, Height - 1))
            );
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using var roi = new Mat(Image, rect);
            var copy = roi.Clone();
            ReplaceWith(copy);
        }
    }

    /// <summary>
    /// Handles selection tools + applies image ops to the document and InkCanvas strokes.
    /// Adds interactive crop overlay. Also bakes strokes before any mutating op.
    /// </summary>
    public sealed class ImageController
    {
        private readonly System.Windows.Controls.Image _imageView;
        private readonly Canvas _artboard;
        private readonly ContentControl _status;
        private readonly InkCanvas _paint; // baked into Image on ops

        public ImageDoc Doc { get; } = new ImageDoc();

        // Notify host (MainWindow) when the underlying bitmap changed (so it can update brush policy).
        public event Action? ImageChanged;

        // --- selection state
        private enum SelMode { None, Rect, Lasso, Polygon, CropInteractive }
        private SelMode _mode = SelMode.None;

        // rectangle
        private Rectangle? _rectBox;
        private bool _isDragging;
        private WPoint _dragStart;

        // lasso (freehand)
        private Polyline? _lassoLine;
        private readonly List<WPoint> _lassoPts = new();

        // polygon (click-to-add)
        private Polyline? _polyLine;
        private readonly List<WPoint> _polyPts = new();
        private bool _polyActive;

        // active area (bounding box)
        private WRect? _activeSelectionRect;

        // ---- interactive crop overlay
        private Path? _cropMaskPath;
        private Rectangle? _cropRectVis;
        private readonly List<Rectangle> _cropHandles = new();
        private WRect _cropRect; // working rect
        private CropHit _cropHit = CropHit.None;
        private WPoint _cropDragStart;
        private const double HandleSize = 8;
        private const double MinCropSize = 4;

        private enum CropHit
        {
            None, Move,
            N, S, E, W,
            NE, NW, SE, SW
        }
        
        // still inside: namespace PhotoMax { public sealed class ImageController { ... } }
        public void ForceRefreshView()
        {
            // Reuse internal plumbing. RefreshView() already ends with: ImageChanged?.Invoke();
            RefreshView();
        }


        public ImageController(System.Windows.Controls.Image imageView, Canvas artboard, ContentControl status, InkCanvas paintCanvas)
        {
            _imageView = imageView;
            _artboard  = artboard;
            _status    = status;
            _paint     = paintCanvas;

            HookSelectionEvents();
            HookCropKeys();
            RefreshView();
        }

        /* ---------------- API called from MainWindow ---------------- */

        public void StartRectSelection()
        {
            BeginSelectionMode(SelMode.Rect);
            EnsureRectBox();
            _status.Content = "Rect: drag to select an area…";
        }

        public void StartLassoSelection()
        {
            BeginSelectionMode(SelMode.Lasso);
            EnsureLasso();
            _status.Content = "Lasso: hold left mouse and draw; release to finish.";
        }

        public void StartPolygonSelection()
        {
            BeginSelectionMode(SelMode.Polygon);
            EnsurePolygon();
            _polyActive = true;
            _status.Content = "Polygon: click to add points, double-click or right-click to finish.";
        }

        /// <summary>
        /// Crop menu: if Rect selection exists -> crop immediately, else open interactive crop overlay.
        /// </summary>
        public void CropCommand()
        {
            if (_mode == SelMode.Rect && _activeSelectionRect is not null)
            {
                BakeStrokesToImage();
                ApplyCropFromRect(_activeSelectionRect.Value);
                return;
            }
            StartInteractiveCrop();
        }

        public void RotateRight90()
        {
            BakeStrokesToImage();
            Doc.RotateRight90();
            EndSelectionMode();
            RefreshView();
        }

        public void RotateLeft90()
        {
            BakeStrokesToImage();
            Doc.RotateLeft90();
            EndSelectionMode();
            RefreshView();
        }

        public void FlipVertical()
        {
            BakeStrokesToImage();
            Doc.FlipVertical();
            EndSelectionMode();
            RefreshView();
        }

        public void FlipHorizontal()
        {
            BakeStrokesToImage();
            Doc.FlipHorizontal();
            EndSelectionMode();
            RefreshView();
        }

        public void ResizeWithDialog(WWindow owner)
        {
            var dlg = new ResizeInlineWindow(Doc.Width, Doc.Height) { Owner = owner };
            if (dlg.ShowDialog() == true)
            {
                BakeStrokesToImage();

                int oldW = Doc.Width, oldH = Doc.Height;
                int newW = dlg.ResultWidth, newH = dlg.ResultHeight;

                // Pixel-art friendly policy:
                //  - Downscale: AREA (box filter)
                //  - Upscale:   NEAREST (preserve blockiness, no fake detail)
                //  - Same size: LINEAR (no-op visually)
                InterpolationFlags mode;
                if (newW < oldW || newH < oldH)
                    mode = InterpolationFlags.Area;
                else if (newW > oldW || newH > oldH)
                    mode = InterpolationFlags.Nearest;
                else
                    mode = InterpolationFlags.Linear;

                Doc.ResizeTo(newW, newH, mode);

                EndSelectionMode();
                RefreshView();
                _status.Content = $"Resized to {newW}×{newH} ({mode})";
            }
        }

        /* ---------------- view plumbing ---------------- */

        private void RefreshView()
        {
            _imageView.Source = Doc.ToBitmapSource();

            // Make both bitmap and vector overlay honor pixel boundaries.
            RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetBitmapScalingMode(_paint, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(_paint, EdgeMode.Aliased);
            _paint.SnapsToDevicePixels = true;

            _artboard.Width  = Doc.Width;
            _artboard.Height = Doc.Height;

            _imageView.Width  = Doc.Width;
            _imageView.Height = Doc.Height;

            ImageChanged?.Invoke(); // let MainWindow recompute brush size vs image resolution
        }

        /* ---------------- selection visuals ---------------- */

        private void EnsureRectBox()
        {
            if (_rectBox != null) return;
            _rectBox = new Rectangle
            {
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(30, 30, 144, 255)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _artboard.Children.Add(_rectBox);
        }

        private void EnsureLasso()
        {
            if (_lassoLine != null) return;
            _lassoLine = new Polyline
            {
                Stroke = Brushes.Orange,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(20, 255, 165, 0)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _artboard.Children.Add(_lassoLine);
        }

        private void EnsurePolygon()
        {
            if (_polyLine != null) return;
            _polyLine = new Polyline
            {
                Stroke = Brushes.MediumVioletRed,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(20, 199, 21, 133)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _artboard.Children.Add(_polyLine);
        }

        private static WRect NormalizeRect(WRect r)
        {
            if (r.Width < 0)  { r.X += r.Width;  r.Width  = -r.Width; }
            if (r.Height < 0) { r.Y += r.Height; r.Height = -r.Height; }
            return r;
        }

        private static WRect BoundsOf(IEnumerable<WPoint> pts)
        {
            var list = pts.ToList();
            if (list.Count == 0) return WRect.Empty;
            double minX = list.Min(p => p.X);
            double minY = list.Min(p => p.Y);
            double maxX = list.Max(p => p.X);
            double maxY = list.Max(p => p.Y);
            return new WRect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }

        private void ShowRect(WRect r)
        {
            EnsureRectBox();
            r = NormalizeRect(r);
            r = WRect.Intersect(r, new WRect(0, 0, _artboard.Width, _artboard.Height));
            if (r.IsEmpty || r.Width < 1 || r.Height < 1)
            {
                _rectBox!.Visibility = Visibility.Collapsed;
                _activeSelectionRect = null;
                return;
            }

            Canvas.SetLeft(_rectBox!, r.X);
            Canvas.SetTop(_rectBox!, r.Y);
            _rectBox!.Width = r.Width;
            _rectBox!.Height = r.Height;
            _rectBox!.Visibility = Visibility.Visible;

            _activeSelectionRect = r;
            _status.Content = $"Selection: {r.X:0},{r.Y:0}  {r.Width:0}×{r.Height:0}";
        }

        private void ShowLasso()
        {
            if (_lassoLine == null) return;
            if (_lassoPts.Count < 2)
            {
                _lassoLine.Visibility = Visibility.Collapsed;
                _activeSelectionRect = null;
                return;
            }

            _lassoLine.Points = new PointCollection(_lassoPts);
            _lassoLine.Visibility = Visibility.Visible;

            var r = BoundsOf(_lassoPts);
            _activeSelectionRect = r;
            _status.Content = $"Lasso: {r.X:0},{r.Y:0}  {r.Width:0}×{r.Height:0}";
        }

        private void ShowPolygon()
        {
            if (_polyLine == null) return;
            if (_polyPts.Count < 2)
            {
                _polyLine.Visibility = Visibility.Collapsed;
                _activeSelectionRect = null;
                return;
            }

            _polyLine.Points = new PointCollection(_polyPts);
            _polyLine.Visibility = Visibility.Visible;

            var r = BoundsOf(_polyPts);
            _activeSelectionRect = r;
            _status.Content = $"Polygon: {r.X:0},{r.Y:0}  {r.Width:0}×{r.Height:0}";
        }

        /* ---------------- selection lifecycle ---------------- */

        private void BeginSelectionMode(SelMode mode)
        {
            EndSelectionMode(); // clear old, if any
            _mode = mode;

            // Disable painting so Artboard gets mouse events
            _paint.EditingMode = InkCanvasEditingMode.None;
            _paint.IsHitTestVisible = false;
            _paint.Cursor = Cursors.Arrow;

            _artboard.Focusable = true;
            _artboard.Focus();
        }

        private void EndSelectionMode()
        {
            // clear visuals & bbox for selection modes
            if (_mode == SelMode.Rect || _mode == SelMode.Lasso || _mode == SelMode.Polygon)
            {
                _isDragging = false;
                _polyActive = false;

                _activeSelectionRect = null;

                if (_rectBox != null) _rectBox.Visibility = Visibility.Collapsed;

                _lassoPts.Clear();
                if (_lassoLine != null) _lassoLine.Visibility = Visibility.Collapsed;

                _polyPts.Clear();
                if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
            }

            // clear interactive crop UI if any
            if (_mode == SelMode.CropInteractive)
            {
                RemoveCropOverlay();
            }

            _mode = SelMode.None;
            _status.Content = "Ready";

            // Re-enable painting
            _paint.IsHitTestVisible = true;
            _paint.Cursor = Cursors.Pen;
        }

        /* ---------------- stroke baking (safe managed blend) ---------------- */

        /// <summary>
        /// Renders InkCanvas strokes into a bitmap and alpha-composites onto Doc.Image (non-premultiplied BGRA),
        /// then clears strokes. Pure managed code, no unsafe.
        /// </summary>
        private void BakeStrokesToImage()
        {
            if (_paint.Strokes == null || _paint.Strokes.Count == 0) return;

            int w = Doc.Width, h = Doc.Height;
            if (w < 1 || h < 1) { _paint.Strokes.Clear(); return; }

            // 1) Render strokes to a Pbgra32 RenderTargetBitmap
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var vb = new VisualBrush(_paint);
                dc.DrawRectangle(vb, null, new WRect(0, 0, w, h));
            }
            rtb.Render(dv);

            int srcStride = w * 4;
            var src = new byte[srcStride * h];
            rtb.CopyPixels(src, srcStride, 0); // Pbgra32 (premultiplied)

            // 2) Copy Doc.Image bytes to managed buffer (may have padding via Step)
            int dstStep = (int)Doc.Image.Step();
            var dst = new byte[dstStep * h];
            Marshal.Copy(Doc.Image.Data, dst, 0, dst.Length); // BGRA non-premultiplied

            // 3) Blend row-by-row (respecting dstStep vs srcStride)
            for (int y = 0; y < h; y++)
            {
                int srcRow = y * srcStride;
                int dstRow = y * dstStep;
                for (int x = 0; x < w; x++)
                {
                    int si = srcRow + x * 4;
                    int di = dstRow + x * 4;

                    byte sb = src[si + 0];
                    byte sg = src[si + 1];
                    byte sr = src[si + 2];
                    byte sa = src[si + 3];

                    if (sa == 0)
                        continue;

                    // src is premultiplied: channels already * a
                    double a = sa / 255.0;
                    dst[di + 0] = (byte)Math.Clamp(sb + (1.0 - a) * dst[di + 0], 0, 255); // B
                    dst[di + 1] = (byte)Math.Clamp(sg + (1.0 - a) * dst[di + 1], 0, 255); // G
                    dst[di + 2] = (byte)Math.Clamp(sr + (1.0 - a) * dst[di + 2], 0, 255); // R
                    dst[di + 3] = 255; // keep opaque
                }
            }

            // 4) Copy blended bytes back into Mat
            Marshal.Copy(dst, 0, Doc.Image.Data, dst.Length);

            // 5) Clear vector strokes after baking
            _paint.Strokes.Clear();
        }

        /* ---------------- interactive crop ---------------- */

        public void StartInteractiveCrop()
        {
            BeginSelectionMode(SelMode.CropInteractive);

            // Default crop rect: center ~70%
            double w = Doc.Width, h = Doc.Height;
            double cw = Math.Max(32, w * 0.7);
            double ch = Math.Max(32, h * 0.7);
            _cropRect = new WRect((w - cw) / 2, (h - ch) / 2, cw, ch);

            BuildCropOverlay();
            UpdateCropOverlay();
            _status.Content = "Crop: drag edges/corners to resize, drag inside to move. Enter = apply, Esc = cancel, double-click = apply.";
        }

        private void CommitInteractiveCrop()
        {
            BakeStrokesToImage();
            ApplyCropFromRect(_cropRect);
        }

        private void CancelInteractiveCrop()
        {
            EndSelectionMode();
            RefreshView();
        }

        private void ApplyCropFromRect(WRect r)
        {
            r = NormalizeRect(r);
            r = WRect.Intersect(r, new WRect(0, 0, Doc.Width, Doc.Height));
            if (r.IsEmpty || r.Width < 1 || r.Height < 1)
            {
                MessageBox.Show("Nothing to crop.");
                return;
            }

            Doc.Crop(
                (int)Math.Floor(r.X),
                (int)Math.Floor(r.Y),
                (int)Math.Round(r.Width),
                (int)Math.Round(r.Height)
            );

            EndSelectionMode();
            RefreshView();
        }

        private void BuildCropOverlay()
        {
            if (_cropMaskPath == null)
            {
                _cropMaskPath = new Path
                {
                    Fill = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(_cropMaskPath, 1000);
                _artboard.Children.Add(_cropMaskPath);
            }

            if (_cropRectVis == null)
            {
                _cropRectVis = new Rectangle
                {
                    Stroke = Brushes.DodgerBlue,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = Brushes.Transparent
                };
                Panel.SetZIndex(_cropRectVis, 1001);
                _artboard.Children.Add(_cropRectVis);
            }

            if (_cropHandles.Count == 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    var r = new Rectangle
                    {
                        Width = HandleSize,
                        Height = HandleSize,
                        Fill = Brushes.White,
                        Stroke = Brushes.Black,
                        StrokeThickness = 1,
                        Cursor = Cursors.SizeAll
                    };
                    Panel.SetZIndex(r, 1002);
                    _cropHandles.Add(r);
                    _artboard.Children.Add(r);
                }
            }
        }

        private void RemoveCropOverlay()
        {
            if (_cropMaskPath != null) { _artboard.Children.Remove(_cropMaskPath); _cropMaskPath = null; }
            if (_cropRectVis != null)  { _artboard.Children.Remove(_cropRectVis);  _cropRectVis = null;  }
            foreach (var h in _cropHandles) _artboard.Children.Remove(h);
            _cropHandles.Clear();
        }

        private void UpdateCropOverlay()
        {
            // Constrain rect to image bounds
            _cropRect = WRect.Intersect(_cropRect, new WRect(0, 0, Doc.Width, Doc.Height));
            if (_cropRect.IsEmpty)
                _cropRect = new WRect(0, 0, Math.Min(64, Doc.Width), Math.Min(64, Doc.Height));

            // Mask geometry: full image minus crop rect
            var outer = new RectangleGeometry(new WRect(0, 0, Doc.Width, Doc.Height));
            var inner = new RectangleGeometry(_cropRect);
            var cg = new GeometryGroup { FillRule = FillRule.EvenOdd };
            cg.Children.Add(outer);
            cg.Children.Add(inner);
            _cropMaskPath!.Data = cg;

            // Crop rect visual
            Canvas.SetLeft(_cropRectVis!, _cropRect.X);
            Canvas.SetTop(_cropRectVis!, _cropRect.Y);
            _cropRectVis!.Width = _cropRect.Width;
            _cropRectVis!.Height = _cropRect.Height;

            // Handles (N, S, E, W, NE, NW, SE, SW)
            var cx = _cropRect.X + _cropRect.Width / 2;
            var cy = _cropRect.Y + _cropRect.Height / 2;
            var left = _cropRect.X;
            var right = _cropRect.Right();
            var top = _cropRect.Y;
            var bottom = _cropRect.Bottom();

            var centers = new (double x, double y, Cursor cur, CropHit hit)[]
            {
                (cx, top,    Cursors.SizeNS,  CropHit.N),
                (cx, bottom, Cursors.SizeNS,  CropHit.S),
                (right, cy,  Cursors.SizeWE,  CropHit.E),
                (left,  cy,  Cursors.SizeWE,  CropHit.W),
                (right, top, Cursors.SizeNESW, CropHit.NE),
                (left,  top, Cursors.SizeNWSE, CropHit.NW),
                (right, bottom, Cursors.SizeNWSE, CropHit.SE),
                (left,  bottom, Cursors.SizeNESW, CropHit.SW),
            };

            for (int i = 0; i < _cropHandles.Count; i++)
            {
                var (hx, hy, cur, _) = centers[i];
                var r = _cropHandles[i];
                Canvas.SetLeft(r, hx - HandleSize / 2);
                Canvas.SetTop(r, hy - HandleSize / 2);
                r.Cursor = cur;
            }
        }

        /* ---------------- crop mouse/key handling ---------------- */

        private CropHit HitTestCrop(WPoint p)
        {
            var cx = _cropRect.X + _cropRect.Width / 2;
            var cy = _cropRect.Y + _cropRect.Height / 2;
            var left = _cropRect.X;
            var right = _cropRect.Right();
            var top = _cropRect.Y;
            var bottom = _cropRect.Bottom();

            var centers = new (double x, double y, CropHit hit)[]
            {
                (cx, top, CropHit.N),
                (cx, bottom, CropHit.S),
                (right, cy, CropHit.E),
                (left,  cy, CropHit.W),
                (right, top, CropHit.NE),
                (left,  top, CropHit.NW),
                (right, bottom, CropHit.SE),
                (left,  bottom, CropHit.SW),
            };

            foreach (var c in centers)
            {
                var hr = new WRect(c.x - HandleSize / 2, c.y - HandleSize / 2, HandleSize, HandleSize);
                if (hr.Contains(p)) return c.hit;
            }

            if (_cropRect.Contains(p)) return CropHit.Move;
            return CropHit.None;
        }

        private void UpdateCropCursor(WPoint p)
        {
            var hit = HitTestCrop(p);
            Cursor cur = Cursors.Arrow;
            switch (hit)
            {
                case CropHit.Move: cur = Cursors.SizeAll; break;
                case CropHit.N:
                case CropHit.S:    cur = Cursors.SizeNS; break;
                case CropHit.E:
                case CropHit.W:    cur = Cursors.SizeWE; break;
                case CropHit.NE:
                case CropHit.SW:   cur = Cursors.SizeNESW; break;
                case CropHit.NW:
                case CropHit.SE:   cur = Cursors.SizeNWSE; break;
            }
            _artboard.Cursor = cur;
        }

        private void DragCrop(WPoint pos)
        {
            var dx = pos.X - _cropDragStart.X;
            var dy = pos.Y - _cropDragStart.Y;

            WRect r = _cropRect;

            switch (_cropHit)
            {
                case CropHit.Move:
                    r.X += dx; r.Y += dy;
                    break;

                case CropHit.N:
                    r.Y += dy; r.Height -= dy;
                    break;
                case CropHit.S:
                    r.Height += dy;
                    break;
                case CropHit.W:
                    r.X += dx; r.Width -= dx;
                    break;
                case CropHit.E:
                    r.Width += dx;
                    break;

                case CropHit.NE:
                    r.Y += dy; r.Height -= dy; r.Width += dx;
                    break;
                case CropHit.NW:
                    r.Y += dy; r.Height -= dy; r.X += dx; r.Width -= dx;
                    break;
                case CropHit.SE:
                    r.Width += dx; r.Height += dy;
                    break;
                case CropHit.SW:
                    r.X += dx; r.Width -= dx; r.Height += dy;
                    break;
            }

            r = NormalizeRect(r);
            if (r.Width  < MinCropSize)  r.Width  = MinCropSize;
            if (r.Height < MinCropSize)  r.Height = MinCropSize;

            if (r.X < 0) r.X = 0;
            if (r.Y < 0) r.Y = 0;
            if (r.Right() > Doc.Width)   r.Width  = Doc.Width  - r.X;
            if (r.Bottom() > Doc.Height) r.Height = Doc.Height - r.Y;

            _cropRect = r;
            _cropDragStart = pos;
            UpdateCropOverlay();
        }

        private void HookSelectionEvents()
        {
            _artboard.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(_artboard);

                switch (_mode)
                {
                    case SelMode.Rect:
                        _isDragging = true;
                        _dragStart = pos;
                        _artboard.CaptureMouse();
                        e.Handled = true;
                        break;

                    case SelMode.Lasso:
                        _isDragging = true;
                        _lassoPts.Clear();
                        _lassoPts.Add(pos);
                        _lassoPts.Add(pos);
                        ShowLasso();
                        _artboard.CaptureMouse();
                        e.Handled = true;
                        break;

                    case SelMode.Polygon:
                        if (!_polyActive)
                        {
                            _polyPts.Clear();
                            _polyActive = true;
                        }
                        _polyPts.Add(pos);
                        ShowPolygon();
                        e.Handled = true;
                        break;

                    case SelMode.CropInteractive:
                        _cropHit = HitTestCrop(pos);
                        _cropDragStart = pos;
                        _artboard.CaptureMouse();
                        e.Handled = true;
                        break;
                }
            };

            _artboard.MouseMove += (s, e) =>
            {
                var pos = e.GetPosition(_artboard);
                switch (_mode)
                {
                    case SelMode.Rect when _isDragging:
                        ShowRect(new WRect(_dragStart, pos));
                        e.Handled = true; break;

                    case SelMode.Lasso when _isDragging:
                        var last = _lassoPts[^1];
                        if ((pos - last).Length > 1.0) _lassoPts.Add(pos);
                        ShowLasso();
                        e.Handled = true; break;

                    case SelMode.CropInteractive when _artboard.IsMouseCaptured:
                        DragCrop(pos);
                        e.Handled = true; break;

                    case SelMode.CropInteractive:
                        UpdateCropCursor(pos);
                        break;
                }
            };

            _artboard.MouseLeftButtonUp += (s, e) =>
            {
                switch (_mode)
                {
                    case SelMode.Rect when _isDragging:
                        _isDragging = false;
                        _artboard.ReleaseMouseCapture();
                        e.Handled = true;
                        break;

                    case SelMode.Lasso when _isDragging:
                        _isDragging = false;
                        _artboard.ReleaseMouseCapture();
                        if (_lassoPts.Count > 2) _lassoPts.Add(_lassoPts[0]); // close loop
                        ShowLasso();
                        e.Handled = true;
                        break;

                    case SelMode.CropInteractive:
                        _artboard.ReleaseMouseCapture();
                        _cropHit = CropHit.None;
                        e.Handled = true;
                        break;
                }
            };

            _artboard.MouseRightButtonUp += (s, e) =>
            {
                if (_mode == SelMode.Polygon && _polyActive)
                {
                    _polyActive = false;
                    if (_polyPts.Count > 2) _polyPts.Add(_polyPts[0]);
                    ShowPolygon();
                    e.Handled = true;
                }
            };

            _artboard.MouseDown += (s, e) =>
            {
                if (_mode == SelMode.Polygon && e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
                {
                    _polyActive = false;
                    if (_polyPts.Count > 2) _polyPts.Add(_polyPts[0]);
                    ShowPolygon();
                    e.Handled = true;
                }

                if (_mode == SelMode.CropInteractive && e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
                {
                    CommitInteractiveCrop();
                    e.Handled = true;
                }
            };
        }

        private void HookCropKeys()
        {
            _artboard.PreviewKeyDown += (s, e) =>
            {
                if (_mode != SelMode.CropInteractive) return;
                if (e.Key == Key.Enter)
                {
                    CommitInteractiveCrop();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    CancelInteractiveCrop();
                    e.Handled = true;
                }
            };
        }
    }

    /// <summary>
    /// Minimal code-only resize dialog so everything stays in this file.
    /// </summary>
    internal sealed class ResizeInlineWindow : WWindow
    {
        private readonly TextBox _wBox = new() { MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBox _hBox = new() { MinWidth = 80, Margin = new Thickness(0, 8, 8, 0) };
        private readonly CheckBox _lockAspect = new() { Content = "Lock aspect ratio", Margin = new Thickness(0, 12, 0, 0), IsChecked = true };
        private readonly int _origW, _origH;
        private bool _updating;

        public int ResultWidth { get; private set; }
        public int ResultHeight { get; private set; }

        public ResizeInlineWindow(int currentWidth, int currentHeight)
        {
            Title = "Resize Image";
            Width = 340; Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            _origW = currentWidth;
            _origH = currentHeight;

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var wLbl = new TextBlock { Text = "Width:",  VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var hLbl = new TextBlock { Text = "Height:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 8, 0) };
            var wUnit = new TextBlock { Text = "px", VerticalAlignment = VerticalAlignment.Center };
            var hUnit = new TextBlock { Text = "px", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };

            Grid.SetRow(wLbl, 0); Grid.SetColumn(wLbl, 0);
            Grid.SetRow(_wBox, 0); Grid.SetColumn(_wBox, 1);
            Grid.SetRow(wUnit, 0); Grid.SetColumn(wUnit, 2);

            Grid.SetRow(hLbl, 1); Grid.SetColumn(hLbl, 0);
            Grid.SetRow(_hBox, 1); Grid.SetColumn(_hBox, 1);
            Grid.SetRow(hUnit, 1); Grid.SetColumn(hUnit, 2);

            Grid.SetRow(_lockAspect, 2); Grid.SetColumnSpan(_lockAspect, 3);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            ok.Click += Ok_Click;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 3); Grid.SetColumnSpan(buttons, 3);

            grid.Children.Add(wLbl); grid.Children.Add(_wBox); grid.Children.Add(wUnit);
            grid.Children.Add(hLbl); grid.Children.Add(_hBox); grid.Children.Add(hUnit);
            grid.Children.Add(_lockAspect);
            grid.Children.Add(buttons);

            Content = grid;

            _wBox.Text = _origW.ToString(CultureInfo.InvariantCulture);
            _hBox.Text = _origH.ToString(CultureInfo.InvariantCulture);

            _wBox.TextChanged += (s, e) => SyncAspect(fromWidth: true);
            _hBox.TextChanged += (s, e) => SyncAspect(fromWidth: false);
        }

        private void SyncAspect(bool fromWidth)
        {
            if (_updating || _lockAspect.IsChecked != true) return;

            if (fromWidth && int.TryParse(_wBox.Text, out var w) && w > 0)
            {
                _updating = true;
                var h = (int)Math.Round((double)w * _origH / _origW);
                _hBox.Text = Math.Max(1, h).ToString();
                _updating = false;
            }
            else if (!fromWidth && int.TryParse(_hBox.Text, out var h2) && h2 > 0)
            {
                _updating = true;
                var w2 = (int)Math.Round((double)h2 * _origW / _origH);
                _wBox.Text = Math.Max(1, w2).ToString();
                _updating = false;
            }
        }

        private void Ok_Click(object? sender, RoutedEventArgs e)
        {
            if (!int.TryParse(_wBox.Text, out var w) || w < 1 ||
                !int.TryParse(_hBox.Text, out var h) || h < 1)
            {
                MessageBox.Show("Please enter positive integers for width and height.");
                return;
            }
            ResultWidth = w;
            ResultHeight = h;
            DialogResult = true;
        }
    }

    internal static class RectExt
    {
        public static double Right(this WRect r) => r.X + r.Width;
        public static double Bottom(this WRect r) => r.Y + r.Height;
    }
}

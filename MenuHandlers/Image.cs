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
using WWindow = System.Windows.Window;
using WPoint = System.Windows.Point;
using WRect = System.Windows.Rect;
using OcvSize = OpenCvSharp.Size;
using OcvRect = OpenCvSharp.Rect;
using WpfPoint = System.Windows.Point;
using OpenCvSharp;

namespace PhotoMax
{
    public partial class MainWindow
    {
        private void Select_Rect_Click(object sender, RoutedEventArgs e)
        {
            Shapes_Disarm();

            if (_img?.SelectionFill != null || (_img?.IsFloatingActive == true))
            {
                MessageBox.Show("Deselect current selection first (Ctrl+D or Enter).", "Selection Active",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _img?.StartRectSelection();
        }

        private void Select_Lasso_Click(object sender, RoutedEventArgs e)
        {
            Shapes_Disarm();

            if (_img?.SelectionFill != null || (_img?.IsFloatingActive == true))
            {
                MessageBox.Show("Deselect current selection first (Ctrl+D or Enter).", "Selection Active",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _img?.StartLassoSelection();
        }

        private void Select_Polygon_Click(object sender, RoutedEventArgs e)
        {
            Shapes_Disarm();

            if (_img?.SelectionFill != null || (_img?.IsFloatingActive == true))
            {
                MessageBox.Show("Deselect current selection first (Ctrl+D or Enter).", "Selection Active",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _img?.StartPolygonSelection();
        }

        private void Image_Crop_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;

            _img.CropCommand();
        }

        private void Image_Resize_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;

            _img.ResizeWithDialog(this);
        }

        private void Rotate_Right_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
            SaveUndoState("Rotate Right 90°");
            _img.RotateRight90();
        }

        private void Rotate_Left_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
            SaveUndoState("Rotate Left 90°");
            _img.RotateLeft90();
        }

        private void Flip_Vert_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
            SaveUndoState("Flip Vertical");
            _img.FlipVertical();
        }

        private void Flip_Horiz_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
            SaveUndoState("Flip Horizontal");
            _img.FlipHorizontal();
        }
    }

    public sealed class ImageDoc : IDisposable
    {
        public Mat Image { get; private set; }
        public int Width => Image?.Width ?? 0;
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

            var pixels = new byte[stride * h];
            Marshal.Copy(src.Data, pixels, 0, pixels.Length);

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();

            if (!ReferenceEquals(src, Image)) src.Dispose();
            return bmp;
        }
    }

    public sealed class ImageController
    {
        private readonly System.Windows.Controls.Image _imageView;
        private readonly Canvas _artboard;
        private readonly ContentControl _status;
        private readonly InkCanvas _paint;
        private Ellipse? _polyStartMarker;

        public ImageDoc Doc { get; } = new ImageDoc();

        private readonly LayerStack _layers = new LayerStack();

        public event Action? SelectionCreated;
        public int Layers_GetActiveIndex() => _layers.ActiveIndex;

        public OpenCvSharp.Mat Mat => _layers.ActiveMat;

        public Action<string>? SaveUndoStateCallback { get; set; }

        public System.Collections.Generic.List<string> Layers_AllNames => _layers.Layers.ConvertAll(l => l.Name);
        public string Layers_ActiveName => _layers.Active.Name;

        public void Layers_AddBlank()
        {
            EnsureLayers();
            _layers.AddBlank();
            RefreshView();
        }

        public void Layers_AddFromFile(string path)
        {
            EnsureLayers();
            using var src = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Unchanged);
            _layers.AddFromMat(src, System.IO.Path.GetFileName(path));
            RefreshView();
        }

        public void Layers_SetSingleFromMat(OpenCvSharp.Mat src)
        {
            if (src is null || src.Empty()) return;
            _layers.SetSingleFromMat(src, "Background");
            RefreshView();
        }

        public void Layers_Select(int idx)
        {
            if (_layers.Layers.Count == 0) return;
            _layers.Select(idx);
            RefreshView();
        }

        public void Layers_DeleteActive()
        {
            if (_layers.Layers.Count <= 1) return;
            _layers.DeleteActive();
            RefreshView();
        }

        public void Layers_RenameActive(string name)
        {
            if (_layers.Layers.Count == 0) return;
            _layers.RenameActive(name);
            RefreshView();
        }

        public void Layers_ToggleActiveVisibility()
        {
            if (_layers.Layers.Count == 0) return;
            _layers.ToggleActiveVisibility();
            RefreshView();
        }

        private void EnsureLayers()
        {
            if (_layers.Layers.Count == 0)
                _layers.SetSingleFromMat(Doc.Image.Clone(), "Background");
        }

        public event Action? ImageChanged;

        private enum SelMode
        {
            None,
            Rect,
            Lasso,
            Polygon,
            CropInteractive
        }

        private SelMode _mode = SelMode.None;

        private Geometry? _selectionClip;
        private Geometry? _selectionEdge;
        private bool _autoDeselectOnClickOutside = false;

        public Geometry? SelectionFill => _selectionClip;

        public bool HasActiveSelection => _selectionClip != null || _lassoPts.Count > 0 || _polyPts.Count > 0 ||
                                          _mode != SelMode.None;

        private byte[]? _selMask;
        private int _selMaskW, _selMaskH;
        private bool _selMaskDirty = true;

        public void RebuildSelectionMaskIfDirty()
        {
            if (!_selMaskDirty) return;

            if (_selectionClip == null)
            {
                _selMask = null;
                _selMaskW = _selMaskH = 0;
                _selMaskDirty = false;
                return;
            }

            int w = Doc.Width, h = Doc.Height;
            if (w < 1 || h < 1)
            {
                _selMask = null;
                _selMaskW = _selMaskH = 0;
                _selMaskDirty = false;
                return;
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawGeometry(Brushes.White, null, _selectionClip);
            }

            rtb.Render(dv);

            int stride = w * 4;
            var buf = new byte[h * stride];
            rtb.CopyPixels(buf, stride, 0);

            _selMask = new byte[w * h];
            int dst = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte a = buf[row + x * 4 + 3];
                    _selMask![dst++] = (a >= 128) ? (byte)255 : (byte)0;
                }
            }

            _selMaskW = w;
            _selMaskH = h;
            _selMaskDirty = false;
        }

        public bool FastMaskInside(int x, int y)
        {
            if (_selMask == null) return true;
            if ((uint)x >= (uint)_selMaskW || (uint)y >= (uint)_selMaskH) return false;
            return _selMask[y * _selMaskW + x] == 255;
        }

        private void MarkSelectionDirty() => _selMaskDirty = true;

        private Rectangle? _rectBox;
        private bool _isDragging;
        private WPoint _dragStart;

        private Polyline? _lassoLine;
        private readonly List<WPoint> _lassoPts = new();

        private Polyline? _polyLine;
        private readonly List<WPoint> _polyPts = new();
        private bool _polyActive;

        private WRect? _activeSelectionRect;

        private Path? _cropMaskPath;
        private Rectangle? _cropRectVis;
        private readonly List<Rectangle> _cropHandles = new();
        private WRect _cropRect;
        private CropHit _cropHit = CropHit.None;
        private WPoint _cropDragStart;
        private const double HandleSize = 8;
        private const double MinCropSize = 4;

        private enum CropHit
        {
            None,
            Move,
            N,
            S,
            E,
            W,
            NE,
            NW,
            SE,
            SW
        }

        private Mat? _floatingMat;
        private System.Windows.Controls.Image? _floatingView;
        private Rectangle? _floatingOutline;
        private WPoint _floatPos;
        private WPoint _floatStart;
        private bool _floatDragging;
        private bool _floatHovering;
        private Vector _floatGrabDelta;
        private bool _floatingActive;
        private bool _floatingFromMove;

        public bool IsFloatingActive => _floatingActive;

        public void ForceRefreshView()
        {
            if (_floatingActive) CommitFloatingPaste();
            RefreshView();
        }

        private void EnsurePolyStartMarker()
        {
            if (_polyStartMarker != null) return;
            _polyStartMarker = new Ellipse
            {
                Width = 20,
                Height = 20,
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0, 255, 0)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(_polyStartMarker, 999);
            _artboard.Children.Add(_polyStartMarker);
        }

        public ImageController(System.Windows.Controls.Image imageView, Canvas artboard, ContentControl status,
            InkCanvas paintCanvas)
        {
            _imageView = imageView;
            _artboard = artboard;
            _status = status;
            _paint = paintCanvas;

            HookSelectionEvents();
            HookSelectionKeys();
            HookCropKeys();
            HookFloatingPasteEvents();
            HookFloatingKeys();
            HookClipboardKeys();
            EnsureLayers();
            HookFloatingKeys();
            HookClipboardKeys();
            RefreshView();
        }

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
            _status.Content =
                "Polygon: click to add points, click near start to close, Enter/Right-click/Double-click to finish, Esc to cancel.";
        }

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
            _layers.RotateRight90();
            EndSelectionMode();
            RefreshView();
        }

        public void RotateLeft90()
        {
            BakeStrokesToImage();
            _layers.RotateLeft90();
            EndSelectionMode();
            RefreshView();
        }

        public void FlipVertical()
        {
            BakeStrokesToImage();
            _layers.FlipVertical();
            EndSelectionMode();
            RefreshView();
        }

        public void FlipHorizontal()
        {
            BakeStrokesToImage();
            _layers.FlipHorizontal();
            EndSelectionMode();
            RefreshView();
        }

        public void RestoreImageState(Mat snapshot)
        {
            if (snapshot == null || snapshot.Empty()) return;

            BakeStrokesToImage();

            var activeLayer = _layers.Active;
            if (activeLayer != null)
            {
                var oldMat = activeLayer.Mat;
                activeLayer.Mat = snapshot.Clone();
                oldMat?.Dispose();

                RefreshView();
            }
        }

        public void ResizeWithDialog(WWindow owner)
        {
            var dlg = new ResizeInlineWindow(Doc.Width, Doc.Height) { Owner = owner };
            if (dlg.ShowDialog() == true)
            {
                BakeStrokesToImage();

                SaveUndoStateCallback?.Invoke($"Resize to {dlg.ResultWidth}×{dlg.ResultHeight}");

                int oldW = Doc.Width, oldH = Doc.Height;
                int newW = dlg.ResultWidth, newH = dlg.ResultHeight;

                InterpolationFlags mode;
                if (newW < oldW || newH < oldH)
                    mode = InterpolationFlags.Area;
                else if (newW > oldW || newH > oldH)
                    mode = InterpolationFlags.Nearest;
                else
                    mode = InterpolationFlags.Linear;

                _layers.ResizeTo(newW, newH, mode);

                EndSelectionMode();
                RefreshView();
                _status.Content = $"Resized to {newW}×{newH} ({mode})";
            }
        }

        public void BeginMoveSelectedArea()
        {
            if (_selectionClip == null)
            {
                MessageBox.Show("Select an area first.");
                return;
            }

            BakeStrokesToImage();

            var b = _selectionClip.Bounds;
            int left = (int)Math.Floor(b.X);
            int top = (int)Math.Floor(b.Y);
            int right = (int)Math.Ceiling(b.X + b.Width);
            int bottom = (int)Math.Ceiling(b.Y + b.Height);

            left = Math.Clamp(left, 0, Math.Max(0, Mat.Width - 1));
            top = Math.Clamp(top, 0, Math.Max(0, Mat.Height - 1));
            right = Math.Clamp(right, 0, Mat.Width);
            bottom = Math.Clamp(bottom, 0, Mat.Height);

            int w = Math.Max(1, right - left);
            int h = Math.Max(1, bottom - top);

            var mask = BuildSelectionMaskBytes_Binary(left, top, w, h, 128);

            var clone = new Mat(h, w, MatType.CV_8UC4);
            int srcStep = (int)Mat.Step();
            int dstStep = (int)clone.Step();
            int mStride = w * 4;

            var srcData = new byte[srcStep * Mat.Height];
            Marshal.Copy(Mat.Data, srcData, 0, srcData.Length);

            var dstData = new byte[dstStep * h];

            for (int y = 0; y < h; y++)
            {
                int srcY = top + y;
                if (srcY < 0 || srcY >= Mat.Height) continue;

                int srcRow = srcY * srcStep;
                int dstRow = y * dstStep;
                int maskRow = y * mStride;

                for (int x = 0; x < w; x++)
                {
                    int srcX = left + x;
                    if (srcX < 0 || srcX >= Mat.Width) continue;

                    int si = srcRow + srcX * 4;
                    int di = dstRow + x * 4;
                    int mi = maskRow + x * 4;

                    byte maskAlpha = mask[mi + 3];

                    if (maskAlpha == 255)
                    {
                        dstData[di + 0] = srcData[si + 0];
                        dstData[di + 1] = srcData[si + 1];
                        dstData[di + 2] = srcData[si + 2];
                        dstData[di + 3] = srcData[si + 3];
                    }
                    else
                    {
                        dstData[di + 0] = 255;
                        dstData[di + 1] = 255;
                        dstData[di + 2] = 255;
                        dstData[di + 3] = 0;
                    }
                }
            }

            Marshal.Copy(dstData, 0, clone.Data, dstData.Length);

            ClearMatRegionWithMaskToTransparent_Binary(Mat, left, top, w, h, mask);

            _floatingFromMove = true;

            BeginFloatingPaste(clone, left, top);

            clone.Dispose();

            RefreshView();

            _paint.Cursor = Cursors.SizeAll;
            _paint.EditingMode = InkCanvasEditingMode.None;
            _paint.IsHitTestVisible = false;

            _status.Content = "Move: drag to reposition, Enter to place (selection stays), Esc to cancel.";
        }

        private void ClearMatRegionWithMaskToTransparent_Binary(Mat targetMat, int x, int y, int w, int h, byte[] mask)
        {
            if (targetMat == null || targetMat.Empty()) return;

            int step = (int)targetMat.Step();
            var dst = new byte[step * targetMat.Height];
            Marshal.Copy(targetMat.Data, dst, 0, dst.Length);

            int mStride = w * 4;

            bool isBackgroundLayer = false;
            if (_layers?.Layers.Count > 0)
            {
                isBackgroundLayer = (_layers.Layers[0].Mat == targetMat);
            }

            for (int yy = 0; yy < h; yy++)
            {
                int iy = y + yy;
                if (iy < 0 || iy >= targetMat.Height) continue;

                int rowD = iy * step;
                int rowM = yy * mStride;

                for (int xx = 0; xx < w; xx++)
                {
                    int ix = x + xx;
                    if (ix < 0 || ix >= targetMat.Width) continue;

                    int di = rowD + ix * 4;
                    int mi = rowM + xx * 4;
                    byte a = mask[mi + 3];

                    if (a == 0) continue;

                    if (isBackgroundLayer)
                    {
                        dst[di + 0] = 255;
                        dst[di + 1] = 255;
                        dst[di + 2] = 255;
                        dst[di + 3] = 255;
                    }
                    else
                    {
                        dst[di + 0] = 0;
                        dst[di + 1] = 0;
                        dst[di + 2] = 0;
                        dst[di + 3] = 0;
                    }
                }
            }

            Marshal.Copy(dst, 0, targetMat.Data, dst.Length);
        }

        private void FinalizeSelection()
        {
            if (_floatingActive) CommitFloatingPaste();

            _selectionClip = null;
            _selectionEdge = null;
            _paint.Clip = null;
            MarkSelectionDirty();

            if (_rectBox != null) _rectBox.Visibility = Visibility.Collapsed;
            if (_lassoLine != null) _lassoLine.Visibility = Visibility.Collapsed;
            if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
            if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;

            _lassoPts.Clear();
            _polyPts.Clear();
            _activeSelectionRect = null;

            _status.Content = "Selection finalized and cleared.";
        }

        private void RefreshView()
        {
            EnsureLayers();
            Doc.ReplaceWith(_layers.Composite());
            _imageView.Source = Doc.ToBitmapSource();

            RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetBitmapScalingMode(_paint, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(_paint, EdgeMode.Aliased);
            _paint.SnapsToDevicePixels = true;

            _artboard.Width = Doc.Width;
            _artboard.Height = Doc.Height;

            _imageView.Width = Doc.Width;
            _imageView.Height = Doc.Height;

            ImageChanged?.Invoke();
        }

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
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(20, 255, 165, 0)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _artboard.Children.Add(_lassoLine);
        }

        private void SetSelectionVisualThickness(double thickness)
        {
            if (_lassoLine != null) _lassoLine.StrokeThickness = thickness;
            if (_polyLine != null) _polyLine.StrokeThickness = thickness;
        }

        private void EnsurePolygon()
        {
            if (_polyLine != null) return;
            _polyLine = new Polyline
            {
                Stroke = Brushes.MediumVioletRed,
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(20, 199, 21, 133)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _artboard.Children.Add(_polyLine);
        }

        private static WRect NormalizeRect(WRect r)
        {
            if (r.Width < 0)
            {
                r.X += r.Width;
                r.Width = -r.Width;
            }

            if (r.Height < 0)
            {
                r.Y += r.Height;
                r.Height = -r.Height;
            }

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

        private void ShowSelectionVisuals()
        {
            if (_selectionClip == null) return;

            if (_lassoPts.Count > 0 && _lassoLine != null)
            {
                ShowLasso();
            }
            else if (_polyPts.Count > 0 && _polyLine != null)
            {
                ShowPolygon();
            }
            else if (_activeSelectionRect is WRect rr && rr.Width >= 1 && rr.Height >= 1)
            {
                if (_mode == SelMode.Rect || (_lassoPts.Count == 0 && _polyPts.Count == 0))
                {
                    ShowRect(rr);
                }
            }
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

            _lassoLine.StrokeThickness = 3.0;

            HideRectBox();

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
                if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;
                return;
            }

            _polyLine.Points = new PointCollection(_polyPts);
            _polyLine.Visibility = Visibility.Visible;

            _polyLine.StrokeThickness = 3.0;

            if (_polyPts.Count >= 3 && _polyActive)
            {
                EnsurePolyStartMarker();
                var start = _polyPts[0];
                Canvas.SetLeft(_polyStartMarker!, start.X - 10);
                Canvas.SetTop(_polyStartMarker!, start.Y - 10);
                _polyStartMarker!.Visibility = Visibility.Visible;
            }
            else if (_polyStartMarker != null)
            {
                _polyStartMarker.Visibility = Visibility.Collapsed;
            }

            HideRectBox();

            var r = BoundsOf(_polyPts);
            _activeSelectionRect = r;
            _status.Content = $"Polygon: {r.X:0},{r.Y:0}  {r.Width:0}×{r.Height:0} ({_polyPts.Count} points)";
        }

        private void BeginSelectionMode(SelMode mode)
        {
            EndSelectionMode();
            _mode = mode;

            _paint.EditingMode = InkCanvasEditingMode.None;
            _paint.IsHitTestVisible = false;
            _paint.Cursor = Cursors.Arrow;

            _artboard.Focusable = true;
            _artboard.Focus();
        }

        private void EndSelectionMode()
        {
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

            if (_mode == SelMode.CropInteractive)
            {
                RemoveCropOverlay();
            }

            DeselectSelection();

            _mode = SelMode.None;
            _status.Content = "Ready";

            _paint.IsHitTestVisible = true;
            _paint.EditingMode = InkCanvasEditingMode.Ink;
            _paint.Cursor = Cursors.Pen;
        }

        private void ActivateSelectionPainting(Geometry clipGeom)
        {
            _selectionClip = clipGeom;
            _selectionClip.Freeze();

            var pen = new Pen(Brushes.Black, 1.0);
            _selectionEdge = _selectionClip.GetWidenedPathGeometry(pen);
            _selectionEdge.Freeze();

            _paint.Clip = _selectionClip;

            MarkSelectionDirty();

            _mode = SelMode.None;
            _paint.IsHitTestVisible = true;
            _paint.EditingMode = InkCanvasEditingMode.Ink;
            _paint.Cursor = Cursors.Pen;

            _autoDeselectOnClickOutside = false;

            SelectionCreated?.Invoke();

            _status.Content = "Selection created — use Move tool to reposition, or paint inside (Enter to finalize).";
        }

        private void DeselectSelection()
        {
            _selectionClip = null;
            _selectionEdge = null;
            _paint.Clip = null;

            MarkSelectionDirty();

            if (_rectBox != null) _rectBox.Visibility = Visibility.Collapsed;
            if (_lassoLine != null) _lassoLine.Visibility = Visibility.Collapsed;
            if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
            if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;

            _lassoPts.Clear();
            _polyPts.Clear();
            _activeSelectionRect = null;
        }

        private void BakeStrokesToImage()
        {
            if (_paint.Strokes == null || _paint.Strokes.Count == 0) return;

            int w = Doc.Width, h = Doc.Height;
            if (w < 1 || h < 1)
            {
                _paint.Strokes.Clear();
                return;
            }

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
            rtb.CopyPixels(src, srcStride, 0);

            int dstStep = (int)Mat.Step();
            var dst = new byte[dstStep * h];
            Marshal.Copy(Doc.Image.Data, dst, 0, dst.Length);

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

                    double a = sa / 255.0;
                    dst[di + 0] = (byte)Math.Clamp(sb + (1.0 - a) * dst[di + 0], 0, 255);
                    dst[di + 1] = (byte)Math.Clamp(sg + (1.0 - a) * dst[di + 1], 0, 255);
                    dst[di + 2] = (byte)Math.Clamp(sr + (1.0 - a) * dst[di + 2], 0, 255);
                    dst[di + 3] = 255;
                }
            }

            Marshal.Copy(dst, 0, Doc.Image.Data, dst.Length);

            _paint.Strokes.Clear();
        }

        public void CopySelectionToClipboard()
        {
            if (_floatingActive) CommitFloatingPaste();
            BakeStrokesToImage();

            if (_selectionClip == null)
            {
                using var entireImage = Mat.Clone();
                CopyMatToClipboardAsPNG(entireImage);
                _status.Content = $"Copied entire image {Mat.Width}×{Mat.Height}";
                return;
            }

            var b = _selectionClip.Bounds;
            int left = (int)Math.Floor(b.X);
            int top = (int)Math.Floor(b.Y);
            int right = (int)Math.Ceiling(b.X + b.Width);
            int bottom = (int)Math.Ceiling(b.Y + b.Height);

            left = Math.Clamp(left, 0, Math.Max(0, Mat.Width - 1));
            top = Math.Clamp(top, 0, Math.Max(0, Mat.Height - 1));
            right = Math.Clamp(right, 0, Mat.Width);
            bottom = Math.Clamp(bottom, 0, Mat.Height);

            int w = Math.Max(1, right - left);
            int h = Math.Max(1, bottom - top);

            var mask = BuildSelectionMaskBytes_Binary(left, top, w, h, 128);

            var extractedMat = new Mat(h, w, MatType.CV_8UC4);
            int srcStep = (int)Mat.Step();
            int dstStep = (int)extractedMat.Step();
            int mStride = w * 4;

            var srcData = new byte[srcStep * Mat.Height];
            Marshal.Copy(Mat.Data, srcData, 0, srcData.Length);

            var dstData = new byte[dstStep * h];

            for (int y = 0; y < h; y++)
            {
                int srcY = top + y;
                if (srcY < 0 || srcY >= Mat.Height) continue;

                int srcRow = srcY * srcStep;
                int dstRow = y * dstStep;
                int maskRow = y * mStride;

                for (int x = 0; x < w; x++)
                {
                    int srcX = left + x;
                    if (srcX < 0 || srcX >= Mat.Width) continue;

                    int si = srcRow + srcX * 4;
                    int di = dstRow + x * 4;
                    int mi = maskRow + x * 4;

                    byte maskAlpha = mask[mi + 3];

                    if (maskAlpha == 255)
                    {
                        dstData[di + 0] = srcData[si + 0];
                        dstData[di + 1] = srcData[si + 1];
                        dstData[di + 2] = srcData[si + 2];
                        dstData[di + 3] = srcData[si + 3];
                    }
                    else
                    {
                        dstData[di + 0] = 255;
                        dstData[di + 1] = 255;
                        dstData[di + 2] = 255;
                        dstData[di + 3] = 0;
                    }
                }
            }

            Marshal.Copy(dstData, 0, extractedMat.Data, dstData.Length);
            ConvertTransparentBlackToWhite(extractedMat);

            CopyMatToClipboardAsPNG(extractedMat);
            extractedMat.Dispose();

            _status.Content = $"Copied {w}×{h}";
        }

        public void CutSelectionToClipboard()
        {
            if (_floatingActive) CommitFloatingPaste();
            BakeStrokesToImage();

            if (_selectionClip == null)
            {
                using var entireImage = Mat.Clone();
                CopyMatToClipboardAsPNG(entireImage);
                Mat.SetTo(new Scalar(255, 255, 255, 255));
                RefreshView();
                _status.Content = $"Cut entire image {Mat.Width}×{Mat.Height}";
                return;
            }

            var b = _selectionClip.Bounds;
            int left = (int)Math.Floor(b.X);
            int top = (int)Math.Floor(b.Y);
            int right = (int)Math.Ceiling(b.X + b.Width);
            int bottom = (int)Math.Ceiling(b.Y + b.Height);

            left = Math.Clamp(left, 0, Math.Max(0, Mat.Width - 1));
            top = Math.Clamp(top, 0, Math.Max(0, Mat.Height - 1));
            right = Math.Clamp(right, 0, Mat.Width);
            bottom = Math.Clamp(bottom, 0, Mat.Height);

            int w = Math.Max(1, right - left);
            int h = Math.Max(1, bottom - top);

            var mask = BuildSelectionMaskBytes_Binary(left, top, w, h, 128);

            var extractedMat = new Mat(h, w, MatType.CV_8UC4);
            int srcStep = (int)Mat.Step();
            int dstStep = (int)extractedMat.Step();
            int mStride = w * 4;

            var srcData = new byte[srcStep * Mat.Height];
            Marshal.Copy(Mat.Data, srcData, 0, srcData.Length);

            var dstData = new byte[dstStep * h];

            for (int y = 0; y < h; y++)
            {
                int srcY = top + y;
                if (srcY < 0 || srcY >= Mat.Height) continue;

                int srcRow = srcY * srcStep;
                int dstRow = y * dstStep;
                int maskRow = y * mStride;

                for (int x = 0; x < w; x++)
                {
                    int srcX = left + x;
                    if (srcX < 0 || srcX >= Mat.Width) continue;

                    int si = srcRow + srcX * 4;
                    int di = dstRow + x * 4;
                    int mi = maskRow + x * 4;

                    byte maskAlpha = mask[mi + 3];

                    if (maskAlpha == 255)
                    {
                        dstData[di + 0] = srcData[si + 0];
                        dstData[di + 1] = srcData[si + 1];
                        dstData[di + 2] = srcData[si + 2];
                        dstData[di + 3] = srcData[si + 3];
                    }
                    else
                    {
                        dstData[di + 0] = 255;
                        dstData[di + 1] = 255;
                        dstData[di + 2] = 255;
                        dstData[di + 3] = 0;
                    }
                }
            }

            Marshal.Copy(dstData, 0, extractedMat.Data, dstData.Length);
            ConvertTransparentBlackToWhite(extractedMat);

            CopyMatToClipboardAsPNG(extractedMat);
            extractedMat.Dispose();

            ClearMatRegionWithMaskToTransparent_Binary(Mat, left, top, w, h, mask);
            RefreshView();
            _status.Content = $"Cut {w}×{h}";
        }

        private void CopyMatToClipboardAsPNG(Mat bgra)
        {
            var bmp = MatToBitmapSourceBGRA(bgra);

            var encoder = new PngBitmapEncoder();

            var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);

            var rawBitmap = BitmapSource.Create(
                bmp.PixelWidth,
                bmp.PixelHeight,
                96, 96,
                PixelFormats.Bgra32,
                null,
                pixels,
                bmp.PixelWidth * 4);
            rawBitmap.Freeze();

            encoder.Frames.Add(BitmapFrame.Create(rawBitmap));

            using (var ms = new System.IO.MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;

                var dataObject = new DataObject();

                var pngBytes = ms.ToArray();
                dataObject.SetData("PNG", new System.IO.MemoryStream(pngBytes), false);

                dataObject.SetImage(rawBitmap);

                Clipboard.SetDataObject(dataObject, true);
            }
        }

        private void HideRectBox()
        {
            if (_rectBox != null)
            {
                _rectBox.Visibility = Visibility.Collapsed;
            }
        }

        public void PasteFromClipboard()
        {
            BakeStrokesToImage();

            Mat? src = null;

            if (Clipboard.ContainsData("PNG"))
            {
                try
                {
                    var pngData = Clipboard.GetData("PNG");
                    System.IO.MemoryStream? pngStream = null;

                    if (pngData is System.IO.MemoryStream ms)
                        pngStream = ms;
                    else if (pngData is byte[] bytes)
                        pngStream = new System.IO.MemoryStream(bytes);

                    if (pngStream != null)
                    {
                        pngStream.Position = 0;
                        var buffer = new byte[pngStream.Length];
                        pngStream.Read(buffer, 0, buffer.Length);

                        src = Cv2.ImDecode(buffer, ImreadModes.Unchanged);

                        if (src != null && !src.Empty())
                        {
                            if (src.Type() == MatType.CV_8UC3)
                            {
                                var bgra = new Mat();
                                Cv2.CvtColor(src, bgra, ColorConversionCodes.BGR2BGRA);
                                src.Dispose();
                                src = bgra;
                            }
                            else if (src.Type() != MatType.CV_8UC4)
                            {
                                var bgra = new Mat();
                                Cv2.CvtColor(src, bgra, ColorConversionCodes.GRAY2BGRA);
                                src.Dispose();
                                src = bgra;
                            }

                            ConvertTransparentBlackToWhite(src);

                            _status.Content = "Pasted from PNG (colors preserved)";
                        }
                    }
                }
                catch
                {
                    src?.Dispose();
                    src = null;
                }
            }

            if (src == null)
            {
                if (!Clipboard.ContainsImage())
                {
                    MessageBox.Show("Clipboard does not contain an image.");
                    return;
                }

                var cb = Clipboard.GetImage();
                if (cb == null)
                {
                    MessageBox.Show("Failed to read image from clipboard.");
                    return;
                }

                src = BitmapSourceToMatBGRA(cb);

                ConvertTransparentBlackToWhite(src);

                _status.Content = "Pasted from clipboard";
            }

            using (src)
            {
                int px, py;
                if (_activeSelectionRect is WRect sel && !sel.IsEmpty)
                {
                    px = (int)Math.Floor(sel.X);
                    py = (int)Math.Floor(sel.Y);
                }
                else
                {
                    px = Math.Max(0, (Doc.Width - src.Width) / 2);
                    py = Math.Max(0, (Doc.Height - src.Height) / 2);
                }

                BeginFloatingPaste(src, px, py);
            }
        }

        private void BeginFloatingPaste(Mat sourceBGRA, int x, int y)
        {
            if (_floatingActive) CommitFloatingPaste();

            _floatingMat = sourceBGRA.Clone();

            ConvertTransparentBlackToWhite(_floatingMat);

            _floatPos = new WpfPoint(x, y);
            _floatStart = _floatPos;
            ClampFloatingToBounds();

            var bmp = MatToBitmapSourceBGRA(_floatingMat);
            _floatingView = new System.Windows.Controls.Image
            {
                Source = bmp,
                Width = _floatingMat.Width,
                Height = _floatingMat.Height,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(_floatingView, BitmapScalingMode.NearestNeighbor);
            Panel.SetZIndex(_floatingView, 2000);
            _artboard.Children.Add(_floatingView);

            _floatingOutline = new Rectangle
            {
                Width = _floatingMat.Width,
                Height = _floatingMat.Height,
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 6, 3 },
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Visible
            };
            RenderOptions.SetEdgeMode(_floatingOutline, EdgeMode.Aliased);
            Panel.SetZIndex(_floatingOutline, 2001);
            _artboard.Children.Add(_floatingOutline);

            UpdateFloatingView();
            UpdateFloatingOutlineVisibility(forceVisible: true);

            _paint.EditingMode = InkCanvasEditingMode.None;
            _paint.IsHitTestVisible = false;
            _paint.Cursor = Cursors.Arrow;

            _floatingActive = true;
            _artboard.Focusable = true;
            _artboard.Focus();
        }

        public void CommitFloatingPaste()
        {
            if (!_floatingActive || _floatingMat is null) return;

            AlphaBlendOverToMat(Mat, _floatingMat, (int)Math.Round(_floatPos.X), (int)Math.Round(_floatPos.Y));

            if (_floatingFromMove && _selectionClip != null)
            {
                var dx = _floatPos.X - _floatStart.X;
                var dy = _floatPos.Y - _floatStart.Y;
                TranslateSelectionGeometry(dx, dy);

                ShowSelectionVisuals();

                _paint.Clip = _selectionClip;
                _paint.IsHitTestVisible = true;
                _paint.EditingMode = InkCanvasEditingMode.Ink;
                _paint.Cursor = Cursors.Pen;

                _floatingFromMove = false;
                _status.Content =
                    "Moved selection placed (still selected) — click Move tool to move again, or press Enter to finalize.";
            }
            else
            {
                _status.Content = "Pasted.";
            }

            RemoveFloatingView();
            RefreshView();
        }

        private void CancelFloatingPaste()
        {
            if (!_floatingActive) return;

            if (_floatingFromMove && _floatingMat != null)
            {
                AlphaBlendOverToMat(Mat, _floatingMat, (int)Math.Round(_floatStart.X), (int)Math.Round(_floatStart.Y));

                ShowSelectionVisuals();

                _status.Content = "Move cancelled - selection restored.";
            }
            else
            {
                _status.Content = "Paste cancelled.";
            }

            RemoveFloatingView();
            RefreshView();
        }

        private void RemoveFloatingView()
        {
            if (_floatingView != null) _artboard.Children.Remove(_floatingView);
            if (_floatingOutline != null) _artboard.Children.Remove(_floatingOutline);

            _floatingView = null;
            _floatingOutline = null;

            _floatingMat?.Dispose();
            _floatingMat = null;
            _floatingActive = false;
            _floatDragging = false;
            _floatHovering = false;

            _paint.IsHitTestVisible = true;
            _paint.EditingMode = InkCanvasEditingMode.Ink;
            _paint.Cursor = Cursors.Pen;
        }

        private void UpdateFloatingView()
        {
            if (_floatingView == null) return;
            Canvas.SetLeft(_floatingView, _floatPos.X);
            Canvas.SetTop(_floatingView, _floatPos.Y);

            if (_floatingOutline != null)
            {
                _floatingOutline.Width = _floatingView.Width;
                _floatingOutline.Height = _floatingView.Height;
                Canvas.SetLeft(_floatingOutline, _floatPos.X);
                Canvas.SetTop(_floatingOutline, _floatPos.Y);
            }

            if (_floatingFromMove && _selectionClip != null)
            {
                var dx = _floatPos.X - _floatStart.X;
                var dy = _floatPos.Y - _floatStart.Y;
                UpdateSelectionVisualsForMove(dx, dy);
            }
        }

        private void UpdateSelectionVisualsForMove(double dx, double dy)
        {
            if (_lassoPts.Count > 0 && _lassoLine != null)
            {
                var movedPoints = new PointCollection();
                foreach (var pt in _lassoPts)
                {
                    movedPoints.Add(new WPoint(pt.X + dx, pt.Y + dy));
                }

                _lassoLine.Points = movedPoints;
                _lassoLine.Visibility = Visibility.Visible;
            }

            if (_polyPts.Count > 0 && _polyLine != null)
            {
                var movedPoints = new PointCollection();
                foreach (var pt in _polyPts)
                {
                    movedPoints.Add(new WPoint(pt.X + dx, pt.Y + dy));
                }

                _polyLine.Points = movedPoints;
                _polyLine.Visibility = Visibility.Visible;
            }

            HideRectBox();
        }

        private void ClampFloatingToBounds()
        {
            if (_floatingMat is null) return;
            double maxX = Math.Max(0, Doc.Width - _floatingMat.Width);
            double maxY = Math.Max(0, Doc.Height - _floatingMat.Height);
            _floatPos = new WPoint(Math.Clamp(_floatPos.X, 0, maxX), Math.Clamp(_floatPos.Y, 0, maxY));
        }

        private bool HitTestFloating(WPoint p)
        {
            if (!_floatingActive || _floatingMat is null) return false;
            var r = new WRect(_floatPos.X, _floatPos.Y, _floatingMat.Width, _floatingMat.Height);
            return r.Contains(p);
        }

        private void UpdateFloatingCursor(WPoint p)
        {
            bool hit = HitTestFloating(p);
            _floatHovering = hit;
            _artboard.Cursor = hit ? Cursors.SizeAll : Cursors.Arrow;

            if (_floatingOutline != null)
            {
                UpdateFloatingOutlineVisibility(forceVisible: _floatDragging || _floatHovering);
            }
        }

        private void UpdateFloatingOutlineVisibility(bool forceVisible)
        {
            if (_floatingOutline == null) return;

            if (!_floatingActive)
            {
                _floatingOutline.Visibility = Visibility.Collapsed;
                return;
            }

            _floatingOutline.Visibility = Visibility.Visible;

            if (_floatDragging)
            {
                _floatingOutline.StrokeThickness = 4.0;
                _floatingOutline.StrokeDashArray = null;
                _floatingOutline.Stroke = Brushes.Cyan;
            }
            else if (_floatHovering || forceVisible)
            {
                _floatingOutline.StrokeThickness = 3.5;
                _floatingOutline.StrokeDashArray = new DoubleCollection { 4, 2 };
                _floatingOutline.Stroke = Brushes.Yellow;
            }
            else
            {
                _floatingOutline.StrokeThickness = 2.5;
                _floatingOutline.StrokeDashArray = new DoubleCollection { 6, 3 };
                _floatingOutline.Stroke = Brushes.DodgerBlue;
            }
        }

        private void HookFloatingPasteEvents()
        {
            _artboard.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!_floatingActive) return;
                var pos = e.GetPosition(_artboard);

                if (HitTestFloating(pos))
                {
                    _floatDragging = true;
                    _floatHovering = true;
                    _floatGrabDelta = (Vector)(pos - _floatPos);
                    _artboard.CaptureMouse();

                    if (_floatingFromMove)
                    {
                        SetSelectionVisualThickness(3.5);
                    }

                    UpdateFloatingOutlineVisibility(forceVisible: true);
                    e.Handled = true;
                }
                else
                {
                    CommitFloatingPaste();
                    e.Handled = true;
                }
            };

            _artboard.PreviewMouseMove += (s, e) =>
            {
                if (!_floatingActive) return;
                var pos = e.GetPosition(_artboard);

                if (_floatDragging)
                {
                    _floatPos = pos - _floatGrabDelta;
                    ClampFloatingToBounds();
                    UpdateFloatingView();
                    UpdateFloatingOutlineVisibility(forceVisible: true);
                    e.Handled = true;
                }
                else
                {
                    bool wasHovering = _floatHovering;
                    UpdateFloatingCursor(pos);

                    if (_floatHovering != wasHovering)
                    {
                        UpdateFloatingOutlineVisibility(forceVisible: _floatHovering);
                    }
                }
            };

            _artboard.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!_floatingActive) return;
                if (_floatDragging)
                {
                    _floatDragging = false;
                    _artboard.ReleaseMouseCapture();

                    if (_floatingFromMove)
                    {
                        SetSelectionVisualThickness(2.5);
                    }

                    UpdateFloatingOutlineVisibility(forceVisible: _floatHovering);
                    e.Handled = true;
                }
            };

            _artboard.MouseLeave += (s, e) =>
            {
                if (!_floatingActive) return;
                _floatHovering = false;

                if (!_floatDragging)
                {
                    UpdateFloatingOutlineVisibility(forceVisible: false);
                }
            };
        }

        private void HookFloatingKeys()
        {
            _artboard.PreviewKeyDown += (s, e) =>
            {
                if (_floatingActive)
                {
                    if (e.Key == Key.Enter)
                    {
                        CommitFloatingPaste();
                        DeselectSelection();
                        _status.Content = "Selection finalized.";
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Escape)
                    {
                        CancelFloatingPaste();
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
                    {
                        int dx = (e.Key == Key.Left) ? -1 : (e.Key == Key.Right) ? 1 : 0;
                        int dy = (e.Key == Key.Up) ? -1 : (e.Key == Key.Down) ? 1 : 0;
                        _floatPos = new WPoint(_floatPos.X + dx, _floatPos.Y + dy);
                        ClampFloatingToBounds();
                        UpdateFloatingView();
                        UpdateFloatingOutlineVisibility(forceVisible: true);
                        e.Handled = true;
                        return;
                    }
                }
            };
        }

        private void HookClipboardKeys()
        {
            KeyEventHandler handler = (s, e) =>
            {
                if (e.Handled) return;

                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                if (!ctrl) return;

                switch (e.Key)
                {
                    case Key.C:

                        CopySelectionToClipboard();
                        e.Handled = true;
                        break;

                    case Key.X:

                        CutSelectionToClipboard();
                        e.Handled = true;
                        break;

                    case Key.V:
                        if (_floatingActive) CommitFloatingPaste();
                        PasteFromClipboard();
                        e.Handled = true;
                        break;
                }
            };

            _artboard.PreviewKeyDown += handler;
            _paint.PreviewKeyDown += handler;
            _imageView.PreviewKeyDown += handler;

            if (Application.Current?.MainWindow is WWindow win)
                win.PreviewKeyDown += handler;
        }

        private void HookSelectionKeys()
        {
            KeyEventHandler handler = (s, e) =>
            {
                if (e.Handled) return;
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                if (ctrl && e.Key == Key.D)
                {
                    DeselectSelection();
                    _status.Content = "Deselected.";
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter)
                {
                    if (_mode == SelMode.Polygon && _polyActive && _polyPts.Count >= 3)
                    {
                        FinishPolygonSelection();
                        e.Handled = true;
                        return;
                    }

                    if (_selectionClip != null || _floatingActive)
                    {
                        FinalizeSelection();
                        e.Handled = true;
                        return;
                    }
                }

                if (e.Key == Key.Escape && _mode == SelMode.Polygon && _polyActive)
                {
                    _polyActive = false;
                    _polyPts.Clear();
                    if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
                    if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;
                    EndSelectionMode();
                    _status.Content = "Polygon cancelled.";
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter && _selectionClip != null)
                {
                    FinalizeSelection();
                    e.Handled = true;
                    return;
                }
            };

            _artboard.PreviewKeyDown += handler;
            _paint.PreviewKeyDown += handler;
            _imageView.PreviewKeyDown += handler;
            if (Application.Current?.MainWindow is WWindow win)
                win.PreviewKeyDown += handler;

            _artboard.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (_floatingActive) return;
                if (_mode != SelMode.None) return;
                if (_selectionClip == null) return;
            };
        }

        public void StartInteractiveCrop()
        {
            BeginSelectionMode(SelMode.CropInteractive);

            double w = Doc.Width, h = Doc.Height;
            double cw = Math.Max(32, w * 0.7);
            double ch = Math.Max(32, h * 0.7);
            _cropRect = new WRect((w - cw) / 2, (h - ch) / 2, cw, ch);

            BuildCropOverlay();
            UpdateCropOverlay();
            _status.Content =
                "Crop: drag edges/corners to resize, drag inside to move. Enter = apply, Esc = cancel, double-click = apply.";
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

            BakeStrokesToImage();

            SaveUndoStateCallback?.Invoke("Crop");
            _layers.Crop(
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
            if (_cropMaskPath != null)
            {
                _artboard.Children.Remove(_cropMaskPath);
                _cropMaskPath = null;
            }

            if (_cropRectVis != null)
            {
                _artboard.Children.Remove(_cropRectVis);
                _cropRectVis = null;
            }

            foreach (var h in _cropHandles) _artboard.Children.Remove(h);
            _cropHandles.Clear();
        }

        private void UpdateCropOverlay()
        {
            _cropRect = WRect.Intersect(_cropRect, new WRect(0, 0, Doc.Width, Doc.Height));
            if (_cropRect.IsEmpty)
                _cropRect = new WRect(0, 0, Math.Min(64, Doc.Width), Math.Min(64, Doc.Height));

            var outer = new RectangleGeometry(new WRect(0, 0, Doc.Width, Doc.Height));
            var inner = new RectangleGeometry(_cropRect);
            var cg = new GeometryGroup { FillRule = FillRule.EvenOdd };
            cg.Children.Add(outer);
            cg.Children.Add(inner);
            _cropMaskPath!.Data = cg;

            Canvas.SetLeft(_cropRectVis!, _cropRect.X);
            Canvas.SetTop(_cropRectVis!, _cropRect.Y);
            _cropRectVis!.Width = _cropRect.Width;
            _cropRectVis!.Height = _cropRect.Height;

            var cx = _cropRect.X + _cropRect.Width / 2;
            var cy = _cropRect.Y + _cropRect.Height / 2;
            var left = _cropRect.X;
            var right = _cropRect.Right();
            var top = _cropRect.Y;
            var bottom = _cropRect.Bottom();

            var centers = new (double x, double y, Cursor cur, CropHit hit)[]
            {
                (cx, top, Cursors.SizeNS, CropHit.N),
                (cx, bottom, Cursors.SizeNS, CropHit.S),
                (right, cy, Cursors.SizeWE, CropHit.E),
                (left, cy, Cursors.SizeWE, CropHit.W),
                (right, top, Cursors.SizeNESW, CropHit.NE),
                (left, top, Cursors.SizeNWSE, CropHit.NW),
                (right, bottom, Cursors.SizeNWSE, CropHit.SE),
                (left, bottom, Cursors.SizeNESW, CropHit.SW),
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
                (left, cy, CropHit.W),
                (right, top, CropHit.NE),
                (left, top, CropHit.NW),
                (right, bottom, CropHit.SE),
                (left, bottom, CropHit.SW),
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
                case CropHit.S: cur = Cursors.SizeNS; break;
                case CropHit.E:
                case CropHit.W: cur = Cursors.SizeWE; break;
                case CropHit.NE:
                case CropHit.SW: cur = Cursors.SizeNESW; break;
                case CropHit.NW:
                case CropHit.SE: cur = Cursors.SizeNWSE; break;
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
                    r.X += dx;
                    r.Y += dy;
                    break;

                case CropHit.N:
                    r.Y += dy;
                    r.Height -= dy;
                    break;
                case CropHit.S:
                    r.Height += dy;
                    break;
                case CropHit.W:
                    r.X += dx;
                    r.Width -= dx;
                    break;
                case CropHit.E:
                    r.Width += dx;
                    break;

                case CropHit.NE:
                    r.Y += dy;
                    r.Height -= dy;
                    r.Width += dx;
                    break;
                case CropHit.NW:
                    r.Y += dy;
                    r.Height -= dy;
                    r.X += dx;
                    r.Width -= dx;
                    break;
                case CropHit.SE:
                    r.Width += dx;
                    r.Height += dy;
                    break;
                case CropHit.SW:
                    r.X += dx;
                    r.Width -= dx;
                    r.Height += dy;
                    break;
            }

            r = NormalizeRect(r);
            if (r.Width < MinCropSize) r.Width = MinCropSize;
            if (r.Height < MinCropSize) r.Height = MinCropSize;

            if (r.X < 0) r.X = 0;
            if (r.Y < 0) r.Y = 0;
            if (r.Right() > Doc.Width) r.Width = Doc.Width - r.X;
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

                        if (_polyPts.Count >= 3)
                        {
                            var start = _polyPts[0];
                            var dist = Math.Sqrt(Math.Pow(pos.X - start.X, 2) + Math.Pow(pos.Y - start.Y, 2));

                            if (dist < 10.0)
                            {
                                FinishPolygonSelection();
                                e.Handled = true;
                                return;
                            }
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
                        e.Handled = true;
                        break;

                    case SelMode.Lasso when _isDragging:
                        var last = _lassoPts[^1];
                        if ((pos - last).Length > 1.0) _lassoPts.Add(pos);
                        ShowLasso();
                        e.Handled = true;
                        break;

                    case SelMode.CropInteractive when _artboard.IsMouseCaptured:
                        DragCrop(pos);
                        e.Handled = true;
                        break;

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

                        if (_activeSelectionRect is WRect rr && rr.Width >= 1 && rr.Height >= 1)
                        {
                            var geom = new RectangleGeometry(rr);
                            ActivateSelectionPainting(geom);
                        }
                        else
                        {
                            DeselectSelection();
                        }

                        e.Handled = true;
                        break;

                    case SelMode.Lasso when _isDragging:
                        _isDragging = false;
                        _artboard.ReleaseMouseCapture();

                        if (_lassoPts.Count > 2)
                        {
                            if (_lassoPts[0] != _lassoPts[^1]) _lassoPts.Add(_lassoPts[0]);
                            ShowLasso();

                            var sg = new StreamGeometry();
                            using (var ctx = sg.Open())
                            {
                                ctx.BeginFigure(_lassoPts[0], isFilled: true, isClosed: true);
                                ctx.PolyLineTo(_lassoPts.Skip(1).ToList(), true, true);
                            }

                            sg.Freeze();
                            ActivateSelectionPainting(sg);
                        }
                        else
                        {
                            DeselectSelection();
                        }

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
                    if (_polyPts.Count >= 3)
                    {
                        FinishPolygonSelection();
                    }
                    else
                    {
                        _status.Content = "Need at least 3 points for polygon.";
                    }

                    e.Handled = true;
                }
            };

            _artboard.MouseDown += (s, e) =>
            {
                if (_mode == SelMode.Polygon && e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
                {
                    if (_polyPts.Count >= 3)
                    {
                        FinishPolygonSelection();
                    }
                    else
                    {
                        _status.Content = "Need at least 3 points for polygon.";
                    }

                    e.Handled = true;
                }

                if (_mode == SelMode.CropInteractive && e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
                {
                    CommitInteractiveCrop();
                    e.Handled = true;
                }
            };
        }

        private void FinishPolygonSelection()
        {
            _polyActive = false;

            if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;

            if (_polyPts.Count > 2)
            {
                if (_polyPts[0] != _polyPts[^1])
                    _polyPts.Add(_polyPts[0]);

                ShowPolygon();

                var sg = new StreamGeometry();
                using (var ctx = sg.Open())
                {
                    ctx.BeginFigure(_polyPts[0], true, true);
                    ctx.PolyLineTo(_polyPts.Skip(1).ToList(), true, true);
                }

                sg.Freeze();
                ActivateSelectionPainting(sg);
            }
            else
            {
                DeselectSelection();
            }
        }

        private void ConvertTransparentBlackToWhite(Mat bgra)
        {
            if (bgra == null || bgra.Empty()) return;

            int w = bgra.Width, h = bgra.Height;
            int step = (int)bgra.Step();
            var data = new byte[step * h];
            Marshal.Copy(bgra.Data, data, 0, data.Length);

            for (int y = 0; y < h; y++)
            {
                int row = y * step;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    byte a = data[i + 3];

                    if (a == 0)
                    {
                        data[i + 0] = 255;
                        data[i + 1] = 255;
                        data[i + 2] = 255;
                    }
                }
            }

            Marshal.Copy(data, 0, bgra.Data, data.Length);
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

        private byte[] BuildSelectionMaskBytes_Binary(int x, int y, int w, int h, byte threshold)
        {
            if (_selectionClip == null)
            {
                var fullMask = new byte[w * h * 4];
                for (int i = 3; i < fullMask.Length; i += 4)
                    fullMask[i] = 255;
                return fullMask;
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new WRect(0, 0, w, h));

                dc.PushTransform(new TranslateTransform(-x, -y));
                dc.DrawGeometry(Brushes.White, null, _selectionClip);
                dc.Pop();
            }

            rtb.Render(dv);

            int stride = w * 4;
            var pixels = new byte[h * stride];
            rtb.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i + 0];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];

                int luminance = (r + g + b) / 3;
                bool inside = (luminance > threshold) && (a > threshold);

                pixels[i + 0] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = inside ? (byte)255 : (byte)0;
            }

            return pixels;
        }

        private void TranslateSelectionGeometry(double dx, double dy)
        {
            if (_selectionClip == null) return;

            var newClip = _selectionClip.Clone();
            var tg = new TransformGroup();
            if (newClip.Transform != null) tg.Children.Add(newClip.Transform);
            tg.Children.Add(new TranslateTransform(dx, dy));
            newClip.Transform = tg;
            newClip.Freeze();
            _selectionClip = newClip;

            if (_selectionEdge != null)
            {
                var newEdge = _selectionEdge.Clone();
                var tge = new TransformGroup();
                if (newEdge.Transform != null) tge.Children.Add(newEdge.Transform);
                tge.Children.Add(new TranslateTransform(dx, dy));
                newEdge.Transform = tge;
                newEdge.Freeze();
                _selectionEdge = newEdge;
            }

            _paint.Clip = _selectionClip;

            MarkSelectionDirty();

            if (_lassoPts.Count > 0)
            {
                for (int i = 0; i < _lassoPts.Count; i++)
                    _lassoPts[i] = new WPoint(_lassoPts[i].X + dx, _lassoPts[i].Y + dy);
            }

            if (_polyPts.Count > 0)
            {
                for (int i = 0; i < _polyPts.Count; i++)
                    _polyPts[i] = new WPoint(_polyPts[i].X + dx, _polyPts[i].Y + dy);
            }

            if (_activeSelectionRect is WRect rr && !rr.IsEmpty)
            {
                var moved = new WRect(rr.X + dx, rr.Y + dy, rr.Width, rr.Height);
                _activeSelectionRect = moved;
            }
        }

        private static BitmapSource MatToBitmapSourceBGRA(Mat m)
        {
            if (m.Empty()) throw new InvalidOperationException("Mat is empty.");
            Mat bgra = m;
            if (m.Type() != MatType.CV_8UC4)
            {
                bgra = new Mat();
                Cv2.CvtColor(m, bgra, ColorConversionCodes.BGR2BGRA);
            }

            int w = bgra.Width, h = bgra.Height, stride = w * 4;

            var pixels = new byte[stride * h];
            Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();

            if (!ReferenceEquals(bgra, m)) bgra.Dispose();
            return bmp;
        }

        private static Mat BitmapSourceToMatBGRA(BitmapSource bmp)
        {
            var fmt = bmp.Format == PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

            int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
            var data = new byte[stride * h];

            fmt.CopyPixels(data, stride, 0);

            for (int i = 0; i < data.Length; i += 4)
            {
                byte a = data[i + 3];
                if (a == 0)
                {
                    data[i + 0] = 255;
                    data[i + 1] = 255;
                    data[i + 2] = 255;
                }
            }

            var mat = new Mat(h, w, MatType.CV_8UC4);
            Marshal.Copy(data, 0, mat.Data, data.Length);
            return mat;
        }

        private void AlphaBlendOverToMat(Mat targetMat, Mat src, int x, int y)
        {
            if (src.Empty() || targetMat == null || targetMat.Empty()) return;

            int dstW = targetMat.Width, dstH = targetMat.Height;

            int sx = 0, sy = 0, dx = x, dy = y;
            if (dx < 0)
            {
                sx = -dx;
                dx = 0;
            }

            if (dy < 0)
            {
                sy = -dy;
                dy = 0;
            }

            int maxW = Math.Min(src.Width - sx, dstW - dx);
            int maxH = Math.Min(src.Height - sy, dstH - dy);
            if (maxW <= 0 || maxH <= 0) return;

            int srcStep = (int)src.Step();
            int dstStep = (int)targetMat.Step();
            var dstData = new byte[dstStep * dstH];
            Marshal.Copy(targetMat.Data, dstData, 0, dstData.Length);

            var srcData = new byte[srcStep * src.Height];
            Marshal.Copy(src.Data, srcData, 0, srcData.Length);

            for (int row = 0; row < maxH; row++)
            {
                int sRow = (sy + row) * srcStep;
                int dRow = (dy + row) * dstStep;

                for (int col = 0; col < maxW; col++)
                {
                    int si = sRow + (sx + col) * 4;
                    int di = dRow + (dx + col) * 4;

                    byte sb = srcData[si + 0];
                    byte sg = srcData[si + 1];
                    byte sr = srcData[si + 2];
                    byte sa = srcData[si + 3];

                    if (sa == 0) continue;

                    if (sa == 255)
                    {
                        dstData[di + 0] = sb;
                        dstData[di + 1] = sg;
                        dstData[di + 2] = sr;
                        dstData[di + 3] = srcData[si + 3];
                    }
                    else
                    {
                        double srcA = sa / 255.0;
                        double dstA = dstData[di + 3] / 255.0;
                        double outA = srcA + dstA * (1.0 - srcA);

                        if (outA > 0)
                        {
                            dstData[di + 0] =
                                (byte)Math.Clamp((sb * srcA + dstData[di + 0] * dstA * (1.0 - srcA)) / outA, 0, 255);
                            dstData[di + 1] =
                                (byte)Math.Clamp((sg * srcA + dstData[di + 1] * dstA * (1.0 - srcA)) / outA, 0, 255);
                            dstData[di + 2] =
                                (byte)Math.Clamp((sr * srcA + dstData[di + 2] * dstA * (1.0 - srcA)) / outA, 0, 255);
                            dstData[di + 3] = (byte)Math.Clamp(outA * 255, 0, 255);
                        }
                    }
                }
            }

            Marshal.Copy(dstData, 0, targetMat.Data, dstData.Length);
        }
    }

    internal sealed class ResizeInlineWindow : WWindow
    {
        private readonly TextBox _wBox = new() { MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBox _hBox = new() { MinWidth = 80, Margin = new Thickness(0, 8, 8, 0) };

        private readonly CheckBox _lockAspect = new()
            { Content = "Lock aspect ratio", Margin = new Thickness(0, 12, 0, 0), IsChecked = true };

        private readonly int _origW, _origH;
        private bool _updating;

        public int ResultWidth { get; private set; }
        public int ResultHeight { get; private set; }

        public ResizeInlineWindow(int currentWidth, int currentHeight)
        {
            Title = "Resize Image";
            Width = 420;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 243, 243));

            _origW = currentWidth;
            _origH = currentHeight;

            var mainGrid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "RESIZE IMAGE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(header, 0);

            var currentSizeText = new TextBlock
            {
                Text = $"Current size: {currentWidth} × {currentHeight} pixels",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(currentSizeText, 1);

            var widthPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var wLabel = new TextBlock
            {
                Text = "Width (pixels)",
                FontSize = 11,
                Foreground = Foreground,
                Margin = new Thickness(0, 0, 0, 6)
            };

            _wBox.Background = new SolidColorBrush(Color.FromArgb(255, 45, 45, 45));
            _wBox.Foreground = Foreground;
            _wBox.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 63, 63, 70));
            _wBox.BorderThickness = new Thickness(1, 1, 1, 1);
            _wBox.Padding = new Thickness(8, 6, 8, 6);
            _wBox.FontSize = 12;

            widthPanel.Children.Add(wLabel);
            widthPanel.Children.Add(_wBox);
            Grid.SetRow(widthPanel, 2);

            var heightPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var hLabel = new TextBlock
            {
                Text = "Height (pixels)",
                FontSize = 11,
                Foreground = Foreground,
                Margin = new Thickness(0, 0, 0, 6)
            };

            _hBox.Background = new SolidColorBrush(Color.FromArgb(255, 45, 45, 45));
            _hBox.Foreground = Foreground;
            _hBox.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 63, 63, 70));
            _hBox.BorderThickness = new Thickness(1, 1, 1, 1);
            _hBox.Padding = new Thickness(8, 6, 8, 6);
            _hBox.FontSize = 12;

            heightPanel.Children.Add(hLabel);
            heightPanel.Children.Add(_hBox);
            Grid.SetRow(heightPanel, 3);

            _lockAspect.Foreground = Foreground;
            _lockAspect.Margin = new Thickness(0, 0, 0, 0);
            Grid.SetRow(_lockAspect, 4);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var okBtn = new Button
            {
                Content = "✓ Resize",
                Width = 120,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(45, 140, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0)
            };
            okBtn.Click += Ok_Click;

            var cancelBtn = new Button
            {
                Content = "✕ Cancel",
                Width = 120,
                Padding = new Thickness(12, 8, 12, 8),
                IsCancel = true,
                Background = new SolidColorBrush(Color.FromRgb(140, 45, 45)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0)
            };

            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            Grid.SetRow(buttonPanel, 6);

            mainGrid.Children.Add(header);
            mainGrid.Children.Add(currentSizeText);
            mainGrid.Children.Add(widthPanel);
            mainGrid.Children.Add(heightPanel);
            mainGrid.Children.Add(_lockAspect);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;

            _wBox.Text = _origW.ToString(CultureInfo.InvariantCulture);
            _hBox.Text = _origH.ToString(CultureInfo.InvariantCulture);

            _wBox.TextChanged += (s, e) => SyncAspect(fromWidth: true);
            _hBox.TextChanged += (s, e) => SyncAspect(fromWidth: false);

            Loaded += (s, e) => _wBox.Focus();
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
                MessageBox.Show("Please enter positive integers for width and height.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
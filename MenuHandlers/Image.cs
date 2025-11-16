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
        {
            if (_img == null) return;
            // SaveUndoState will be called inside ApplyCropFromRect when crop is applied
            _img.CropCommand();
        }

        private void Image_Resize_Click(object sender, RoutedEventArgs e)
        {
            if (_img == null) return;
            // SaveUndoState will be called inside ResizeWithDialog after dialog confirmation
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

        // ---- Selection mini-tab (like Filters) ----
        private void Selection_PaintInside_Click(object sender, RoutedEventArgs e)
            => _img?.EnableSelectionPaintMode();

        private void Selection_Move_Click(object sender, RoutedEventArgs e)
            => _img?.BeginMoveSelectedArea();

        private void Selection_Copy_Click(object sender, RoutedEventArgs e)
            => _img?.CopySelectionToClipboard();
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
        // --- LAYERS integration ---
        private readonly LayerStack _layers = new LayerStack();

        // Active bitmap target for all pixel-writing tools (Brush/Shapes/Text/Paste)
        public OpenCvSharp.Mat Mat => _layers.ActiveMat;

        // Callback for saving undo state (set by MainWindow)
        public Action<string>? SaveUndoStateCallback { get; set; }

        // Lightweight layer API for MainWindow menu
        public System.Collections.Generic.List<string> Layers_AllNames => _layers.Layers.ConvertAll(l => l.Name);
        public string Layers_ActiveName => _layers.Active.Name;

        public void Layers_AddBlank() { EnsureLayers(); _layers.AddBlank(); RefreshView(); }
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
        public void Layers_Select(int idx) { if (_layers.Layers.Count == 0) return; _layers.Select(idx); RefreshView(); }
        public void Layers_DeleteActive() { if (_layers.Layers.Count <= 1) return; _layers.DeleteActive(); RefreshView(); }
        public void Layers_RenameActive(string name) { if (_layers.Layers.Count == 0) return; _layers.RenameActive(name); RefreshView(); }
        public void Layers_ToggleActiveVisibility() { if (_layers.Layers.Count == 0) return; _layers.ToggleActiveVisibility(); RefreshView(); }

        private void EnsureLayers()
        {
            if (_layers.Layers.Count == 0)
                _layers.SetSingleFromMat(Doc.Image.Clone(), "Background");
        }

        // Notify host (MainWindow) when the underlying bitmap changed (so it can update brush policy).
        public event Action? ImageChanged;

        // --- selection mode (temporary while creating) ---
        private enum SelMode { None, Rect, Lasso, Polygon, CropInteractive }
        private SelMode _mode = SelMode.None;

        // --- active selection (persistent while painting) ---
        private Geometry? _selectionClip;              // paints are clipped to this when not null
        private Geometry? _selectionEdge;              // 1px widened ring of selection for edge painting
        private bool _autoDeselectOnClickOutside = false; // sticky selection behavior

        // Public exposure of selection state for Tools.cs
        public Geometry? SelectionFill => _selectionClip;
        public Geometry? SelectionEdge => _selectionEdge;
        public bool HasActiveSelection => _selectionClip != null;

        // ===== FAST SELECTION MASK (for lag-free painting) =====
        private byte[]? _selMask;     // 1 byte per pixel: 255 = inside, 0 = outside
        private int _selMaskW, _selMaskH;
        private bool _selMaskDirty = true;

        /// <summary>Call once per stroke (or whenever selection changed) to (re)build the mask.</summary>
        public void RebuildSelectionMaskIfDirty()
        {
            if (!_selMaskDirty) return;

            if (_selectionClip == null)
            {
                _selMask = null; _selMaskW = _selMaskH = 0; _selMaskDirty = false;
                return;
            }

            int w = Doc.Width, h = Doc.Height;
            if (w < 1 || h < 1)
            {
                _selMask = null; _selMaskW = _selMaskH = 0; _selMaskDirty = false;
                return;
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Draw selection filled white; antialias will produce gray alpha we will threshold.
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
            _selMaskW = w; _selMaskH = h;
            _selMaskDirty = false;
        }

        /// <summary>Ultra-fast test: true if pixel is allowed for painting (or no selection at all).</summary>
        public bool FastMaskInside(int x, int y)
        {
            if (_selMask == null) return true;
            if ((uint)x >= (uint)_selMaskW || (uint)y >= (uint)_selMaskH) return false;
            return _selMask[y * _selMaskW + x] == 255;
        }

        private void MarkSelectionDirty() => _selMaskDirty = true;

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

        // ---- floating paste (drag after paste, click elsewhere to place) ----
        private Mat? _floatingMat;                            // BGRA content being moved
        private System.Windows.Controls.Image? _floatingView; // visual for the floating content
        private Rectangle? _floatingOutline;                  // subtle outline while hover/drag
        private WPoint _floatPos;                             // top-left of floating content
        private WPoint _floatStart;                           // sticky: remember origin of floating
        private bool _floatDragging;
        private bool _floatHovering;
        private Vector _floatGrabDelta;                       // mouse offset at drag start
        private bool _floatingActive;
        private bool _floatingFromMove;                       // true if created via MoveSelection

        public bool IsFloatingActive => _floatingActive;
        public bool PixelFullyInsideSelection(int x, int y)
        {
            if (_selectionClip == null) return true;

            // Require the entire 1×1 pixel box (shrunk slightly) to be *fully* inside the selection.
            // This prevents painting on the dashed outline (edge anti-alias).
            var rect = new WRect(x + 0.05, y + 0.05, 0.90, 0.90);
            var detail = _selectionClip.FillContainsWithDetail(new RectangleGeometry(rect));
            return detail == IntersectionDetail.FullyContains || detail == IntersectionDetail.FullyInside;
        }

        // ForceRefreshView is used by Tools.cs on tool switches.
        // Implement it so that if a floating paste exists, we COMMIT it,
        // but KEEP the selection (sticky-until-Enter behavior).
        public void ForceRefreshView()
        {
            if (_floatingActive) CommitFloatingPaste(); // place but do NOT clear selection
            RefreshView();
        }

        public ImageController(System.Windows.Controls.Image imageView, Canvas artboard, ContentControl status, InkCanvas paintCanvas)
        {
            _imageView = imageView;
            _artboard  = artboard;
            _status    = status;
            _paint     = paintCanvas;

            HookSelectionEvents();
            HookSelectionKeys();     // Ctrl+D and sticky Enter finalize; click-outside disabled for sticky
            HookCropKeys();
            HookFloatingPasteEvents();
            HookFloatingKeys();
            HookClipboardKeys();   // Ctrl+C / X / V
            EnsureLayers();
            HookFloatingKeys();      // Enter = place & clear; Esc = cancel; Arrows = nudge
            HookClipboardKeys();     // Ctrl+C / X / V
            RefreshView();
        }

        /* ---------------- API called from MainWindow / Tools.cs ---------------- */

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
                // SaveUndoState should be called from MainWindow before this method
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

        /// <summary>
        /// Restores the active layer's Mat from a snapshot (used for undo/redo).
        /// </summary>
        public void RestoreImageState(Mat snapshot)
        {
            if (snapshot == null || snapshot.Empty()) return;
            
            BakeStrokesToImage(); // Bake any pending strokes first
            
            // Replace the active layer's Mat with the snapshot
            var activeLayer = _layers.Active;
            if (activeLayer != null)
            {
                var oldMat = activeLayer.Mat;
                activeLayer.Mat = snapshot.Clone(); // Clone to avoid disposing the snapshot
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
                
                // Save undo state before resizing
                SaveUndoStateCallback?.Invoke($"Resize to {dlg.ResultWidth}×{dlg.ResultHeight}");

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

                _layers.ResizeTo(newW, newH, mode);

                EndSelectionMode();
                RefreshView();
                _status.Content = $"Resized to {newW}×{newH} ({mode})";
            }
        }

        /* -------- Selection paint mode / Move selection / Copy -------- */

        public void EnableSelectionPaintMode()
        {
            if (_selectionClip != null)
            {
                _paint.Clip = _selectionClip;
                _status.Content = "Selection paint mode: brush is clipped to selection (Enter to finalize).";
            }
            else
            {
                _status.Content = "No active selection.";
            }
        }

        // Image.cs (inside ImageController)
        public void BeginMoveSelectedArea()
        {
            if (_selectionClip == null)
            {
                MessageBox.Show("Select an area first.");
                return;
            }

            // Keep selection active; we just move its contents.
            BakeStrokesToImage();

            // Use exact geometry bounds, but ensure we fully cover the right/bottom via Ceil.
            var b = _selectionClip.Bounds;
            int left   = (int)Math.Floor(b.X);
            int top    = (int)Math.Floor(b.Y);
            int right  = (int)Math.Ceiling(b.X + b.Width);
            int bottom = (int)Math.Ceiling(b.Y + b.Height);

            left = Math.Clamp(left, 0, Math.Max(0, Doc.Width  - 1));
            top  = Math.Clamp(top,  0, Math.Max(0, Doc.Height - 1));
            right  = Math.Clamp(right,  0, Doc.Width);
            bottom = Math.Clamp(bottom, 0, Doc.Height);

            int w = Math.Max(1, right - left);
            int h = Math.Max(1, bottom - top);

            using var roi = new Mat(Doc.Image, new OcvRect(left, top, w, h));
            using var clone = roi.Clone(); // BGRA

            // Build mask for exact selection inside this ROI and binarize it (hard edge).
            var mask = BuildSelectionMaskBytes_Binary(left, top, w, h, 128);

            // Apply the mask alpha to the floating clone (outside selection => alpha 0).
            ApplyAlphaMaskToMat_Binary(clone, mask, w, h);

            // Clear ONLY selected pixels in the document (using the same binary mask).
            ClearDocRegionWithMaskToWhite_Binary(left, top, w, h, mask);

            // Start floating paste with masked sprite
            BeginFloatingPaste(clone, left, top);
            RefreshView();

            _floatingFromMove = true;
            _status.Content = "Move: drag to reposition, Enter to place (selection stays), Esc to cancel.";
        }

        /// <summary>Finalize (place floating if any) and clear the selection — bound to Enter.</summary>
        private void FinalizeSelection()
        {
            if (_floatingActive) CommitFloatingPaste(); // now keeps the selection; we'll clear next
            DeselectSelection();
            _status.Content = "Selection finalized.";
        }

        /* ---------------- view plumbing ---------------- */

        private void RefreshView()
        {
            EnsureLayers();
            Doc.ReplaceWith(_layers.Composite());
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
            EndSelectionMode(); // clear any prior selection + clip
            _mode = mode;

            // Disable painting so Artboard gets mouse events for selection
            _paint.EditingMode = InkCanvasEditingMode.None;
            _paint.IsHitTestVisible = false;
            _paint.Cursor = Cursors.Arrow;

            _artboard.Focusable = true;
            _artboard.Focus();
        }

        private void EndSelectionMode()
        {
            // clear selection visuals from *temporary* modes
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

            // always clear persistent selection/clip on EndSelectionMode
            DeselectSelection();

            _mode = SelMode.None;
            _status.Content = "Ready";

            // Re-enable painting
            _paint.IsHitTestVisible = true;
            _paint.EditingMode = InkCanvasEditingMode.Ink;
            _paint.Cursor = Cursors.Pen;
        }

        // Finalize a created selection and immediately return to painting, clipped to the selection.
        private void ActivateSelectionPainting(Geometry clipGeom)
        {
            _selectionClip = clipGeom;
            _selectionClip.Freeze();

            // Precompute a crisp 1px ring for "edge paint when outside"
            var pen = new Pen(Brushes.Black, 1.0);
            _selectionEdge = _selectionClip.GetWidenedPathGeometry(pen);
            _selectionEdge.Freeze();

            _paint.Clip = _selectionClip;

            // selection mask will need rebuilding
            MarkSelectionDirty();

            // show visuals but exit selection mode -> back to painting
            _mode = SelMode.None;
            _paint.IsHitTestVisible = true;
            _paint.EditingMode = InkCanvasEditingMode.Ink;
            _paint.Cursor = Cursors.Pen;

            // sticky: do NOT auto-deselect on click-outside
            _autoDeselectOnClickOutside = false;

            _status.Content = "Selection active — painting is clipped (Enter to finalize).";
        }

        private void DeselectSelection()
        {
            _selectionClip = null;
            _selectionEdge = null;
            _paint.Clip = null;

            // selection mask invalidated
            MarkSelectionDirty();

            // keep visuals hidden when deselecting
            if (_rectBox != null) _rectBox.Visibility = Visibility.Collapsed;
            if (_lassoLine != null) _lassoLine.Visibility = Visibility.Collapsed;
            if (_polyLine  != null) _polyLine.Visibility  = Visibility.Collapsed;

            _activeSelectionRect = null;
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
            int dstStep = (int)Mat.Step();
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

        /* ---------------- clipboard (copy / cut / paste) ---------------- */

        public void CopySelectionToClipboard()
        {
            // If floating exists, place it first (predictable outcome)
            if (_floatingActive) CommitFloatingPaste();

            BakeStrokesToImage();

            var r = _activeSelectionRect ?? new WRect(0, 0, Doc.Width, Doc.Height);
            r = NormalizeRect(r);
            r = WRect.Intersect(r, new WRect(0, 0, Doc.Width, Doc.Height));
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) { MessageBox.Show("Nothing to copy."); return; }

            int x = (int)Math.Floor(r.X);
            int y = (int)Math.Floor(r.Y);
            int w = (int)Math.Round(r.Width);
            int h = (int)Math.Round(r.Height);

            using var roi = new Mat(Doc.Image, new OcvRect(x, y, w, h));
            using var clone = roi.Clone();
            var bmp = MatToBitmapSourceBGRA(clone);
            var pbgra = new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
            pbgra.Freeze();
            Clipboard.SetImage(pbgra);

            _status.Content = $"Copied {w}×{h}";
        }

        public void CutSelectionToClipboard()
        {
            if (_floatingActive) CommitFloatingPaste();

            BakeStrokesToImage();

            var r = _activeSelectionRect ?? new WRect(0, 0, Doc.Width, Doc.Height);
            r = NormalizeRect(r);
            r = WRect.Intersect(r, new WRect(0, 0, Doc.Width, Doc.Height));
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) { MessageBox.Show("Nothing to cut."); return; }

            int x = (int)Math.Floor(r.X);
            int y = (int)Math.Floor(r.Y);
            int w = (int)Math.Round(r.Width);
            int h = (int)Math.Round(r.Height);

            // Copy to clipboard first
            using (var roi = new Mat(Doc.Image, new OcvRect(x, y, w, h)))
            using (var clone = roi.Clone())
            {
                var bmp = MatToBitmapSourceBGRA(clone);
                var pbgra = new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
                pbgra.Freeze();
                Clipboard.SetImage(pbgra);
            }

            // Clear selection area to white (opaque). Keep selection visible for predictability.
            Cv2.Rectangle(Doc.Image, new OcvRect(x, y, w, h), new Scalar(255, 255, 255, 255), -1);
            RefreshView();
            _status.Content = $"Cut {w}×{h}";
        }

        public void PasteFromClipboard()
        {
            BakeStrokesToImage();

            if (!Clipboard.ContainsImage()) { MessageBox.Show("Clipboard does not contain an image."); return; }
            var cb = Clipboard.GetImage();
            if (cb == null) { MessageBox.Show("Failed to read image from clipboard."); return; }

            using var src = BitmapSourceToMatBGRA(cb);

            // Initial placement: selection top-left if present, otherwise center
            int px, py;
            if (_activeSelectionRect is WRect sel && !sel.IsEmpty)
            {
                px = (int)Math.Floor(sel.X);
                py = (int)Math.Floor(sel.Y);
            }
            else
            {
                px = Math.Max(0, (Doc.Width  - src.Width)  / 2);
                py = Math.Max(0, (Doc.Height - src.Height) / 2);
            }

            BeginFloatingPaste(src, px, py);
            _status.Content = "Paste: drag to move; click outside to place (Enter=finalize, Esc=cancel).";
        }

        /* ---------------- floating paste implementation ---------------- */

        private void BeginFloatingPaste(Mat sourceBGRA, int x, int y)
        {
            // If a previous floating paste exists, place it first for predictability
            if (_floatingActive) CommitFloatingPaste();

            _floatingMat = sourceBGRA.Clone(); // keep our own copy
            _floatPos = new WPoint(x, y);
            _floatStart = _floatPos; // remember origin for sticky translate
            ClampFloatingToBounds();

            // Clear selection visuals so the user sees the pasted thing clearly
            // (we keep _selectionClip to allow later enabling paint-inside if user wants)
            _mode = SelMode.None;

            // Make a WPF Image that shows the floating content on top
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

            // Create the subtle outline (dashed, crisp)
            _floatingOutline = new Rectangle
            {
                Width = _floatingMat.Width,
                Height = _floatingMat.Height,
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            RenderOptions.SetEdgeMode(_floatingOutline, EdgeMode.Aliased);
            Panel.SetZIndex(_floatingOutline, 2001);
            _artboard.Children.Add(_floatingOutline);

            UpdateFloatingView();
            UpdateFloatingOutlineVisibility(forceVisible: false);

            // Disable painting so dragging is smooth and Artboard gets the mouse
            _paint.EditingMode = InkCanvasEditingMode.None;
            _paint.IsHitTestVisible = false;
            _paint.Cursor = Cursors.Arrow;

            _floatingActive = true;
            _artboard.Focusable = true;
            _artboard.Focus();
        }

        private void CommitFloatingPaste()
        {
            if (!_floatingActive || _floatingMat is null) return;
            AlphaBlendOver(_floatingMat, (int)Math.Round(_floatPos.X), (int)Math.Round(_floatPos.Y));

            // If this floating came from MoveSelection, translate selection geometry & visuals
            if (_floatingFromMove && _selectionClip != null)
            {
                var dx = _floatPos.X - _floatStart.X;
                var dy = _floatPos.Y - _floatStart.Y;
                TranslateSelectionGeometry(dx, dy);
                _floatingFromMove = false;
                _status.Content = "Moved selection placed (still selected).";
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
            RemoveFloatingView();
            RefreshView();
            _status.Content = "Paste cancelled.";
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

            // Re-enable painting (respect clip if there was one)
            _paint.IsHitTestVisible = true;
            _paint.EditingMode = InkCanvasEditingMode.Ink;
            _paint.Cursor = Cursors.Pen;
        }

        private void UpdateFloatingView()
        {
            if (_floatingView == null) return;
            Canvas.SetLeft(_floatingView, _floatPos.X);
            Canvas.SetTop(_floatingView,  _floatPos.Y);

            if (_floatingOutline != null)
            {
                _floatingOutline.Width = _floatingView.Width;
                _floatingOutline.Height = _floatingView.Height;
                Canvas.SetLeft(_floatingOutline, _floatPos.X);
                Canvas.SetTop(_floatingOutline,  _floatPos.Y);
            }
        }

        private void ClampFloatingToBounds()
        {
            if (_floatingMat is null) return;
            double maxX = Math.Max(0, Doc.Width  - _floatingMat.Width);
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
            UpdateFloatingOutlineVisibility(forceVisible: _floatDragging || _floatHovering);
        }

        private void UpdateFloatingOutlineVisibility(bool forceVisible)
        {
            if (_floatingOutline == null) return;
            _floatingOutline.Visibility = (forceVisible && _floatingActive) ? Visibility.Visible : Visibility.Collapsed;
        }

        /* ---------------- floating paste event hooks ---------------- */

        private void HookFloatingPasteEvents()
        {
            // Use Preview* so we can swallow events while floating
            _artboard.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!_floatingActive) return;
                var pos = e.GetPosition(_artboard);

                if (HitTestFloating(pos))
                {
                    _floatDragging = true;
                    _floatGrabDelta = (Vector)(pos - _floatPos);
                    _artboard.CaptureMouse();
                    UpdateFloatingOutlineVisibility(forceVisible: true);
                    e.Handled = true;
                }
                else
                {
                    // Click outside: place it (selection stays)
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
                    UpdateFloatingCursor(pos); // also toggles outline on hover
                }
            };

            _artboard.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!_floatingActive) return;
                if (_floatDragging)
                {
                    _floatDragging = false;
                    _artboard.ReleaseMouseCapture();
                    // After releasing, only show outline if still hovering
                    UpdateFloatingOutlineVisibility(forceVisible: _floatHovering);
                    e.Handled = true;
                }
            };

            _artboard.MouseLeave += (s, e) =>
            {
                if (!_floatingActive) return;
                _floatHovering = false;
                if (!_floatDragging) UpdateFloatingOutlineVisibility(forceVisible: false);
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
                        // Place then clear selection (finalize)
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
                        // Arrow-key nudge for fine placement
                        int dx = (e.Key == Key.Left)  ? -1 : (e.Key == Key.Right) ? 1 : 0;
                        int dy = (e.Key == Key.Up)    ? -1 : (e.Key == Key.Down)  ? 1 : 0;
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
                        // Place floating first if present, then copy (CopySelectionToClipboard handles baking)
                        CopySelectionToClipboard();
                        e.Handled = true;
                        break;

                    case Key.X:
                        // Place floating first if present, then cut
                        CutSelectionToClipboard();
                        e.Handled = true;
                        break;

                    case Key.V:
                        if (_floatingActive) CommitFloatingPaste(); // predictable behavior
                        PasteFromClipboard();
                        e.Handled = true;
                        break;
                }
            };

            // Catch keys regardless of which control currently has focus
            _artboard.PreviewKeyDown += handler;
            _paint.PreviewKeyDown += handler;
            _imageView.PreviewKeyDown += handler;

            // Belt & suspenders: listen on the window too (use alias to avoid OpenCvSharp.Window clash)
            if (Application.Current?.MainWindow is WWindow win)
                win.PreviewKeyDown += handler;
        }

        private void HookSelectionKeys()
        {
            // Ctrl+D -> Deselect (industry standard)
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

                // Sticky: Enter always finalizes selection (place floating if any, then clear)
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

            // Sticky mode: disable click-outside auto-deselect (keeps selection until Enter)
            _artboard.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (_floatingActive) return;         // floating handler owns clicks
                if (_mode != SelMode.None) return;   // currently creating a selection
                if (_selectionClip == null) return;

                // do nothing on click-outside — sticky until Enter
            };
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

            BakeStrokesToImage();
            // Save undo state before applying crop
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

                        // finalize: clip to rectangle & return to painting
                        if (_activeSelectionRect is WRect rr && rr.Width >= 1 && rr.Height >= 1)
                        {
                            var geom = new RectangleGeometry(rr);
                            ActivateSelectionPainting(geom);

                            // keep the marching-ants box visible
                            if (_rectBox != null) _rectBox.Visibility = Visibility.Visible;
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
                            // close and show
                            if (_lassoPts[0] != _lassoPts[^1]) _lassoPts.Add(_lassoPts[0]);
                            ShowLasso();

                            // build polygon geometry for clip
                            var sg = new StreamGeometry();
                            using (var ctx = sg.Open())
                            {
                                ctx.BeginFigure(_lassoPts[0], isFilled: true, isClosed: true);
                                ctx.PolyLineTo(_lassoPts.Skip(1).ToList(), true, true);
                            }
                            sg.Freeze();
                            ActivateSelectionPainting(sg);

                            // keep lasso visible
                            if (_lassoLine != null) _lassoLine.Visibility = Visibility.Visible;
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
                    _polyActive = false;
                    if (_polyPts.Count > 2) _polyPts.Add(_polyPts[0]);
                    ShowPolygon();

                    // finalize polygon to clip & return to paint
                    if (_polyPts.Count > 2)
                    {
                        var sg = new StreamGeometry();
                        using (var ctx = sg.Open())
                        {
                            ctx.BeginFigure(_polyPts[0], true, true);
                            ctx.PolyLineTo(_polyPts.Skip(1).ToList(), true, true);
                        }
                        sg.Freeze();
                        ActivateSelectionPainting(sg);

                        if (_polyLine != null) _polyLine.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        DeselectSelection();
                    }

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

                    if (_polyPts.Count > 2)
                    {
                        var sg = new StreamGeometry();
                        using (var ctx = sg.Open())
                        {
                            ctx.BeginFigure(_polyPts[0], true, true);
                            ctx.PolyLineTo(_polyPts.Skip(1).ToList(), true, true);
                        }
                        sg.Freeze();
                        ActivateSelectionPainting(sg);
                        if (_polyLine != null) _polyLine.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        DeselectSelection();
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

        /* ---------------- helpers for sticky selection translation ---------------- */
        // Builds a Pbgra32 mask for the current _selectionClip inside the ROI at (x,y,w,h).
        // Image.cs (inside ImageController)
        private byte[] BuildSelectionMaskBytes_Binary(int x, int y, int w, int h, byte threshold)
        {
            if (_selectionClip == null) return new byte[w * h * 4];

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(-x, -y));
                // Fill the selection solid white; antialias may produce gray alpha -> we will binarize.
                dc.DrawGeometry(Brushes.White, null, _selectionClip);
                dc.Pop();
            }
            rtb.Render(dv);

            int stride = w * 4;
            var buf = new byte[h * stride];
            rtb.CopyPixels(buf, stride, 0);

            // HARD threshold alpha so edges are crisp (no soft fringe).
            for (int i = 0; i < buf.Length; i += 4)
            {
                byte a = buf[i + 3];
                byte bin = (a >= threshold) ? (byte)255 : (byte)0;
                buf[i + 0] = 0;
                buf[i + 1] = 0;
                buf[i + 2] = 0;
                buf[i + 3] = bin;
            }
            return buf;
        }

        private void ApplyAlphaMaskToMat_Binary(Mat bgra, byte[] mask, int w, int h)
        {
            int step = (int)bgra.Step();
            var data = new byte[step * h];
            Marshal.Copy(bgra.Data, data, 0, data.Length);

            int mStride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int rowD = y * step;
                int rowM = y * mStride;
                for (int x = 0; x < w; x++)
                {
                    int di = rowD + x * 4;
                    int mi = rowM + x * 4;
                    byte a = mask[mi + 3]; // already 0 or 255
                    data[di + 3] = a;

                    if (a == 0)
                    {
                        // outside selection: fully transparent in the floating sprite
                        data[di + 0] = 0;
                        data[di + 1] = 0;
                        data[di + 2] = 0;
                    }
                }
            }

            Marshal.Copy(data, 0, bgra.Data, data.Length);
        }

        private void ClearDocRegionWithMaskToWhite_Binary(int x, int y, int w, int h, byte[] mask)
        {
            int step = (int)Doc.Image.Step();
            var dst = new byte[step * Doc.Height];
            Marshal.Copy(Doc.Image.Data, dst, 0, dst.Length);

            int mStride = w * 4;

            for (int yy = 0; yy < h; yy++)
            {
                int iy = y + yy; if (iy < 0 || iy >= Doc.Height) continue;
                int rowD = iy * step;
                int rowM = yy * mStride;

                for (int xx = 0; xx < w; xx++)
                {
                    int ix = x + xx; if (ix < 0 || ix >= Doc.Width) continue;

                    int di = rowD + ix * 4;
                    int mi = rowM + xx * 4;
                    byte a = mask[mi + 3]; // 0 or 255

                    if (a == 0) continue; // do not clear outside selection

                    dst[di + 0] = 255;
                    dst[di + 1] = 255;
                    dst[di + 2] = 255;
                    dst[di + 3] = 255;
                }
            }

            Marshal.Copy(dst, 0, Doc.Image.Data, dst.Length);
        }

        private byte[] BuildSelectionMaskBytes(int x, int y, int w, int h)
        {
            if (_selectionClip == null) return new byte[w * h * 4];

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Shift selection geometry into ROI-local coords
                dc.PushTransform(new TranslateTransform(-x, -y));
                dc.DrawGeometry(Brushes.White, null, _selectionClip);
                dc.Pop();
            }
            rtb.Render(dv);

            int stride = w * 4;
            var buf = new byte[h * stride];
            rtb.CopyPixels(buf, stride, 0);
            return buf;
        }

        // Applies alpha from mask (Pbgra32) to the given Bgra Mat (straight alpha expected).
        private void ApplyAlphaMaskToMat(Mat bgra, byte[] mask, int w, int h)
        {
            int step = (int)bgra.Step();
            var data = new byte[step * h];
            Marshal.Copy(bgra.Data, data, 0, data.Length);

            int mStride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int rowD = y * step;
                int rowM = y * mStride;
                for (int x = 0; x < w; x++)
                {
                    int di = rowD + x * 4;
                    int mi = rowM + x * 4;
                    byte a = mask[mi + 3];
                    data[di + 3] = a;          // set alpha
                    if (a == 0)
                    {
                        // optional: zero color outside selection (keeps the preview tidy)
                        data[di + 0] = 0;
                        data[di + 1] = 0;
                        data[di + 2] = 0;
                    }
                }
            }

            Marshal.Copy(data, 0, bgra.Data, data.Length);
        }

        // Uses the selection mask to set ONLY the selected pixels in the doc to white (opaque).
        private void ClearDocRegionWithMaskToWhite(int x, int y, int w, int h, byte[] mask)
        {
            int step = (int)Doc.Image.Step();
            var dst = new byte[step * Doc.Height];
            Marshal.Copy(Doc.Image.Data, dst, 0, dst.Length);

            int mStride = w * 4;

            for (int yy = 0; yy < h; yy++)
            {
                int iy = y + yy; if (iy < 0 || iy >= Doc.Height) continue;
                int rowD = iy * step;
                int rowM = yy * mStride;

                for (int xx = 0; xx < w; xx++)
                {
                    int ix = x + xx; if (ix < 0 || ix >= Doc.Width) continue;

                    int di = rowD + ix * 4;
                    int mi = rowM + xx * 4;
                    byte a = mask[mi + 3];
                    if (a == 0) continue;

                    // Clear selected pixel to opaque white
                    dst[di + 0] = 255;
                    dst[di + 1] = 255;
                    dst[di + 2] = 255;
                    dst[di + 3] = 255;
                }
            }

            Marshal.Copy(dst, 0, Doc.Image.Data, dst.Length);
        }

        private void TranslateSelectionGeometry(double dx, double dy)
        {
            if (_selectionClip == null) return;

            // Clone to detach & apply translation
            var newClip = _selectionClip.Clone();
            var tg = new TransformGroup();
            if (newClip.Transform != null) tg.Children.Add(newClip.Transform);
            tg.Children.Add(new TranslateTransform(dx, dy));
            newClip.Transform = tg;
            newClip.Freeze();
            _selectionClip = newClip;

            // Update edge as well
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

            // Keep InkCanvas clipping in sync
            _paint.Clip = _selectionClip;

            // Mask now outdated
            MarkSelectionDirty();

            // Update visuals and bounding rectangle if present
            if (_activeSelectionRect is WRect rr && !rr.IsEmpty)
            {
                var moved = new WRect(rr.X + dx, rr.Y + dy, rr.Width, rr.Height);
                _activeSelectionRect = moved;
                ShowRect(moved); // updates rectBox and status
            }

            if (_lassoPts.Count > 0 && _lassoLine != null && _lassoLine.Visibility == Visibility.Visible)
            {
                for (int i = 0; i < _lassoPts.Count; i++)
                    _lassoPts[i] = new WPoint(_lassoPts[i].X + dx, _lassoPts[i].Y + dy);
                ShowLasso(); // updates _activeSelectionRect
            }

            if (_polyPts.Count > 0 && _polyLine != null && _polyLine.Visibility == Visibility.Visible)
            {
                for (int i = 0; i < _polyPts.Count; i++)
                    _polyPts[i] = new WPoint(_polyPts[i].X + dx, _polyPts[i].Y + dy);
                ShowPolygon(); // updates _activeSelectionRect
            }
        }

        /* ---------------- low-level helpers ---------------- */

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
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra.Data, stride * h, stride);
            bmp.Freeze();
            if (!ReferenceEquals(bgra, m)) bgra.Dispose();
            return bmp;
        }

        private static Mat BitmapSourceToMatBGRA(BitmapSource bmp)
        {
            var fmt = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
            var data = new byte[stride * h];
            fmt.CopyPixels(data, stride, 0);
            var mat = new Mat(h, w, MatType.CV_8UC4);
            Marshal.Copy(data, 0, mat.Data, data.Length);
            return mat;
        }

        private void AlphaBlendOver(Mat src, int x, int y)
        {
            if (src.Empty()) return;

            int dstW = Doc.Width, dstH = Doc.Height;

            int sx = 0, sy = 0, dx = x, dy = y;
            if (dx < 0) { sx = -dx; dx = 0; }
            if (dy < 0) { sy = -dy; dy = 0; }

            int maxW = Math.Min(src.Width  - sx, dstW - dx);
            int maxH = Math.Min(src.Height - sy, dstH - dy);
            if (maxW <= 0 || maxH <= 0) return;

            int srcStep = (int)src.Step();
            int dstStep = (int)Mat.Step();
            var dstData = new byte[dstStep * dstH];
            Marshal.Copy(Mat.Data, dstData, 0, dstData.Length);

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

                    double a = sa / 255.0;
                    dstData[di + 0] = (byte)Math.Clamp(sb * a + dstData[di + 0] * (1.0 - a), 0, 255); // B
                    dstData[di + 1] = (byte)Math.Clamp(sg * a + dstData[di + 1] * (1.0 - a), 0, 255); // G
                    dstData[di + 2] = (byte)Math.Clamp(sr * a + dstData[di + 2] * (1.0 - a), 0, 255); // R
                    dstData[di + 3] = 255; // keep opaque
                }
            }

            Marshal.Copy(dstData, 0, Mat.Data, dstData.Length);
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

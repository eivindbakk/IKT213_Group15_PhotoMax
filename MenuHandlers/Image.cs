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
using WPoint = System.Windows.Point;
using WRect = System.Windows.Rect;
using OcvSize = OpenCvSharp.Size;
using OcvRect = OpenCvSharp.Rect;
using WpfPoint = System.Windows.Point;
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
        {
            // **FIX: Disarm shapes tool first**
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
            // **FIX: Disarm shapes tool first**
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
            // **FIX: Disarm shapes tool first**
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
        public Mat Image { get; private set; } // CV_8UC4 (BGRA)
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

            // **FIX: Copy to managed array to bypass color management**
            var pixels = new byte[stride * h];
            Marshal.Copy(src.Data, pixels, 0, pixels.Length);

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();

            if (!ReferenceEquals(src, Image)) src.Dispose();
            return bmp;
        }

        public void FromBitmapSource(BitmapSource bmp)
        {
            var fmt = bmp.Format == PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
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
                Math.Clamp(width, 1, Width - Math.Clamp(x, 0, Width - 1)),
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
        private Ellipse? _polyStartMarker;

        public ImageDoc Doc { get; } = new ImageDoc();

        // --- LAYERS integration ---
        private readonly LayerStack _layers = new LayerStack();

        public event Action? SelectionCreated;
        public int Layers_GetActiveIndex() => _layers.ActiveIndex;

        // Active bitmap target for all pixel-writing tools (Brush/Shapes/Text/Paste)
        public OpenCvSharp.Mat Mat => _layers.ActiveMat;

        // Callback for saving undo state (set by MainWindow)
        public Action<string>? SaveUndoStateCallback { get; set; }

        // Lightweight layer API for MainWindow menu
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

        // Notify host (MainWindow) when the underlying bitmap changed (so it can update brush policy).
        public event Action? ImageChanged;

        // --- selection mode (temporary while creating) ---
        private enum SelMode
        {
            None,
            Rect,
            Lasso,
            Polygon,
            CropInteractive
        }

        private SelMode _mode = SelMode.None;

        // --- active selection (persistent while painting) ---
        private Geometry? _selectionClip; // paints are clipped to this when not null
        private Geometry? _selectionEdge; // 1px widened ring of selection for edge painting
        private bool _autoDeselectOnClickOutside = false; // sticky selection behavior

        // Public exposure of selection state for Tools.cs
        public Geometry? SelectionFill => _selectionClip;

        public Geometry? SelectionEdge => _selectionEdge;

        // In ImageController class, update the property:
        public bool HasActiveSelection => _selectionClip != null || _lassoPts.Count > 0 || _polyPts.Count > 0 ||
                                          _mode != SelMode.None;

        // ===== FAST SELECTION MASK (for lag-free painting) =====
        private byte[]? _selMask; // 1 byte per pixel: 255 = inside, 0 = outside
        private int _selMaskW, _selMaskH;
        private bool _selMaskDirty = true;

        /// <summary>Call once per stroke (or whenever selection changed) to (re)build the mask.</summary>
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

            _selMaskW = w;
            _selMaskH = h;
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

        // ---- floating paste (drag after paste, click elsewhere to place) ----
        private Mat? _floatingMat; // BGRA content being moved
        private System.Windows.Controls.Image? _floatingView; // visual for the floating content
        private Rectangle? _floatingOutline; // subtle outline while hover/drag
        private WPoint _floatPos; // top-left of floating content
        private WPoint _floatStart; // sticky: remember origin of floating
        private bool _floatDragging;
        private bool _floatHovering;
        private Vector _floatGrabDelta; // mouse offset at drag start
        private bool _floatingActive;
        private bool _floatingFromMove; // true if created via MoveSelection

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
            HookSelectionKeys(); // Ctrl+D and sticky Enter finalize; click-outside disabled for sticky
            HookCropKeys();
            HookFloatingPasteEvents();
            HookFloatingKeys();
            HookClipboardKeys(); // Ctrl+C / X / V
            EnsureLayers();
            HookFloatingKeys(); // Enter = place & clear; Esc = cancel; Arrows = nudge
            HookClipboardKeys(); // Ctrl+C / X / V
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
            _status.Content =
                "Polygon: click to add points, click near start to close, Enter/Right-click/Double-click to finish, Esc to cancel.";
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
        // Replace the BeginMoveSelectedArea method with this fixed version:

        public void BeginMoveSelectedArea()
        {
            if (_selectionClip == null)
            {
                MessageBox.Show("Select an area first.");
                return;
            }

            // Keep selection active; we just move its contents.
            BakeStrokesToImage();

            // **FIX: Use exact geometry bounds with proper rounding**
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

            // **FIX: Build mask for EXACT selection (not rectangular)**
            var mask = BuildSelectionMaskBytes_Binary(left, top, w, h, 128);

            var clone = new Mat(h, w, MatType.CV_8UC4);
            int srcStep = (int)Mat.Step();
            int dstStep = (int)clone.Step();
            int mStride = w * 4;

            var srcData = new byte[srcStep * Mat.Height];
            Marshal.Copy(Mat.Data, srcData, 0, srcData.Length);

            var dstData = new byte[dstStep * h];

            // Copy pixels and apply mask in one pass
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

                    byte maskAlpha = mask[mi + 3]; // 0 or 255

                    if (maskAlpha == 255)
                    {
                        // Inside selection - copy pixel as-is
                        dstData[di + 0] = srcData[si + 0]; // B
                        dstData[di + 1] = srcData[si + 1]; // G
                        dstData[di + 2] = srcData[si + 2]; // R
                        dstData[di + 3] = srcData[si + 3]; // A
                    }
                    else
                    {
                        // **CRITICAL FIX: Outside selection - transparent WHITE (not black)**
                        dstData[di + 0] = 255; // B
                        dstData[di + 1] = 255; // G
                        dstData[di + 2] = 255; // R
                        dstData[di + 3] = 0; // A = 0 (transparent)
                    }
                }
            }

            Marshal.Copy(dstData, 0, clone.Data, dstData.Length);

            // Clear ONLY selected pixels in the ACTIVE LAYER (Mat) to TRANSPARENT
            ClearMatRegionWithMaskToTransparent_Binary(Mat, left, top, w, h, mask);

            _floatingFromMove = true;

            // Start floating paste with masked sprite
            BeginFloatingPaste(clone, left, top);

            clone.Dispose(); // BeginFloatingPaste makes its own copy

            // Refresh immediately so the cleared area shows
            RefreshView();

            // Set cursor to move cursor
            _paint.Cursor = Cursors.SizeAll;
            _paint.EditingMode = InkCanvasEditingMode.None;
            _paint.IsHitTestVisible = false;

            _status.Content = "Move: drag to reposition, Enter to place (selection stays), Esc to cancel.";
        }

        private void HideSelectionVisuals()
        {
            if (_rectBox != null) _rectBox.Visibility = Visibility.Collapsed;
            if (_lassoLine != null) _lassoLine.Visibility = Visibility.Collapsed;
            if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
        }

        private void ClearMatRegionWithMaskToTransparent_Binary(Mat targetMat, int x, int y, int w, int h, byte[] mask)
        {
            if (targetMat == null || targetMat.Empty()) return;

            int step = (int)targetMat.Step();
            var dst = new byte[step * targetMat.Height];
            Marshal.Copy(targetMat.Data, dst, 0, dst.Length);

            int mStride = w * 4;

            // **FIX: Check if target is the background layer (first layer with index 0)**
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
                    byte a = mask[mi + 3]; // 0 or 255

                    if (a == 0) continue; // do not clear outside selection

                    // **FIX: Background layer clears to WHITE, other layers to TRANSPARENT**
                    if (isBackgroundLayer)
                    {
                        // Background layer: opaque white (like Photoshop/GIMP)
                        dst[di + 0] = 255; // B
                        dst[di + 1] = 255; // G
                        dst[di + 2] = 255; // R
                        dst[di + 3] = 255; // A = 255 (opaque)
                    }
                    else
                    {
                        // Other layers: fully transparent
                        dst[di + 0] = 0; // B
                        dst[di + 1] = 0; // G
                        dst[di + 2] = 0; // R
                        dst[di + 3] = 0; // A = 0 (transparent)
                    }
                }
            }

            Marshal.Copy(dst, 0, targetMat.Data, dst.Length);
        }

        /// <summary>Finalize (place floating if any) and clear the selection — bound to Enter.</summary>
        /// <summary>Finalize (place floating if any) and clear the selection — bound to Enter.</summary>
        private void FinalizeSelection()
        {
            if (_floatingActive) CommitFloatingPaste(); // now keeps the selection; we'll clear next

            // **FIX: Properly clear ALL selection state**
            _selectionClip = null;
            _selectionEdge = null;
            _paint.Clip = null;
            MarkSelectionDirty();

            // Clear visual elements
            if (_rectBox != null) _rectBox.Visibility = Visibility.Collapsed;
            if (_lassoLine != null) _lassoLine.Visibility = Visibility.Collapsed;
            if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
            if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;

            // Clear point lists
            _lassoPts.Clear();
            _polyPts.Clear();
            _activeSelectionRect = null;

            _status.Content = "Selection finalized and cleared.";
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

            _artboard.Width = Doc.Width;
            _artboard.Height = Doc.Height;

            _imageView.Width = Doc.Width;
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
                StrokeThickness = 2.5, // **FIX: Thicker border (was 1.5)**
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
                StrokeThickness = 2.5, // **FIX: Thicker border (was 1.5)**
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

            // Prefer original shape visuals if available
            if (_lassoPts.Count > 0 && _lassoLine != null)
            {
                ShowLasso(); // This will hide the rect box
            }
            else if (_polyPts.Count > 0 && _polyLine != null)
            {
                ShowPolygon(); // This will hide the rect box
            }
            else if (_activeSelectionRect is WRect rr && rr.Width >= 1 && rr.Height >= 1)
            {
                // Only show rect box if it's actually a rectangle selection
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

            // **FIX: Thicker border (was 2.5)**
            _lassoLine.StrokeThickness = 3.0;

            // **FIX: Hide the rectangle box for lasso selections**
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

            // **FIX: Thicker border (was 2.5)**
            _polyLine.StrokeThickness = 3.0;

            // **FIX: Show green circle at start point when you have 3+ points**
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

            // **FIX: Hide the rectangle box for polygon selections**
            HideRectBox();

            var r = BoundsOf(_polyPts);
            _activeSelectionRect = r;
            _status.Content = $"Polygon: {r.X:0},{r.Y:0}  {r.Width:0}×{r.Height:0} ({_polyPts.Count} points)";
        }

        /* ---------------- selection lifecycle ---------------- */

        private void BeginSelectionMode(SelMode mode)
        {
            EndSelectionMode(); // clear any prior selection + clip
            _mode = mode;

            // **FIX: Disable painting AND shapes tool**
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
        // Finalize a created selection and immediately return to painting, clipped to the selection.
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

            // **NEW: Notify that selection was created**
            SelectionCreated?.Invoke();

            _status.Content = "Selection created — use Move tool to reposition, or paint inside (Enter to finalize).";
        }

        private void DeselectSelection()
        {
            _selectionClip = null;
            _selectionEdge = null;
            _paint.Clip = null;

            // selection mask invalidated
            MarkSelectionDirty();

            // **FIX: Clear ALL visual elements**
            if (_rectBox != null) _rectBox.Visibility = Visibility.Collapsed;
            if (_lassoLine != null) _lassoLine.Visibility = Visibility.Collapsed;
            if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
            if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;

            // **FIX: Clear point lists**
            _lassoPts.Clear();
            _polyPts.Clear();
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
            if (w < 1 || h < 1)
            {
                _paint.Strokes.Clear();
                return;
            }

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
            if (_floatingActive) CommitFloatingPaste();
            BakeStrokesToImage();

            if (_selectionClip == null)
            {
                using var entireImage = Mat.Clone();
                CopyMatToClipboardAsPNG(entireImage);
                _status.Content = $"Copied entire image {Mat.Width}×{Mat.Height}";
                return;
            }

            // Extract with mask (same as before)
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
                        // **FIX: PRESERVE original alpha!**
                        dstData[di + 0] = srcData[si + 0]; // B
                        dstData[di + 1] = srcData[si + 1]; // G
                        dstData[di + 2] = srcData[si + 2]; // R
                        dstData[di + 3] = srcData[si + 3]; // A - KEEP ORIGINAL!
                    }
                    else
                    {
                        // Outside selection - transparent white
                        dstData[di + 0] = 255;
                        dstData[di + 1] = 255;
                        dstData[di + 2] = 255;
                        dstData[di + 3] = 0;
                    }
                }
            }

            Marshal.Copy(dstData, 0, extractedMat.Data, dstData.Length);
            ConvertTransparentBlackToWhite(extractedMat);

            // **FIX: Copy as PNG to preserve transparency**
            CopyMatToClipboardAsPNG(extractedMat);
            extractedMat.Dispose();

            _status.Content = $"Copied {w}×{h}";
        }

        private void CopyRectRegionToClipboard(WRect r, bool applyMask, byte[]? mask)
        {
            int x = (int)Math.Floor(r.X);
            int y = (int)Math.Floor(r.Y);
            int w = (int)Math.Ceiling(r.Width);
            int h = (int)Math.Ceiling(r.Height);

            // Clamp to image bounds
            x = Math.Clamp(x, 0, Math.Max(0, Doc.Image.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, Doc.Image.Height - 1));
            w = Math.Clamp(w, 1, Doc.Image.Width - x);
            h = Math.Clamp(h, 1, Doc.Image.Height - y);

            if (w <= 0 || h <= 0)
            {
                MessageBox.Show("Nothing to copy.");
                return;
            }

            using var roi = new Mat(Doc.Image, new OcvRect(x, y, w, h));
            using var clone = roi.Clone();

            // **FIX: Apply mask if needed (for lasso/polygon selections)**
            if (applyMask && mask != null)
            {
                ApplyAlphaMaskToMat_Binary(clone, mask, w, h);
            }

            var bmp = MatToBitmapSourceBGRA(clone);
            var pbgra = new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
            pbgra.Freeze();
            Clipboard.SetImage(pbgra);
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

            // Same extraction logic as Copy
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

            // **FIX: Create PNG with explicit linear color profile to prevent gamma shift**
            var encoder = new PngBitmapEncoder();

            // Create a new bitmap with NO color profile (raw pixel values)
            var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);

            var rawBitmap = BitmapSource.Create(
                bmp.PixelWidth,
                bmp.PixelHeight,
                96, 96,
                PixelFormats.Bgra32,
                null, // NO color palette - this is critical
                pixels,
                bmp.PixelWidth * 4);
            rawBitmap.Freeze();

            encoder.Frames.Add(BitmapFrame.Create(rawBitmap));

            using (var ms = new System.IO.MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;

                var dataObject = new DataObject();

                // Copy the stream bytes to prevent disposal issues
                var pngBytes = ms.ToArray();
                dataObject.SetData("PNG", new System.IO.MemoryStream(pngBytes), false);

                // Also set as regular bitmap
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

            // Try PNG first
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

                            // **FIX: Convert transparent black to transparent white IMMEDIATELY**
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

            // Fallback
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

                // **FIX: Convert transparent black to transparent white here too**
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

        /* ---------------- floating paste implementation ---------------- */

        private void BeginFloatingPaste(Mat sourceBGRA, int x, int y)
        {
            // If a previous floating paste exists, place it first for predictability
            if (_floatingActive) CommitFloatingPaste();

            _floatingMat = sourceBGRA.Clone(); // keep our own copy

            // **FIX: Convert transparent black to transparent white to avoid black background**
            ConvertTransparentBlackToWhite(_floatingMat);

            _floatPos = new WpfPoint(x, y);
            _floatStart = _floatPos; // remember origin for sticky translate
            ClampFloatingToBounds();

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

            // **FIX: ALWAYS create outline, even for move operations!**
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

            // Disable painting so dragging is smooth and Artboard gets the mouse
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

            // Blend to the ACTIVE LAYER (Mat), not Doc.Image
            AlphaBlendOverToMat(Mat, _floatingMat, (int)Math.Round(_floatPos.X), (int)Math.Round(_floatPos.Y));

            // If this floating came from MoveSelection, translate selection geometry & visuals
            if (_floatingFromMove && _selectionClip != null)
            {
                var dx = _floatPos.X - _floatStart.X;
                var dy = _floatPos.Y - _floatStart.Y;
                TranslateSelectionGeometry(dx, dy);

                // **FIX: Show the correct selection visuals (lasso, not rect)**
                ShowSelectionVisuals();

                // **RE-ENABLE PAINTING CLIPPED TO SELECTION**
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
                // **FIX: Regular paste doesn't create a selection - just place it**
                _status.Content = "Pasted.";
            }

            RemoveFloatingView();
            RefreshView();
        }

        private void CancelFloatingPaste()
        {
            if (!_floatingActive) return;

            // If this was a move operation, we need to restore the original pixels
            if (_floatingFromMove && _floatingMat != null)
            {
                // Restore the pixels at the ORIGINAL location (before move started)
                AlphaBlendOverToMat(Mat, _floatingMat, (int)Math.Round(_floatStart.X), (int)Math.Round(_floatStart.Y));

                // Show the selection visuals back at original location
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

            // Re-enable painting (respect clip if there was one)
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

            // **FIX: Update lasso/polygon visuals in real-time during move**
            if (_floatingFromMove && _selectionClip != null)
            {
                var dx = _floatPos.X - _floatStart.X;
                var dy = _floatPos.Y - _floatStart.Y;
                UpdateSelectionVisualsForMove(dx, dy);
            }
        }

        private void UpdateSelectionVisualsForMove(double dx, double dy)
        {
            // Update lasso visual
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

            // Update polygon visual
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

            // Always hide the rectangle box during move
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

            // **FIX: Only update outline if it exists (won't exist for move operations)**
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

            // **NEW: Change appearance based on hover/drag state**
            if (_floatDragging)
            {
                // Dragging: thick solid line (bright cyan)
                _floatingOutline.StrokeThickness = 4.0;
                _floatingOutline.StrokeDashArray = null; // Solid
                _floatingOutline.Stroke = Brushes.Cyan;
            }
            else if (_floatHovering || forceVisible)
            {
                // Hovering: thick dashed line (yellow)
                _floatingOutline.StrokeThickness = 3.5;
                _floatingOutline.StrokeDashArray = new DoubleCollection { 4, 2 };
                _floatingOutline.Stroke = Brushes.Yellow;
            }
            else
            {
                // Idle: medium dashed line (blue)
                _floatingOutline.StrokeThickness = 2.5;
                _floatingOutline.StrokeDashArray = new DoubleCollection { 6, 3 };
                _floatingOutline.Stroke = Brushes.DodgerBlue;
            }
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
                    _floatHovering = true; // **ADD THIS**
                    _floatGrabDelta = (Vector)(pos - _floatPos);
                    _artboard.CaptureMouse();

                    // **FIX: Make border thicker when actively dragging**
                    if (_floatingFromMove)
                    {
                        SetSelectionVisualThickness(3.5);
                    }

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
                    // **FIX: Update hover state and cursor**
                    bool wasHovering = _floatHovering;
                    UpdateFloatingCursor(pos);

                    // **Update outline when hover state changes**
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

                    // **FIX: Return to normal thickness when done dragging**
                    if (_floatingFromMove)
                    {
                        SetSelectionVisualThickness(2.5);
                    }

                    // **FIX: Update outline based on current hover state**
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

                // Enter finalizes selection (place floating if any, then clear)
                if (e.Key == Key.Enter)
                {
                    // If actively drawing polygon, finish it
                    if (_mode == SelMode.Polygon && _polyActive && _polyPts.Count >= 3)
                    {
                        FinishPolygonSelection();
                        e.Handled = true;
                        return;
                    }

                    // If there's any selection (active or sticky), finalize and clear it
                    if (_selectionClip != null || _floatingActive)
                    {
                        FinalizeSelection();
                        e.Handled = true;
                        return;
                    }
                }

                // **FIX: Escape cancels polygon drawing**
                if (e.Key == Key.Escape && _mode == SelMode.Polygon && _polyActive)
                {
                    _polyActive = false;
                    _polyPts.Clear();
                    if (_polyLine != null) _polyLine.Visibility = Visibility.Collapsed;
                    if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed; // **ADD THIS**
                    EndSelectionMode();
                    _status.Content = "Polygon cancelled.";
                    e.Handled = true;
                    return;
                }

                // Enter finalizes selection (place floating if any, then clear)
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
                if (_floatingActive) return; // floating handler owns clicks
                if (_mode != SelMode.None) return; // currently creating a selection
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

                        // **FIX: Auto-close if clicking near the start point**
                        if (_polyPts.Count >= 3)
                        {
                            var start = _polyPts[0];
                            var dist = Math.Sqrt(Math.Pow(pos.X - start.X, 2) + Math.Pow(pos.Y - start.Y, 2));

                            // If within 10 pixels of start, close the polygon
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

                        // finalize: clip to rectangle & return to painting
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
                // **FIX: Double-click to finish polygon**
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

            // **FIX: Hide the start marker**
            if (_polyStartMarker != null) _polyStartMarker.Visibility = Visibility.Collapsed;

            if (_polyPts.Count > 2)
            {
                // Close the polygon by adding first point if not already closed
                if (_polyPts[0] != _polyPts[^1])
                    _polyPts.Add(_polyPts[0]);

                ShowPolygon();

                // Create geometry and activate selection
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

        // Convert transparent-black pixels to transparent-white to avoid black fringing
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
                        // Fully transparent - set RGB to white to avoid black fringing
                        data[i + 0] = 255; // B
                        data[i + 1] = 255; // G
                        data[i + 2] = 255; // R
                        // alpha stays 0
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

        /* ---------------- helpers for sticky selection translation ---------------- */
        // Builds a Pbgra32 mask for the current _selectionClip inside the ROI at (x,y,w,h).
        // Image.cs (inside ImageController)
        // Find the BuildSelectionMaskBytes_Binary method and replace it with this corrected version:

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
                // **FIX: Use proper background and antialiasing**
                dc.DrawRectangle(Brushes.Black, null, new WRect(0, 0, w, h));

                // Translate and draw selection in white
                dc.PushTransform(new TranslateTransform(-x, -y));
                dc.DrawGeometry(Brushes.White, null, _selectionClip);
                dc.Pop();
            }

            rtb.Render(dv);

            int stride = w * 4;
            var pixels = new byte[h * stride];
            rtb.CopyPixels(pixels, stride, 0);

            // **FIX: Use proper thresholding based on luminance AND alpha**
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i + 0];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];

                // Check both color luminance AND alpha
                int luminance = (r + g + b) / 3;
                bool inside = (luminance > threshold) && (a > threshold);

                pixels[i + 0] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = inside ? (byte)255 : (byte)0;
            }

            return pixels;
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
                    byte maskAlpha = mask[mi + 3]; // 0 or 255

                    if (maskAlpha == 0)
                    {
                        // **FIX: Outside selection - transparent WHITE (prevents black fringing)**
                        data[di + 0] = 255; // B = white
                        data[di + 1] = 255; // G = white
                        data[di + 2] = 255; // R = white
                        data[di + 3] = 0; // A = 0 (transparent)
                    }
                    else
                    {
                        // Inside selection - keep original pixel with full opacity
                        data[di + 3] = 255;
                    }
                }
            }

            Marshal.Copy(data, 0, bgra.Data, data.Length);
        }

        private void ClearDocRegionWithMaskToWhite_Binary(int x, int y, int w, int h, byte[] mask)
        {
            // Forward to the Mat version using the active layer
            ClearMatRegionWithMaskToTransparent_Binary(Mat, x, y, w, h, mask);
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
                    data[di + 3] = a; // set alpha
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
            if (Mat == null || Mat.Empty()) return;

            int step = (int)Mat.Step();
            var dst = new byte[step * Mat.Height];
            Marshal.Copy(Mat.Data, dst, 0, dst.Length);

            int mStride = w * 4;

            for (int yy = 0; yy < h; yy++)
            {
                int iy = y + yy;
                if (iy < 0 || iy >= Mat.Height) continue;

                int rowD = iy * step;
                int rowM = yy * mStride;

                for (int xx = 0; xx < w; xx++)
                {
                    int ix = x + xx;
                    if (ix < 0 || ix >= Mat.Width) continue;

                    int di = rowD + ix * 4;
                    int mi = rowM + xx * 4;
                    byte a = mask[mi + 3];
                    if (a == 0) continue;

                    // Clear to TRANSPARENT (alpha = 0)
                    dst[di + 0] = 0; // B
                    dst[di + 1] = 0; // G
                    dst[di + 2] = 0; // R
                    dst[di + 3] = 0; // A = 0 (fully transparent)
                }
            }

            Marshal.Copy(dst, 0, Mat.Data, dst.Length);
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

            // **FIX: Update the underlying point lists for lasso/polygon**
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

            // Update bounding rectangle if present
            if (_activeSelectionRect is WRect rr && !rr.IsEmpty)
            {
                var moved = new WRect(rr.X + dx, rr.Y + dy, rr.Width, rr.Height);
                _activeSelectionRect = moved;
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

            // **FIX: Copy pixels to managed array first to avoid color space issues**
            var pixels = new byte[stride * h];
            Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);

            // Create bitmap from managed array with NO color profile
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();

            if (!ReferenceEquals(bgra, m)) bgra.Dispose();
            return bmp;
        }

        private static Mat BitmapSourceToMatBGRA(BitmapSource bmp)
        {
            // **FIX: Copy pixels through managed array to avoid gamma issues**
            var fmt = bmp.Format == PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

            int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
            var data = new byte[stride * h];

            // Force raw pixel copy without color management
            fmt.CopyPixels(data, stride, 0);

            // **FIX: Convert transparent black pixels to transparent white**
            for (int i = 0; i < data.Length; i += 4)
            {
                byte a = data[i + 3];
                if (a == 0)
                {
                    // Fully transparent - set RGB to white to prevent black fringing
                    data[i + 0] = 255; // B
                    data[i + 1] = 255; // G
                    data[i + 2] = 255; // R
                }
            }

            var mat = new Mat(h, w, MatType.CV_8UC4);
            Marshal.Copy(data, 0, mat.Data, data.Length);
            return mat;
        }

        private void AlphaBlendOver(Mat src, int x, int y)
        {
            AlphaBlendOverToMat(Mat, src, x, y);
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

                    if (sa == 0) continue; // **FIX: Skip fully transparent pixels**

                    if (sa == 255)
                    {
                        // Fully opaque - replace
                        dstData[di + 0] = sb;
                        dstData[di + 1] = sg;
                        dstData[di + 2] = sr;
                        dstData[di + 3] = srcData[si + 3]; // PRESERVE ALPHA!
                    }
                    else
                    {
                        // **FIX: Proper alpha blending for semi-transparent**
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

    /// <summary>
    /// Minimal code-only resize dialog with modern dark theme.
    /// </summary>
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

            // Modern dark theme
            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 243, 243));

            _origW = currentWidth;
            _origH = currentHeight;

            var mainGrid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Current size
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Width input
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Height input
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Lock checkbox
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Spacer
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

            // Header
            var header = new TextBlock
            {
                Text = "RESIZE IMAGE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(header, 0);

            // Current size info
            var currentSizeText = new TextBlock
            {
                Text = $"Current size: {currentWidth} × {currentHeight} pixels",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(currentSizeText, 1);

            // Width input
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

            // Height input
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

            // Lock aspect ratio checkbox
            _lockAspect.Foreground = Foreground;
            _lockAspect.Margin = new Thickness(0, 0, 0, 0);
            Grid.SetRow(_lockAspect, 4);

            // Buttons
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

            // Add all to grid
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

            // Focus the width box on open
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
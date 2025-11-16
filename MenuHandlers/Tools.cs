// File: MenuHandlers/Tools.cs

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenCvSharp;
using Microsoft.VisualBasic;
using WF = System.Windows.Forms;

// ---- Aliases to avoid type clashes ----
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;

namespace PhotoMax
{
    public partial class MainWindow
    {
        // ===== Live blocky drawing state (strict bitmap overlay) =====
        private Image? _liveImage; // sits above InkCanvas
        private WriteableBitmap? _liveWB; // BGRA32, same size as Doc
        private byte[]? _liveBuf; // backing buffer for _liveWB
        private bool _isDrawing = false;
        private readonly List<WpfPoint> _snappedPoints = new();

        // Cursor preview (zoom-aware outline of true stamp footprint)
        private Rectangle? _brushPreview; // outline showing true brush footprint
        private bool _liveHooks = false;

        // ----------------- Text tool (interactive) -----------------
        private enum TextPhase
        {
            None,
            Arming,
            Dragging,
            Editing
        }

        private TextPhase _textPhase = TextPhase.None;
        private bool _textToolArmed = false;

        private Rectangle? _textDragRect; // rubber-band while dragging
        private Border? _textBorder; // visual box
        private TextBox? _textEditor; // live editor (wraps)
        private readonly List<Rectangle> _textHandles = new(); // 4 corner handles
        private double _textFontPx = 24; // default font size (px)
        private WpfPoint _textDragStart; // start of box (image px)
        private WpfPoint _moveStart; // move/resize helpers
        private WpfRect _boxStart; // box snapshot at begin drag
        private bool _isMoving = false;
        private bool _isResizing = false;
        private string _resizeCorner = ""; // "NW","NE","SW","SE"

        // ----------------- ZOOM (menu handlers) -----------------
        private void Zoom_In_Click(object sender, RoutedEventArgs e)
        {
            double next = _zoom * ZoomStep;
            if (AtOrBelowOne(next))
            {
                ApplyZoom_NoScroll(next);
            }
            else
            {
                var m = new WpfPoint(Scroller.ViewportWidth / 2.0, Scroller.ViewportHeight / 2.0);
                var p = ViewportPointToWorkspaceBeforeZoom(m);
                SetZoomToCursor(next, m, p);
            }
        }

        private void Tool_MoveTool_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = false;
            Shapes_Disarm(); // ← disarm shapes when moving
            Text_Disarm();
            UpdateBrushPreviewVisibility(false);
            PaintCanvas.Cursor = Cursors.SizeAll;

            _img?.BeginMoveSelectedArea();
            StatusText.Content =
                "Move tool: drag the selected area; press Enter to bake. Switch back to Brush to paint inside.";
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
                var m = new WpfPoint(Scroller.ViewportWidth / 2.0, Scroller.ViewportHeight / 2.0);
                var p = ViewportPointToWorkspaceBeforeZoom(m);
                SetZoomToCursor(next, m, p);
            }
        }

        private void Zoom_100_Click(object? sender, RoutedEventArgs e) => SetZoomCentered(1.0);

        private void Zoom_Fit_Click(object? sender, RoutedEventArgs e)
        {
            var margin = 16.0;
            var vw = System.Math.Max(1, Scroller.ViewportWidth - margin);
            var vh = System.Math.Max(1, Scroller.ViewportHeight - margin);

            var cw = System.Math.Max(1, Artboard.ActualWidth);
            var ch = System.Math.Max(1, Artboard.ActualHeight);

            var fit = System.Math.Max(MinZoom, System.Math.Min(vw / cw, vh / ch));
            SetZoomCentered(fit);
        }

        // ----------------- BRUSH CORE -----------------
        private int ComputeEffectiveBrushSizePx()
        {
            int imgMin = 0;
            if (_img != null && _img.Doc != null)
                imgMin = System.Math.Max(1, System.Math.Min(_img.Doc.Width, _img.Doc.Height));

            int baseSize = _brushSizes[_brushIndex]; // <- from MainWindow.xaml.cs
            int minPx = imgMin > 0 ? System.Math.Max(1, imgMin / 24) : 1;
            int eff = System.Math.Max(baseSize, minPx);
            return System.Math.Clamp(eff, 1, 256);
        }

        private static (int cx, int cy) ClampCenterToFit(int cx, int cy, int eff, int w, int h)
        {
            int half = eff / 2;
            int minCenX = half;
            int minCenY = half;
            int maxCenX = System.Math.Max(half, w - eff + half);
            int maxCenY = System.Math.Max(half, h - eff + half);
            cx = System.Math.Clamp(cx, minCenX, maxCenX);
            cy = System.Math.Clamp(cy, minCenY, maxCenY);
            return (cx, cy);
        }

        // === Selection-aware helpers for painting ===
        private bool HasSelectionClip()
            => _img != null && _img.SelectionFill != null;

        // Tools.cs
        private bool PixelIsInsideSelection(int x, int y)
        {
            // Falls back to true if no selection or _img not ready
            return _img?.PixelFullyInsideSelection(x, y) ?? true;
        }


        internal void ApplyInkBrushAttributes()
        {
            // In brush mode we hide system cursor and draw our own outline
            PaintCanvas.EditingMode = InkCanvasEditingMode.None;
            if (!_textToolArmed) PaintCanvas.Cursor = Cursors.None;

            var da = PaintCanvas.DefaultDrawingAttributes;
            da.IsHighlighter = false;
            da.IgnorePressure = true;
            da.FitToCurve = false;
            da.StylusTip = StylusTip.Rectangle;
            da.StylusTipTransform = Matrix.Identity;
            da.Color = _brushColor; // <- from MainWindow.xaml.cs
            da.Width = 1;
            da.Height = 1;

            RenderOptions.SetBitmapScalingMode(PaintCanvas, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(PaintCanvas, EdgeMode.Aliased);
            PaintCanvas.SnapsToDevicePixels = true;

            EnsureLiveOverlayBitmap();
            HookLiveDrawingEvents();

            EnsureBrushPreview();
            UpdateBrushPreviewVisibility(!_textToolArmed);
            SyncBrushPreviewStrokeToZoom();
            SyncBrushPreviewColor();

            int eff = ComputeEffectiveBrushSizePx();
            StatusText.Content = _eraseMode // <- from MainWindow.xaml.cs
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
                    _liveImage.Width = w;
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

            // Brush live drawing (already uses Preview*)
            PaintCanvas.PreviewMouseDown += PaintCanvas_PreviewMouseDown_BeginDraw;
            PaintCanvas.PreviewMouseMove += PaintCanvas_PreviewMouseMove_Draw;
            PaintCanvas.PreviewMouseUp += PaintCanvas_PreviewMouseUp_EndDraw;

            // Text tool — use Preview* so TextBox can't swallow the events
            PaintCanvas.PreviewMouseLeftButtonDown += Text_OnCanvasMouseDown;
            PaintCanvas.PreviewMouseMove += Text_OnCanvasMouseMove;
            PaintCanvas.PreviewMouseLeftButtonUp += Text_OnCanvasMouseUp;
            PaintCanvas.PreviewKeyDown += Text_OnPreviewKeyDown;

            PaintCanvas.MouseMove += PaintCanvas_MouseMove_UpdatePreview;
            PaintCanvas.MouseLeave += (_, __) =>
            {
                UpdateBrushPreviewVisibility(false);
                if (!_textToolArmed) PaintCanvas.Cursor = Cursors.Arrow;
            };
            PaintCanvas.MouseEnter += (s, e) =>
            {
                if (!_textToolArmed) PaintCanvas.Cursor = Cursors.None;
                UpdateBrushPreviewVisibility(!_textToolArmed);
                if (!_textToolArmed) UpdateBrushPreviewAt(e.GetPosition(PaintCanvas));
            };

            if (_img != null) _img.ImageChanged += EnsureLiveOverlayBitmap;
        }

        private void PaintCanvas_PreviewMouseDown_BeginDraw(object? sender, MouseButtonEventArgs e)
        {
            if (_textToolArmed) return;
            if (_img == null || _img.Doc == null) return;
            EnsureLiveOverlayBitmap();

            // Save undo state before starting a new stroke (only once per stroke)
            if (!_isDrawing)
            {
                SaveUndoState(_eraseMode ? "Erase" : "Brush");
            }

            _isDrawing = true;
            _snappedPoints.Clear();

            var p = e.GetPosition(PaintCanvas);
            var s = SnapToImagePixel(p);

            int eff = ComputeEffectiveBrushSizePx();
            int w = _img.Doc.Width, h = _img.Doc.Height;

            var fit = ClampCenterToFit((int)s.X, (int)s.Y, eff, w, h);
            s = new WpfPoint(fit.cx, fit.cy);
            _snappedPoints.Add(s);

            FillSquareIntoOverlay(fit.cx, fit.cy, eff, _eraseMode ? Colors.White : _brushColor);
            PushOverlay();

            PaintCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void PaintCanvas_PreviewMouseMove_Draw(object? sender, MouseEventArgs e)
        {
            if (_textToolArmed) return;
            if (!_isDrawing) return;
            if (_img == null || _img.Doc == null) return;

            var p = e.GetPosition(PaintCanvas);
            var s = SnapToImagePixel(p);

            int eff = ComputeEffectiveBrushSizePx();
            int w = _img.Doc.Width, h = _img.Doc.Height;

            var fit = ClampCenterToFit((int)s.X, (int)s.Y, eff, w, h);
            s = new WpfPoint(fit.cx, fit.cy);

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
            if (_textToolArmed) return;
            if (!_isDrawing) return;
            _isDrawing = false;
            PaintCanvas.ReleaseMouseCapture();

            CommitOverlayToImage();
            ClearLiveOverlay();
            _snappedPoints.Clear();
            e.Handled = true;
        }

        // Snap to integer pixel inside image bounds (center may still be adjusted later)
        private WpfPoint SnapToImagePixel(WpfPoint p)
        {
            int x = (int)System.Math.Round(p.X);
            int y = (int)System.Math.Round(p.Y);

            int w = _img?.Doc?.Width ?? int.MaxValue;
            int h = _img?.Doc?.Height ?? int.MaxValue;

            if (w != int.MaxValue && h != int.MaxValue)
            {
                x = System.Math.Clamp(x, 0, System.Math.Max(0, w - 1));
                y = System.Math.Clamp(y, 0, System.Math.Max(0, h - 1));
            }

            return new WpfPoint(x, y);
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
                    if (d <= 0)
                    {
                        d += incrE;
                        x += sx;
                    }
                    else
                    {
                        d += incrNE;
                        x += sx;
                        y += sy;
                        yield return (x - sx, y);
                    }
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
                    if (d <= 0)
                    {
                        d += incrN;
                        y += sy;
                    }
                    else
                    {
                        d += incrNE;
                        y += sy;
                        x += sx;
                        yield return (x, y - sy);
                    }
                }
            }
        }

        private void FillSquareIntoOverlay(int cx, int cy, int eff, Color color)
        {
            if (_liveWB == null || _liveBuf == null) return;
            int w = _liveWB.PixelWidth, h = _liveWB.PixelHeight;
            int half = eff / 2;

            int left = cx - half;
            int top = cy - half;
            if (left < 0) left = 0;
            if (top < 0) top = 0;
            int right = System.Math.Min(w, left + eff);
            int bottom = System.Math.Min(h, top + eff);
            if (right <= left || bottom <= top) return;

            bool hasSel = HasSelectionClip();
            byte B = color.B, G = color.G, R = color.R;

            // Paint ONLY inside the selection (or everywhere if no selection).
            for (int y = top; y < bottom; y++)
            {
                int row = y * w * 4;
                for (int x = left; x < right; x++)
                {
                    if (hasSel && !PixelIsInsideSelection(x, y)) continue;

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
            if (_img == null || _liveWB == null || _liveBuf == null) return;

            int w = _liveWB.PixelWidth, h = _liveWB.PixelHeight;

            var mat = _img.Mat; // <-- commit to ACTIVE LAYER
            if (mat == null || mat.Empty()) return;

            int step = (int)mat.Step();
            var dst = new byte[step * h];
            Marshal.Copy(mat.Data, dst, 0, dst.Length);

            for (int y = 0; y < h; y++)
            {
                int rb = y * w * 4; // buffer row
                int rd = y * step; // mat row
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

            Marshal.Copy(dst, 0, mat.Data, dst.Length);
            _img.ForceRefreshView();
            _hasUnsavedChanges = true; // from MainWindow.xaml.cs
        }


        // ----------------- Brush preview (zoom-aware, snapped) -----------------
        private void EnsureBrushPreview()
        {
            if (_brushPreview != null) return;

            _brushPreview = new Rectangle
            {
                Stroke = new SolidColorBrush(_brushColor),
                Fill = Brushes.Transparent,
                StrokeThickness = 1,
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

        private void UpdateBrushPreviewAt(WpfPoint canvasPos)
        {
            if (_brushPreview == null) return;
            if (_img == null || _img.Doc == null)
            {
                UpdateBrushPreviewVisibility(false);
                return;
            }

            var s = SnapToImagePixel(canvasPos);
            int eff = ComputeEffectiveBrushSizePx();
            int w = _img.Doc.Width, h = _img.Doc.Height;

            var fit = ClampCenterToFit((int)s.X, (int)s.Y, eff, w, h);

            int left = fit.cx - eff / 2;
            int top = fit.cy - eff / 2;

            _brushPreview.Stroke = _eraseMode ? Brushes.Red : new SolidColorBrush(_brushColor);
            _brushPreview.Fill = Brushes.Transparent;

            InkCanvas.SetLeft(_brushPreview, left);
            InkCanvas.SetTop(_brushPreview, top);
            _brushPreview.Width = eff;
            _brushPreview.Height = eff;

            SyncBrushPreviewStrokeToZoom();

            if (_brushPreview.Visibility != Visibility.Visible)
                _brushPreview.Visibility = Visibility.Visible;
        }

        private void PaintCanvas_MouseMove_UpdatePreview(object? sender, MouseEventArgs e)
        {
            if (_textToolArmed) return;
            UpdateBrushPreviewAt(e.GetPosition(PaintCanvas));
        }

        internal void SyncBrushPreviewStrokeToZoom()
        {
            if (_brushPreview == null) return;

            double thickness = 1.0 / System.Math.Max(0.0001, _zoom);
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

        // ----------------- Helpers -----------------
        private bool EnsureImageOpen()
        {
            if (_img == null || _img.Doc == null || _img.Doc.Image.Empty())
            {
                MessageBox.Show("Open an image first.");
                return false;
            }

            return true;
        }

        private WpfRect ClampRectToImage(WpfRect r)
        {
            if (_img?.Doc == null) return r;
            int w = _img.Doc.Width, h = _img.Doc.Height;
            double x = System.Math.Clamp(r.X, 0.0, (double)System.Math.Max(0, w - 1));
            double y = System.Math.Clamp(r.Y, 0.0, (double)System.Math.Max(0, h - 1));
            double right = System.Math.Clamp(r.X + r.Width, 0.0, (double)w);
            double bottom = System.Math.Clamp(r.Y + r.Height, 0.0, (double)h);
            double width = System.Math.Max(1.0, right - x);
            double height = System.Math.Max(1.0, bottom - y);
            return new WpfRect(x, y, width, height);
        }

        private void PlaceOnCanvas(FrameworkElement fe, double x, double y, double w, double h)
        {
            InkCanvas.SetLeft(fe, x);
            InkCanvas.SetTop(fe, y);
            fe.Width = w;
            fe.Height = h;
        }

        // ----------------- Tools menu extras for selection -----------------
        private void Tool_MoveSelection_Click(object sender, RoutedEventArgs e)
            => _img?.BeginMoveSelectedArea();

        private void Tool_PaintInsideSelection_Click(object sender, RoutedEventArgs e)
            => _img?.EnableSelectionPaintMode();

        private void Tool_CopySelection_Click(object sender, RoutedEventArgs e)
            => _img?.CopySelectionToClipboard();

        // ----------------- Menu actions -----------------
        private void Tool_Erase_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = !_eraseMode;
            Shapes_Disarm(); // ← disarm shapes when erasing
            Text_Disarm();
            PaintCanvas.Cursor = Cursors.None;
            UpdateBrushPreviewVisibility(true);
            ApplyInkBrushAttributes();
        }


        private void Tool_ColorPicker_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WF.ColorDialog { AllowFullOpen = true, FullOpen = true };
            if (dlg.ShowDialog() == WF.DialogResult.OK)
            {
                _brushColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _eraseMode = false;
                UpdateBrushPreviewVisibility(!_textToolArmed);
                ApplyInkBrushAttributes();
                if (_textEditor != null) _textEditor.Foreground = new SolidColorBrush(_brushColor);
            }
        }

        private void Tool_Brushes_Click(object sender, RoutedEventArgs e)
        {
            _eraseMode = false;
            Shapes_Disarm(); // ← make sure shapes stop intercepting input
            Text_Disarm();
            PaintCanvas.Cursor = Cursors.None;
            UpdateBrushPreviewVisibility(true);
            ApplyInkBrushAttributes();
            StatusText.Content = "Paint Brushes enabled.";
        }

        private void Colors_BrushSize_Click(object sender, RoutedEventArgs e)
        {
            _brushIndex = (_brushIndex + 1) % _brushSizes.Length; // <- from MainWindow.xaml.cs
            _eraseMode = false;
            Text_Disarm();
            PaintCanvas.Cursor = Cursors.None;
            UpdateBrushPreviewVisibility(true);
            ApplyInkBrushAttributes();
        }

        // ---------- Text Box (interactive) ----------
        private void Tool_TextBox_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            Shapes_Disarm(); // ← disarm shapes before arming text
            Text_Disarm(); // clear any existing text box
            _textToolArmed = true;
            _textPhase = TextPhase.Arming;
            UpdateBrushPreviewVisibility(false);
            PaintCanvas.Cursor = Cursors.Cross;
            StatusText.Content = "Text: drag to create a box; right-click for size/color/commit.";
        }

        private void Text_OnCanvasMouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (!_textToolArmed) return;

            var pos = SnapToImagePixel(e.GetPosition(PaintCanvas));

            // If an existing box is present, check for move/resize begin
            if (_textPhase == TextPhase.Editing && _textBorder != null)
            {
                var box = new WpfRect(InkCanvas.GetLeft(_textBorder), InkCanvas.GetTop(_textBorder), _textBorder.Width,
                    _textBorder.Height);
                if (IsOverHandle(pos, out string corner))
                {
                    _isResizing = true;
                    _resizeCorner = corner;
                    _boxStart = box;
                    _moveStart = pos;
                    PaintCanvas.CaptureMouse();
                    e.Handled = true;
                    return; // important: stop TextBox selection
                }

                if (box.Contains(pos))
                {
                    _isMoving = true;
                    _boxStart = box;
                    _moveStart = pos;
                    PaintCanvas.CaptureMouse();
                    e.Handled = true;
                    return; // important: stop TextBox selection
                }
            }

            if (_textPhase == TextPhase.Arming)
            {
                _textPhase = TextPhase.Dragging;
                _textDragStart = pos;

                _textDragRect = new Rectangle
                {
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(_textDragRect, 1005);
                PaintCanvas.Children.Add(_textDragRect);
                PlaceOnCanvas(_textDragRect, pos.X, pos.Y, 1, 1);

                PaintCanvas.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Text_OnCanvasMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_textToolArmed) return;

            var p = SnapToImagePixel(e.GetPosition(PaintCanvas));
            if (_textPhase == TextPhase.Dragging && _textDragRect != null)
            {
                var x = System.Math.Min(_textDragStart.X, p.X);
                var y = System.Math.Min(_textDragStart.Y, p.Y);
                var w = System.Math.Abs(p.X - _textDragStart.X);
                var h = System.Math.Abs(p.Y - _textDragStart.Y);
                if (w < 1) w = 1;
                if (h < 1) h = 1;
                var r = ClampRectToImage(new WpfRect(x, y, w, h));
                PlaceOnCanvas(_textDragRect, r.X, r.Y, r.Width, r.Height);
                e.Handled = true;
            }
            else if (_isMoving && _textBorder != null)
            {
                var dx = p.X - _moveStart.X;
                var dy = p.Y - _moveStart.Y;
                var r = new WpfRect(_boxStart.X + dx, _boxStart.Y + dy, _boxStart.Width, _boxStart.Height);
                r = ClampRectToImage(r);
                PlaceOnCanvas(_textBorder, r.X, r.Y, r.Width, r.Height);
                UpdateTextHandles();
                e.Handled = true;
            }
            else if (_isResizing && _textBorder != null)
            {
                var r = _boxStart;
                double dx = p.X - _moveStart.X;
                double dy = p.Y - _moveStart.Y;

                switch (_resizeCorner)
                {
                    case "NW": r = new WpfRect(r.X + dx, r.Y + dy, r.Width - dx, r.Height - dy); break;
                    case "NE": r = new WpfRect(r.X, r.Y + dy, r.Width + dx, r.Height - dy); break;
                    case "SW": r = new WpfRect(r.X + dx, r.Y, r.Width - dx, r.Height + dy); break;
                    case "SE": r = new WpfRect(r.X, r.Y, r.Width + dx, r.Height + dy); break;
                }

                if (r.Width < 1) r.Width = 1;
                if (r.Height < 1) r.Height = 1;
                r = ClampRectToImage(r);

                PlaceOnCanvas(_textBorder, r.X, r.Y, r.Width, r.Height);
                UpdateTextHandles();
                e.Handled = true;
            }
        }

        private void Text_OnCanvasMouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (!_textToolArmed) return;

            if (_textPhase == TextPhase.Dragging && _textDragRect != null)
            {
                // finalize box and enter editing
                var r = new WpfRect(InkCanvas.GetLeft(_textDragRect), InkCanvas.GetTop(_textDragRect),
                    _textDragRect.Width, _textDragRect.Height);
                PaintCanvas.Children.Remove(_textDragRect);
                _textDragRect = null;

                CreateTextOverlay(r);
                _textPhase = TextPhase.Editing;
                PaintCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (_isMoving || _isResizing)
            {
                _isMoving = false;
                _isResizing = false;
                _resizeCorner = "";
                PaintCanvas.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void Text_OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_textToolArmed) return;
            if (_textEditor == null) return;

            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Text_Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Text_Disarm();
                e.Handled = true;
            }
        }

        private void CreateTextOverlay(WpfRect r)
        {
            // container
            _textBorder = new Border
            {
                BorderBrush = Brushes.DeepSkyBlue,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(_textBorder, 1006);

            _textEditor = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(_brushColor),
                FontSize = _textFontPx,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(2),
                SpellCheck = { IsEnabled = false }
            };

            // right-click menu (attach to BOTH border and editor)
            var cm = new ContextMenu();
            MenuItem inc = new MenuItem { Header = "Font Size +2" };
            MenuItem dec = new MenuItem { Header = "Font Size -2" };
            MenuItem set = new MenuItem { Header = "Set Font Size..." };
            MenuItem color = new MenuItem { Header = "Text Color..." };
            MenuItem commit = new MenuItem { Header = "Commit (Ctrl+Enter)" };
            MenuItem cancel = new MenuItem { Header = "Cancel (Esc)" };
            inc.Click += (_, __) =>
            {
                _textFontPx = System.Math.Clamp(_textFontPx + 2, 6, 256);
                if (_textEditor != null) _textEditor.FontSize = _textFontPx;
            };
            dec.Click += (_, __) =>
            {
                _textFontPx = System.Math.Clamp(_textFontPx - 2, 6, 256);
                if (_textEditor != null) _textEditor.FontSize = _textFontPx;
            };
            set.Click += (_, __) =>
            {
                string s = Interaction.InputBox("Font size (px):", "Text", _textFontPx.ToString());
                if (int.TryParse(s, out int px))
                {
                    _textFontPx = System.Math.Clamp(px, 6, 256);
                    if (_textEditor != null) _textEditor.FontSize = _textFontPx;
                }
            };
            color.Click += (_, __) =>
            {
                using var dlg = new WF.ColorDialog
                {
                    AllowFullOpen = true, FullOpen = true,
                    Color = System.Drawing.Color.FromArgb(_brushColor.A, _brushColor.R, _brushColor.G, _brushColor.B)
                };
                if (dlg.ShowDialog() == WF.DialogResult.OK)
                {
                    _brushColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    if (_textEditor != null) _textEditor.Foreground = new SolidColorBrush(_brushColor);
                }
            };
            commit.Click += (_, __) => Text_Commit();
            cancel.Click += (_, __) => Text_Disarm();
            cm.Items.Add(inc);
            cm.Items.Add(dec);
            cm.Items.Add(new Separator());
            cm.Items.Add(set);
            cm.Items.Add(color);
            cm.Items.Add(new Separator());
            cm.Items.Add(commit);
            cm.Items.Add(cancel);
            _textBorder.ContextMenu = cm;
            _textEditor.ContextMenu = cm;

            _textBorder.Child = _textEditor;
            PaintCanvas.Children.Add(_textBorder);
            PlaceOnCanvas(_textBorder, r.X, r.Y, r.Width, r.Height);

            // handles
            CreateTextHandles();
            UpdateTextHandles();

            // focus text
            _textEditor.Focus();
            _textEditor.CaretIndex = _textEditor.Text.Length;

            PaintCanvas.Cursor = Cursors.IBeam;
            StatusText.Content = "Text editing: type; drag to move; drag corners to resize; right-click for options.";
        }

        private void CreateTextHandles()
        {
            foreach (var h in _textHandles) PaintCanvas.Children.Remove(h);
            _textHandles.Clear();

            _textHandles.Add(MakeHandle("NW"));
            _textHandles.Add(MakeHandle("NE"));
            _textHandles.Add(MakeHandle("SW"));
            _textHandles.Add(MakeHandle("SE"));
        }

        private Rectangle MakeHandle(string tag)
        {
            var r = new Rectangle
            {
                Width = 8, Height = 8,
                Fill = Brushes.DeepSkyBlue,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Tag = tag,
                IsHitTestVisible = true
            };
            Panel.SetZIndex(r, 1007);
            r.MouseLeftButtonDown += (s, e) =>
            {
                if (!_textToolArmed || _textPhase != TextPhase.Editing || _textBorder == null) return;
                _isResizing = true;
                _resizeCorner = (string)((FrameworkElement)s!).Tag;
                _boxStart = new WpfRect(InkCanvas.GetLeft(_textBorder), InkCanvas.GetTop(_textBorder),
                    _textBorder.Width, _textBorder.Height);
                _moveStart = SnapToImagePixel(e.GetPosition(PaintCanvas));
                PaintCanvas.CaptureMouse();
                e.Handled = true;
            };
            return r;
        }

        private void UpdateTextHandles()
        {
            if (_textBorder == null) return;
            var x = InkCanvas.GetLeft(_textBorder);
            var y = InkCanvas.GetTop(_textBorder);
            var w = _textBorder.Width;
            var h = _textBorder.Height;

            foreach (var hnd in _textHandles)
            {
                string t = (string)hnd.Tag;
                double hx = t.Contains("W") ? x - 4 : x + w - 4;
                double hy = t.Contains("N") ? y - 4 : y + h - 4;
                PlaceOnCanvas(hnd, hx, hy, 8, 8);
                if (!PaintCanvas.Children.Contains(hnd)) PaintCanvas.Children.Add(hnd);
            }
        }

        private bool IsOverHandle(WpfPoint p, out string corner)
        {
            foreach (var h in _textHandles)
            {
                var r = new WpfRect(InkCanvas.GetLeft(h), InkCanvas.GetTop(h), h.Width, h.Height);
                if (r.Contains(p))
                {
                    corner = (string)h.Tag;
                    return true;
                }
            }

            corner = "";
            return false;
        }

        private void Text_Commit()
        {
            if (_textBorder == null || _textEditor == null || _img?.Doc == null) return;

            int w = (int)System.Math.Round(_textBorder.Width);
            int h = (int)System.Math.Round(_textBorder.Height);
            if (w < 1 || h < 1)
            {
                Text_Disarm();
                return;
            }

            string textContent = _textEditor.Text;
            if (string.IsNullOrWhiteSpace(textContent))
            {
                StatusText.Content = "No text to commit.";
                Text_Disarm();
                return;
            }

            // Save undo state before committing text
            SaveUndoState("Text");

            try
            {
                // Create a Grid with TextBlock to ensure proper rendering
                var grid = new Grid
                {
                    Width = w,
                    Height = h,
                    Background = Brushes.Transparent
                };

                var textBlock = new TextBlock
                {
                    Text = textContent,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(_brushColor),
                    FontSize = _textEditor.FontSize,
                    FontFamily = _textEditor.FontFamily,
                    FontStyle = _textEditor.FontStyle,
                    FontWeight = _textEditor.FontWeight,
                    FontStretch = _textEditor.FontStretch,
                    TextAlignment = _textEditor.TextAlignment,
                    Padding = new Thickness(2),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                grid.Children.Add(textBlock);

                grid.Measure(new WpfSize(w, h));
                grid.Arrange(new WpfRect(0, 0, w, h));

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(grid);

                var stride = w * 4;
                var pixels = new byte[h * stride];
                rtb.CopyPixels(pixels, stride, 0);

                // Debug: Check if we actually rendered anything
                int nonZeroPixels = 0;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > 0) nonZeroPixels++;
                }

                StatusText.Content = $"Rendered {nonZeroPixels} non-transparent pixels";

                if (nonZeroPixels == 0)
                {
                    MessageBox.Show(
                        $"Text rendering produced no visible pixels!\nText: '{textContent}'\nColor: {_brushColor}\nFont Size: {_textEditor.FontSize}");
                    Text_Disarm();
                    return;
                }

                // Blend into image
                int imgW = _img.Doc.Width, imgH = _img.Doc.Height;
                int step = (int)_img.Doc.Image.Step();
                var dst = new byte[step * imgH];
                Marshal.Copy(_img.Doc.Image.Data, dst, 0, dst.Length);

                int left = (int)System.Math.Round(InkCanvas.GetLeft(_textBorder));
                int top = (int)System.Math.Round(InkCanvas.GetTop(_textBorder));

                int pixelsWritten = 0;

                for (int y = 0; y < h; y++)
                {
                    int iy = top + y;
                    if (iy < 0 || iy >= imgH) continue;

                    int dstRow = iy * step;
                    int srcRow = y * stride;

                    for (int x = 0; x < w; x++)
                    {
                        int ix = left + x;
                        if (ix < 0 || ix >= imgW) continue;

                        int si = srcRow + x * 4;
                        byte a = pixels[si + 3];
                        if (a == 0) continue;

                        int di = dstRow + ix * 4;

                        // Alpha blend
                        if (a == 255)
                        {
                            dst[di + 0] = pixels[si + 0];
                            dst[di + 1] = pixels[si + 1];
                            dst[di + 2] = pixels[si + 2];
                            dst[di + 3] = 255;
                        }
                        else
                        {
                            int invA = 255 - a;
                            dst[di + 0] = (byte)((dst[di + 0] * invA + pixels[si + 0] * a) / 255);
                            dst[di + 1] = (byte)((dst[di + 1] * invA + pixels[si + 1] * a) / 255);
                            dst[di + 2] = (byte)((dst[di + 2] * invA + pixels[si + 2] * a) / 255);
                            dst[di + 3] = 255;
                        }

                        pixelsWritten++;
                    }
                }

                Marshal.Copy(dst, 0, _img.Doc.Image.Data, dst.Length);
                _img.ForceRefreshView();

                StatusText.Content = $"Text committed. Wrote {pixelsWritten} pixels.";
                _hasUnsavedChanges = true;
                Text_Disarm();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error committing text: {ex.Message}");
                StatusText.Content = $"Error: {ex.Message}";
            }
        }

        private void Text_Disarm()
        {
            _textToolArmed = false;
            _textPhase = TextPhase.None;

            if (_textDragRect != null)
            {
                PaintCanvas.Children.Remove(_textDragRect);
                _textDragRect = null;
            }

            if (_textBorder != null)
            {
                PaintCanvas.Children.Remove(_textBorder);
                _textBorder = null;
            }

            foreach (var h in _textHandles) PaintCanvas.Children.Remove(h);
            _textHandles.Clear();
            _textEditor = null;

            _isMoving = _isResizing = false;
            _resizeCorner = "";
            PaintCanvas.ReleaseMouseCapture();

            PaintCanvas.Cursor = Cursors.None;
            UpdateBrushPreviewVisibility(true);
        }

        // ===================== Layer‑specific filter PREVIEW (Gaussian/Sobel/Binary/Otsu) =====================
        private bool _lpRunning = false;
        private string _lpMode = "";
        private Image? _lpOverlay;
        private Border? _lpToolbar;
        private Slider? _lpBinarySlider;
        private Mat? _lpSrc;
        private Mat? _lpWork;
        private byte[]? _lpBackupBytes;
        private WriteableBitmap? _lpWB;
        private int _lpW, _lpH, _lpStride;

        private void StartLayerPreview(string mode)
        {
            if (_img?.Mat == null || _img.Mat.Empty()) return;
            EndLayerPreview(apply: false);

            // Save undo state before starting filter preview
            SaveUndoState(mode + " Filter");

            _lpMode = mode;
            _lpRunning = true;

            _lpSrc = _img.Mat.Clone();
            _lpW = _lpSrc.Width;
            _lpH = _lpSrc.Height;
            _lpStride = (int)_img.Mat.Step();

            _lpBackupBytes = new byte[_lpStride * _lpH];
            Marshal.Copy(_img.Mat.Data, _lpBackupBytes, 0, _lpBackupBytes.Length);
            _img.Mat.SetTo(new Scalar(0, 0, 0, 0));
            _img.ForceRefreshView();

            _lpWB = new WriteableBitmap(_lpW, _lpH, 96, 96, PixelFormats.Bgra32, null);
            _lpOverlay = new Image { Source = _lpWB, IsHitTestVisible = false, Width = _lpW, Height = _lpH };
            RenderOptions.SetBitmapScalingMode(_lpOverlay, BitmapScalingMode.NearestNeighbor);
            InkCanvas.SetLeft(_lpOverlay, 0);
            InkCanvas.SetTop(_lpOverlay, 0);
            Panel.SetZIndex(_lpOverlay, 1001);
            Artboard.Children.Add(_lpOverlay);

            _lpToolbar = BuildLayerToolbar();
            Canvas.SetLeft(_lpToolbar, 8);
            Canvas.SetTop(_lpToolbar, 8);
            Panel.SetZIndex(_lpToolbar, int.MaxValue);
            Artboard.Children.Add(_lpToolbar);

            UpdateLayerPreview();
            this.PreviewKeyDown += OnLayerPreviewKeyDown;
            StatusText.Content = $"Preview: {mode}. Enter = Apply, Esc = Cancel.";
        }

        private void OnLayerPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_lpRunning) return;
            if (e.Key == Key.Enter)
            {
                EndLayerPreview(apply: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndLayerPreview(apply: false);
                e.Handled = true;
            }
        }

        private Border BuildLayerToolbar()
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
            stack.Children.Add(new TextBlock
            {
                Text = $"Layer Filter: {_lpMode}",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            if (_lpMode == "Binary")
            {
                var lbl = new TextBlock { Text = "Threshold", Foreground = Brushes.Gainsboro };
                _lpBinarySlider = new Slider
                {
                    Minimum = 0, Maximum = 255, Value = 128,
                    Width = 220, IsSnapToTickEnabled = false, IsMoveToPointEnabled = true,
                    SmallChange = 1, LargeChange = 16
                };
                _lpBinarySlider.ValueChanged += (_, __) => UpdateLayerPreview();
                stack.Children.Add(lbl);
                stack.Children.Add(_lpBinarySlider);
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var apply = new Button
            {
                Content = "Apply (Enter)", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4)
            };
            var cancel = new Button { Content = "Cancel (Esc)", Padding = new Thickness(10, 4, 10, 4) };
            apply.Click += (_, __) => EndLayerPreview(apply: true);
            cancel.Click += (_, __) => EndLayerPreview(apply: false);
            row.Children.Add(apply);
            row.Children.Add(cancel);
            stack.Children.Add(row);

            border.Child = stack;
            return border;
        }

        private void UpdateLayerPreview()
        {
            if (_lpSrc == null || _lpWB == null) return;

            _lpWork?.Dispose();
            _lpWork = _lpSrc.Clone();

            using var alpha = new Mat();
            Cv2.ExtractChannel(_lpSrc, alpha, 3);

            switch (_lpMode)
            {
                case "Gaussian":
                    Cv2.GaussianBlur(_lpSrc, _lpWork, new CvSize(5, 5), 0);
                    Cv2.InsertChannel(alpha, _lpWork, 3);
                    break;

                case "Sobel":
                    using (var gray = new Mat())
                    using (var gx = new Mat())
                    using (var gy = new Mat())
                    using (var agx = new Mat())
                    using (var agy = new Mat())
                    using (var mag = new Mat())
                    using (var bgr = new Mat())
                    {
                        Cv2.CvtColor(_lpSrc, gray, ColorConversionCodes.BGRA2GRAY);
                        Cv2.Sobel(gray, gx, MatType.CV_16S, 1, 0, ksize: 3, scale: 1, delta: 0, BorderTypes.Default);
                        Cv2.Sobel(gray, gy, MatType.CV_16S, 0, 1, ksize: 3, scale: 1, delta: 0, BorderTypes.Default);
                        Cv2.ConvertScaleAbs(gx, agx);
                        Cv2.ConvertScaleAbs(gy, agy);
                        Cv2.AddWeighted(agx, 0.5, agy, 0.5, 0, mag);
                        Cv2.CvtColor(mag, bgr, ColorConversionCodes.GRAY2BGR);
                        var chans = bgr.Split();
                        Cv2.Merge(new[] { chans[0], chans[1], chans[2], alpha }, _lpWork);
                        foreach (var c in chans) c.Dispose();
                    }

                    break;

                case "Binary":
                    using (var gray = new Mat())
                    using (var bw = new Mat())
                    using (var bgr = new Mat())
                    {
                        Cv2.CvtColor(_lpSrc, gray, ColorConversionCodes.BGRA2GRAY);
                        double t = _lpBinarySlider?.Value ?? 128;
                        Cv2.Threshold(gray, bw, t, 255, ThresholdTypes.Binary);
                        Cv2.CvtColor(bw, bgr, ColorConversionCodes.GRAY2BGR);
                        var chans = bgr.Split();
                        Cv2.Merge(new[] { chans[0], chans[1], chans[2], alpha }, _lpWork);
                        foreach (var c in chans) c.Dispose();
                    }

                    break;

                case "Otsu":
                    using (var gray = new Mat())
                    using (var bw = new Mat())
                    using (var bgr = new Mat())
                    {
                        Cv2.CvtColor(_lpSrc, gray, ColorConversionCodes.BGRA2GRAY);
                        Cv2.Threshold(gray, bw, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                        Cv2.CvtColor(bw, bgr, ColorConversionCodes.GRAY2BGR);
                        var chans = bgr.Split();
                        Cv2.Merge(new[] { chans[0], chans[1], chans[2], alpha }, _lpWork);
                        foreach (var c in chans) c.Dispose();
                    }

                    break;
            }

            int srcStep = (int)_lpWork.Step();
            int dstStride = _lpW * 4;
            var srcBytes = new byte[srcStep * _lpH];
            Marshal.Copy(_lpWork.Data, srcBytes, 0, srcBytes.Length);
            var dstBytes = new byte[dstStride * _lpH];
            for (int y = 0; y < _lpH; y++)
            {
                Buffer.BlockCopy(srcBytes, y * srcStep, dstBytes, y * dstStride, dstStride);
            }

            _lpWB.WritePixels(new Int32Rect(0, 0, _lpW, _lpH), dstBytes, dstStride, 0);
        }

        private void EndLayerPreview(bool apply)
        {
            if (!_lpRunning) return;
            _lpRunning = false;
            this.PreviewKeyDown -= OnLayerPreviewKeyDown;

            try
            {
                if (_img?.Mat != null && !_img.Mat.Empty())
                {
                    if (apply && _lpWork != null)
                    {
                        int step = (int)_img.Mat.Step();
                        int srcStep = (int)_lpWork.Step();
                        int h = _lpH;
                        int w = _lpW;

                        var srcBytes = new byte[srcStep * h];
                        Marshal.Copy(_lpWork.Data, srcBytes, 0, srcBytes.Length);

                        var dstBytes = new byte[step * h];
                        Marshal.Copy(_img.Mat.Data, dstBytes, 0, dstBytes.Length);

                        int rowBytes = w * 4;
                        for (int y = 0; y < h; y++)
                        {
                            Buffer.BlockCopy(srcBytes, y * srcStep, dstBytes, y * step, rowBytes);
                        }

                        Marshal.Copy(dstBytes, 0, _img.Mat.Data, dstBytes.Length);
                        _img.ForceRefreshView();
                        _hasUnsavedChanges = true;
                    }
                    else if (_lpBackupBytes != null)
                    {
                        Marshal.Copy(_lpBackupBytes, 0, _img.Mat.Data, _lpBackupBytes.Length);
                        _img.ForceRefreshView();
                    }
                }
            }
            finally
            {
                if (_lpOverlay != null) Artboard.Children.Remove(_lpOverlay);
                if (_lpToolbar != null) Artboard.Children.Remove(_lpToolbar);
                _lpOverlay = null;
                _lpToolbar = null;
                _lpWB = null;

                _lpWork?.Dispose();
                _lpWork = null;
                _lpSrc?.Dispose();
                _lpSrc = null;
                _lpBackupBytes = null;
                StatusText.Content = "";
            }
        }

        private void Filter_Gaussian_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            StartLayerPreview("Gaussian");
        }

        private void Filter_Sobel_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            StartLayerPreview("Sobel");
        }

        private void Filter_Binary_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            StartLayerPreview("Binary");
        }

        private void Filter_HistogramThreshold_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            StartLayerPreview("Otsu");
        }
    }
}
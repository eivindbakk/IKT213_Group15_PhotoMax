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
using WWindow = System.Windows.Window; // avoid clash with OpenCvSharp.Window

namespace PhotoMax
{
    public partial class MainWindow
    {
        // ===== Live blocky drawing state (strict bitmap overlay) =====
        private Image? _liveImage;
        private WriteableBitmap? _liveWB;
        private byte[]? _liveBuf;
        private bool _isDrawing = false;
        private readonly List<WpfPoint> _snappedPoints = new(); 
        // Track total strokes for statistics
        private int _totalStrokesSinceLastSave = 0;
        // Public property to access stroke count
        public int TotalStrokesSinceLastSave => _totalStrokesSinceLastSave;

        // Cursor preview (zoom-aware outline of true stamp footprint)
        private Rectangle? _brushPreview;
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

        private Rectangle? _textDragRect;
        private Border? _textBorder;
        private TextBox? _textEditor;
        private readonly List<Rectangle> _textHandles = new();
        private double _textFontPx = 24;
        private WpfPoint _textDragStart;
        private WpfPoint _moveStart;
        private WpfRect _boxStart;
        private bool _isMoving = false;
        private bool _isResizing = false;
        private string _resizeCorner = "";

        private const double MinTextBoxSize = 40;

        // ----- Move tool state (canvas pan + selection move) -----
        private bool _moveToolArmed = false;
        private bool _moveToolPanning = false;
        private WpfPoint _moveToolStartMouse;
        private double _moveToolStartScrollX;
        private double _moveToolStartScrollY;

        // Selection event subscription (to auto-arm Move tool after selection)
        private bool _selectionEventsHooked = false;

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
            // If there's already an active selection being moved (floating), do nothing
            if (_img != null && _img.IsFloatingActive)
            {
                StatusText.Content = "Already moving selection. Press Enter to place, Esc to cancel.";
                return;
            }

            // If there's a selection, IMMEDIATELY start moving it (don't wait for click)
            if (_img != null && _img.HasActiveSelection && !_img.IsFloatingActive)
            {
                _eraseMode = false;
                Shapes_Disarm();
                Text_Disarm();
                UpdateBrushPreviewVisibility(false);

                // Disable any previous move tool pan mode
                if (_moveToolArmed)
                {
                    UnhookMoveToolPanEvents();
                }

                _moveToolArmed = true; // Keep move tool armed for after placement
                PaintCanvas.Cursor = Cursors.SizeAll;

                _img.BeginMoveSelectedArea();
                StatusText.Content = "Move: drag to reposition selection, Enter to place, Esc to cancel.";
                return;
            }

            // No selection - toggle pan mode
            _moveToolArmed = !_moveToolArmed;

            if (_moveToolArmed)
            {
                _eraseMode = false;
                Shapes_Disarm();
                Text_Disarm();
                UpdateBrushPreviewVisibility(false);

                PaintCanvas.EditingMode = InkCanvasEditingMode.None;
                PaintCanvas.IsHitTestVisible = true;
                PaintCanvas.Cursor = Cursors.SizeAll;

                // Hook pan events
                HookMoveToolPanEvents();

                StatusText.Content = "Move tool: drag to pan canvas (click again to disable).";
            }
            else
            {
                // Disable move tool
                UnhookMoveToolPanEvents();

                PaintCanvas.Cursor = Cursors.None;
                PaintCanvas.EditingMode = InkCanvasEditingMode.Ink;
                UpdateBrushPreviewVisibility(true);

                StatusText.Content = "Move tool disabled.";
            }
        }

        private void HookMoveToolPanEvents()
        {
            PaintCanvas.PreviewMouseLeftButtonDown += MoveToolPan_MouseDown;
            PaintCanvas.PreviewMouseMove += MoveToolPan_MouseMove;
            PaintCanvas.PreviewMouseLeftButtonUp += MoveToolPan_MouseUp;

            // **NEW: Watch for selection creation while move tool is active**
            if (_img != null)
            {
                _img.SelectionCreated += OnSelectionCreatedWhileMoveToolActive;
            }
        }

        private void UnhookMoveToolPanEvents()
        {
            PaintCanvas.PreviewMouseLeftButtonDown -= MoveToolPan_MouseDown;
            PaintCanvas.PreviewMouseMove -= MoveToolPan_MouseMove;
            PaintCanvas.PreviewMouseLeftButtonUp -= MoveToolPan_MouseUp;

            _moveToolPanning = false;

            // **NEW: Unsubscribe from selection event**
            if (_img != null)
            {
                _img.SelectionCreated -= OnSelectionCreatedWhileMoveToolActive;
            }
        }

        private void OnSelectionCreatedWhileMoveToolActive()
        {
            if (!_moveToolArmed) return;

            // Selection was just created while move tool is active
            // Automatically start moving it
            if (_img != null && _img.HasActiveSelection && !_img.IsFloatingActive)
            {
                UpdateBrushPreviewVisibility(false);
                PaintCanvas.Cursor = Cursors.SizeAll;

                _img.BeginMoveSelectedArea();
                StatusText.Content = "Move: drag to reposition selection, Enter to place, Esc to cancel.";
            }
        }

        private void MoveToolPan_MouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (!_moveToolArmed) return;

            // Regular pan behavior (selection move is handled in Tool_MoveTool_Click)
            _moveToolPanning = true;
            _moveToolStartMouse = e.GetPosition(Scroller);
            _moveToolStartScrollX = Scroller.HorizontalOffset;
            _moveToolStartScrollY = Scroller.VerticalOffset;

            PaintCanvas.CaptureMouse();
            PaintCanvas.Cursor = Cursors.ScrollAll;
            e.Handled = true;
        }

        private void MoveToolPan_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_moveToolArmed || !_moveToolPanning) return;

            var current = e.GetPosition(Scroller);
            var dx = _moveToolStartMouse.X - current.X;
            var dy = _moveToolStartMouse.Y - current.Y;

            Scroller.ScrollToHorizontalOffset(_moveToolStartScrollX + dx);
            Scroller.ScrollToVerticalOffset(_moveToolStartScrollY + dy);

            e.Handled = true;
        }

        private void MoveToolPan_MouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (!_moveToolArmed) return;

            _moveToolPanning = false;
            PaintCanvas.ReleaseMouseCapture();
            PaintCanvas.Cursor = Cursors.SizeAll;
            e.Handled = true;
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
            int baseSize = _brushSizes[_brushIndex];
            return System.Math.Clamp(baseSize, 1, 256);
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

        private bool HasSelectionClip()
            => _img != null && _img.SelectionFill != null;

        private bool PixelIsInsideSelection(int x, int y)
        {
            return _img?.PixelFullyInsideSelection(x, y) ?? true;
        }

        internal void ApplyInkBrushAttributes()
        {
            PaintCanvas.EditingMode = InkCanvasEditingMode.None;
            if (!_textToolArmed) PaintCanvas.Cursor = Cursors.None;

            var da = PaintCanvas.DefaultDrawingAttributes;
            da.IsHighlighter = false;
            da.IgnorePressure = true;
            da.FitToCurve = false;
            da.StylusTip = StylusTip.Rectangle;
            da.StylusTipTransform = Matrix.Identity;
            da.Color = _brushColor;
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

            PaintCanvas.PreviewMouseDown += PaintCanvas_PreviewMouseDown_BeginDraw;
            PaintCanvas.PreviewMouseMove += PaintCanvas_PreviewMouseMove_Draw;
            PaintCanvas.PreviewMouseUp += PaintCanvas_PreviewMouseUp_EndDraw;

            PaintCanvas.PreviewMouseLeftButtonDown += Text_OnCanvasMouseDown;
            PaintCanvas.PreviewMouseMove += Text_OnCanvasMouseMove;
            PaintCanvas.PreviewMouseLeftButtonUp += Text_OnCanvasMouseUp;
            PaintCanvas.PreviewKeyDown += Text_OnPreviewKeyDown;

            // REMOVE THIS LINE - it's overriding our cursor changes!
            // PaintCanvas.MouseMove  += PaintCanvas_MouseMove_UpdatePreview;

            // ADD BOTH to PreviewMouseMove instead
            PaintCanvas.PreviewMouseMove += PaintCanvas_PreviewMouseMove_UpdateCursors;

            PaintCanvas.MouseLeave += (_, __) =>
            {
                UpdateBrushPreviewVisibility(false);
                if (_moveToolArmed)
                    PaintCanvas.Cursor = Cursors.SizeAll;
                else if (!_textToolArmed)
                    PaintCanvas.Cursor = Cursors.Arrow;
            };
            PaintCanvas.MouseEnter += (s, e) =>
            {
                if (_moveToolArmed)
                {
                    PaintCanvas.Cursor = Cursors.SizeAll;
                    UpdateBrushPreviewVisibility(false);
                }
                else if (!_textToolArmed)
                {
                    PaintCanvas.Cursor = Cursors.None;
                    UpdateBrushPreviewVisibility(true);
                    UpdateBrushPreviewAt(e.GetPosition(PaintCanvas));
                }
            };

            if (_img != null) _img.ImageChanged += EnsureLiveOverlayBitmap;
        }

        private void PaintCanvas_PreviewMouseMove_UpdateCursors(object? sender, MouseEventArgs e)
        {
            if (_textToolArmed)
            {
                UpdateTextCursor(e.GetPosition(PaintCanvas));
            }
            else if (_img != null && _img.IsFloatingActive)
            {
                // When floating (from move), let the floating paste handle cursor
                UpdateBrushPreviewVisibility(false);
                // Cursor will be managed by ImageController's floating paste logic
            }
            else if (_moveToolArmed)
            {
                // Move tool: no brush preview, always SizeAll cursor.
                UpdateBrushPreviewVisibility(false);
                PaintCanvas.Cursor = Cursors.SizeAll;
            }
            else
            {
                UpdateBrushPreviewAt(e.GetPosition(PaintCanvas));
            }
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

            // **FIX: Rebuild selection mask ONCE at stroke start**
            if (HasSelectionClip() && _img != null)
            {
                _img.RebuildSelectionMaskIfDirty();
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

            // **ALWAYS increment stroke counter, even without an image**
            _totalStrokesSinceLastSave++;
            _hasUnsavedChanges = true;

            // Only commit to image if one exists
            if (_img != null && _img.Doc != null && !_img.Doc.Image.Empty())
            {
                CommitOverlayToImage();
            }
    
            ClearLiveOverlay();
            _snappedPoints.Clear();
            e.Handled = true;
        }

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

            // **FIX: Rebuild mask once per stroke, not per pixel!**
            if (hasSel && _img != null)
            {
                _img.RebuildSelectionMaskIfDirty();
            }

            byte B = color.B, G = color.G, R = color.R;

            for (int y = top; y < bottom; y++)
            {
                int row = y * w * 4;
                for (int x = left; x < right; x++)
                {
                    // **FIX: Use fast mask instead of geometry test**
                    if (hasSel && _img != null && !_img.FastMaskInside(x, y)) continue;

                    int i = row + x * 4;
                    _liveBuf[i + 0] = B;
                    _liveBuf[i + 1] = G;
                    _liveBuf[i + 2] = R;
                    _liveBuf[i + 3] = 255;
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

            var mat = _img.Mat;
            if (mat == null || mat.Empty()) return;

            int step = (int)mat.Step();
            var dst = new byte[step * h];
            Marshal.Copy(mat.Data, dst, 0, dst.Length);

            for (int y = 0; y < h; y++)
            {
                int rb = y * w * 4;
                int rd = y * step;
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
            _hasUnsavedChanges = true;
        }

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
            if (_textToolArmed)
            {
                UpdateTextCursor(e.GetPosition(PaintCanvas));
                return;
            }

            UpdateBrushPreviewAt(e.GetPosition(PaintCanvas));
        }

        private void UpdateTextCursor(WpfPoint canvasPos)
        {
            if (_textPhase != TextPhase.Editing || _textBorder == null)
            {
                PaintCanvas.Cursor = Cursors.Cross;
                Mouse.OverrideCursor = null; // Clear any override
                return;
            }

            // Don't change cursor during active move/resize
            if (_isMoving || _isResizing) return;

            var box = new WpfRect(InkCanvas.GetLeft(_textBorder), InkCanvas.GetTop(_textBorder), _textBorder.Width,
                _textBorder.Height);

            // Check handles first (use canvas position, not snapped)
            foreach (var h in _textHandles)
            {
                var hx = InkCanvas.GetLeft(h);
                var hy = InkCanvas.GetTop(h);
                var hr = new WpfRect(hx, hy, h.Width, h.Height);

                // Expand hit area slightly for easier interaction
                hr.Inflate(2, 2);

                if (hr.Contains(canvasPos))
                {
                    string corner = (string)h.Tag;
                    Mouse.OverrideCursor = corner switch
                    {
                        "NW" => Cursors.SizeNWSE,
                        "NE" => Cursors.SizeNESW,
                        "SW" => Cursors.SizeNESW,
                        "SE" => Cursors.SizeNWSE,
                        _ => Cursors.Arrow
                    };
                    return;
                }
            }

            // Check border (use actual canvas coordinates, not snapped)
            double borderThickness = 3;
            var innerBox = new WpfRect(
                box.X + borderThickness,
                box.Y + borderThickness,
                box.Width - borderThickness * 2,
                box.Height - borderThickness * 2
            );

            // Expand border hit area outward for easier grabbing
            var outerBox = box;
            outerBox.Inflate(2, 2);

            if (outerBox.Contains(canvasPos) && !innerBox.Contains(canvasPos))
            {
                Mouse.OverrideCursor = Cursors.SizeAll;
            }
            else if (innerBox.Contains(canvasPos))
            {
                Mouse.OverrideCursor = Cursors.IBeam;
            }
            else
            {
                Mouse.OverrideCursor = null; // Clear override when outside
                PaintCanvas.Cursor = Cursors.Cross;
            }
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

        private void SyncBrushPreviewColor()
        {
            if (_brushPreview == null) return;
            if (_eraseMode) return;
            _brushPreview.Stroke = new SolidColorBrush(_brushColor);
        }

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

        private void Tool_MoveSelection_Click(object sender, RoutedEventArgs e)
            => _img?.BeginMoveSelectedArea();

        private void Tool_PaintInsideSelection_Click(object sender, RoutedEventArgs e)
            => _img?.EnableSelectionPaintMode();

        private void Tool_CopySelection_Click(object sender, RoutedEventArgs e)
            => _img?.CopySelectionToClipboard();

        private void Tool_Erase_Click(object sender, RoutedEventArgs e)
        {
            // If there's a floating selection, commit it first
            if (_img != null && _img.IsFloatingActive)
            {
                _img.CommitFloatingPaste();
            }

            // **FIX: Disable move tool if active**
            if (_moveToolArmed)
            {
                _moveToolArmed = false;
                UnhookMoveToolPanEvents();
            }

            _eraseMode = !_eraseMode;
            Shapes_Disarm();
            Text_Disarm();
            PaintCanvas.Cursor = Cursors.None;
            UpdateBrushPreviewVisibility(true);
            ApplyInkBrushAttributes();
        }

        private void Tool_ColorPicker_Click(object sender, RoutedEventArgs e)
        {
            int savedSelectionStart = 0;
            int savedSelectionLength = 0;
            if (_textToolArmed && _textEditor != null)
            {
                savedSelectionStart = _textEditor.SelectionStart;
                savedSelectionLength = _textEditor.SelectionLength;
            }

            using var dlg = new WF.ColorDialog { AllowFullOpen = true, FullOpen = true };
            if (dlg.ShowDialog() == WF.DialogResult.OK)
            {
                _brushColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _eraseMode = false;

                if (_textToolArmed && _textEditor != null)
                {
                    _textEditor.Foreground = new SolidColorBrush(_brushColor);
                    StatusText.Content = "Text color updated.";

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_textEditor != null)
                        {
                            _textEditor.Focus();
                            Keyboard.Focus(_textEditor);
                            _textEditor.SelectionStart = savedSelectionStart;
                            _textEditor.SelectionLength = savedSelectionLength;
                        }
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
                else
                {
                    // If there's a floating selection, commit it first
                    if (_img != null && _img.IsFloatingActive)
                    {
                        _img.CommitFloatingPaste();
                    }

                    UpdateBrushPreviewVisibility(!_textToolArmed);
                    ApplyInkBrushAttributes();
                }
            }
        }

        private void Tool_Brushes_Click(object sender, RoutedEventArgs e)
        {
            // If there's a floating selection, commit it first
            if (_img != null && _img.IsFloatingActive)
            {
                _img.CommitFloatingPaste();
            }

            // **FIX: Disable move tool if active**
            if (_moveToolArmed)
            {
                _moveToolArmed = false;
                UnhookMoveToolPanEvents();
            }

            _eraseMode = false;
            Shapes_Disarm();
            Text_Disarm();
            PaintCanvas.Cursor = Cursors.None;
            UpdateBrushPreviewVisibility(true);
            ApplyInkBrushAttributes();
            StatusText.Content = "Paint Brushes enabled.";
        }

        private void Colors_BrushSize_Click(object sender, RoutedEventArgs e)
        {
            // If there's a floating selection, commit it first
            if (_img != null && _img.IsFloatingActive)
            {
                _img.CommitFloatingPaste();
            }

            var dlg = new BrushSizeWindow(_brushIndex, _brushSizes)
            {
                Owner = this
            };

            if (dlg.ShowDialog() == true)
            {
                _brushIndex = dlg.SelectedIndex;
                _eraseMode = false;
                Text_Disarm();
                PaintCanvas.Cursor = Cursors.None;
                UpdateBrushPreviewVisibility(true);
                ApplyInkBrushAttributes();
            }
        }

        private void Tool_TextBox_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;

            // If there's a floating selection, commit it first
            if (_img != null && _img.IsFloatingActive)
            {
                _img.CommitFloatingPaste();
            }

            Shapes_Disarm();
            Text_Disarm();
            _textToolArmed = true;
            _textPhase = TextPhase.Arming;
            UpdateBrushPreviewVisibility(false);
            PaintCanvas.Cursor = Cursors.Cross;
            StatusText.Content = "Text: drag to create a box; right-click for size/color/commit.";
        }

        private void Text_OnCanvasMouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (!_textToolArmed) return;

            var pos = e.GetPosition(PaintCanvas);

            if (_textPhase == TextPhase.Editing && _textBorder != null && _textEditor != null)
            {
                var box = new WpfRect(InkCanvas.GetLeft(_textBorder), InkCanvas.GetTop(_textBorder), _textBorder.Width,
                    _textBorder.Height);

                // Check handles first (use canvas position for hit testing)
                foreach (var h in _textHandles)
                {
                    var hx = InkCanvas.GetLeft(h);
                    var hy = InkCanvas.GetTop(h);
                    var hr = new WpfRect(hx, hy, h.Width, h.Height);
                    hr.Inflate(2, 2); // Expand hit area

                    if (hr.Contains(pos))
                    {
                        _isResizing = true;
                        _resizeCorner = (string)h.Tag;
                        _boxStart = box;
                        _moveStart = SnapToImagePixel(pos);
                        PaintCanvas.CaptureMouse();
                        e.Handled = true;
                        return;
                    }
                }

                // Check if clicking on the border (expanded hit area)
                double borderThickness = 3;
                var innerBox = new WpfRect(
                    box.X + borderThickness,
                    box.Y + borderThickness,
                    box.Width - borderThickness * 2,
                    box.Height - borderThickness * 2
                );

                // Expand outer box for easier border grabbing
                var outerBox = box;
                outerBox.Inflate(2, 2);

                // Clicking on border (outside inner box but inside expanded outer box)
                if (outerBox.Contains(pos) && !innerBox.Contains(pos))
                {
                    _isMoving = true;
                    _boxStart = box;
                    _moveStart = SnapToImagePixel(pos);
                    PaintCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }

                // Clicking inside the text content area - allow normal text interaction
                return;
            }

            if (_textPhase == TextPhase.Arming)
            {
                _textPhase = TextPhase.Dragging;
                _textDragStart = SnapToImagePixel(pos);

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
                PlaceOnCanvas(_textDragRect, _textDragStart.X, _textDragStart.Y, 1, 1);

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
                return;
            }

            if (_isMoving && _textBorder != null)
            {
                var dx = p.X - _moveStart.X;
                var dy = p.Y - _moveStart.Y;
                var r = new WpfRect(_boxStart.X + dx, _boxStart.Y + dy, _boxStart.Width, _boxStart.Height);
                r = ClampRectToImage(r);
                PlaceOnCanvas(_textBorder, r.X, r.Y, r.Width, r.Height);
                UpdateTextHandles();
                Mouse.OverrideCursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            if (_isResizing && _textBorder != null)
            {
                double dx = p.X - _moveStart.X;
                double dy = p.Y - _moveStart.Y;

                double newX = _boxStart.X;
                double newY = _boxStart.Y;
                double newW = _boxStart.Width;
                double newH = _boxStart.Height;

                switch (_resizeCorner)
                {
                    case "NW":
                        // Calculate new dimensions
                        double testW = _boxStart.Width - dx;
                        double testH = _boxStart.Height - dy;

                        // Apply width change only if above minimum
                        if (testW >= MinTextBoxSize)
                        {
                            newW = testW;
                            newX = _boxStart.X + dx;
                        }
                        else
                        {
                            newW = MinTextBoxSize;
                            newX = _boxStart.X + _boxStart.Width - MinTextBoxSize;
                        }

                        // Apply height change only if above minimum
                        if (testH >= MinTextBoxSize)
                        {
                            newH = testH;
                            newY = _boxStart.Y + dy;
                        }
                        else
                        {
                            newH = MinTextBoxSize;
                            newY = _boxStart.Y + _boxStart.Height - MinTextBoxSize;
                        }

                        break;

                    case "NE":
                        newW = _boxStart.Width + dx;
                        if (newW < MinTextBoxSize) newW = MinTextBoxSize;

                        double testHNE = _boxStart.Height - dy;
                        if (testHNE >= MinTextBoxSize)
                        {
                            newH = testHNE;
                            newY = _boxStart.Y + dy;
                        }
                        else
                        {
                            newH = MinTextBoxSize;
                            newY = _boxStart.Y + _boxStart.Height - MinTextBoxSize;
                        }

                        break;

                    case "SW":
                        double testWSW = _boxStart.Width - dx;
                        if (testWSW >= MinTextBoxSize)
                        {
                            newW = testWSW;
                            newX = _boxStart.X + dx;
                        }
                        else
                        {
                            newW = MinTextBoxSize;
                            newX = _boxStart.X + _boxStart.Width - MinTextBoxSize;
                        }

                        newH = _boxStart.Height + dy;
                        if (newH < MinTextBoxSize) newH = MinTextBoxSize;
                        break;

                    case "SE":
                        newW = _boxStart.Width + dx;
                        newH = _boxStart.Height + dy;
                        if (newW < MinTextBoxSize) newW = MinTextBoxSize;
                        if (newH < MinTextBoxSize) newH = MinTextBoxSize;
                        break;
                }

                var r = new WpfRect(newX, newY, newW, newH);
                r = ClampRectToImage(r);

                PlaceOnCanvas(_textBorder, r.X, r.Y, r.Width, r.Height);
                UpdateTextHandles();

                Mouse.OverrideCursor = _resizeCorner switch
                {
                    "NW" => Cursors.SizeNWSE,
                    "NE" => Cursors.SizeNESW,
                    "SW" => Cursors.SizeNESW,
                    "SE" => Cursors.SizeNWSE,
                    _ => Cursors.Arrow
                };

                e.Handled = true;
                return;
            }
        }

        private void Text_OnCanvasMouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (!_textToolArmed) return;

            if (_textPhase == TextPhase.Dragging && _textDragRect != null)
            {
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
            _textBorder = new Border
            {
                BorderBrush = Brushes.DeepSkyBlue,
                BorderThickness = new Thickness(3),
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

            CreateTextHandles();
            UpdateTextHandles();

            _textEditor.Focus();
            _textEditor.CaretIndex = _textEditor.Text.Length;

            PaintCanvas.Cursor = Cursors.IBeam;
            StatusText.Content =
                "Text editing: type; drag border to move; drag corners to resize; right-click for options.";
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

                var mat = _img.Mat;
                if (mat == null || mat.Empty())
                {
                    MessageBox.Show("Active layer is null or empty!");
                    Text_Disarm();
                    return;
                }

                int imgW = mat.Width;
                int imgH = mat.Height;
                int step = (int)mat.Step();
                var dst = new byte[step * imgH];
                Marshal.Copy(mat.Data, dst, 0, dst.Length);

                int left = (int)System.Math.Round(InkCanvas.GetLeft(_textBorder));
                int top = (int)System.Math.Round(InkCanvas.GetTop(_textBorder));

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
                    }
                }

                Marshal.Copy(dst, 0, mat.Data, dst.Length);
                _img.ForceRefreshView();

                StatusText.Content = "Text committed.";
                _hasUnsavedChanges = true;
                Text_Disarm();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error committing text: {ex.Message}\n{ex.StackTrace}");
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

            Mouse.OverrideCursor = null; // Clear the override
            PaintCanvas.Cursor = Cursors.None;
            UpdateBrushPreviewVisibility(true);
        }

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

        // ========== LAYER FILTERS (NEW CLEAN VERSION) ==========
        private void Filter_Gaussian_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            ShowLayerFilterWindow(LayerFilterType.Gaussian);
        }

        private void Filter_Sobel_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            ShowLayerFilterWindow(LayerFilterType.Sobel);
        }

        private void Filter_Binary_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            ShowLayerFilterWindow(LayerFilterType.Binary);
        }

        private void Filter_HistogramThreshold_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureImageOpen()) return;
            ShowLayerFilterWindow(LayerFilterType.Otsu);
        }

        private void ShowLayerFilterWindow(LayerFilterType filterType)
        {
            var dlg = new LayerFilterWindow(this, filterType)
            {
                Owner = this
            };

            if (dlg.ShowDialog() == true)
            {
                _hasUnsavedChanges = true;
                StatusText.Content = $"Applied {filterType} filter";
            }
            else
            {
                StatusText.Content = "Filter cancelled";
            }
        }
    }

    // ========== BRUSH SIZE WINDOW ==========
    internal sealed class BrushSizeWindow : WWindow
    {
        public int SelectedIndex { get; private set; }

        private readonly Slider _sizeSlider;
        private readonly TextBlock _sizeValue;
        private readonly TextBox _sizeInput;
        private readonly Border _preview;
        private int[] _sizes;
        private int _customSize;

        public BrushSizeWindow(int currentIndex, int[] brushSizes)
        {
            _sizes = brushSizes;
            SelectedIndex = currentIndex;
            _customSize = _sizes[currentIndex];

            Title = "Brush Size";
            Width = 380;
            Height = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 243, 243));

            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = new TextBlock
            {
                Text = "BRUSH SIZE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(header, 0);

            // Slider row
            var sliderRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            _sizeSlider = new Slider
            {
                Minimum = 1,
                Maximum = 160,
                Value = _customSize,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            _sizeSlider.ValueChanged += (s, e) =>
            {
                _customSize = (int)_sizeSlider.Value;
                _sizeValue.Text = $"{_customSize} px";
                _sizeInput.Text = _customSize.ToString();
                UpdatePreview();
            };
            Grid.SetColumn(_sizeSlider, 0);

            _sizeValue = new TextBlock
            {
                Text = $"{_customSize} px",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Foreground,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(_sizeValue, 1);

            sliderRow.Children.Add(_sizeSlider);
            sliderRow.Children.Add(_sizeValue);
            Grid.SetRow(sliderRow, 1);

            // Custom input row
            var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var inputLabel = new TextBlock
            {
                Text = "Custom size (px):",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Foreground
            };
            Grid.SetColumn(inputLabel, 0);

            _sizeInput = new TextBox
            {
                Text = _customSize.ToString(),
                Width = 80,
                Padding = new Thickness(6, 4, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _sizeInput.TextChanged += (s, e) =>
            {
                if (int.TryParse(_sizeInput.Text, out int val))
                {
                    val = Math.Clamp(val, 1, 160);
                    _customSize = val;
                    _sizeSlider.Value = val;
                    UpdatePreview();
                }
            };
            _sizeInput.PreviewTextInput += (s, e) =>
            {
                // Only allow digits
                e.Handled = !int.TryParse(e.Text, out _);
            };
            Grid.SetColumn(_sizeInput, 1);

            inputRow.Children.Add(inputLabel);
            inputRow.Children.Add(_sizeInput);
            Grid.SetRow(inputRow, 2);

            // Preview
            var previewLabel = new TextBlock
            {
                Text = "PREVIEW",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(previewLabel, 3);
            previewLabel.VerticalAlignment = VerticalAlignment.Top;

            _preview = new Border
            {
                Width = 340,
                Height = 100,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 63, 63, 70)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(255, 45, 45, 45)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 30, 0, 0)
            };

            var previewCanvas = new Canvas();
            _preview.Child = previewCanvas;
            Grid.SetRow(_preview, 3);

            UpdatePreview();

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            var okBtn = new Button
            {
                Content = "OK",
                Width = 80,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            okBtn.Click += (s, e) =>
            {
                // Update the brush sizes array with the custom value
                // Find the closest existing size or add new one
                int closestIndex = 0;
                int closestDiff = Math.Abs(_sizes[0] - _customSize);

                for (int i = 1; i < _sizes.Length; i++)
                {
                    int diff = Math.Abs(_sizes[i] - _customSize);
                    if (diff < closestDiff)
                    {
                        closestDiff = diff;
                        closestIndex = i;
                    }
                }

                // If exact match, use that index
                if (_sizes[closestIndex] == _customSize)
                {
                    SelectedIndex = closestIndex;
                }
                else
                {
                    // Replace the closest size with our custom size
                    _sizes[closestIndex] = _customSize;
                    SelectedIndex = closestIndex;
                }

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

            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            Grid.SetRow(buttonPanel, 4);

            mainGrid.Children.Add(header);
            mainGrid.Children.Add(sliderRow);
            mainGrid.Children.Add(inputRow);
            mainGrid.Children.Add(previewLabel);
            mainGrid.Children.Add(_preview);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;

            // Focus the input box for quick typing
            Loaded += (s, e) => _sizeInput.Focus();
        }

        private void UpdatePreview()
        {
            int size = _customSize;

            if (_preview.Child is Canvas canvas)
            {
                canvas.Children.Clear();

                // Draw a centered square representing the brush
                double maxSize = Math.Min(_preview.Width - 20, _preview.Height - 20);
                double displaySize = Math.Min(size, maxSize);

                var rect = new Rectangle
                {
                    Width = displaySize,
                    Height = displaySize,
                    Fill = Brushes.White,
                    Stroke = Brushes.LightGray,
                    StrokeThickness = 1
                };

                Canvas.SetLeft(rect, (_preview.Width - displaySize) / 2);
                Canvas.SetTop(rect, (_preview.Height - displaySize) / 2);

                canvas.Children.Add(rect);

                // Add size label
                var label = new TextBlock
                {
                    Text = $"{size}×{size} px",
                    Foreground = Brushes.LightGray,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Canvas.SetLeft(label, (_preview.Width - 60) / 2);
                Canvas.SetTop(label, _preview.Height - 15);
                canvas.Children.Add(label);
            }
        }
    }

    // ========== LAYER FILTER TYPES ==========
    public enum LayerFilterType
    {
        Gaussian,
        Sobel,
        Binary,
        Otsu
    }

    // ========== LAYER FILTER PREVIEW WINDOW ==========
    internal sealed class LayerFilterWindow : WWindow
    {
        private readonly MainWindow _main;

        private readonly LayerFilterType _filterType;

        private Mat? _originalMat;

        private Mat? _filteredMat;

        private Slider? _thresholdSlider;

        private TextBlock? _thresholdLabel;

        private StackPanel? _controlsPanel;

        private void BuildUI()
        {
            var mainGrid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = new TextBlock
            {
                Text = $"{_filterType.ToString().ToUpper()} FILTER",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(header, 0);

            // Info text about live preview
            var livePreviewText = new TextBlock
            {
                Text = "⚡ Live preview shown on canvas",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 200, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(livePreviewText, 1);

            // Controls panel
            _controlsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            BuildFilterControls();
            Grid.SetRow(_controlsPanel, 2);

            // Info text - moved ABOVE buttons
            var infoText = new TextBlock
            {
                Text = "Press Enter to apply • Esc to discard",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(infoText, 2);
            infoText.VerticalAlignment = VerticalAlignment.Bottom;

// Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)  // Add top margin
            };

            var applyBtn = new Button
            {
                Content = "✓ Apply",
                Width = 120,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(45, 140, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0)
            };
            applyBtn.Click += (s, e) =>
            {
                DialogResult = true;
                Close();
            };

            var discardBtn = new Button
            {
                Content = "✕ Discard",
                Width = 120,
                Padding = new Thickness(12, 8, 12, 8),
                IsCancel = true,
                Background = new SolidColorBrush(Color.FromRgb(140, 45, 45)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0)
            };
            discardBtn.Click += (s, e) =>
            {
                DialogResult = false;
                Close();
            };

            buttonPanel.Children.Add(applyBtn);
            buttonPanel.Children.Add(discardBtn);
            Grid.SetRow(buttonPanel, 3);

            mainGrid.Children.Add(header);
            mainGrid.Children.Add(livePreviewText);
            mainGrid.Children.Add(_controlsPanel);
            mainGrid.Children.Add(infoText);        // Info text now in row 2
            mainGrid.Children.Add(buttonPanel);     // Buttons in row 3

            Content = mainGrid;
        }

        public LayerFilterWindow(MainWindow main, LayerFilterType filterType)
        {
            _main = main;
            _filterType = filterType;

            Title = $"{filterType} Filter";
            Width = 420; // Changed from 600
            Height = 280; // Changed from 520
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 243, 243));

            // Backup original
            if (_main._img?.Mat != null && !_main._img.Mat.Empty())
            {
                _originalMat = _main._img.Mat.Clone();
            }

            BuildUI();
            ApplyFilter();

            // Handle window closing
            Closing += (s, e) =>
            {
                if (DialogResult != true)
                {
                    RestoreOriginal();
                }

                Cleanup();
            };

            // Keyboard shortcuts
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    DialogResult = true;
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    Close();
                    e.Handled = true;
                }
            };
        }

        private void BuildFilterControls()
        {
            if (_controlsPanel == null) return;

            if (_filterType == LayerFilterType.Binary)
            {
                var label = new TextBlock
                {
                    Text = "THRESHOLD",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                _controlsPanel.Children.Add(label);

                var sliderGrid = new Grid();
                sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

                _thresholdSlider = new Slider
                {
                    Minimum = 0,
                    Maximum = 255,
                    Value = 128,
                    TickFrequency = 1,
                    IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(_thresholdSlider, 0);

                _thresholdLabel = new TextBlock
                {
                    Text = "128",
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = Foreground,
                    Width = 60,
                    TextAlignment = TextAlignment.Center
                };
                Grid.SetColumn(_thresholdLabel, 1);

                _thresholdSlider.ValueChanged += (s, e) =>
                {
                    if (_thresholdLabel != null)
                    {
                        _thresholdLabel.Text = ((int)e.NewValue).ToString();
                    }

                    ApplyFilter();
                };

                sliderGrid.Children.Add(_thresholdSlider);
                sliderGrid.Children.Add(_thresholdLabel);
                _controlsPanel.Children.Add(sliderGrid);
            }
            else
            {
                var infoText = new TextBlock
                {
                    Text = GetFilterDescription(),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 176, 176)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 0)
                };
                _controlsPanel.Children.Add(infoText);
            }
        }

        private string GetFilterDescription()
        {
            return _filterType switch
            {
                LayerFilterType.Gaussian => "Smooths the image using a Gaussian blur kernel (5×5).",
                LayerFilterType.Sobel => "Detects edges using the Sobel operator.",
                LayerFilterType.Otsu => "Automatically finds the optimal threshold using Otsu's method.",
                _ => ""
            };
        }

        private void ApplyFilter()
        {
            if (_originalMat == null || _originalMat.Empty()) return;

            _filteredMat?.Dispose();
            _filteredMat = _originalMat.Clone();

            using var alpha = new Mat();
            Cv2.ExtractChannel(_originalMat, alpha, 3);

            switch (_filterType)
            {
                case LayerFilterType.Gaussian:
                    Cv2.GaussianBlur(_originalMat, _filteredMat, new OpenCvSharp.Size(5, 5), 0);
                    Cv2.InsertChannel(alpha, _filteredMat, 3);
                    break;

                case LayerFilterType.Sobel:
                    using (var gray = new Mat())
                    using (var gx = new Mat())
                    using (var gy = new Mat())
                    using (var agx = new Mat())
                    using (var agy = new Mat())
                    using (var mag = new Mat())
                    using (var bgr = new Mat())
                    {
                        Cv2.CvtColor(_originalMat, gray, ColorConversionCodes.BGRA2GRAY);
                        Cv2.Sobel(gray, gx, MatType.CV_16S, 1, 0, ksize: 3);
                        Cv2.Sobel(gray, gy, MatType.CV_16S, 0, 1, ksize: 3);
                        Cv2.ConvertScaleAbs(gx, agx);
                        Cv2.ConvertScaleAbs(gy, agy);
                        Cv2.AddWeighted(agx, 0.5, agy, 0.5, 0, mag);
                        Cv2.CvtColor(mag, bgr, ColorConversionCodes.GRAY2BGR);
                        var chans = bgr.Split();
                        Cv2.Merge(new[] { chans[0], chans[1], chans[2], alpha }, _filteredMat);
                        foreach (var c in chans) c.Dispose();
                    }

                    break;

                case LayerFilterType.Binary:
                    using (var gray = new Mat())
                    using (var bw = new Mat())
                    using (var bgr = new Mat())
                    {
                        Cv2.CvtColor(_originalMat, gray, ColorConversionCodes.BGRA2GRAY);
                        double threshold = _thresholdSlider?.Value ?? 128;
                        Cv2.Threshold(gray, bw, threshold, 255, ThresholdTypes.Binary);
                        Cv2.CvtColor(bw, bgr, ColorConversionCodes.GRAY2BGR);
                        var chans = bgr.Split();
                        Cv2.Merge(new[] { chans[0], chans[1], chans[2], alpha }, _filteredMat);
                        foreach (var c in chans) c.Dispose();
                    }

                    break;

                case LayerFilterType.Otsu:
                    using (var gray = new Mat())
                    using (var bw = new Mat())
                    using (var bgr = new Mat())
                    {
                        Cv2.CvtColor(_originalMat, gray, ColorConversionCodes.BGRA2GRAY);
                        Cv2.Threshold(gray, bw, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                        Cv2.CvtColor(bw, bgr, ColorConversionCodes.GRAY2BGR);
                        var chans = bgr.Split();
                        Cv2.Merge(new[] { chans[0], chans[1], chans[2], alpha }, _filteredMat);
                        foreach (var c in chans) c.Dispose();
                    }

                    break;
            }

            UpdateMainWindowPreview();
        }

        private void UpdateMainWindowPreview()
        {
            if (_filteredMat == null || _filteredMat.Empty()) return;
            if (_main._img?.Mat == null || _main._img.Mat.Empty()) return;

            int step = (int)_main._img.Mat.Step();
            int w = _filteredMat.Width;
            int h = _filteredMat.Height;

            var srcBytes = new byte[step * h];
            Marshal.Copy(_filteredMat.Data, srcBytes, 0, Math.Min(srcBytes.Length, (int)_filteredMat.Total() * 4));

            var dstBytes = new byte[step * h];
            Marshal.Copy(_main._img.Mat.Data, dstBytes, 0, dstBytes.Length);

            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(srcBytes, y * rowBytes, dstBytes, y * step, rowBytes);
            }

            Marshal.Copy(dstBytes, 0, _main._img.Mat.Data, dstBytes.Length);
            _main._img.ForceRefreshView();
        }

        private void RestoreOriginal()
        {
            if (_originalMat == null || _originalMat.Empty()) return;
            if (_main._img?.Mat == null || _main._img.Mat.Empty()) return;

            int step = (int)_main._img.Mat.Step();
            int h = _originalMat.Height;

            var srcBytes = new byte[step * h];
            Marshal.Copy(_originalMat.Data, srcBytes, 0, Math.Min(srcBytes.Length, (int)_originalMat.Total() * 4));
            Marshal.Copy(srcBytes, 0, _main._img.Mat.Data, Math.Min(srcBytes.Length, step * h));

            _main._img.ForceRefreshView();
        }

        private void Cleanup()
        {
            _originalMat?.Dispose();
            _originalMat = null;
            _filteredMat?.Dispose();
            _filteredMat = null;
        }
    }
}
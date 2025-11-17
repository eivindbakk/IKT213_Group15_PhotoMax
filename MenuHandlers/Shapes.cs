// File: MenuHandlers/Shapes.cs

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WF = System.Windows.Forms;
using OpenCvSharp;

// Aliases
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;
using WWindow = System.Windows.Window; // avoid clash with OpenCvSharp.Window

namespace PhotoMax
{
    public partial class MainWindow
    {
        internal enum Shapes_Kind
        {
            Rectangle,
            Ellipse,
            Line
        }

        private enum Shapes_Drag
        {
            None,
            Create,
            Move,
            ResizeNW,
            ResizeNE,
            ResizeSW,
            ResizeSE,
            MoveLine,
            ResizeP1,
            ResizeP2
        }

        // ===== Shapes state =====
        private bool _sh_armed = false;
        private bool _sh_pendingCommit = false;
        private Shapes_Kind _sh_kind = Shapes_Kind.Rectangle;

        private Color _sh_outline = Colors.Red;
        private Color _sh_fill = Color.FromArgb(0, 0, 0, 0);
        private int _sh_strokePx = 1;

        private Shape? _sh_active;
        private readonly List<FrameworkElement> _sh_handles = new();

        private Shapes_Drag _sh_dragMode = Shapes_Drag.None;
        private WpfPoint _sh_dragStart;
        private WpfPoint _sh_moveStart;

        private Canvas? _sh_host;

        private readonly List<Shape> _sh_pendingShapes = new();

        // For resize anchoring (original box at start of resize)
        private WpfRect _sh_boxStart;
        private const double Shapes_MinSize = 1.0;

        // ---------- Menu ----------
        private void Shapes_List_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ShapesConfigWindow(_sh_kind, _sh_outline, _sh_fill, _sh_strokePx)
            {
                Owner = this
            };

            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                _sh_kind = dlg.SelectedKind;
                _sh_outline = dlg.OutlineColor;
                _sh_fill = dlg.FillColor;
                _sh_strokePx = dlg.StrokeThickness;

                Shapes_Arm();
                Shapes_UpdateActiveBrushes();

                StatusText.Content =
                    $"Shapes: {_sh_kind} | Outline: RGB({_sh_outline.R},{_sh_outline.G},{_sh_outline.B}) | " +
                    $"Fill: {(_sh_fill.A == 0 ? "Transparent" : $"RGB({_sh_fill.R},{_sh_fill.G},{_sh_fill.B})")} | " +
                    $"Stroke: {_sh_strokePx}px";
            }
        }

        // Kept for compatibility; not shown in menu anymore
        private void Shapes_BakePending_Click(object? sender, RoutedEventArgs e)
        {
            var mat = TryGetImageMat_Shapes();
            if (mat == null || mat.Empty())
            {
                MessageBox.Show("Open or create an image first.", "PhotoMax",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var host = Shapes_GetHostOrThrow();
            var toBake = new List<Shape>(_sh_pendingShapes);
            foreach (var s in toBake)
            {
                if (Shapes_TryCommitToImage(s))
                {
                    host.Children.Remove(s);
                    _sh_pendingShapes.Remove(s);
                }
            }

            TryForceRefreshView_Shapes();
        }

        // ---------- Arming / Disarming ----------
        private void Shapes_Arm()
        {
            if (_sh_armed) return;

            // **FIX: Disable move tool if active**
            Tool_DisableMoveToolIfActive();

            _sh_host = Shapes_FindHost();
            if (_sh_host == null)
            {
                MessageBox.Show("Shapes host not found (need an Artboard Canvas or PaintCanvas).");
                return;
            }

            _sh_armed = true;

            _sh_host.PreviewMouseLeftButtonDown += Shapes_MouseDown;
            _sh_host.PreviewMouseMove += Shapes_MouseMove;
            _sh_host.PreviewMouseLeftButtonUp += Shapes_MouseUp;
            _sh_host.PreviewKeyDown += Shapes_KeyDown;

            _sh_host.Focusable = true;
            _sh_host.Focus();

            if (StatusText != null)
                StatusText.Content =
                    "Shapes: drag to draw; drag to move; drag corners to resize; Enter=commit, Esc=cancel.";
        }
        
        private void Tool_DisableMoveToolIfActive()
        {
            // Access the _moveToolArmed field and disable if needed
            var field = GetType().GetField("_moveToolArmed", 
                System.Reflection.BindingFlags.Instance | 
                System.Reflection.BindingFlags.NonPublic);
    
            if (field != null)
            {
                bool isArmed = (bool)(field.GetValue(this) ?? false);
                if (isArmed)
                {
                    field.SetValue(this, false);
            
                    // Call unhook method
                    var unhookMethod = GetType().GetMethod("UnhookMoveToolPanEvents",
                        System.Reflection.BindingFlags.Instance | 
                        System.Reflection.BindingFlags.NonPublic);
                    unhookMethod?.Invoke(this, null);
                }
            }
        }

        private void Shapes_Disarm()
        {
            // If there's a floating selection, commit it first
            if (_img != null && _img.IsFloatingActive)
            {
                _img.CommitFloatingPaste();
            }

            if (!_sh_armed || _sh_host == null) return;

            _sh_armed = false;

            _sh_host.PreviewMouseLeftButtonDown -= Shapes_MouseDown;
            _sh_host.PreviewMouseMove -= Shapes_MouseMove;
            _sh_host.PreviewMouseLeftButtonUp -= Shapes_MouseUp;
            _sh_host.PreviewKeyDown -= Shapes_KeyDown;

            Shapes_ClearActive();
            _sh_pendingCommit = false;
            _sh_dragMode = Shapes_Drag.None;
            _sh_host.Cursor = Cursors.Arrow;
        }

        // ---------- Mouse / Keyboard ----------
        private void Shapes_MouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (_sh_host == null) return;
            _sh_host.Focus();

            var pos = e.GetPosition(_sh_host);

            // If a shape is pending commit, manipulate it instead of creating a new one
            if (_sh_active != null && _sh_pendingCommit)
            {
                // Line: endpoints or move
                if (_sh_active is Line ln)
                {
                    var p1 = new WpfPoint(ln.X1, ln.Y1);
                    var p2 = new WpfPoint(ln.X2, ln.Y2);
                    double d1 = Shapes_Distance(pos, p1);
                    double d2 = Shapes_Distance(pos, p2);

                    if (d1 < 10)
                    {
                        _sh_dragMode = Shapes_Drag.ResizeP1;
                    }
                    else if (d2 < 10)
                    {
                        _sh_dragMode = Shapes_Drag.ResizeP2;
                    }
                    else
                    {
                        _sh_dragMode = Shapes_Drag.MoveLine;
                    }

                    _sh_dragStart = pos;
                    Mouse.Capture(_sh_host);
                    e.Handled = true;
                    return;
                }
                else
                {
                    // Rect/Ellipse: corner handles
                    if (Shapes_TryHitHandle(pos, out var mode))
                    {
                        if (_sh_active != null)
                        {
                            _sh_boxStart = new WpfRect(
                                Canvas.GetLeft(_sh_active),
                                Canvas.GetTop(_sh_active),
                                _sh_active.Width,
                                _sh_active.Height);
                        }

                        _sh_dragMode = mode;
                        _sh_dragStart = pos;
                        Mouse.Capture(_sh_host);
                        e.Handled = true;
                        return;
                    }

                    // Inside -> move
                    double left = Canvas.GetLeft(_sh_active);
                    double top = Canvas.GetTop(_sh_active);
                    double w = _sh_active.Width;
                    double h = _sh_active.Height;

                    if (pos.X >= left && pos.X <= left + w &&
                        pos.Y >= top && pos.Y <= top + h)
                    {
                        _sh_dragMode = Shapes_Drag.Move;
                        _sh_dragStart = pos;
                        _sh_moveStart = new WpfPoint(left, top);
                        Mouse.Capture(_sh_host);
                        e.Handled = true;
                        return;
                    }
                }

                // Clicked outside pending shape: ignore (keep shape)
                return;
            }

            if (!_sh_armed) return;

            // Begin creating a new shape
            _sh_dragMode = Shapes_Drag.Create;
            _sh_dragStart = pos;

            _sh_active = Shapes_CreateElement(_sh_kind);
            if (_sh_active == null) return;

            Shapes_AttachContextMenu(_sh_active);
            RenderOptions.SetEdgeMode(_sh_active, EdgeMode.Aliased);
            _sh_active.SnapsToDevicePixels = true;

            if (_sh_active is Line lnNew)
            {
                lnNew.X1 = pos.X;
                lnNew.Y1 = pos.Y;
                lnNew.X2 = pos.X;
                lnNew.Y2 = pos.Y;
            }
            else
            {
                Canvas.SetLeft(_sh_active, pos.X);
                Canvas.SetTop(_sh_active, pos.Y);
                _sh_active.Width = 0;
                _sh_active.Height = 0;
            }

            Panel.SetZIndex(_sh_active, 10000);
            _sh_host.Children.Add(_sh_active);
            Mouse.Capture(_sh_host);
            e.Handled = true;
        }

        private void Shapes_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_sh_host == null) return;

            // Hover feedback: show move cursor when over body of pending shape
            if (_sh_pendingCommit && _sh_active != null && _sh_dragMode == Shapes_Drag.None)
            {
                var hoverPos = e.GetPosition(_sh_host);
                if (Shapes_HitShapeBody(_sh_active, hoverPos))
                    _sh_host.Cursor = Cursors.SizeAll;
                else
                    _sh_host.Cursor = Cursors.Arrow;
            }

            if (_sh_active == null) return;

            var curr = e.GetPosition(_sh_host);
            bool shift =
                Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            switch (_sh_dragMode)
            {
                case Shapes_Drag.Create:
                    if (_sh_active is Line ln)
                        Shapes_UpdateLinePreview(ln, _sh_dragStart, curr, shift);
                    else
                        Shapes_UpdateRectPreview(_sh_active, _sh_dragStart, curr, shift);
                    break;

                case Shapes_Drag.Move:
                {
                    double dx = curr.X - _sh_dragStart.X;
                    double dy = curr.Y - _sh_dragStart.Y;
                    Canvas.SetLeft(_sh_active, _sh_moveStart.X + dx);
                    Canvas.SetTop(_sh_active, _sh_moveStart.Y + dy);
                    Shapes_UpdateHandles();
                    break;
                }

                case Shapes_Drag.ResizeNW:
                case Shapes_Drag.ResizeNE:
                case Shapes_Drag.ResizeSW:
                case Shapes_Drag.ResizeSE:
                    if (!(_sh_active is Line))
                    {
                        Shapes_ResizeFromCorner(_sh_dragMode, curr, shift);
                        Shapes_UpdateHandles();
                    }

                    break;

                case Shapes_Drag.MoveLine:
                    if (_sh_active is Line m)
                    {
                        double dx = curr.X - _sh_dragStart.X;
                        double dy = curr.Y - _sh_dragStart.Y;
                        m.X1 += dx;
                        m.Y1 += dy;
                        m.X2 += dx;
                        m.Y2 += dy;
                        _sh_dragStart = curr;
                        Shapes_UpdateHandles();
                    }

                    break;

                case Shapes_Drag.ResizeP1:
                    if (_sh_active is Line p1)
                    {
                        Shapes_UpdateLineEndpoint(p1, editFirst: true, curr, shift);
                        Shapes_UpdateHandles();
                    }

                    break;

                case Shapes_Drag.ResizeP2:
                    if (_sh_active is Line p2)
                    {
                        Shapes_UpdateLineEndpoint(p2, editFirst: false, curr, shift);
                        Shapes_UpdateHandles();
                    }

                    break;
            }
        }

        private void Shapes_MouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (_sh_active == null || _sh_host == null) return;

            if (_sh_dragMode == Shapes_Drag.Create)
            {
                _sh_dragMode = Shapes_Drag.None;
                Mouse.Capture(null);

                var shape = _sh_active;

                // Check if anything meaningful was drawn
                bool hasArea = true;
                if (shape is Line ln)
                {
                    if (Math.Abs(ln.X1 - ln.X2) < 0.5 && Math.Abs(ln.Y1 - ln.Y2) < 0.5)
                        hasArea = false;
                }
                else
                {
                    if (shape.Width < 0.5 || shape.Height < 0.5)
                        hasArea = false;
                }

                if (hasArea)
                {
                    // Keep as live overlay so user can move/resize before committing
                    _sh_pendingCommit = true;
                    Shapes_AddHandles();
                    if (StatusText != null)
                        StatusText.Content =
                            "Shape ready: drag to move, drag corners to resize, Enter=commit to image, Esc=cancel.";
                }
                else
                {
                    // Nothing drawn -> remove overlay
                    _sh_host.Children.Remove(shape);
                    Shapes_ClearActive();
                    _sh_pendingCommit = false;
                }

                e.Handled = true;
                return;
            }

            if (_sh_dragMode != Shapes_Drag.None)
            {
                _sh_dragMode = Shapes_Drag.None;
                Mouse.Capture(null);
                e.Handled = true;
            }
        }

        private void Shapes_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_sh_active == null || _sh_host == null) return;

            if (e.Key == Key.Enter)
            {
                bool committed = Shapes_TryCommitToImage(_sh_active);
                if (committed)
                    _sh_host.Children.Remove(_sh_active);
                else if (!_sh_pendingShapes.Contains(_sh_active))
                    _sh_pendingShapes.Add(_sh_active);

                Shapes_ClearActive();
                _sh_pendingCommit = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_sh_pendingShapes.Contains(_sh_active))
                    _sh_pendingShapes.Remove(_sh_active);
                _sh_host.Children.Remove(_sh_active);
                Shapes_ClearActive();
                _sh_pendingCommit = false;
                e.Handled = true;
            }
            else if (e.Key == Key.R)
            {
                _sh_kind = Shapes_Kind.Rectangle;
                e.Handled = true;
            }
            else if (e.Key == Key.E)
            {
                _sh_kind = Shapes_Kind.Ellipse;
                e.Handled = true;
            }
            else if (e.Key == Key.L)
            {
                _sh_kind = Shapes_Kind.Line;
                e.Handled = true;
            }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                _sh_strokePx = Math.Max(1, Math.Min(64, _sh_strokePx + 1));
                Shapes_UpdateStrokePreview();
                e.Handled = true;
            }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                _sh_strokePx = Math.Max(1, _sh_strokePx - 1);
                Shapes_UpdateStrokePreview();
                e.Handled = true;
            }
        }

        // ---------- Preview + handles ----------
        private void Shapes_UpdateRectPreview(Shape s, WpfPoint start, WpfPoint curr, bool keepSquare)
        {
            double left = Math.Min(start.X, curr.X);
            double top = Math.Min(start.Y, curr.Y);
            double w = Math.Abs(curr.X - start.X);
            double h = Math.Abs(curr.Y - start.Y);

            if (keepSquare)
            {
                double side = Math.Max(w, h);
                left = (curr.X < start.X) ? start.X - side : start.X;
                top = (curr.Y < start.Y) ? start.Y - side : start.Y;
                w = side;
                h = side;
            }

            Canvas.SetLeft(s, left);
            Canvas.SetTop(s, top);
            s.Width = w;
            s.Height = h;
        }

        private void Shapes_UpdateLinePreview(Line ln, WpfPoint start, WpfPoint curr, bool snap45)
        {
            double x2 = curr.X;
            double y2 = curr.Y;
            if (snap45)
            {
                double dx = curr.X - start.X;
                double dy = curr.Y - start.Y;
                double ang = Math.Atan2(dy, dx);
                double step = Math.PI / 4.0;
                ang = Math.Round(ang / step) * step;
                double len = Math.Sqrt(dx * dx + dy * dy);
                x2 = start.X + len * Math.Cos(ang);
                y2 = start.Y + len * Math.Sin(ang);
            }

            ln.X1 = start.X;
            ln.Y1 = start.Y;
            ln.X2 = x2;
            ln.Y2 = y2;
        }

        private void Shapes_UpdateLineEndpoint(Line line, bool editFirst, WpfPoint curr, bool snap45)
        {
            if (!snap45)
            {
                if (editFirst)
                {
                    line.X1 = curr.X;
                    line.Y1 = curr.Y;
                }
                else
                {
                    line.X2 = curr.X;
                    line.Y2 = curr.Y;
                }

                return;
            }

            double otherX = editFirst ? line.X2 : line.X1;
            double otherY = editFirst ? line.Y2 : line.Y1;

            double dx = curr.X - otherX;
            double dy = curr.Y - otherY;

            double ang = Math.Atan2(dy, dx);
            double step = Math.PI / 4.0;
            ang = Math.Round(ang / step) * step;

            double len = Math.Sqrt(dx * dx + dy * dy);
            double x = otherX + len * Math.Cos(ang);
            double y = otherY + len * Math.Sin(ang);

            if (editFirst)
            {
                line.X1 = x;
                line.Y1 = y;
            }
            else
            {
                line.X2 = x;
                line.Y2 = y;
            }
        }

        private void Shapes_AddHandles()
        {
            Shapes_RemoveHandles();

            if (_sh_active == null || _sh_host == null) return;

            if (_sh_active is Line ln)
            {
                _sh_handles.Add(Shapes_MakeKnob(ln.X1, ln.Y1));
                _sh_handles.Add(Shapes_MakeKnob(ln.X2, ln.Y2));
            }
            else
            {
                double left = Canvas.GetLeft(_sh_active);
                double top = Canvas.GetTop(_sh_active);
                double w = _sh_active.Width;
                double h = _sh_active.Height;

                _sh_handles.Add(Shapes_MakeHandle(left, top, Cursors.SizeNWSE, Shapes_Drag.ResizeNW));
                _sh_handles.Add(Shapes_MakeHandle(left + w, top, Cursors.SizeNESW, Shapes_Drag.ResizeNE));
                _sh_handles.Add(Shapes_MakeHandle(left, top + h, Cursors.SizeNESW, Shapes_Drag.ResizeSW));
                _sh_handles.Add(Shapes_MakeHandle(left + w, top + h, Cursors.SizeNWSE, Shapes_Drag.ResizeSE));
            }
        }

        private void Shapes_UpdateHandles()
        {
            if (_sh_active == null) return;

            if (_sh_active is Line ln)
            {
                if (_sh_handles.Count >= 2)
                {
                    Shapes_SetHandlePos(_sh_handles[0], ln.X1, ln.Y1);
                    Shapes_SetHandlePos(_sh_handles[1], ln.X2, ln.Y2);
                }
            }
            else
            {
                double left = Canvas.GetLeft(_sh_active);
                double top = Canvas.GetTop(_sh_active);
                double w = _sh_active.Width;
                double h = _sh_active.Height;

                if (_sh_handles.Count >= 4)
                {
                    Shapes_SetHandlePos(_sh_handles[0], left, top);
                    Shapes_SetHandlePos(_sh_handles[1], left + w, top);
                    Shapes_SetHandlePos(_sh_handles[2], left, top + h);
                    Shapes_SetHandlePos(_sh_handles[3], left + w, top + h);
                }
            }
        }

        private void Shapes_RemoveHandles()
        {
            if (_sh_host == null)
            {
                _sh_handles.Clear();
                return;
            }

            foreach (var h in _sh_handles)
                _sh_host.Children.Remove(h);
            _sh_handles.Clear();
        }

        private FrameworkElement Shapes_MakeHandle(double x, double y, Cursor cursor, Shapes_Drag mode)
        {
            var r = new Rectangle
            {
                Width = 8,
                Height = 8,
                StrokeThickness = 1,
                Stroke = Brushes.Black,
                Fill = Brushes.White,
                Cursor = cursor
            };

            Canvas.SetLeft(r, x - 4);
            Canvas.SetTop(r, y - 4);
            Panel.SetZIndex(r, 10001);

            // Down/up handled via Shapes_MouseDown / Shapes_MouseUp on host
            r.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = false; };
            r.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = false; };

            _sh_host!.Children.Add(r);
            return r;
        }

        private FrameworkElement Shapes_MakeKnob(double x, double y)
        {
            var e = new Ellipse
            {
                Width = 8,
                Height = 8,
                StrokeThickness = 1,
                Stroke = Brushes.Black,
                Fill = Brushes.White,
                Cursor = Cursors.Cross
            };

            Canvas.SetLeft(e, x - 4);
            Canvas.SetTop(e, y - 4);
            Panel.SetZIndex(e, 10001);

            // Actual hit/drag handled in Shapes_MouseDown (for the line)
            e.PreviewMouseLeftButtonDown += (_, ev) => { ev.Handled = false; };
            e.PreviewMouseLeftButtonUp += (_, ev) => { ev.Handled = false; };

            _sh_host!.Children.Add(e);
            return e;
        }

        private void Shapes_SetHandlePos(FrameworkElement fe, double x, double y)
        {
            Canvas.SetLeft(fe, x - fe.Width / 2.0);
            Canvas.SetTop(fe, y - fe.Height / 2.0);
        }

        private bool Shapes_TryHitHandle(WpfPoint pos, out Shapes_Drag mode)
        {
            mode = Shapes_Drag.None;

            for (int i = 0; i < _sh_handles.Count; i++)
            {
                var h = _sh_handles[i];
                double left = Canvas.GetLeft(h);
                double top = Canvas.GetTop(h);
                var rect = new WpfRect(left, top, h.Width, h.Height);
                rect.Inflate(2, 2);
                if (rect.Contains(pos))
                {
                    mode = i switch
                    {
                        0 => Shapes_Drag.ResizeNW,
                        1 => Shapes_Drag.ResizeNE,
                        2 => Shapes_Drag.ResizeSW,
                        3 => Shapes_Drag.ResizeSE,
                        _ => Shapes_Drag.None
                    };
                    return true;
                }
            }

            return false;
        }

        private void Shapes_ResizeFromCorner(Shapes_Drag mode, WpfPoint curr, bool keepSquare)
        {
            if (_sh_active == null)
                return;

            // Starting rectangle when the drag began
            double left0 = _sh_boxStart.X;
            double top0 = _sh_boxStart.Y;
            double w0 = _sh_boxStart.Width;
            double h0 = _sh_boxStart.Height;

            // Fallback if _sh_boxStart was not initialised for some reason
            if (w0 <= 0 || h0 <= 0)
            {
                left0 = Canvas.GetLeft(_sh_active);
                top0 = Canvas.GetTop(_sh_active);
                w0 = _sh_active.Width;
                h0 = _sh_active.Height;
            }

            // Choose fixed anchor corner (opposite of the dragged handle)
            double anchorX, anchorY;
            switch (mode)
            {
                case Shapes_Drag.ResizeNW:
                    // dragging NW corner -> anchor is original bottom-right
                    anchorX = left0 + w0;
                    anchorY = top0 + h0;
                    break;
                case Shapes_Drag.ResizeNE:
                    // dragging NE corner -> anchor is original bottom-left
                    anchorX = left0;
                    anchorY = top0 + h0;
                    break;
                case Shapes_Drag.ResizeSW:
                    // dragging SW corner -> anchor is original top-right
                    anchorX = left0 + w0;
                    anchorY = top0;
                    break;
                case Shapes_Drag.ResizeSE:
                    // dragging SE corner -> anchor is original top-left
                    anchorX = left0;
                    anchorY = top0;
                    break;
                default:
                    return;
            }

            // Vector from anchor to mouse
            double dx = curr.X - anchorX;
            double dy = curr.Y - anchorY;

            if (keepSquare)
            {
                // Square: side is max of |dx|,|dy|, direction comes from quadrant of (dx,dy)
                double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (side < Shapes_MinSize)
                    side = Shapes_MinSize;

                double signX = dx >= 0 ? 1.0 : -1.0;
                double signY = dy >= 0 ? 1.0 : -1.0;

                double cornerX = anchorX + signX * side;
                double cornerY = anchorY + signY * side;

                double left = Math.Min(anchorX, cornerX);
                double top = Math.Min(anchorY, cornerY);
                double w = Math.Abs(cornerX - anchorX);
                double h = Math.Abs(cornerY - anchorY);

                Canvas.SetLeft(_sh_active, left);
                Canvas.SetTop(_sh_active, top);
                _sh_active.Width = w;
                _sh_active.Height = h;
            }
            else
            {
                // Free rectangle: width/height from anchor to mouse, no directional clamping
                double w = Math.Abs(dx);
                double h = Math.Abs(dy);

                if (w < Shapes_MinSize) w = Shapes_MinSize;
                if (h < Shapes_MinSize) h = Shapes_MinSize;

                double signX = dx >= 0 ? 1.0 : -1.0;
                double signY = dy >= 0 ? 1.0 : -1.0;

                double cornerX = anchorX + signX * w;
                double cornerY = anchorY + signY * h;

                double left = Math.Min(anchorX, cornerX);
                double top = Math.Min(anchorY, cornerY);

                Canvas.SetLeft(_sh_active, left);
                Canvas.SetTop(_sh_active, top);
                _sh_active.Width = w;
                _sh_active.Height = h;
            }
        }

        private void Shapes_AttachContextMenu(Shape s)
        {
            var cm = new ContextMenu();

            var commit = new MenuItem { Header = "Commit to image (Enter)" };
            var cancel = new MenuItem { Header = "Cancel (Esc)" };

            commit.Click += (_, __) =>
            {
                if (_sh_active == null || _sh_host == null) return;
                bool ok = Shapes_TryCommitToImage(_sh_active);
                if (ok)
                    _sh_host.Children.Remove(_sh_active);
                else if (!_sh_pendingShapes.Contains(_sh_active))
                    _sh_pendingShapes.Add(_sh_active);

                Shapes_ClearActive();
                _sh_pendingCommit = false;
            };

            cancel.Click += (_, __) =>
            {
                if (_sh_active == null || _sh_host == null) return;
                if (_sh_pendingShapes.Contains(_sh_active))
                    _sh_pendingShapes.Remove(_sh_active);
                _sh_host.Children.Remove(_sh_active);
                Shapes_ClearActive();
                _sh_pendingCommit = false;
            };

            cm.Items.Add(commit);
            cm.Items.Add(cancel);

            s.ContextMenu = cm;
        }

        private void Shapes_UpdateActiveBrushes()
        {
            if (_sh_active == null) return;

            var stroke = new SolidColorBrush(_sh_outline);
            var fill = new SolidColorBrush(_sh_fill);
            _sh_active.Stroke = stroke;
            if (!(_sh_active is Line))
                _sh_active.Fill = _sh_fill.A == 0 ? Brushes.Transparent : fill;
        }

        private void Shapes_UpdateStrokePreview()
        {
            if (_sh_active == null) return;
            _sh_active.StrokeThickness = Math.Max(1, _sh_strokePx);
        }

        private void Shapes_ClearActive()
        {
            Shapes_RemoveHandles();
            _sh_active = null;
            _sh_dragMode = Shapes_Drag.None;
            if (_sh_host != null)
                _sh_host.Cursor = Cursors.Arrow;
        }

        // ---------- Hit testing for body ----------
        private bool Shapes_HitShapeBody(Shape shape, WpfPoint pos)
        {
            if (shape is Line ln)
            {
                var p = pos;
                var a = new WpfPoint(ln.X1, ln.Y1);
                var b = new WpfPoint(ln.X2, ln.Y2);

                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                if (len2 < 0.001) return false;

                double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
                t = Math.Max(0, Math.Min(1, t));

                double projX = a.X + t * dx;
                double projY = a.Y + t * dy;
                double dist = Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));

                double hitRadius = Math.Max(4, _sh_strokePx + 2);
                return dist <= hitRadius;
            }

            double left = Canvas.GetLeft(shape);
            double top = Canvas.GetTop(shape);
            var rect = new WpfRect(left, top, shape.Width, shape.Height);
            rect.Inflate(2, 2);
            return rect.Contains(pos);
        }

        // ---------- Element + commit ----------
        private Shape Shapes_CreateElement(Shapes_Kind kind)
        {
            Brush stroke = new SolidColorBrush(_sh_outline);
            Brush fill = new SolidColorBrush(_sh_fill);
            double thicknessPreview = Math.Max(1, _sh_strokePx);

            return kind switch
            {
                Shapes_Kind.Rectangle => new Rectangle
                {
                    Stroke = stroke,
                    Fill = _sh_fill.A == 0 ? Brushes.Transparent : fill,
                    StrokeThickness = thicknessPreview
                },
                Shapes_Kind.Ellipse => new Ellipse
                {
                    Stroke = stroke,
                    Fill = _sh_fill.A == 0 ? Brushes.Transparent : fill,
                    StrokeThickness = thicknessPreview
                },
                Shapes_Kind.Line => new Line
                {
                    Stroke = stroke,
                    StrokeThickness = thicknessPreview,
                    StrokeStartLineCap = PenLineCap.Square,
                    StrokeEndLineCap = PenLineCap.Square
                },
                _ => throw new NotSupportedException()
            };
        }

        private bool Shapes_TryCommitToImage(Shape shape)
        {
            var mat = TryGetImageMat_Shapes();
            if (mat == null || mat.Empty())
                return false;

            // Save undo state before committing shape
            SaveUndoState("Shape");

            Scalar outline = new Scalar(_sh_outline.B, _sh_outline.G, _sh_outline.R, _sh_outline.A);
            Scalar fill = new Scalar(_sh_fill.B, _sh_fill.G, _sh_fill.R, _sh_fill.A);
            int thickness = Math.Max(1, _sh_strokePx);

            if (shape is Line ln)
            {
                var p1 = CanvasToImagePx_Shapes(new WpfPoint(ln.X1, ln.Y1));
                var p2 = CanvasToImagePx_Shapes(new WpfPoint(ln.X2, ln.Y2));
                Cv2.Line(mat, new CvPoint(p1.X, p1.Y), new CvPoint(p2.X, p2.Y), outline,
                    thickness, LineTypes.AntiAlias);
            }
            else
            {
                double left = Canvas.GetLeft(shape);
                double top = Canvas.GetTop(shape);
                double w = shape.Width;
                double h = shape.Height;
                if (w < 1 || h < 1)
                {
                    TryForceRefreshView_Shapes();
                    return true;
                }

                var tl = CanvasToImagePx_Shapes(new WpfPoint(left, top));
                var br = CanvasToImagePx_Shapes(new WpfPoint(left + w, top + h));
                tl = ClampToImage_Shapes(tl);
                br = ClampToImage_Shapes(br);

                if (shape is Rectangle)
                {
                    var rect = new OpenCvSharp.Rect(
                        Math.Min(tl.X, br.X),
                        Math.Min(tl.Y, br.Y),
                        Math.Abs(br.X - tl.X),
                        Math.Abs(br.Y - tl.Y));
                    if (_sh_fill.A > 0)
                        Cv2.Rectangle(mat, rect, fill, -1, LineTypes.AntiAlias);
                    Cv2.Rectangle(mat, rect, outline, thickness, LineTypes.AntiAlias);
                }
                else if (shape is Ellipse)
                {
                    var center = new CvPoint((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
                    var axes = new CvSize(Math.Abs(br.X - tl.X) / 2, Math.Abs(br.Y - tl.Y) / 2);
                    if (_sh_fill.A > 0)
                        Cv2.Ellipse(mat, center, axes, 0, 0, 360, fill, -1, LineTypes.AntiAlias);
                    Cv2.Ellipse(mat, center, axes, 0, 0, 360, outline, thickness, LineTypes.AntiAlias);
                }
            }

            TryForceRefreshView_Shapes();
            _hasUnsavedChanges = true;
            return true;
        }

        // ---------- Coords (Artboard space == image pixels) ----------
        private System.Drawing.Point CanvasToImagePx_Shapes(WpfPoint p)
        {
            int ix = (int)Math.Round(p.X);
            int iy = (int)Math.Round(p.Y);
            return new System.Drawing.Point(ix, iy);
        }

        private System.Drawing.Point ClampToImage_Shapes(System.Drawing.Point q)
        {
            int w = GetImagePixelWidth_Shapes();
            int h = GetImagePixelHeight_Shapes();
            int x = Math.Max(0, Math.Min(Math.Max(0, w - 1), q.X));
            int y = Math.Max(0, Math.Min(Math.Max(0, h - 1), q.Y));
            return new System.Drawing.Point(x, y);
        }

        private static double Shapes_Distance(WpfPoint a, WpfPoint b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ---------- Host discovery ----------
        private Canvas? Shapes_FindHost()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var f = GetType().GetField("Artboard", flags);
            if (f?.GetValue(this) is Canvas artboard)
                return artboard;

            var fp = GetType().GetField("PaintCanvas", flags);
            if (fp?.GetValue(this) is Canvas paintCanvas)
                return paintCanvas;

            return null;
        }

        private Canvas Shapes_GetHostOrThrow()
        {
            return _sh_host ?? Shapes_FindHost() ??
                throw new InvalidOperationException("Shapes host not found.");
        }

        // ---------- Image access shims ----------
        private object? TryGetImageController_Shapes()
        {
            var names = new[]
            {
                "_img", "Img",
                "_image", "Image",
                "_imageController", "ImageController"
            };

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = GetType();

            foreach (var n in names)
            {
                var f = t.GetField(n, flags);
                if (f != null) return f.GetValue(this);

                var p = t.GetProperty(n, flags);
                if (p != null) return p.GetValue(this);
            }

            return null;
        }

        private Mat? TryGetImageMat_Shapes()
        {
            var ctrl = TryGetImageController_Shapes();
            if (ctrl == null) return null;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var ct = ctrl.GetType();

            var pMat = ct.GetProperty("Mat", flags)?.GetValue(ctrl) as Mat;
            if (pMat != null) return pMat;

            var pDoc = ct.GetProperty("Doc", flags)?.GetValue(ctrl);
            if (pDoc != null)
            {
                var dt = pDoc.GetType();
                var imgProp = dt.GetProperty("Image", flags)?.GetValue(pDoc) as Mat;
                if (imgProp != null) return imgProp;
                var matProp = dt.GetProperty("Mat", flags)?.GetValue(pDoc) as Mat;
                if (matProp != null) return matProp;
            }

            var pImageDoc = ct.GetProperty("ImageDoc", flags)?.GetValue(ctrl);
            if (pImageDoc != null)
            {
                var idt = pImageDoc.GetType();
                var idMat = idt.GetProperty("Mat", flags)?.GetValue(pImageDoc) as Mat;
                if (idMat != null) return idMat;
                var idImg = idt.GetProperty("Image", flags)?.GetValue(pImageDoc) as Mat;
                if (idImg != null) return idImg;
            }

            var mGetMat = ct.GetMethod("GetMat", flags);
            if (mGetMat != null && mGetMat.GetParameters().Length == 0)
                return mGetMat.Invoke(ctrl, null) as Mat;

            return null;
        }

        private int GetImagePixelWidth_Shapes()
        {
            var ctrl = TryGetImageController_Shapes();
            if (ctrl == null) return 0;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var doc = ctrl.GetType().GetProperty("Doc", flags)?.GetValue(ctrl);
            if (doc != null)
            {
                var wProp = doc.GetType().GetProperty("Width", flags);
                if (wProp?.GetValue(doc) is int w1) return w1;
            }

            var wVal = ctrl.GetType().GetProperty("Width", flags)?.GetValue(ctrl);
            if (wVal is int w2) return w2;

            var mat = TryGetImageMat_Shapes();
            return mat?.Cols ?? 0;
        }

        private int GetImagePixelHeight_Shapes()
        {
            var ctrl = TryGetImageController_Shapes();
            if (ctrl == null) return 0;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var doc = ctrl.GetType().GetProperty("Doc", flags)?.GetValue(ctrl);
            if (doc != null)
            {
                var hProp = doc.GetType().GetProperty("Height", flags);
                if (hProp?.GetValue(doc) is int h1) return h1;
            }

            var hVal = ctrl.GetType().GetProperty("Height", flags)?.GetValue(ctrl);
            if (hVal is int h2) return h2;

            var mat = TryGetImageMat_Shapes();
            return mat?.Rows ?? 0;
        }

        private void TryForceRefreshView_Shapes()
        {
            var ctrl = TryGetImageController_Shapes();
            if (ctrl == null) return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var m = ctrl.GetType().GetMethod("ForceRefreshView", flags);
            if (m != null && m.GetParameters().Length == 0)
            {
                m.Invoke(ctrl, null);
            }
            else
            {
                InvalidateVisual();
            }
        }
    }

    // ========== SHAPES CONFIG WINDOW (Dark Theme) ==========
    internal sealed class ShapesConfigWindow : WWindow
    {
        public PhotoMax.MainWindow.Shapes_Kind SelectedKind { get; private set; }
        public Color OutlineColor { get; private set; }
        public Color FillColor { get; private set; }
        public int StrokeThickness { get; private set; }
        
        private Color _fillBaseColor;

        private readonly RadioButton _rectRadio;
        private readonly RadioButton _ellipseRadio;
        private readonly RadioButton _lineRadio;
        private readonly Border _outlinePreview;
        private readonly Border _fillPreview;
        private readonly TextBlock _outlineLabel;
        private readonly TextBlock _fillLabel;
        private readonly Slider _thicknessSlider;
        private readonly TextBlock _thicknessValue;
        private Slider? _fillOpacitySlider; // **NEW**
        
        
        public ShapesConfigWindow(
            PhotoMax.MainWindow.Shapes_Kind currentKind,
            Color currentOutline,
            Color currentFill,
            int currentThickness)
        {
            SelectedKind = currentKind;
            OutlineColor = currentOutline;
            FillColor = currentFill;
            StrokeThickness = currentThickness;
    
            // **NEW: Remember base color (default to white if fully transparent)**
            _fillBaseColor = currentFill.A > 0 
                ? Color.FromRgb(currentFill.R, currentFill.G, currentFill.B)
                : Colors.White;

            Title = "Shape Settings";
            Width = 400;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            // Apply dark theme-ish
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromRgb(243, 243, 243));

            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // === SHAPE TYPE SECTION ===
            var shapePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var shapeHeader = new TextBlock
            {
                Text = "SHAPE TYPE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            shapePanel.Children.Add(shapeHeader);

            _rectRadio = new RadioButton
            {
                Content = "Rectangle",
                IsChecked = SelectedKind == PhotoMax.MainWindow.Shapes_Kind.Rectangle,
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = Foreground
            };
            _ellipseRadio = new RadioButton
            {
                Content = "Ellipse",
                IsChecked = SelectedKind == PhotoMax.MainWindow.Shapes_Kind.Ellipse,
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = Foreground
            };
            _lineRadio = new RadioButton
            {
                Content = "Line",
                IsChecked = SelectedKind == PhotoMax.MainWindow.Shapes_Kind.Line,
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = Foreground
            };

            shapePanel.Children.Add(_rectRadio);
            shapePanel.Children.Add(_ellipseRadio);
            shapePanel.Children.Add(_lineRadio);

            // === COLORS SECTION ===
            var colorPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var colorHeader = new TextBlock
            {
                Text = "COLORS",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            colorPanel.Children.Add(colorHeader);

// Outline color row
            var outlineRow = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            outlineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            outlineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outlineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var outlineText = new TextBlock
            {
                Text = "Outline:",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Foreground
            };
            Grid.SetColumn(outlineText, 0);

            _outlinePreview = new Border
            {
                Width = 120,
                Height = 30,
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(currentOutline),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(_outlinePreview, 1);

            var outlineBtn = new Button
            {
                Content = "Change...",
                Width = 80,
                Padding = new Thickness(8, 4, 8, 4)
            };
            outlineBtn.Click += OutlineBtn_Click;
            Grid.SetColumn(outlineBtn, 2);

            outlineRow.Children.Add(outlineText);
            outlineRow.Children.Add(_outlinePreview);
            outlineRow.Children.Add(outlineBtn);

            _outlineLabel = new TextBlock
            {
                Text = FormatColorLabel(currentOutline, isFill: false),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                Margin = new Thickness(100, 2, 0, 8)
            };

            colorPanel.Children.Add(outlineRow);
            colorPanel.Children.Add(_outlineLabel);

// Fill color row
            var fillRow = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            fillRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            fillRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fillRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var fillText = new TextBlock
            {
                Text = "Fill:",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Foreground
            };
            Grid.SetColumn(fillText, 0);

            _fillPreview = new Border
            {
                Width = 120,
                Height = 30,
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            UpdateFillPreview();
            Grid.SetColumn(_fillPreview, 1);

            var fillBtn = new Button
            {
                Content = "Change...",
                Width = 80,
                Padding = new Thickness(8, 4, 8, 4)
            };
            fillBtn.Click += FillBtn_Click;
            Grid.SetColumn(fillBtn, 2);

            fillRow.Children.Add(fillText);
            fillRow.Children.Add(_fillPreview);
            fillRow.Children.Add(fillBtn);

            _fillLabel = new TextBlock
            {
                Text = FormatColorLabel(currentFill, isFill: true),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                Margin = new Thickness(100, 2, 0, 8)
            };

            colorPanel.Children.Add(fillRow);
            colorPanel.Children.Add(_fillLabel);


            // **NEW: Fill Opacity Slider**
            var opacityRow = new Grid { Margin = new Thickness(100, 4, 0, 0) };
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            _fillOpacitySlider = new Slider // **CHANGED: assigned to field**
            {
                Minimum = 0,
                Maximum = 255,
                Value = currentFill.A,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            var opacityValue = new TextBlock
            {
                Text = currentFill.A == 0 ? "Transp." : $"Alpha: {currentFill.A}",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                FontSize = 10,
                Margin = new Thickness(8, 0, 0, 0)
            };

            _fillOpacitySlider.ValueChanged += (s, e) =>
            {
                byte alpha = (byte)_fillOpacitySlider.Value;
    
                // **FIX: Always use the remembered base color**
                FillColor = Color.FromArgb(alpha, _fillBaseColor.R, _fillBaseColor.G, _fillBaseColor.B);
    
                UpdateFillPreview();
                _fillLabel.Text = FormatColorLabel(FillColor, isFill: true);
                opacityValue.Text = alpha == 0 ? "Transp." : $"Alpha: {alpha}";
            };

            Grid.SetColumn(_fillOpacitySlider, 0);
            Grid.SetColumn(opacityValue, 1);

            opacityRow.Children.Add(_fillOpacitySlider);
            opacityRow.Children.Add(opacityValue);

            colorPanel.Children.Add(opacityRow);

            // === STROKE THICKNESS SECTION ===
            var strokePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var strokeHeader = new TextBlock
            {
                Text = "STROKE THICKNESS",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            strokePanel.Children.Add(strokeHeader);

            var sliderRow = new Grid();
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            _thicknessSlider = new Slider
            {
                Minimum = 1,
                Maximum = 32,
                Value = currentThickness,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            _thicknessSlider.ValueChanged += (s, e) =>
            {
                StrokeThickness = (int)_thicknessSlider.Value;
                _thicknessValue.Text = $"{StrokeThickness} px";
            };
            Grid.SetColumn(_thicknessSlider, 0);

            _thicknessValue = new TextBlock
            {
                Text = $"{currentThickness} px",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Foreground,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(_thicknessValue, 1);

            sliderRow.Children.Add(_thicknessSlider);
            sliderRow.Children.Add(_thicknessValue);

            strokePanel.Children.Add(sliderRow);

            // === BUTTONS ===
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
                if (_rectRadio.IsChecked == true)
                    SelectedKind = PhotoMax.MainWindow.Shapes_Kind.Rectangle;
                else if (_ellipseRadio.IsChecked == true)
                    SelectedKind = PhotoMax.MainWindow.Shapes_Kind.Ellipse;
                else if (_lineRadio.IsChecked == true)
                    SelectedKind = PhotoMax.MainWindow.Shapes_Kind.Line;

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

            // === ASSEMBLE LAYOUT ===
            var contentStack = new StackPanel();
            contentStack.Children.Add(shapePanel);
            contentStack.Children.Add(colorPanel);
            contentStack.Children.Add(strokePanel);

            Grid.SetRow(contentStack, 0);
            Grid.SetRow(buttonPanel, 2);

            mainGrid.Children.Add(contentStack);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private void OutlineBtn_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WF.ColorDialog
            {
                AllowFullOpen = true,
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(OutlineColor.R, OutlineColor.G, OutlineColor.B)
            };

            if (dlg.ShowDialog() == WF.DialogResult.OK)
            {
                OutlineColor = Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _outlinePreview.Background = new SolidColorBrush(OutlineColor);
                _outlineLabel.Text = FormatColorLabel(OutlineColor, isFill: false);
            }
        }

        private void FillBtn_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                AllowFullOpen = true,
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(_fillBaseColor.R, _fillBaseColor.G, _fillBaseColor.B)
            };

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                // **FIX: Remember the base color and set to FULLY OPAQUE (255)**
                _fillBaseColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                FillColor = Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B);
        
                // **FIX: Reset slider to 255 (fully opaque)**
                if (_fillOpacitySlider != null)
                {
                    _fillOpacitySlider.Value = 255;
                }
        
                UpdateFillPreview();
                _fillLabel.Text = FormatColorLabel(FillColor, isFill: true);
            }
        }

        private void UpdateFillPreview()
        {
            if (FillColor.A == 0)
            {
                // Simple checkerboard pattern for transparency
                var drawingGroup = new DrawingGroup();
                var gray1 = new SolidColorBrush(Color.FromRgb(180, 180, 180));
                var gray2 = new SolidColorBrush(Color.FromRgb(140, 140, 140));

                drawingGroup.Children.Add(
                    new GeometryDrawing(gray1, null,
                        new RectangleGeometry(new WpfRect(0, 0, 16, 16))));
                drawingGroup.Children.Add(
                    new GeometryDrawing(gray2, null,
                        new RectangleGeometry(new WpfRect(0, 0, 8, 8))));
                drawingGroup.Children.Add(
                    new GeometryDrawing(gray2, null,
                        new RectangleGeometry(new WpfRect(8, 8, 8, 8))));

                _fillPreview.Background = new DrawingBrush(drawingGroup)
                {
                    TileMode = TileMode.Tile,
                    Viewport = new WpfRect(0, 0, 16, 16),
                    ViewportUnits = BrushMappingMode.Absolute
                };
            }
            else
            {
                _fillPreview.Background = new SolidColorBrush(FillColor);
            }
        }

        private static string FormatColorLabel(Color c, bool isFill)
        {
            if (isFill && c.A == 0)
                return "Transparent";

            if (c.A == 255)
                return $"RGB({c.R}, {c.G}, {c.B})";

            return $"RGB({c.R}, {c.G}, {c.B}) • Alpha: {c.A}";
        }
    }

// ========== FILL OPACITY PICKER WINDOW ==========
    internal sealed class FillOpacityWindow : WWindow
    {
        public Color ResultColor { get; private set; }

        private readonly Slider _opacitySlider;
        private readonly TextBlock _opacityValue;
        private Color _baseColor;
        private byte _alpha = 255;

        public FillOpacityWindow(Color currentFill)
        {
            Title = "Fill Color & Opacity";
            Width = 350;
            Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromRgb(243, 243, 243));

            // Extract base color and alpha from current fill
            _baseColor = currentFill.A > 0 ? currentFill : Color.FromRgb(255, 255, 255);
            _alpha = currentFill.A > 0 ? currentFill.A : (byte)255;
            ResultColor = Color.FromArgb(_alpha, _baseColor.R, _baseColor.G, _baseColor.B);

            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Color picker button
            var colorBtn = new Button
            {
                Content = "Choose Color...",
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 20)
            };
            colorBtn.Click += ColorBtn_Click;
            Grid.SetRow(colorBtn, 0);

            // Opacity slider
            var opacityPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var opacityHeader = new TextBlock
            {
                Text = "OPACITY",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            opacityPanel.Children.Add(opacityHeader);

            var sliderRow = new Grid();
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            _opacitySlider = new Slider
            {
                Minimum = 1,
                Maximum = 255,
                Value = _alpha,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            _opacitySlider.ValueChanged += (s, e) =>
            {
                _alpha = (byte)_opacitySlider.Value;
                ResultColor = Color.FromArgb(_alpha, _baseColor.R, _baseColor.G, _baseColor.B);
                _opacityValue.Text = $"{_alpha}";
            };
            Grid.SetColumn(_opacitySlider, 0);

            _opacityValue = new TextBlock
            {
                Text = $"{_alpha}",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Foreground,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(_opacityValue, 1);

            sliderRow.Children.Add(_opacitySlider);
            sliderRow.Children.Add(_opacityValue);
            opacityPanel.Children.Add(sliderRow);

            Grid.SetRow(opacityPanel, 1);

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
            Grid.SetRow(buttonPanel, 3);

            mainGrid.Children.Add(colorBtn);
            mainGrid.Children.Add(opacityPanel);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private void ColorBtn_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                AllowFullOpen = true,
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(_baseColor.R, _baseColor.G, _baseColor.B)
            };

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _baseColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                ResultColor = Color.FromArgb(_alpha, _baseColor.R, _baseColor.G, _baseColor.B);
            }
        }
    }
}
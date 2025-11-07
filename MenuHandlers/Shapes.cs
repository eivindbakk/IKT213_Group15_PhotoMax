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
using CvPoint  = OpenCvSharp.Point;
using CvSize   = OpenCvSharp.Size;

namespace PhotoMax
{
    public partial class MainWindow
    {
        private enum Shapes_Kind { Rectangle, Ellipse, Line }
        private enum Shapes_Drag { None, Create, Move, ResizeNW, ResizeNE, ResizeSW, ResizeSE, MoveLine, ResizeP1, ResizeP2 }

        // ===== Shapes state (namespaced to avoid collisions with Tools.cs) =====
        private bool _sh_armed = false;
        private bool _sh_pendingCommit = false;
        private Shapes_Kind _sh_kind = Shapes_Kind.Rectangle;

        private Color _sh_outline = Colors.Red;
        private Color _sh_fill    = Color.FromArgb(0, 0, 0, 0);
        private int   _sh_strokePx = 1;

        private Shape? _sh_active;
        private readonly List<FrameworkElement> _sh_handles = new();

        private Shapes_Drag _sh_dragMode = Shapes_Drag.None;
        private WpfPoint _sh_dragStart;
        private WpfPoint _sh_moveStart; // for Move baseline

        // Host surface (prefer Artboard Canvas; fallback to PaintCanvas)
        private Canvas? _sh_host;

        // Pending shapes when no image is open
        private readonly List<Shape> _sh_pendingShapes = new();

        // ---------- Menu ----------
        private void Shapes_List_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            void AddPick(string label, Shapes_Kind kind)
            {
                var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = (_sh_kind == kind) };
                mi.Click += (_, __) => { _sh_kind = kind; Shapes_Arm(); };
                menu.Items.Add(mi);
            }

            AddPick("Rectangle (R)", Shapes_Kind.Rectangle);
            AddPick("Ellipse (E)",   Shapes_Kind.Ellipse);
            AddPick("Line (L)",      Shapes_Kind.Line);

            var bake = new MenuItem { Header = "Bake Pending to Image" };
            bake.Click += Shapes_BakePending_Click;
            menu.Items.Add(new Separator());
            menu.Items.Add(bake);

            if (sender is FrameworkElement fe) { fe.ContextMenu = menu; menu.IsOpen = true; fe.ContextMenu = null; }
            else { menu.PlacementTarget = this; menu.IsOpen = true; }
        }

        private void Shapes_OutlineColor_Click(object sender, RoutedEventArgs e)
        {
            var cd = new WF.ColorDialog { AllowFullOpen = true, FullOpen = true };
            cd.Color = System.Drawing.Color.FromArgb(_sh_outline.A, _sh_outline.R, _sh_outline.G, _sh_outline.B);
            if (cd.ShowDialog() == WF.DialogResult.OK)
                _sh_outline = Color.FromArgb(255, cd.Color.R, cd.Color.G, cd.Color.B);
            Shapes_UpdateActiveBrushes();
        }

        private void Shapes_FillColor_Click(object sender, RoutedEventArgs e)
        {
            var cd = new WF.ColorDialog { AllowFullOpen = true, FullOpen = true };
            cd.Color = System.Drawing.Color.FromArgb(_sh_fill.A == 0 ? 255 : _sh_fill.A, _sh_fill.R, _sh_fill.G, _sh_fill.B);
            if (cd.ShowDialog() == WF.DialogResult.OK)
            {
                if (_sh_fill.A > 0 &&
                    _sh_fill.R == cd.Color.R &&
                    _sh_fill.G == cd.Color.G &&
                    _sh_fill.B == cd.Color.B)
                    _sh_fill = Color.FromArgb(0, 0, 0, 0);
                else
                    _sh_fill = Color.FromArgb(200, cd.Color.R, cd.Color.G, cd.Color.B);
            }
            Shapes_UpdateActiveBrushes();
        }

        private void Shapes_BakePending_Click(object? sender, RoutedEventArgs e)
        {
            var mat = TryGetImageMat_Shapes();
            if (mat == null || mat.Empty())
            {
                MessageBox.Show("Open or create an image first.", "PhotoMax", MessageBoxButton.OK, MessageBoxImage.Information);
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

        // ---------- Arming ----------
        private void Shapes_Arm()
        {
            if (_sh_armed) return;
            _sh_host = Shapes_FindHost();
            if (_sh_host == null)
            {
                MessageBox.Show("Shapes host not found (need an Artboard Canvas or PaintCanvas).");
                return;
            }

            _sh_armed = true;
            _sh_host.PreviewMouseLeftButtonDown += Shapes_MouseDown;
            _sh_host.PreviewMouseMove           += Shapes_MouseMove;
            _sh_host.PreviewMouseLeftButtonUp   += Shapes_MouseUp;
            _sh_host.PreviewKeyDown             += Shapes_KeyDown;
            _sh_host.Focusable = true;
            _sh_host.Focus();
        }

        private void Shapes_Disarm()
        {
            if (!_sh_armed || _sh_host == null) return;
            _sh_armed = false;

            _sh_host.PreviewMouseLeftButtonDown -= Shapes_MouseDown;
            _sh_host.PreviewMouseMove           -= Shapes_MouseMove;
            _sh_host.PreviewMouseLeftButtonUp   -= Shapes_MouseUp;
            _sh_host.PreviewKeyDown             -= Shapes_KeyDown;

            Shapes_ClearActive();
            _sh_pendingCommit = false;
            _sh_dragMode = Shapes_Drag.None;
        }

        // ---------- Input ----------
        private void Shapes_MouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (_sh_host == null) return;
            _sh_host.Focus();

            var pos = e.GetPosition(_sh_host);

            // If a shape is pending commit, manipulate it (no new shape)
            if (_sh_active != null && _sh_pendingCommit)
            {
                if (_sh_active is Line ln)
                {
                    var p1 = new WpfPoint(ln.X1, ln.Y1);
                    var p2 = new WpfPoint(ln.X2, ln.Y2);
                    double d1 = Shapes_Distance(pos, p1);
                    double d2 = Shapes_Distance(pos, p2);
                    if (d1 < 10) { _sh_dragMode = Shapes_Drag.ResizeP1; }
                    else if (d2 < 10) { _sh_dragMode = Shapes_Drag.ResizeP2; }
                    else { _sh_dragMode = Shapes_Drag.MoveLine; }
                    _sh_dragStart = pos;
                    e.Handled = true;
                    return;
                }
                else
                {
                    // Corner handles?
                    if (Shapes_TryHitHandle(pos, out var mode))
                    {
                        _sh_dragMode = mode;
                        _sh_dragStart = pos;
                        e.Handled = true;
                        return;
                    }
                    // Inside -> move
                    var left = Canvas.GetLeft(_sh_active);
                    var top  = Canvas.GetTop(_sh_active);
                    var w = _sh_active.Width;
                    var h = _sh_active.Height;
                    if (pos.X >= left && pos.X <= left + w && pos.Y >= top && pos.Y <= top + h)
                    {
                        _sh_dragMode = Shapes_Drag.Move;
                        _sh_dragStart = pos;
                        _sh_moveStart = new WpfPoint(left, top);
                        e.Handled = true;
                        return;
                    }
                }
                return; // do not create new
            }

            if (!_sh_armed || _sh_pendingCommit) return;

            // Begin creating a new shape at pointer
            _sh_dragMode = Shapes_Drag.Create;
            _sh_dragStart = pos;

            _sh_active = Shapes_CreateElement(_sh_kind);
            if (_sh_active == null) return;

            Shapes_AttachContextMenu(_sh_active);
            RenderOptions.SetEdgeMode(_sh_active, EdgeMode.Aliased);
            _sh_active.SnapsToDevicePixels = true;

            if (_sh_active is Line lnNew)
            {
                lnNew.X1 = pos.X; lnNew.Y1 = pos.Y;
                lnNew.X2 = pos.X; lnNew.Y2 = pos.Y;
            }
            else
            {
                Canvas.SetLeft(_sh_active, pos.X);
                Canvas.SetTop (_sh_active, pos.Y);
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
            if (_sh_active == null || _sh_host == null) return;
            var curr = e.GetPosition(_sh_host);
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

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
                        var dx = curr.X - _sh_dragStart.X;
                        var dy = curr.Y - _sh_dragStart.Y;
                        Canvas.SetLeft(_sh_active, _sh_moveStart.X + dx);
                        Canvas.SetTop (_sh_active, _sh_moveStart.Y + dy);
                        Shapes_UpdateHandles();
                    }
                    break;

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
                        var dx = curr.X - _sh_dragStart.X;
                        var dy = curr.Y - _sh_dragStart.Y;
                        m.X1 += dx; m.Y1 += dy;
                        m.X2 += dx; m.Y2 += dy;
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
                _sh_pendingCommit = true;
                _sh_dragMode = Shapes_Drag.None;
                Mouse.Capture(null);
                Shapes_AddHandles();
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
                {
                    _sh_host.Children.Remove(_sh_active);
                }
                else
                {
                    if (!_sh_pendingShapes.Contains(_sh_active))
                        _sh_pendingShapes.Add(_sh_active);
                }
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
            else if (e.Key == Key.R) { _sh_kind = Shapes_Kind.Rectangle; e.Handled = true; }
            else if (e.Key == Key.E) { _sh_kind = Shapes_Kind.Ellipse;   e.Handled = true; }
            else if (e.Key == Key.L) { _sh_kind = Shapes_Kind.Line;      e.Handled = true; }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                _sh_strokePx = Math.Min(64, _sh_strokePx + 1); Shapes_UpdateStrokePreview(); e.Handled = true;
            }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                _sh_strokePx = Math.Max(1, _sh_strokePx - 1);  Shapes_UpdateStrokePreview(); e.Handled = true;
            }
        }

        // ---------- Preview + handles ----------
        private void Shapes_UpdateRectPreview(Shape s, WpfPoint start, WpfPoint curr, bool keepSquare)
        {
            double left = Math.Min(start.X, curr.X);
            double top  = Math.Min(start.Y, curr.Y);
            double w    = Math.Abs(curr.X - start.X);
            double h    = Math.Abs(curr.Y - start.Y);

            if (keepSquare)
            {
                double side = Math.Max(w, h);
                left = (curr.X < start.X) ? start.X - side : start.X;
                top  = (curr.Y < start.Y) ? start.Y - side : start.Y;
                w = side; h = side;
            }

            Canvas.SetLeft(s, left);
            Canvas.SetTop (s, top);
            s.Width  = w;
            s.Height = h;
        }

        private void Shapes_UpdateLinePreview(Line ln, WpfPoint start, WpfPoint curr, bool snap45)
        {
            double x2 = curr.X, y2 = curr.Y;
            if (snap45)
            {
                var dx = curr.X - start.X;
                var dy = curr.Y - start.Y;
                var ang = Math.Atan2(dy, dx);
                var step = Math.PI / 4.0;
                ang = Math.Round(ang / step) * step;
                var len = Math.Sqrt(dx * dx + dy * dy);
                x2 = start.X + len * Math.Cos(ang);
                y2 = start.Y + len * Math.Sin(ang);
            }
            ln.X1 = start.X; ln.Y1 = start.Y;
            ln.X2 = x2;      ln.Y2 = y2;
        }

        private void Shapes_UpdateLineEndpoint(Line line, bool editFirst, System.Windows.Point curr, bool snap45)
        {
            if (!snap45)
            {
                if (editFirst) { line.X1 = curr.X; line.Y1 = curr.Y; }
                else           { line.X2 = curr.X; line.Y2 = curr.Y; }
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

            if (editFirst) { line.X1 = x; line.Y1 = y; }
            else           { line.X2 = x; line.Y2 = y; }
        }

        private void Shapes_AddHandles()
        {
            Shapes_RemoveHandles();

            if (_sh_active is Line ln)
            {
                _sh_handles.Add(Shapes_MakeKnob(ln.X1, ln.Y1, Cursors.Cross));
                _sh_handles.Add(Shapes_MakeKnob(ln.X2, ln.Y2, Cursors.Cross));
            }
            else
            {
                var left = Canvas.GetLeft(_sh_active);
                var top  = Canvas.GetTop(_sh_active);
                var w = _sh_active.Width;
                var h = _sh_active.Height;

                _sh_handles.Add(Shapes_MakeHandle(left,       top,        Cursors.SizeNWSE, Shapes_Drag.ResizeNW));
                _sh_handles.Add(Shapes_MakeHandle(left + w,   top,        Cursors.SizeNESW, Shapes_Drag.ResizeNE));
                _sh_handles.Add(Shapes_MakeHandle(left,       top + h,    Cursors.SizeNESW, Shapes_Drag.ResizeSW));
                _sh_handles.Add(Shapes_MakeHandle(left + w,   top + h,    Cursors.SizeNWSE, Shapes_Drag.ResizeSE));
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
                var left = Canvas.GetLeft(_sh_active);
                var top  = Canvas.GetTop(_sh_active);
                var w = _sh_active.Width;
                var h = _sh_active.Height;
                if (_sh_handles.Count >= 4)
                {
                    Shapes_SetHandlePos(_sh_handles[0], left,     top);
                    Shapes_SetHandlePos(_sh_handles[1], left + w, top);
                    Shapes_SetHandlePos(_sh_handles[2], left,     top + h);
                    Shapes_SetHandlePos(_sh_handles[3], left + w, top + h);
                }
            }
        }

        private void Shapes_RemoveHandles()
        {
            if (_sh_host == null) { _sh_handles.Clear(); return; }
            foreach (var h in _sh_handles)
                _sh_host.Children.Remove(h);
            _sh_handles.Clear();
        }

        private FrameworkElement Shapes_MakeHandle(double x, double y, Cursor cursor, Shapes_Drag mode)
        {
            var r = new Rectangle
            {
                Width = 10, Height = 10,
                StrokeThickness = 1, Stroke = Brushes.Black,
                Fill = Brushes.White,
                Cursor = cursor
            };
            Canvas.SetLeft(r, x - 5);
            Canvas.SetTop (r, y - 5);
            Panel.SetZIndex(r, 10001);
            r.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _sh_dragMode = mode;
                _sh_dragStart = e.GetPosition(_sh_host);
                Mouse.Capture(_sh_host);
                e.Handled = true;
            };
            r.PreviewMouseLeftButtonUp += (_, e) => { _sh_dragMode = Shapes_Drag.None; Mouse.Capture(null); e.Handled = true; };
            _sh_host!.Children.Add(r);
            return r;
        }

        private FrameworkElement Shapes_MakeKnob(double x, double y, Cursor cursor)
        {
            var e = new Ellipse
            {
                Width = 10, Height = 10,
                StrokeThickness = 1, Stroke = Brushes.Black,
                Fill = Brushes.White,
                Cursor = cursor
            };
            Canvas.SetLeft(e, x - 5);
            Canvas.SetTop (e, y - 5);
            Panel.SetZIndex(e, 10001);
            e.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                var line = _sh_active as Line;
                if (line == null) return;
                var pos = ev.GetPosition(_sh_host);
                double d1 = Shapes_Distance(pos, new WpfPoint(line.X1, line.Y1));
                double d2 = Shapes_Distance(pos, new WpfPoint(line.X2, line.Y2));
                _sh_dragMode = (d1 <= d2) ? Shapes_Drag.ResizeP1 : Shapes_Drag.ResizeP2;
                _sh_dragStart = pos;
                Mouse.Capture(_sh_host);
                ev.Handled = true;
            };
            e.PreviewMouseLeftButtonUp += (_, ev) => { _sh_dragMode = Shapes_Drag.None; Mouse.Capture(null); ev.Handled = true; };
            _sh_host!.Children.Add(e);
            return e;
        }

        private void Shapes_SetHandlePos(FrameworkElement fe, double x, double y)
        {
            Canvas.SetLeft(fe, x - 5);
            Canvas.SetTop (fe, y - 5);
        }

        private bool Shapes_TryHitHandle(WpfPoint pos, out Shapes_Drag mode)
        {
            mode = Shapes_Drag.None;
            foreach (var h in _sh_handles)
            {
                double left = Canvas.GetLeft(h);
                double top  = Canvas.GetTop(h);
                if (pos.X >= left && pos.X <= left + h.Width &&
                    pos.Y >= top  && pos.Y <= top  + h.Height)
                {
                    if (_sh_active is Line)
                        mode = Shapes_Drag.MoveLine; // knobs individually grab via their own handlers
                    else
                    {
                        int idx = _sh_handles.IndexOf(h);
                        mode = idx switch
                        {
                            0 => Shapes_Drag.ResizeNW,
                            1 => Shapes_Drag.ResizeNE,
                            2 => Shapes_Drag.ResizeSW,
                            3 => Shapes_Drag.ResizeSE,
                            _ => Shapes_Drag.None
                        };
                    }
                    return true;
                }
            }
            return false;
        }

        private void Shapes_ResizeFromCorner(Shapes_Drag mode, WpfPoint curr, bool keepSquare)
        {
            if (_sh_active == null) return;

            var left = Canvas.GetLeft(_sh_active);
            var top  = Canvas.GetTop(_sh_active);
            var w = _sh_active.Width;
            var h = _sh_active.Height;

            WpfPoint anchor = mode switch
            {
                Shapes_Drag.ResizeNW => new WpfPoint(left + w, top + h),
                Shapes_Drag.ResizeNE => new WpfPoint(left,     top + h),
                Shapes_Drag.ResizeSW => new WpfPoint(left + w, top),
                Shapes_Drag.ResizeSE => new WpfPoint(left,     top),
                _ => new WpfPoint(left, top)
            };

            Shapes_UpdateRectPreview(_sh_active, anchor, curr, keepSquare);
        }

        private void Shapes_AttachContextMenu(Shape s)
        {
            var cm = new ContextMenu();
            var commit = new MenuItem { Header = "Commit (Enter)" };
            var cancel = new MenuItem { Header = "Cancel (Esc)" };
            commit.Click += (_, __) =>
            {
                if (_sh_active == null || _sh_host == null) return;
                bool ok = Shapes_TryCommitToImage(_sh_active);
                if (ok) _sh_host.Children.Remove(_sh_active);
                else if (!_sh_pendingShapes.Contains(_sh_active)) _sh_pendingShapes.Add(_sh_active);
                Shapes_ClearActive();
                _sh_pendingCommit = false;
            };
            cancel.Click += (_, __) =>
            {
                if (_sh_active == null || _sh_host == null) return;
                if (_sh_pendingShapes.Contains(_sh_active)) _sh_pendingShapes.Remove(_sh_active);
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
            var fill   = new SolidColorBrush(_sh_fill);
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
        }

        // ---------- Element + commit ----------
        private Shape Shapes_CreateElement(Shapes_Kind kind)
        {
            Brush stroke = new SolidColorBrush(_sh_outline);
            Brush fill   = new SolidColorBrush(_sh_fill);
            double thicknessPreview = Math.Max(1, _sh_strokePx);

            return kind switch
            {
                Shapes_Kind.Rectangle => new Rectangle { Stroke = stroke, Fill = _sh_fill.A == 0 ? Brushes.Transparent : fill, StrokeThickness = thicknessPreview },
                Shapes_Kind.Ellipse   => new Ellipse   { Stroke = stroke, Fill = _sh_fill.A == 0 ? Brushes.Transparent : fill, StrokeThickness = thicknessPreview },
                Shapes_Kind.Line      => new Line      { Stroke = stroke, StrokeThickness = thicknessPreview, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square },
                _ => throw new NotSupportedException()
            };
        }

        private bool Shapes_TryCommitToImage(Shape shape)
        {
            var mat = TryGetImageMat_Shapes();
            if (mat == null || mat.Empty())
                return false;

            Scalar outline = new Scalar(_sh_outline.B, _sh_outline.G, _sh_outline.R, _sh_outline.A);
            Scalar fill    = new Scalar(_sh_fill.B,    _sh_fill.G,    _sh_fill.R,    _sh_fill.A);
            int thickness  = Math.Max(1, _sh_strokePx);

            if (shape is Line ln)
            {
                var p1 = CanvasToImagePx_Shapes(new WpfPoint(ln.X1, ln.Y1));
                var p2 = CanvasToImagePx_Shapes(new WpfPoint(ln.X2, ln.Y2));
                Cv2.Line(mat, new CvPoint(p1.X, p1.Y), new CvPoint(p2.X, p2.Y), outline, thickness, LineTypes.AntiAlias);
            }
            else
            {
                double left = Canvas.GetLeft(shape);
                double top  = Canvas.GetTop(shape);
                double w = shape.Width, h = shape.Height;
                if (w < 1 || h < 1) { TryForceRefreshView_Shapes(); return true; }

                var tl = CanvasToImagePx_Shapes(new WpfPoint(left, top));
                var br = CanvasToImagePx_Shapes(new WpfPoint(left + w, top + h));
                tl = ClampToImage_Shapes(tl);
                br = ClampToImage_Shapes(br);

                if (shape is Rectangle)
                {
                    var rect = new OpenCvSharp.Rect(Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y), Math.Abs(br.X - tl.X), Math.Abs(br.Y - tl.Y));
                    if (_sh_fill.A > 0) Cv2.Rectangle(mat, rect, fill, -1, LineTypes.AntiAlias);
                    Cv2.Rectangle(mat, rect, outline, thickness, LineTypes.AntiAlias);
                }
                else if (shape is Ellipse)
                {
                    var center = new CvPoint((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
                    var axes   = new CvSize(Math.Abs(br.X - tl.X) / 2, Math.Abs(br.Y - tl.Y) / 2);
                    if (_sh_fill.A > 0) Cv2.Ellipse(mat, center, axes, 0, 0, 360, fill, -1, LineTypes.AntiAlias);
                    Cv2.Ellipse(mat, center, axes, 0, 0, 360, outline, thickness, LineTypes.AntiAlias);
                }
            }

            TryForceRefreshView_Shapes();
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
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ---------- Host discovery ----------
        private Canvas? Shapes_FindHost()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var f = GetType().GetField("Artboard", flags);
            if (f?.GetValue(this) is Canvas artboard) return artboard;

            var fp = GetType().GetField("PaintCanvas", flags);
            if (fp?.GetValue(this) is Canvas paintCanvas) return paintCanvas;

            return null;
        }

        private Canvas Shapes_GetHostOrThrow()
        {
            return _sh_host ?? Shapes_FindHost() ?? throw new InvalidOperationException("Shapes host not found.");
        }

        // ---------- Image access shims ----------
        private object? TryGetImageController_Shapes()
        {
            var names = new[] { "_img", "Img", "_image", "Image", "_imageController", "ImageController" };
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var t = GetType();
            foreach (var n in names)
            {
                var f = t.GetField(n, flags); if (f != null) return f.GetValue(this);
                var p = t.GetProperty(n, flags); if (p != null) return p.GetValue(this);
            }
            return null;
        }

        private Mat? TryGetImageMat_Shapes()
        {
            var ctrl = TryGetImageController_Shapes();
            if (ctrl == null) return null;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var ct = ctrl.GetType();

            var pMat = ct.GetProperty("Mat", flags)?.GetValue(ctrl) as Mat; if (pMat != null) return pMat;

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

            // Prefer controller.Doc.Width
            var doc = ctrl.GetType().GetProperty("Doc", flags)?.GetValue(ctrl);
            if (doc != null)
            {
                var wProp = doc.GetType().GetProperty("Width", flags);
                if (wProp?.GetValue(doc) is int w1) return w1;
            }

            // Try controller.Width
            var wVal = ctrl.GetType().GetProperty("Width", flags)?.GetValue(ctrl);
            if (wVal is int w2) return w2;

            // Fallback to Mat.Cols
            var mat = TryGetImageMat_Shapes();
            return mat?.Cols ?? 0;
        }

        private int GetImagePixelHeight_Shapes()
        {
            var ctrl = TryGetImageController_Shapes();
            if (ctrl == null) return 0;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // Prefer controller.Doc.Height
            var doc = ctrl.GetType().GetProperty("Doc", flags)?.GetValue(ctrl);
            if (doc != null)
            {
                var hProp = doc.GetType().GetProperty("Height", flags);
                if (hProp?.GetValue(doc) is int h1) return h1;
            }

            // Try controller.Height
            var hVal = ctrl.GetType().GetProperty("Height", flags)?.GetValue(ctrl);
            if (hVal is int h2) return h2;

            // Fallback to Mat.Rows
            var mat = TryGetImageMat_Shapes();
            return mat?.Rows ?? 0;
        }

        private void TryForceRefreshView_Shapes()
        {
            var ctrl = TryGetImageController_Shapes();
            if (ctrl == null) return;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var m = ctrl.GetType().GetMethod("ForceRefreshView", flags);
            if (m != null && m.GetParameters().Length == 0) m.Invoke(ctrl, null);
            else InvalidateVisual();
        }
    }
}

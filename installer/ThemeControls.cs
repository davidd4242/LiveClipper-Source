using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiveClipper.Installation
{
    static class Theme
    {
        static Color Tone(int rgb, Color contrast) =>
            SystemInformation.HighContrast ? contrast : Color.FromArgb(255, Color.FromArgb(rgb));

        public static Color Frame => Tone(0x000000, SystemColors.WindowFrame);
        public static Color Background => Tone(0x17191C, SystemColors.Window);
        public static Color Surface => Tone(0x24272B, SystemColors.Control);
        public static Color Well => Tone(0x181A1D, SystemColors.Window);
        public static Color Border => Tone(0x44484E, SystemColors.WindowFrame);
        public static Color Text => Tone(0xF7F5F1, SystemColors.WindowText);
        public static Color Secondary => Tone(0xB8BABD, SystemColors.WindowText);
        public static Color Accent => Tone(0xFF895F, SystemColors.Highlight);
        public static Color AccentText => Tone(0xFFAA87, SystemColors.WindowText);
        public static Color Ink => Tone(0x211510, SystemColors.HighlightText);

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int bytes);

        // Keep HWND chrome, caption buttons, Alt+Space and resize hit testing native.
        public static void FrameWindow(Form window)
        {
            window.BackColor = Frame;
            window.Padding = new Padding(5);
            window.HandleCreated += (s, e) => {
                if (SystemInformation.HighContrast) return;
                int dark = 1, black = 0, white = 0xFFFFFF;
                if (DwmSetWindowAttribute(window.Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(window.Handle, 19, ref dark, sizeof(int));
                // Windows 11 supports explicit COLORREF values; earlier systems keep their native chrome.
                DwmSetWindowAttribute(window.Handle, 34, ref black, sizeof(int));
                DwmSetWindowAttribute(window.Handle, 35, ref black, sizeof(int));
                DwmSetWindowAttribute(window.Handle, 36, ref white, sizeof(int));
            };
        }
    }

    enum ActionStyle { Primary, Secondary, Quiet }

    // Remains a real Button: WinForms owns activation, accessibility, and Tab/Space/Enter.
    sealed class SetupButton : Button
    {
        readonly ActionStyle style;

        public SetupButton(ActionStyle style)
        {
            this.style = style;
            AutoSize = false;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Margin = Padding.Empty;
            bool primary = style == ActionStyle.Primary;
            FlatAppearance.BorderSize = 1;
            FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Frame;
            BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.Black;
            ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.White;
            FlatAppearance.MouseOverBackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(27, 28, 30);
            FlatAppearance.MouseDownBackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(11, 12, 13);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled || SystemInformation.HighContrast) {
                base.OnPaint(e);
            } else {
                e.Graphics.Clear(Color.FromArgb(12, 12, 12));
                using (var outline = new Pen(Theme.Border))
                    e.Graphics.DrawRectangle(outline, 0, 0, Width - 1, Height - 1);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(166, 166, 167),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            if (Enabled && Focused && ShowFocusCues && !SystemInformation.HighContrast) {
                var thickness = Math.Max(1, DeviceDpi / 80);
                var inset = 3 * thickness;
                using (var pen = new Pen(style == ActionStyle.Primary ? Theme.AccentText : Theme.Text, thickness))
                    e.Graphics.DrawRectangle(pen, inset, inset, Width - 2 * inset - 1, Height - 2 * inset - 1);
            }
        }
    }

    sealed class ThemeProgress : Control
    {
        int value;
        public int Value { get => value; set { this.value = Math.Min(100, Math.Max(0, value)); Invalidate(); } }
        public ThemeProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.ProgressBar;
            AccessibleName = "操作进度";
            TabStop = false;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Border);
            using (var brush = new SolidBrush(Theme.Accent))
                e.Graphics.FillRectangle(brush, 0, 0, Width * value / 100, Height);
        }
    }

    // A printed waveform, not an animated indicator of installation progress.
    sealed class SignalArt : Control
    {
        public SignalArt()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "LiveClipper 波形装饰";
            TabStop = false;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast || Width < 12 || Height < 12) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = DeviceDpi / 96f, cy = Height * .52f;
            using (var grid = new Pen(Color.FromArgb(50, 48, 47), unit))
            using (var quiet = new Pen(Color.FromArgb(106, 77, 67), 2 * unit))
            using (var signal = new Pen(Theme.Accent, 2 * unit)) {
                g.DrawLine(grid, 0, cy, Width, cy);
                for (float x = 0; x < Width; x += 6 * unit) {
                    float t = x / Width;
                    double envelope = Math.Exp(-Math.Pow((t - .49) * 7, 2)) + .47 * Math.Exp(-Math.Pow((t - .71) * 12, 2));
                    float h = (float)((.07 + .90 * envelope) * (.28 + .72 * Math.Abs(Math.Sin(t * 48) * Math.Cos(t * 13))) * Height * .43);
                    g.DrawLine(t > .30 && t < .76 ? signal : quiet, x, cy - h, x, cy + h);
                }
            }
        }
    }

    sealed class LocationCard : Panel
    {
        public Rectangle FieldBounds { get; set; }
        public bool FieldFocused { get; set; }
        public LocationCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            TabStop = false;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float u = DeviceDpi / 96f;
            if (Width <= 8 * u || Height <= 10 * u) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var face = new RectangleF(2*u, u, Width-4*u, Height-8*u);
            if (!SystemInformation.HighContrast) {
                for (int i = 3; i >= 1; i--) {
                    var offset = face;
                    offset.Offset(0, i*1.5f*u);
                    using (var shape = Rounded(offset, 16*u))
                    using (var shade = new SolidBrush(Color.FromArgb(42, 0, 0, 0)))
                        g.FillPath(shade, shape);
                }
            }
            using (var shape = Rounded(face, 16*u))
            using (var brush = new SolidBrush(Theme.Surface))
            using (var line = new Pen(Theme.Frame, u)) {
                g.FillPath(brush, shape);
                g.DrawPath(line, shape);
            }
            if (FieldBounds.Width > 2 && FieldBounds.Height > 2) {
                var rect = RectangleF.Inflate(FieldBounds, -.5f*u, -.5f*u);
                using (var shape = Rounded(rect, 6*u))
                using (var brush = new SolidBrush(Theme.Well))
                using (var line = new Pen(FieldFocused ? Theme.Accent : Theme.Frame, u)) {
                    g.FillPath(brush, shape);
                    g.DrawPath(line, shape);
                }
            }
        }
    }

    // Keep the Win32 multiline EDIT: native selection, text metrics, wheel, keyboard,
    // clipboard, and read-only accessibility. Our two rails use its real scroll range.
    sealed class LogEdit : TextBox
    {
        public event EventHandler ViewChanged;
        [StructLayout(LayoutKind.Sequential)]
        public struct ScrollRange
        {
            public int size, mask, min, max, page, pos, track;
            public int Last => Math.Max(min, max - Math.Max(0, page - 1));
        }

        [DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollRange info);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect { public int left, top, right, bottom; }
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

        public Size NativeEdges
        {
            get {
                if (!IsHandleCreated) return Size.Empty;
                GetWindowRect(Handle, out var window);
                GetClientRect(Handle, out var client);
                return new Size(window.right - window.left - client.right, window.bottom - window.top - client.bottom);
            }
        }

        Size? viewport;
        public void ClipToViewport(Size size)
        {
            if (viewport == size) return;
            viewport = size;
            // Clip the actual EDIT HWND, not just the layout of its parent. The OS
            // still owns scrolling/selection, but its light nonclient bars are invisible.
            var previous = Region;
            Region = new Region(new Rectangle(Point.Empty, size));
            previous?.Dispose();
        }

        public ScrollRange Range(bool vertical)
        {
            var range = new ScrollRange { size = Marshal.SizeOf(typeof(ScrollRange)), mask = 7 };
            if (IsHandleCreated) GetScrollInfo(Handle, vertical ? 1 : 0, ref range);
            return range;
        }

        void Send(int msg, int w, int l) => SendMessage(Handle, msg, (IntPtr)w, (IntPtr)l);

        public void Page(bool vertical, bool forward) =>
            Send(vertical ? 0x115 : 0x114, forward ? 3 : 2, 0); // WM_*SCROLL, SB_PAGE*

        public void MoveThumb(bool vertical, int target)
        {
            var r = Range(vertical);
            if (target == r.pos) return;
            if (target <= r.min || target >= r.Last) {
                Send(vertical ? 0x115 : 0x114, target <= r.min ? 6 : 7, 0);
            } else if (vertical) {
                // EM_LINESCROLL has a full-width line delta, unlike the packed thumb message.
                Send(0xB6, 0, target - r.pos);
            } else if (target <= ushort.MaxValue) {
                Send(0x114, unchecked(target << 16) | 4, 0); // SB_THUMBPOSITION
            } else {
                // An exceptional long log line can exceed the 16-bit thumb position.
                // Ask EDIT for one character step, then move by its actual native units.
                Send(0xB6, Math.Sign(target - r.pos), 0);
                var after = Range(false).pos;
                var step = Math.Abs(after - r.pos);
                if (step > 0) Send(0xB6, (int)Math.Round((target - after) / (double)step), 0);
            }
        }

        public void ForwardWheel(ref Message m) => SendMessage(Handle, m.Msg, m.WParam, m.LParam);

        protected override void WndProc(ref Message m)
        {
            // WM_PRINT asks each child to render independently. With the dark rails in
            // use, EDIT owns only the text viewport on screen and in print.
            if (viewport.HasValue && m.Msg == 0x0317) // WM_PRINT
                m.LParam = new IntPtr(m.LParam.ToInt64() & ~2L); // omit PRF_NONCLIENT
            base.WndProc(ref m);
            switch (m.Msg) {
                case 0x000F: case 0x0005: case 0x0114: case 0x0115: case 0x00B6:
                case 0x020A: case 0x020E: case 0x0100: case 0x00B7: case 0x00C2:
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
    }

    sealed class LogRail : Control
    {
        readonly LogEdit edit;
        readonly bool vertical;
        int grip;
        bool hot, dragging;
        int Length => vertical ? Height : Width;
        int Axis(Point p) => vertical ? p.Y : p.X;

        public LogRail(LogEdit edit, bool vertical)
        {
            this.edit = edit;
            this.vertical = vertical;
            AccessibleRole = AccessibleRole.ScrollBar;
            AccessibleName = vertical ? "诊断日志 · 垂直滚动" : "诊断日志 · 水平滚动";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Margin = Padding.Empty;
            Dock = DockStyle.Fill;
            TabStop = false;
            BackColor = Color.Black;
        }

        Rectangle Thumb(LogEdit.ScrollRange r)
        {
            if (r.Last <= r.min || Length <= 0) return Rectangle.Empty;
            var total = Math.Max(1, (long)r.max - r.min + 1);
            int size = Math.Min(Length, Math.Max(22 * DeviceDpi / 96, (int)(Length * (long)r.page / total)));
            int offset = (int)((Length - size) * (long)(r.pos - r.min) / (r.Last - r.min));
            return vertical ? new Rectangle(0, offset, Width, size) : new Rectangle(offset, 0, size, Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            var thumb = Thumb(edit.Range(vertical));
            if (thumb.IsEmpty) return;
            int inset = Math.Max(2, 3 * DeviceDpi / 96);
            thumb.Inflate(-inset, -inset);
            if (thumb.Width <= 0 || thumb.Height <= 0) return;
            using (var ink = new SolidBrush(Capture ? Color.FromArgb(109, 112, 117) :
                hot ? Color.FromArgb(91, 94, 100) : Color.FromArgb(68, 72, 78)))
                e.Graphics.FillRectangle(ink, thumb);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var thumb = Thumb(edit.Range(vertical));
            if (thumb.IsEmpty) return;
            edit.Focus();
            var start = vertical ? thumb.Y : thumb.X;
            if (thumb.Contains(e.Location)) {
                grip = Axis(e.Location) - start;
                dragging = true;
                Capture = true;
            } else {
                edit.Page(vertical, Axis(e.Location) > start);
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging || !Capture || e.Button != MouseButtons.Left) return;
            var r = edit.Range(vertical);
            var thumb = Thumb(r);
            int travel = Length - (vertical ? thumb.Height : thumb.Width);
            if (thumb.IsEmpty || travel <= 0) return;
            int offset = Math.Min(travel, Math.Max(0, Axis(e.Location) - grip));
            edit.MoveThumb(vertical, r.min + (int)Math.Round((r.Last - r.min) * (double)offset / travel));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) dragging = false; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A || m.Msg == 0x020E) { edit.ForwardWheel(ref m); return; }
            base.WndProc(ref m);
        }
    }

    sealed class LogSurface : TableLayoutPanel
    {
        public LogSurface(LogEdit edit)
        {
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.Black;
            if (SystemInformation.HighContrast) {
                Controls.Add(edit); // Honor the user's chosen native contrast scheme.
                return;
            }
            ColumnCount = RowCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            RowStyles.Add(new RowStyle(SizeType.Absolute, 14));

            // Only EDIT's client area is visible. Its scrollbar HWND geometry remains
            // intact for accurate native range/first-line data, but outside this clip.
            var clip = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Well };
            var horizontal = new LogRail(edit, false);
            var vertical = new LogRail(edit, true);
            edit.Dock = DockStyle.None;
            clip.Controls.Add(edit);
            Controls.Add(clip, 0, 0);
            Controls.Add(vertical, 1, 0);
            Controls.Add(horizontal, 0, 1);
            Controls.Add(new Panel {
                Dock = DockStyle.Fill, BackColor = Color.Black, Margin = Padding.Empty, TabStop = false
            }, 1, 1); // the intersection is a real surface, not an exposed size-box
            void FitEdit()
            {
                var edges = edit.NativeEdges;
                int minimum = (int)Math.Ceiling(14 * edit.DeviceDpi / 96f);
                var railWidth = Math.Max(minimum, edges.Width);
                var railHeight = Math.Max(minimum, edges.Height);
                if (ColumnStyles[1].Width != railWidth) ColumnStyles[1].Width = railWidth;
                if (RowStyles[1].Height != railHeight) RowStyles[1].Height = railHeight;
                edit.ClipToViewport(clip.ClientSize);
                edit.SetBounds(0, 0, clip.ClientSize.Width + edges.Width, clip.ClientSize.Height + edges.Height);
            }
            clip.ClientSizeChanged += (s,e) => FitEdit();
            edit.HandleCreated += (s,e) => FitEdit();
            edit.ViewChanged += (s,e) => { horizontal.Invalidate(); vertical.Invalidate(); };
            FitEdit();
        }
    }

    sealed class ContentViewport : Panel
    {
        public ContentViewport() { AutoScroll = true; }
        protected override void OnLayout(LayoutEventArgs e)
        {
            if (Controls.Count == 1) {
                var content = Controls[0];
                content.Height = Math.Max(ClientSize.Height, content.MinimumSize.Height);
            }
            base.OnLayout(e);
        }
    }

    sealed class SourceSelector:FlowLayoutPanel
    {
        readonly RadioButton[] choices=new RadioButton[3];
        public event EventHandler SelectedIndexChanged;
        public int SelectedIndex
        {
            get{for(var i=0;i<choices.Length;i++)if(choices[i].Checked)return i;return 0;}
            set{choices[value].Checked=true;}
        }
        public SourceSelector()
        {
            WrapContents=false;Margin=Padding.Empty;
            var titles=new[]{"自动（按公网 IP 地区）","国内源优先","官方源优先"};
            for(var i=0;i<choices.Length;i++)
            {
                var button=new RadioButton{Text=titles[i],Appearance=Appearance.Button,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleCenter,Width=i==0?235:175,Height=31,Margin=new Padding(0,0,8,0),Cursor=Cursors.Hand,ForeColor=Theme.Text,BackColor=Theme.Background};
                button.FlatAppearance.BorderColor=Theme.Border;button.FlatAppearance.CheckedBackColor=Theme.Accent;choices[i]=button;Controls.Add(button);
                button.CheckedChanged+=(s,e)=>{if(button.Checked)SelectedIndexChanged?.Invoke(this,EventArgs.Empty);};
            }
            choices[0].Checked=true;
        }
    }
}

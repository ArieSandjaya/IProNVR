using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Warna, font, dan bantuan umum untuk tampilan ala ASM300 (gelap, panel biru-abu, aksen biru).</summary>
    public static class Asm
    {
        public static readonly Color Bg = Color.FromArgb(10, 12, 16);
        public static readonly Color Panel = Color.FromArgb(22, 26, 34);
        public static readonly Color Panel2 = Color.FromArgb(30, 35, 46);
        public static readonly Color Bar = Color.FromArgb(44, 58, 82);
        public static readonly Color BarHot = Color.FromArgb(60, 80, 114);
        public static readonly Color BarOpen = Color.FromArgb(32, 84, 148);
        public static readonly Color Border = Color.FromArgb(62, 72, 90);
        public static readonly Color Text = Color.FromArgb(228, 233, 241);
        public static readonly Color Muted = Color.FromArgb(138, 150, 168);
        public static readonly Color Accent = Color.FromArgb(70, 152, 255);
        public static readonly Color Danger = Color.FromArgb(236, 84, 84);
        public static readonly Color Ok = Color.FromArgb(80, 200, 120);
        public static readonly Color Warn = Color.FromArgb(245, 170, 50);
        public static readonly Color Yellow = Color.FromArgb(250, 214, 70);

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public static Font F(float pt, bool bold)
        {
            string key = pt + (bold ? "b" : "r") + AppSettings.Current.AsmMagnification;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font("Segoe UI", pt * AppSettings.Current.AsmMagnification / 100f, bold ? FontStyle.Bold : FontStyle.Regular);
                fonts[key] = f;
            }
            return f;
        }

        /// <summary>Ukuran piksel mengikuti Display magnification.</summary>
        public static int S(int px)
        {
            return (int)Math.Round(px * AppSettings.Current.AsmMagnification / 100.0);
        }

        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip();
            Theme.StyleMenu(m, true);
            return m;
        }

        /// <summary>Warnai item menu yang ditambahkan setelah Menu() dibuat.</summary>
        public static void StyleMenu(ContextMenuStrip m)
        {
            Theme.StyleItems(m.Items, true);
        }

        public static ToolStripMenuItem Item(ContextMenuStrip m, string text, EventHandler click)
        {
            var it = new ToolStripMenuItem(text);
            if (click != null) it.Click += click;
            m.Items.Add(it);
            return it;
        }

        public static ToolStripMenuItem Sub(ToolStripMenuItem parent, string text, EventHandler click)
        {
            var it = new ToolStripMenuItem(text);
            if (click != null) it.Click += click;
            parent.DropDownItems.Add(it);
            return it;
        }

        public static string FormatTime(DateTime t)
        {
            AppSettings s = AppSettings.Current;
            string d = s.DateFormat ?? "yyyy/MM/dd";
            return t.ToString(d + (s.Time24h ? " HH:mm:ss" : " hh:mm:ss tt"), System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string DateFormats
        {
            get { return "yyyy/MM/dd|MM/dd/yyyy|dd/MM/yyyy|yyyy-MM-dd|dd-MMM-yyyy"; }
        }
    }

    public enum AsmIcon
    {
        None, Menu, Live, Map, Setting, Search, Play, Reverse, Pause, Stop, FastFwd, FastRew, NextFrame, PrevFrame,
        SkipFwd, SkipBack, PrevRec, NextRec, Camera, Print, Full, Plus, Minus, Close, ChevronDown, ChevronUp, ChevronLeft,
        ChevronRight, Folder, Download, Alarm, Mic, Speaker, Layout
    }

    public static class AsmIcons
    {
        public static void Draw(Graphics g, AsmIcon ic, Rectangle r, Color col)
        {
            if (ic == AsmIcon.None) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height);
            float x0 = r.X + (r.Width - s) / 2f;
            float y0 = r.Y + (r.Height - s) / 2f;
            float pw = Math.Max(1.4f, s / 11f);
            using (var pen = new Pen(col, pw))
            using (var br = new SolidBrush(col))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                Func<float, float, PointF> P = (fx, fy) => new PointF(x0 + fx * s, y0 + fy * s);
                Func<float, float, float, float, RectangleF> R = (fx, fy, fw, fh) => new RectangleF(x0 + fx * s, y0 + fy * s, fw * s, fh * s);
                Action<float, float, float, float> L = (ax, ay, bx, by) => g.DrawLine(pen, P(ax, ay), P(bx, by));
                Action<PointF[]> Fill = pts => g.FillPolygon(br, pts);
                switch (ic)
                {
                    case AsmIcon.Menu:
                        L(.15f, .26f, .85f, .26f); L(.15f, .5f, .85f, .5f); L(.15f, .74f, .85f, .74f);
                        break;
                    case AsmIcon.Live:
                        g.DrawRectangle(pen, x0 + .1f * s, y0 + .16f * s, .8f * s, .52f * s);
                        L(.5f, .68f, .5f, .84f); L(.3f, .84f, .7f, .84f);
                        Fill(new[] { P(.42f, .28f), P(.42f, .56f), P(.64f, .42f) });
                        break;
                    case AsmIcon.Map:
                        g.DrawPolygon(pen, new[] { P(.1f, .2f), P(.37f, .3f), P(.63f, .2f), P(.9f, .3f), P(.9f, .8f), P(.63f, .7f), P(.37f, .8f), P(.1f, .7f) });
                        L(.37f, .3f, .37f, .8f); L(.63f, .2f, .63f, .7f);
                        break;
                    case AsmIcon.Setting:
                        g.DrawEllipse(pen, R(.27f, .27f, .46f, .46f));
                        g.DrawEllipse(pen, R(.4f, .4f, .2f, .2f));
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            float cx = (float)Math.Cos(a), cy = (float)Math.Sin(a);
                            L(.5f + cx * .27f, .5f + cy * .27f, .5f + cx * .4f, .5f + cy * .4f);
                        }
                        break;
                    case AsmIcon.Search:
                        g.DrawEllipse(pen, R(.14f, .14f, .5f, .5f));
                        L(.6f, .6f, .88f, .88f);
                        break;
                    case AsmIcon.Play:
                        Fill(new[] { P(.26f, .14f), P(.26f, .86f), P(.86f, .5f) });
                        break;
                    case AsmIcon.Reverse:
                        Fill(new[] { P(.74f, .14f), P(.74f, .86f), P(.14f, .5f) });
                        break;
                    case AsmIcon.Pause:
                        g.FillRectangle(br, R(.22f, .16f, .18f, .68f)); g.FillRectangle(br, R(.6f, .16f, .18f, .68f));
                        break;
                    case AsmIcon.Stop:
                        g.FillRectangle(br, R(.2f, .2f, .6f, .6f));
                        break;
                    case AsmIcon.FastFwd:
                        Fill(new[] { P(.06f, .2f), P(.06f, .8f), P(.5f, .5f) }); Fill(new[] { P(.5f, .2f), P(.5f, .8f), P(.94f, .5f) });
                        break;
                    case AsmIcon.FastRew:
                        Fill(new[] { P(.94f, .2f), P(.94f, .8f), P(.5f, .5f) }); Fill(new[] { P(.5f, .2f), P(.5f, .8f), P(.06f, .5f) });
                        break;
                    case AsmIcon.NextFrame:
                        Fill(new[] { P(.14f, .2f), P(.14f, .8f), P(.64f, .5f) }); g.FillRectangle(br, R(.72f, .2f, .14f, .6f));
                        break;
                    case AsmIcon.PrevFrame:
                        Fill(new[] { P(.86f, .2f), P(.86f, .8f), P(.36f, .5f) }); g.FillRectangle(br, R(.14f, .2f, .14f, .6f));
                        break;
                    case AsmIcon.SkipFwd:
                        Fill(new[] { P(.08f, .2f), P(.08f, .8f), P(.46f, .5f) }); Fill(new[] { P(.46f, .2f), P(.46f, .8f), P(.84f, .5f) });
                        g.FillRectangle(br, R(.86f, .2f, .09f, .6f));
                        break;
                    case AsmIcon.SkipBack:
                        Fill(new[] { P(.92f, .2f), P(.92f, .8f), P(.54f, .5f) }); Fill(new[] { P(.54f, .2f), P(.54f, .8f), P(.16f, .5f) });
                        g.FillRectangle(br, R(.05f, .2f, .09f, .6f));
                        break;
                    case AsmIcon.NextRec:
                        Fill(new[] { P(.08f, .2f), P(.08f, .8f), P(.55f, .5f) });
                        g.FillRectangle(br, R(.62f, .2f, .1f, .6f)); g.FillRectangle(br, R(.8f, .2f, .1f, .6f));
                        break;
                    case AsmIcon.PrevRec:
                        Fill(new[] { P(.92f, .2f), P(.92f, .8f), P(.45f, .5f) });
                        g.FillRectangle(br, R(.28f, .2f, .1f, .6f)); g.FillRectangle(br, R(.1f, .2f, .1f, .6f));
                        break;
                    case AsmIcon.Camera:
                        g.DrawRectangle(pen, x0 + .1f * s, y0 + .3f * s, .8f * s, .5f * s);
                        L(.34f, .2f, .66f, .2f); L(.34f, .2f, .3f, .3f); L(.66f, .2f, .7f, .3f);
                        g.DrawEllipse(pen, R(.36f, .42f, .28f, .26f));
                        break;
                    case AsmIcon.Print:
                        g.DrawRectangle(pen, x0 + .12f * s, y0 + .4f * s, .76f * s, .34f * s);
                        g.DrawRectangle(pen, x0 + .28f * s, y0 + .14f * s, .44f * s, .26f * s);
                        g.DrawRectangle(pen, x0 + .28f * s, y0 + .62f * s, .44f * s, .24f * s);
                        break;
                    case AsmIcon.Full:
                        L(.12f, .36f, .12f, .12f); L(.12f, .12f, .36f, .12f);
                        L(.64f, .12f, .88f, .12f); L(.88f, .12f, .88f, .36f);
                        L(.12f, .64f, .12f, .88f); L(.12f, .88f, .36f, .88f);
                        L(.88f, .64f, .88f, .88f); L(.88f, .88f, .64f, .88f);
                        break;
                    case AsmIcon.Plus:
                        L(.2f, .5f, .8f, .5f); L(.5f, .2f, .5f, .8f);
                        break;
                    case AsmIcon.Minus:
                        L(.2f, .5f, .8f, .5f);
                        break;
                    case AsmIcon.Close:
                        L(.2f, .2f, .8f, .8f); L(.8f, .2f, .2f, .8f);
                        break;
                    case AsmIcon.ChevronDown:
                        g.DrawLines(pen, new[] { P(.2f, .36f), P(.5f, .66f), P(.8f, .36f) });
                        break;
                    case AsmIcon.ChevronUp:
                        g.DrawLines(pen, new[] { P(.2f, .64f), P(.5f, .34f), P(.8f, .64f) });
                        break;
                    case AsmIcon.ChevronLeft:
                        g.DrawLines(pen, new[] { P(.64f, .2f), P(.34f, .5f), P(.64f, .8f) });
                        break;
                    case AsmIcon.ChevronRight:
                        g.DrawLines(pen, new[] { P(.36f, .2f), P(.66f, .5f), P(.36f, .8f) });
                        break;
                    case AsmIcon.Folder:
                        g.DrawPolygon(pen, new[] { P(.1f, .24f), P(.4f, .24f), P(.48f, .34f), P(.9f, .34f), P(.9f, .78f), P(.1f, .78f) });
                        break;
                    case AsmIcon.Download:
                        L(.5f, .14f, .5f, .62f); g.DrawLines(pen, new[] { P(.28f, .42f), P(.5f, .64f), P(.72f, .42f) }); L(.18f, .84f, .82f, .84f);
                        break;
                    case AsmIcon.Alarm:
                        g.DrawPolygon(pen, new[] { P(.5f, .12f), P(.92f, .84f), P(.08f, .84f) });
                        L(.5f, .38f, .5f, .62f); g.FillEllipse(br, R(.46f, .7f, .08f, .08f));
                        break;
                    case AsmIcon.Mic:
                        g.DrawRectangle(pen, x0 + .38f * s, y0 + .1f * s, .24f * s, .42f * s);
                        g.DrawArc(pen, R(.26f, .3f, .48f, .38f), 0, 180);
                        L(.5f, .68f, .5f, .86f); L(.36f, .86f, .64f, .86f);
                        break;
                    case AsmIcon.Speaker:
                        Fill(new[] { P(.14f, .38f), P(.34f, .38f), P(.54f, .2f), P(.54f, .8f), P(.34f, .62f), P(.14f, .62f) });
                        g.DrawArc(pen, R(.5f, .3f, .3f, .4f), -60, 120);
                        break;
                    case AsmIcon.Layout:
                        g.DrawRectangle(pen, x0 + .12f * s, y0 + .14f * s, .76f * s, .72f * s);
                        L(.5f, .14f, .5f, .86f); L(.12f, .5f, .88f, .5f);
                        break;
                }
            }
            g.SmoothingMode = old;
        }
    }

    /// <summary>Tombol datar bergambar ikon (dan/atau teks) bergaya ASM300; bisa toggle.</summary>
    public class AsmButton : Control
    {
        bool hot, down;
        public AsmIcon Icon = AsmIcon.None;
        public string Label = "";
        public bool Toggle;
        public bool Highlight;           // tampil aksen (mis. mode aktif)
        public bool Borderless;
        public Color? IconColor;
        bool isChecked;

        public AsmButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(Asm.S(34), Asm.S(30));
            Cursor = Cursors.Hand;
            Tag = "nostyle";
            TabStop = false;
        }

        public AsmButton(AsmIcon icon, string tip, EventHandler click) : this()
        {
            Icon = icon;
            if (click != null) Click += click;
            if (!string.IsNullOrEmpty(tip)) new ToolTip().SetToolTip(this, tip);
        }

        public bool Checked
        {
            get { return isChecked; }
            set { if (isChecked != value) { isChecked = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnClick(EventArgs e)
        {
            if (Toggle) isChecked = !isChecked;
            Invalidate();
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Asm.Panel2;
            if (!Enabled) bg = Asm.Panel;
            else if (down) bg = Asm.BarOpen;
            else if (isChecked || Highlight) bg = Color.FromArgb(34, 86, 150);
            else if (hot) bg = Asm.BarHot;
            using (var b = new SolidBrush(Borderless && !hot && !isChecked && !down ? Parent != null ? Parent.BackColor : Asm.Panel : bg))
                g.FillRectangle(b, ClientRectangle);
            if (!Borderless)
            {
                using (var p = new Pen(isChecked || Highlight ? Asm.Accent : Asm.Border))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            Color fg = !Enabled ? Color.FromArgb(84, 92, 106) : (IconColor.HasValue ? IconColor.Value : Asm.Text);
            int pad = Asm.S(5);
            if (Icon != AsmIcon.None && string.IsNullOrEmpty(Label))
            {
                int sz = Math.Min(Width, Height) - pad * 2;
                AsmIcons.Draw(g, Icon, new Rectangle((Width - sz) / 2, (Height - sz) / 2, sz, sz), fg);
            }
            else
            {
                int x = pad;
                if (Icon != AsmIcon.None)
                {
                    int sz = Height - pad * 2;
                    AsmIcons.Draw(g, Icon, new Rectangle(pad, pad, sz, sz), fg);
                    x += sz + pad;
                }
                TextRenderer.DrawText(g, Label, Font, new Rectangle(x, 0, Width - x - pad, Height), fg,
                    TextFormatFlags.VerticalCenter | (Icon == AsmIcon.None ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    public class AsmTab
    {
        public string Key = "";
        public string Title = "";
        public bool Closable = true;
        public bool Detachable = true;
    }

    /// <summary>Strip tab di title bar: "Operation window", "Live window", "Map window", "Setup", "Search".</summary>
    public class AsmTabStrip : Control
    {
        readonly List<AsmTab> tabs = new List<AsmTab>();
        readonly List<Rectangle> rects = new List<Rectangle>();
        string selected = "";
        int hotIdx = -1;
        int hotClose = -1;
        int pressIdx = -1;
        Point pressPt;
        bool detachFired;

        public event Action<string> TabSelected;
        public event Action<string> TabClosed;
        public event Action<string> TabDetach;

        public AsmTabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Tag = "nostyle";
            Height = Asm.S(34);
        }

        public string Selected { get { return selected; } }

        public bool Has(string key) { return Find(key) != null; }

        AsmTab Find(string key)
        {
            foreach (AsmTab t in tabs) if (t.Key == key) return t;
            return null;
        }

        public void Add(AsmTab t)
        {
            tabs.Add(t);
            Invalidate();
        }

        public void Remove(string key)
        {
            AsmTab t = Find(key);
            if (t == null) return;
            tabs.Remove(t);
            Invalidate();
        }

        public void Rename(string key, string title)
        {
            AsmTab t = Find(key);
            if (t != null) { t.Title = title; Invalidate(); }
        }

        public void Select(string key)
        {
            if (Find(key) == null) return;
            selected = key;
            Invalidate();
        }

        public List<AsmTab> Tabs { get { return tabs; } }

        void Layout2()
        {
            rects.Clear();
            int x = 0;
            using (Graphics g = CreateGraphics())
            {
                foreach (AsmTab t in tabs)
                {
                    int w = TextRenderer.MeasureText(g, t.Title, Asm.F(9f, true)).Width + Asm.S(34) + (t.Closable ? Asm.S(22) : 0);
                    rects.Add(new Rectangle(x, 0, w, Height));
                    x += w + 2;
                }
            }
        }

        Rectangle CloseRect(Rectangle r)
        {
            int s = Asm.S(14);
            return new Rectangle(r.Right - s - Asm.S(8), (r.Height - s) / 2, s, s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Bg);
            Layout2();
            for (int i = 0; i < tabs.Count; i++)
            {
                AsmTab t = tabs[i];
                Rectangle r = rects[i];
                bool sel = t.Key == selected;
                using (var b = new SolidBrush(sel ? Asm.Panel2 : (i == hotIdx ? Asm.Panel : Asm.Bg)))
                    g.FillRectangle(b, r);
                if (sel)
                {
                    using (var b = new SolidBrush(Asm.Accent)) g.FillRectangle(b, r.X, r.Bottom - Asm.S(3), r.Width, Asm.S(3));
                }
                TextRenderer.DrawText(g, t.Title, Asm.F(9f, sel), new Rectangle(r.X + Asm.S(14), 0, r.Width - Asm.S(14) - (t.Closable ? Asm.S(26) : Asm.S(8)), Height),
                    sel ? Asm.Text : Asm.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (t.Closable)
                {
                    Rectangle c = CloseRect(r);
                    if (i == hotIdx && hotClose == i)
                    {
                        using (var b = new SolidBrush(Asm.BarHot)) g.FillRectangle(b, c);
                    }
                    Rectangle ic = Rectangle.Inflate(c, -Asm.S(3), -Asm.S(3));
                    AsmIcons.Draw(g, AsmIcon.Close, ic, sel ? Asm.Text : Asm.Muted);
                }
            }
        }

        int HitTab(Point p)
        {
            for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTab(e.Location);
            int hc = (i >= 0 && tabs[i].Closable && CloseRect(rects[i]).Contains(e.Location)) ? i : -1;
            if (i != hotIdx || hc != hotClose) { hotIdx = i; hotClose = hc; Invalidate(); }

            if (e.Button == MouseButtons.Left && pressIdx >= 0 && !detachFired && pressIdx < tabs.Count)
            {
                int dy = Math.Abs(e.Y - pressPt.Y);
                if (dy > Height * 1.6 && tabs[pressIdx].Detachable)
                {
                    detachFired = true;
                    if (TabDetach != null) TabDetach(tabs[pressIdx].Key);
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hotIdx = -1;
            hotClose = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = HitTab(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0)
            {
                if (tabs[i].Closable && CloseRect(rects[i]).Contains(e.Location))
                {
                    if (TabClosed != null) TabClosed(tabs[i].Key);
                    return;
                }
                pressIdx = i;
                pressPt = e.Location;
                detachFired = false;
                if (TabSelected != null) TabSelected(tabs[i].Key);
            }
            else if (e.Button == MouseButtons.Right && i >= 0)
            {
                AsmTab t = tabs[i];
                ContextMenuStrip m = Asm.Menu();
                if (t.Detachable && AppSettings.Current.SeparateTab)
                    Asm.Item(m, "Pisahkan ke jendela sendiri", delegate { if (TabDetach != null) TabDetach(t.Key); });
                if (t.Closable) Asm.Item(m, "Tutup tab", delegate { if (TabClosed != null) TabClosed(t.Key); });
                if (m.Items.Count > 0)
                {
                    Asm.StyleMenu(m);
                    m.Show(this, e.Location);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressIdx = -1;
            base.OnMouseUp(e);
        }
    }

    /// <summary>Tombol buka/tutup panel (strip tipis di tepi function panel / operation panel).</summary>
    public class CollapseHandle : Control
    {
        public bool Horizontal;        // true = strip mendatar (operation panel di bawah)
        public bool Collapsed;
        bool hot;

        public CollapseHandle(bool horizontal)
        {
            Horizontal = horizontal;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Tag = "nostyle";
            if (horizontal) Height = Asm.S(14); else Width = Asm.S(14);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(hot ? Asm.BarHot : Asm.Bar)) g.FillRectangle(b, ClientRectangle);
            AsmIcon ic;
            if (Horizontal) ic = Collapsed ? AsmIcon.ChevronUp : AsmIcon.ChevronDown;
            else ic = Collapsed ? AsmIcon.ChevronRight : AsmIcon.ChevronLeft;
            int s = Math.Min(Width, Height) - 2;
            AsmIcons.Draw(g, ic, new Rectangle((Width - s) / 2, (Height - s) / 2, s, s), Asm.Text);
        }
    }

    /// <summary>Panel fungsi ala ASM300: kolom pencarian, bar yang bisa dibuka (hanya satu terbuka), isi panel di tengah.</summary>
    public class FunctionPanel : Panel
    {
        class Section
        {
            public string Key;
            public string Title;
            public Control Content;
            public AccordionBar Bar;
            public bool Top;
        }

        class AccordionBar : Control
        {
            public string Title = "";
            public bool Open;
            public bool Match;
            bool hot;

            public AccordionBar()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                Height = Asm.S(26);
                Cursor = Cursors.Hand;
                Tag = "nostyle";
            }

            protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (var b = new SolidBrush(Open ? Asm.BarOpen : (hot ? Asm.BarHot : Asm.Bar))) g.FillRectangle(b, ClientRectangle);
                using (var p = new Pen(Asm.Bg)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
                int s = Asm.S(14);
                AsmIcons.Draw(g, Open ? AsmIcon.ChevronUp : AsmIcon.ChevronDown, new Rectangle(Asm.S(6), (Height - s) / 2, s, s), Asm.Text);
                TextRenderer.DrawText(g, Title, Asm.F(9f, true), new Rectangle(Asm.S(26), 0, Width - Asm.S(30), Height),
                    Match ? Asm.Warn : Asm.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        readonly List<Section> sections = new List<Section>();
        readonly Panel top = new Panel();
        readonly Panel bottom = new Panel();
        readonly Panel body = new Panel();
        readonly Panel searchRow = new Panel();
        readonly TextBox txtSearch = new TextBox();
        string openKey = "";
        bool suppressSearch;

        public event Action<string> SearchTextChanged;
        public event Action<string> SectionOpened;

        public FunctionPanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            Width = Asm.S(270);

            searchRow.Dock = DockStyle.Top;
            searchRow.Height = Asm.S(32);
            searchRow.BackColor = Asm.Panel;
            searchRow.Padding = new Padding(Asm.S(4));
            searchRow.Tag = "nostyle";
            txtSearch.Dock = DockStyle.Fill;
            txtSearch.BorderStyle = BorderStyle.FixedSingle;
            txtSearch.BackColor = Asm.Panel2;
            txtSearch.ForeColor = Asm.Text;
            txtSearch.MaxLength = 16;
            txtSearch.Font = Asm.F(9f, false);
            txtSearch.TextChanged += delegate
            {
                if (!suppressSearch && SearchTextChanged != null) SearchTextChanged(txtSearch.Text.Trim());
            };
            var clear = new AsmButton(AsmIcon.Close, "Hapus pencarian", delegate { txtSearch.Text = ""; });
            clear.Dock = DockStyle.Right;
            clear.Width = Asm.S(26);
            clear.Borderless = true;
            searchRow.Controls.Add(txtSearch);
            searchRow.Controls.Add(clear);

            top.Dock = DockStyle.Top;
            top.Height = 0;
            top.Tag = "nostyle";
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 0;
            bottom.Tag = "nostyle";
            body.Dock = DockStyle.Fill;
            body.BackColor = Asm.Panel;
            body.Tag = "nostyle";

            Controls.Add(body);
            Controls.Add(bottom);
            Controls.Add(top);
            Controls.Add(searchRow);
        }

        public string OpenKey { get { return openKey; } }

        public bool SearchVisible
        {
            get { return searchRow.Visible; }
            set { searchRow.Visible = value; }
        }

        public void ClearSearch()
        {
            suppressSearch = true;
            txtSearch.Text = "";
            suppressSearch = false;
            foreach (Section s in sections) s.Bar.Match = false;
            Invalidate(true);
        }

        public void AddSection(string key, string title, Control content, bool atTop)
        {
            var s = new Section();
            s.Key = key;
            s.Title = title;
            s.Content = content;
            s.Top = atTop;
            s.Bar = new AccordionBar();
            s.Bar.Title = title;
            s.Bar.Dock = DockStyle.Top;
            string k = key;
            s.Bar.Click += delegate { Toggle(k); };
            content.Dock = DockStyle.Fill;
            content.Visible = false;
            body.Controls.Add(content);
            sections.Add(s);
            // Panel.AutoSize + Dock.Top: urutan tampil = kebalikan urutan Add, jadi sisipkan di indeks 0
            // urutan docking WinForms: indeks tertinggi dipasang lebih dulu (paling luar).
            // Grup atas: bar pertama harus paling atas -> sisipkan tiap bar baru di indeks 0.
            // Grup bawah: bar terakhir harus paling bawah -> cukup ditambahkan di akhir.
            Panel host = atTop ? top : bottom;
            host.Controls.Add(s.Bar);
            if (atTop) host.Controls.SetChildIndex(s.Bar, 0);
            host.Height = host.Controls.Count * s.Bar.Height;
        }

        Section Find(string key)
        {
            foreach (Section s in sections) if (s.Key == key) return s;
            return null;
        }

        public Control ContentOf(string key)
        {
            Section s = Find(key);
            return s == null ? null : s.Content;
        }

        public void Toggle(string key)
        {
            if (openKey == key) Open("");
            else Open(key);
        }

        public void Open(string key)
        {
            openKey = key;
            foreach (Section s in sections)
            {
                bool o = s.Key == key;
                s.Bar.Open = o;
                s.Bar.Invalidate();
                s.Content.Visible = o;
            }
            if (SectionOpened != null && key.Length > 0) SectionOpened(key);
        }

        public void MarkMatch(string key, bool match)
        {
            Section s = Find(key);
            if (s != null && s.Bar.Match != match) { s.Bar.Match = match; s.Bar.Invalidate(); }
        }
    }

    /// <summary>Pad kontrol PTZ lingkaran: arah dari sudut klik, kecepatan dari jarak ke pusat.</summary>
    public class PtzPad : Control
    {
        /// <summary>dx, dy dalam -1..1 (kanan, bawah = positif); pecahan kecepatan 0..1.</summary>
        public event Action<int, int, double> Move;
        public event Action Released;

        bool pressed;
        int curDx, curDy;
        double curFrac;

        public PtzPad()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Asm.S(120), Asm.S(120));
            Tag = "nostyle";
            Cursor = Cursors.Hand;
        }

        void Compute(Point p)
        {
            double cx = Width / 2.0, cy = Height / 2.0;
            double dx = p.X - cx, dy = p.Y - cy;
            double r = Math.Min(Width, Height) / 2.0;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            curFrac = Math.Min(1.0, dist / r);
            curDx = 0;
            curDy = 0;
            if (dist < r * 0.2) return; // pusat = tidak bergerak
            double ang = Math.Atan2(dy, dx); // 0 = kanan, positif ke bawah
            double deg = ang * 180 / Math.PI;
            // 8 sektor 45 derajat
            int sector = (int)Math.Round(deg / 45.0);
            switch (((sector % 8) + 8) % 8)
            {
                case 0: curDx = 1; curDy = 0; break;
                case 1: curDx = 1; curDy = 1; break;
                case 2: curDx = 0; curDy = 1; break;
                case 3: curDx = -1; curDy = 1; break;
                case 4: curDx = -1; curDy = 0; break;
                case 5: curDx = -1; curDy = -1; break;
                case 6: curDx = 0; curDy = -1; break;
                default: curDx = 1; curDy = -1; break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            Compute(e.Location);
            Invalidate();
            if ((curDx != 0 || curDy != 0) && Move != null) Move(curDx, curDy, curFrac);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!pressed) return;
            int ox = curDx, oy = curDy;
            Compute(e.Location);
            if (ox != curDx || oy != curDy)
            {
                Invalidate();
                if (Move != null && (curDx != 0 || curDy != 0)) Move(curDx, curDy, curFrac);
                else if (Released != null) Released();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!pressed) return;
            pressed = false;
            curDx = curDy = 0;
            Invalidate();
            if (Released != null) Released();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Asm.Panel);
            int d = Math.Min(Width, Height) - 4;
            var rc = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
            using (var b = new SolidBrush(Asm.Panel2)) g.FillEllipse(b, rc);
            using (var p = new Pen(Asm.Border, 2f)) g.DrawEllipse(p, rc);
            int inner = d / 3;
            var ic = new Rectangle((Width - inner) / 2, (Height - inner) / 2, inner, inner);
            using (var b = new SolidBrush(Asm.Bar)) g.FillEllipse(b, ic);
            using (var p = new Pen(Asm.Border)) g.DrawEllipse(p, ic);

            float cx = Width / 2f, cy = Height / 2f;
            float rr = d / 2f;
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                int dx = (int)Math.Round(Math.Cos(a)), dy = (int)Math.Round(Math.Sin(a));
                bool act = pressed && dx == curDx && dy == curDy;
                float tx = cx + (float)Math.Cos(a) * rr * 0.72f, ty = cy + (float)Math.Sin(a) * rr * 0.72f;
                float sz = rr * 0.13f;
                var pts = new PointF[3];
                // segitiga menghadap keluar
                double ox = Math.Cos(a), oy = Math.Sin(a);
                double px = -oy, py = ox;
                pts[0] = new PointF((float)(tx + ox * sz * 1.3), (float)(ty + oy * sz * 1.3));
                pts[1] = new PointF((float)(tx - ox * sz + px * sz), (float)(ty - oy * sz + py * sz));
                pts[2] = new PointF((float)(tx - ox * sz - px * sz), (float)(ty - oy * sz - py * sz));
                using (var b = new SolidBrush(act ? Asm.Accent : (Enabled ? Asm.Text : Asm.Muted))) g.FillPolygon(b, pts);
            }
        }
    }

    /// <summary>TabControl gelap (ala ASM300) untuk halaman setup/pencarian.</summary>
    public class AsmTabControl : TabControl
    {
        public AsmTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SizeMode = TabSizeMode.Normal;
            ItemSize = new Size(100, Asm.S(30));
            Padding = new Point(Asm.S(16), 4);
            DrawMode = TabDrawMode.OwnerDrawFixed;
            Tag = "nostyle";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Panel);
            using (var line = new Pen(Asm.Border))
                g.DrawLine(line, 0, ItemSize.Height + 2, Width, ItemSize.Height + 2);
            for (int i = 0; i < TabCount; i++)
            {
                Rectangle r = GetTabRect(i);
                bool sel = i == SelectedIndex;
                using (var b = new SolidBrush(sel ? Asm.Panel2 : Asm.Panel)) g.FillRectangle(b, r);
                TextRenderer.DrawText(g, TabPages[i].Text, Asm.F(9f, sel), r, sel ? Asm.Text : Asm.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (sel)
                {
                    using (var b = new SolidBrush(Asm.Accent)) g.FillRectangle(b, r.X, r.Bottom - 2, r.Width, 3);
                }
            }
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }
    }
}

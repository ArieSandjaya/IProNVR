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
        // palet disamplel dari screenshot ASM300
        public static readonly Color Bg = Color.FromArgb(0, 0, 0);
        public static readonly Color Panel = Color.FromArgb(21, 24, 31);       // #15181f badan panel samping
        public static readonly Color Panel2 = Color.FromArgb(32, 34, 41);      // #202229 tombol / kolom
        public static readonly Color Bar = Color.FromArgb(48, 54, 67);         // #303643 bar accordion
        public static readonly Color BarHot = Color.FromArgb(58, 66, 82);
        public static readonly Color BarOpen = Color.FromArgb(48, 54, 67);
        public static readonly Color Border = Color.FromArgb(64, 74, 92);      // #404a5c
        public static readonly Color Text = Color.FromArgb(182, 188, 210);     // #b6bcd2 teks & ikon
        public static readonly Color Muted = Color.FromArgb(88, 91, 103);      // #585b67 nonaktif
        public static readonly Color Accent = Color.FromArgb(104, 146, 191);   // #6892bf
        public static readonly Color TabBg = Color.FromArgb(30, 55, 77);       // #1e374d tab aktif
        public static readonly Color TabLine = Color.FromArgb(69, 97, 128);    // #456180
        public static readonly Color Input = Color.FromArgb(13, 14, 16);       // #0d0e10 kolom isian
        public static readonly Color BtnBorder = Color.FromArgb(53, 62, 77);   // #353e4d
        public static readonly Color ViewBorder = Color.FromArgb(95, 95, 95);  // #5f5f5f
        public static readonly Color CellFrame = Color.FromArgb(95, 95, 95);   // bingkai tipis antar area gambar
        public static readonly Color Label = Color.FromArgb(221, 221, 221);    // #dddddd label
        public static readonly Color Danger = Color.FromArgb(236, 84, 84);
        public static readonly Color Ok = Color.FromArgb(80, 200, 120);
        public static readonly Color Warn = Color.FromArgb(245, 170, 50);
        public static readonly Color Yellow = Color.FromArgb(250, 214, 70);

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static string famReg, famBold;
        static float fontScale = 1f;

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

        /// <summary>Muat Source Sans 3 (font bawaan tampilan ASM300) dari resource; bila gagal pakai Segoe UI.</summary>
        static void InitFonts()
        {
            famReg = "Segoe UI"; famBold = "Segoe UI";
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                int ok = 0;
                foreach (string res in new[] { "SourceSans3-400.ttf", "SourceSans3-600.ttf" })
                {
                    string name = null;
                    foreach (string n in asm.GetManifestResourceNames()) if (n.EndsWith(res)) name = n;
                    if (name == null) continue;
                    byte[] data;
                    using (var st = asm.GetManifestResourceStream(name))
                    {
                        data = new byte[st.Length];
                        int off = 0;
                        while (off < data.Length) { int r = st.Read(data, off, data.Length - off); if (r <= 0) break; off += r; }
                    }
                    IntPtr p = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(data.Length);
                    System.Runtime.InteropServices.Marshal.Copy(data, 0, p, data.Length);
                    uint cnt = 0;
                    if (AddFontMemResourceEx(p, (uint)data.Length, IntPtr.Zero, ref cnt) != IntPtr.Zero) ok++;
                }
                if (ok == 2)
                {
                    using (var t1 = new Font("Source Sans 3", 10f))
                    using (var t2 = new Font("Source Sans 3 SemiBold", 10f))
                        if (t1.Name == "Source Sans 3" && t2.Name == "Source Sans 3 SemiBold")
                        { famReg = "Source Sans 3"; famBold = "Source Sans 3 SemiBold"; fontScale = 1.12f; }
                }
            }
            catch { }
        }

        /// <summary>Faktor dasar: seluruh UI 25% lebih kecil dari ukuran piksel ASM300 mentah.</summary>
        public const double Base = 0.75;

        public static Font F(float pt, bool bold)
        {
            if (famReg == null) InitFonts();
            string key = pt + (bold ? "b" : "r") + AppSettings.Current.AsmMagnification;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                bool semi = famBold != famReg;
                f = new Font(bold ? famBold : famReg, (float)(pt * fontScale * Base * AppSettings.Current.AsmMagnification / 100.0),
                    bold && !semi ? FontStyle.Bold : FontStyle.Regular);
                fonts[key] = f;
            }
            return f;
        }

        /// <summary>Ukuran piksel mengikuti Display magnification.</summary>
        public static int S(int px)
        {
            return (int)Math.Round(px * Base * AppSettings.Current.AsmMagnification / 100.0);
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
        ChevronRight, Folder, Download, Alarm, Mic, Speaker, Layout, PrevChap, NextChap, StepBack, StepFwd, CurveBack, CurveFwd
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
                    case AsmIcon.PrevChap:
                        g.FillRectangle(br, R(.06f, .22f, .1f, .56f));
                        Fill(new[] { P(.5f, .22f), P(.5f, .78f), P(.18f, .5f) }); Fill(new[] { P(.86f, .22f), P(.86f, .78f), P(.54f, .5f) });
                        break;
                    case AsmIcon.NextChap:
                        g.FillRectangle(br, R(.84f, .22f, .1f, .56f));
                        Fill(new[] { P(.5f, .22f), P(.5f, .78f), P(.82f, .5f) }); Fill(new[] { P(.14f, .22f), P(.14f, .78f), P(.46f, .5f) });
                        break;
                    case AsmIcon.StepBack:
                        Fill(new[] { P(.08f, .5f), P(.4f, .22f), P(.4f, .78f) });
                        g.FillRectangle(br, R(.52f, .22f, .13f, .56f)); g.FillRectangle(br, R(.74f, .22f, .13f, .56f));
                        break;
                    case AsmIcon.StepFwd:
                        Fill(new[] { P(.92f, .5f), P(.6f, .22f), P(.6f, .78f) });
                        g.FillRectangle(br, R(.35f, .22f, .13f, .56f)); g.FillRectangle(br, R(.13f, .22f, .13f, .56f));
                        break;
                    case AsmIcon.CurveBack:
                    case AsmIcon.CurveFwd:
                    {
                        bool fw = ic == AsmIcon.CurveFwd;
                        using (var cp = new Pen(col, Math.Max(2f, s / 7f)))
                        {
                            cp.StartCap = LineCap.Round; cp.EndCap = LineCap.Round;
                            g.DrawArc(cp, R(.16f, .24f, .68f, .68f), fw ? 200 : -20, fw ? 160 : -160);
                        }
                        // kepala panah di ujung busur
                        if (fw) Fill(new[] { P(.92f, .52f), P(.66f, .50f), P(.80f, .78f) });
                        else Fill(new[] { P(.08f, .52f), P(.34f, .50f), P(.20f, .78f) });
                        break;
                    }
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
        public int GridN;               // >0: gambar pola layar n x n (kotak kecil) sebagai ikon
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
            if (!Enabled) bg = Color.FromArgb(24, 27, 34);
            else if (down) bg = Asm.TabBg;
            else if (isChecked || Highlight) bg = Asm.TabBg;
            else if (hot) bg = Color.FromArgb(40, 46, 58);
            using (var b = new SolidBrush(Borderless && !hot && !isChecked && !down ? Parent != null ? Parent.BackColor : Asm.Panel : bg))
                g.FillRectangle(b, ClientRectangle);
            if (!Borderless)
            {
                using (var p = new Pen(isChecked || Highlight ? Asm.Accent : Asm.BtnBorder))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            Color fg = !Enabled ? Asm.Muted : (IconColor.HasValue ? IconColor.Value : Asm.Text);
            int pad = Asm.S(5);
            if (GridN > 0)
            {
                int box = Math.Min(Width, Height) - pad * 3;
                int gap = GridN > 3 ? 1 : Math.Max(1, Asm.S(2));
                int cell = Math.Max(2, (box - gap * (GridN - 1)) / GridN);
                int tot = cell * GridN + gap * (GridN - 1);
                int ox = (Width - tot) / 2, oy = (Height - tot) / 2;
                using (var br = new SolidBrush(fg))
                    for (int r = 0; r < GridN; r++)
                        for (int c = 0; c < GridN; c++)
                            g.FillRectangle(br, ox + c * (cell + gap), oy + r * (cell + gap), cell, cell);
            }
            else if (Icon != AsmIcon.None && string.IsNullOrEmpty(Label))
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
                    int w = TextRenderer.MeasureText(g, t.Title, Asm.F(12.5f, false)).Width + Asm.S(34) + Asm.S(30) + (t.Closable ? Asm.S(22) : 0);
                    rects.Add(new Rectangle(x, Height - Asm.S(42), w, Asm.S(40)));
                    x += w + 4;
                }
            }
        }

        Rectangle CloseRect(Rectangle r)
        {
            int s = Asm.S(14);
            return new Rectangle(r.Right - s - Asm.S(22), r.Y + (r.Height - s) / 2, s, s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Bg);
            Layout2();
            using (var ln = new Pen(Color.FromArgb(104, 146, 191))) g.DrawLine(ln, 0, Height - 2, Width, Height - 2);
            using (var ln = new Pen(Asm.TabLine)) g.DrawLine(ln, 0, Height - 1, Width, Height - 1);
            for (int i = 0; i < tabs.Count; i++)
            {
                AsmTab t = tabs[i];
                Rectangle r = rects[i];
                bool sel = t.Key == selected;
                int sl = Asm.S(18);                           // sisi kanan miring seperti tab ASM300
                var poly = new[] { new Point(r.X, r.Bottom), new Point(r.X, r.Y), new Point(r.Right - sl, r.Y), new Point(r.Right, r.Bottom) };
                if (sel)
                {
                    using (var b = new SolidBrush(Asm.TabBg)) g.FillPolygon(b, poly);
                    using (var p = new Pen(Color.FromArgb(137, 193, 253))) g.DrawLines(p, poly);
                }
                else
                {
                    using (var b = new SolidBrush(i == hotIdx ? Color.FromArgb(30, 34, 44) : Color.FromArgb(21, 24, 31))) g.FillPolygon(b, poly);
                    using (var p = new Pen(Asm.BtnBorder)) g.DrawLines(p, poly);
                }
                AsmIcons.Draw(g, AsmIcon.Live, new Rectangle(r.X + Asm.S(10), r.Y + (r.Height - Asm.S(22)) / 2, Asm.S(22), Asm.S(22)), sel ? Color.FromArgb(160, 205, 245) : Asm.Muted);
                TextRenderer.DrawText(g, t.Title, Asm.F(12.5f, false), new Rectangle(r.X + Asm.S(40), r.Y, r.Width - Asm.S(40) - sl - (t.Closable ? Asm.S(22) : 0), r.Height),
                    sel ? Color.FromArgb(200, 225, 248) : Asm.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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
            if (horizontal) Height = Asm.S(26); else Width = Asm.S(26);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = hot ? Color.FromArgb(40, 46, 58) : Color.FromArgb(34, 39, 48);
            Color edge = Color.FromArgb(48, 54, 67);
            int w = Width, h = Height;
            PointF[] poly;
            if (Horizontal)
            {
                // tab trapesium di tengah, seperti ASM300
                float cx = w / 2f, tw = Asm.S(31), bw = Asm.S(40), th = h - 1;
                poly = new[] { new PointF(cx - bw, h), new PointF(cx - tw, 1), new PointF(cx + tw, 1), new PointF(cx + bw, h) };
                using (var b = new SolidBrush(Color.FromArgb(10, 11, 13))) g.FillRectangle(b, 0, h - 1, w, 1);
            }
            else
            {
                float cy = h / 2f, tw = Asm.S(36), bw = Asm.S(46);
                poly = new[] { new PointF(w, cy - bw), new PointF(1, cy - tw), new PointF(1, cy + tw), new PointF(w, cy + bw) };
                using (var b = new SolidBrush(Color.FromArgb(38, 44, 53))) g.FillRectangle(b, 0, 0, 1, h);
            }
            using (var b = new SolidBrush(fill)) g.FillPolygon(b, poly);
            using (var p = new Pen(edge)) g.DrawPolygon(p, poly);
            // panah kecil biru
            float ax = w / 2f, ay = h / 2f, sz = Asm.S(5);
            PointF[] ar;
            if (Horizontal) ar = Collapsed ? new[] { new PointF(ax - sz, ay + sz / 2), new PointF(ax + sz, ay + sz / 2), new PointF(ax, ay - sz / 2) } : new[] { new PointF(ax - sz, ay - sz / 2), new PointF(ax + sz, ay - sz / 2), new PointF(ax, ay + sz / 2) };
            else ar = Collapsed ? new[] { new PointF(ax - sz / 2, ay - sz), new PointF(ax - sz / 2, ay + sz), new PointF(ax + sz / 2, ay) } : new[] { new PointF(ax + sz / 2, ay - sz), new PointF(ax + sz / 2, ay + sz), new PointF(ax - sz / 2, ay) };
            using (var b = new SolidBrush(Color.FromArgb(74, 123, 178))) g.FillPolygon(b, ar);
        }
    }

    /// <summary>Kolom pencarian berbentuk pil: ikon kaca pembesar, teks bantuan "Search", tombol hapus.</summary>
    public class PillSearch : Control
    {
        public readonly TextBox Box = new TextBox();
        public PillSearch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Tag = "nostyle";
            Height = Asm.S(43);
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Asm.Panel2;
            Box.ForeColor = Asm.Text;
            Box.Font = Asm.F(12f, false);
            Box.MaxLength = 16;
            Box.Tag = "nostyle";
            Box.TextChanged += delegate { Invalidate(); };
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { if (Box.Text.Length == 0) Box.Visible = false; Invalidate(); };
            Box.Visible = false;       // saat kosong & tidak fokus: tampilkan teks bantuan "Search" (digambar di bawah)
            Controls.Add(Box);
        }

        Rectangle ClearRect { get { int s = Asm.S(26); return new Rectangle(Width - s - Asm.S(12), (Height - s) / 2, s, s); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Asm.S(46), r = Asm.S(46);
            Box.Left = x;
            Box.Width = Math.Max(20, Width - x - r);
            Box.Top = (Height - Box.Height) / 2;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Box.Text.Length > 0 && ClearRect.Contains(e.Location)) Box.Text = "";
                Box.Visible = true;
                Box.Focus();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = Box.Text.Length > 0 && ClearRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Asm.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(Asm.S(1), Asm.S(3), Width - Asm.S(3), Height - Asm.S(6));
            using (var gp = AsmUi.RoundRect(r, r.Height / 2))
            {
                using (var b = new SolidBrush(Asm.Panel2)) g.FillPath(b, gp);
                using (var p = new Pen(Box.Focused ? Asm.Accent : Color.FromArgb(40, 56, 74), 1.5f)) g.DrawPath(p, gp);
            }
            int s = Asm.S(22);
            AsmIcons.Draw(g, AsmIcon.Search, new Rectangle(Asm.S(14), (Height - s) / 2, s, s), Asm.Text);
            if (!Box.Visible)
                TextRenderer.DrawText(g, "Search", Asm.F(13f, true), new Rectangle(Box.Left, 0, Box.Width, Height), Color.FromArgb(125, 129, 144),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            if (Box.Text.Length > 0)
                AsmIcons.Draw(g, AsmIcon.Close, new Rectangle(Width - Asm.S(34), (Height - Asm.S(16)) / 2, Asm.S(16), Asm.S(16)), Asm.Text);
        }

        public override string Text { get { return Box.Text; } set { Box.Text = value; if (value.Length > 0) Box.Visible = true; } }
    }

    /// <summary>Label teks tengah di bawah kelompok tombol (digambar sendiri agar tidak ikut redup saat panel dinonaktifkan).</summary>
    public class CapLabel : Control
    {
        public string Caption = "";
        public CapLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Tag = "nostyle";
            Enabled = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Asm.Bg);
            TextRenderer.DrawText(e.Graphics, Caption, Asm.F(11f, false), ClientRectangle, Asm.Label,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public static class AsmUi
    {
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Panel fungsi ala ASM300: kolom pencarian, bar yang bisa dibuka (hanya satu terbuka).
    /// Bar grup atas ditumpuk dari atas dengan isi di area tengah; bar grup bawah ditumpuk dari bawah dengan isi tepat di bawah bar-nya.</summary>
    public class FunctionPanel : Panel
    {
        class Section
        {
            public string Key;
            public string Title;
            public Control Content;
            public AccordionBar Bar;
            public bool Top;
            public int Height;
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
                Height = Asm.S(37);
                Cursor = Cursors.Hand;
                Tag = "nostyle";
            }

            protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Asm.Panel);
                Rectangle r = new Rectangle(0, Asm.S(3), Width, Height - Asm.S(6));
                using (var b = new SolidBrush(hot && !Open ? Asm.BarHot : Asm.Bar)) g.FillRectangle(b, r);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int s = Asm.S(14);
                AsmIcons.Draw(g, Open ? AsmIcon.ChevronUp : AsmIcon.ChevronDown, new Rectangle(Asm.S(10), (Height - s) / 2, s, s), Asm.Text);
                TextRenderer.DrawText(g, Title, Asm.F(14f, true), new Rectangle(Asm.S(40), 0, Width - Asm.S(44), Height),
                    Match ? Asm.Warn : Asm.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>Area penampung yang menata bar & isi secara manual (tidak bergantung pada urutan Dock).</summary>
        class Host : Panel
        {
            public FunctionPanel Owner;
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (Owner != null && Owner.splitY >= 0)
                    using (var b = new SolidBrush(Color.FromArgb(160, 160, 160))) e.Graphics.FillRectangle(b, 0, Owner.splitY, Width, Asm.S(2));
            }
            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (Owner != null) Owner.DoLayout(this);
            }
        }

        readonly List<Section> sections = new List<Section>();
        readonly Host host = new Host();
        readonly PillSearch searchRow = new PillSearch();
        string openKey = "";
        bool suppressSearch;

        public event Action<string> SearchTextChanged;
        public event Action<string> SectionOpened;

        public FunctionPanel()
        {
            Tag = "nostyle";
            BackColor = Asm.Panel;
            Width = Asm.S(374);

            searchRow.Dock = DockStyle.Top;
            searchRow.Box.TextChanged += delegate
            {
                if (!suppressSearch && SearchTextChanged != null) SearchTextChanged(searchRow.Box.Text.Trim());
            };

            host.Owner = this;
            host.Dock = DockStyle.Fill;
            host.BackColor = Asm.Bg;
            host.Tag = "nostyle";

            Controls.Add(host);
            Controls.Add(searchRow);
        }

        void DoLayout(Host h)
        {
            int w = h.ClientSize.Width, H = h.ClientSize.Height;
            int barH = Asm.S(37);
            int y = 0;
            // grup atas: bar ditumpuk dari atas
            foreach (Section s in sections)
            {
                if (!s.Top) continue;
                s.Bar.SetBounds(0, y, w, barH);
                y += barH;
            }
            var bot = sections.FindAll(delegate(Section s) { return !s.Top; });
            Section op = bot.Find(delegate(Section s) { return s.Key == openKey; });
            splitY = -1;
            int by;
            if (op != null)
            {
                // bagian bawah yang terbuka: ditaruh tepat di bawah ruang kosong + garis pemisah (seperti ASM300); bar lain menempel di dasar
                int gap = Asm.S(31);
                splitY = y + gap;
                int oy = splitY + Asm.S(6);
                int others = bot.Count - 1;
                int openH = Math.Max(Asm.S(120), H - oy - barH - others * barH - Asm.S(3));
                op.Bar.SetBounds(0, oy, w, barH);
                op.Content.SetBounds(0, oy + barH, w, openH);
                by = H - others * barH;
                foreach (Section s in bot) { if (s == op) continue; s.Bar.SetBounds(0, by, w, barH); by += barH; }
            }
            else
            {
                by = H - bot.Count * barH;
                foreach (Section s in bot) { s.Bar.SetBounds(0, by, w, barH); by += barH; }
                // isi grup atas yang terbuka mengisi sisa tengah
                foreach (Section s in sections)
                {
                    if (!s.Top || s.Key != openKey) continue;
                    s.Content.SetBounds(0, y, w, Math.Max(0, H - bot.Count * barH - y));
                }
            }
            h.Invalidate();
        }

        int splitY = -1;

        public string OpenKey { get { return openKey; } }

        public bool SearchVisible
        {
            get { return searchRow.Visible; }
            set { searchRow.Visible = value; }
        }

        public void ClearSearch()
        {
            suppressSearch = true;
            searchRow.Box.Text = "";
            suppressSearch = false;
            foreach (Section s in sections) s.Bar.Match = false;
            Invalidate(true);
        }

        public void AddSection(string key, string title, Control content, bool atTop)
        {
            AddSection(key, title, content, atTop, 300);
        }

        public void AddSection(string key, string title, Control content, bool atTop, int heightPx)
        {
            var s = new Section();
            s.Key = key;
            s.Title = title;
            s.Content = content;
            s.Top = atTop;
            s.Height = Asm.S(heightPx);
            s.Bar = new AccordionBar();
            s.Bar.Title = title;
            string k = key;
            s.Bar.Click += delegate { Toggle(k); };
            content.Dock = DockStyle.None;
            content.Visible = false;
            sections.Add(s);
            host.Controls.Add(content);
            host.Controls.Add(s.Bar);
            host.PerformLayout();
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
            host.PerformLayout();
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

    /// <summary>Tab vertikal di sisi kiri (teks diputar 90 derajat) seperti panel Camera/HDD di ASM300.</summary>
    public class VTabs : Panel
    {
        readonly List<string> titles = new List<string>();
        readonly List<Panel> pages = new List<Panel>();
        readonly Strip strip;
        int sel;
        public event Action SelectedChanged;

        class Strip : Control
        {
            public VTabs Owner;
            public Strip()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                Tag = "nostyle";
            }
            Rectangle Rect(int i) { int h = Asm.S(93); return new Rectangle(0, i * (h + 2), Width, h); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                for (int i = 0; i < Owner.titles.Count; i++) if (Rect(i).Contains(e.Location)) { Owner.SelectedIndex = i; break; }
                base.OnMouseDown(e);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Asm.Bg);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                for (int i = 0; i < Owner.titles.Count; i++)
                {
                    Rectangle r = Rect(i);
                    bool on = i == Owner.sel;
                    using (var b = new SolidBrush(on ? Asm.TabBg : Asm.Panel2)) g.FillRectangle(b, r);
                    using (var p = new Pen(on ? Asm.TabLine : Asm.BtnBorder)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                    var st = g.Save();
                    g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
                    g.RotateTransform(-90);
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (var br = new SolidBrush(on ? Asm.Label : Asm.Text))
                        g.DrawString(Owner.titles[i], Asm.F(12f, true), br, 0, 0, sf);
                    g.Restore(st);
                }
            }
        }

        public VTabs()
        {
            Tag = "nostyle";
            BackColor = Asm.Bg;
            strip = new Strip { Owner = this, Width = Asm.S(43) };
            Controls.Add(strip);
        }

        public List<Panel> Pages { get { return pages; } }

        public void AddPage(string title, Panel page)
        {
            titles.Add(title);
            pages.Add(page);
            page.Dock = DockStyle.None;
            page.Visible = pages.Count - 1 == sel;
            Controls.Add(page);
            PerformLayout();
            strip.Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int sw = strip.Width;
            strip.SetBounds(0, 0, sw, Height);
            foreach (Panel p in pages) p.SetBounds(sw, 0, Math.Max(0, Width - sw), Height);
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value < 0 || value >= pages.Count || value == sel) return;
                sel = value;
                for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == sel;
                strip.Invalidate();
                if (SelectedChanged != null) SelectedChanged();
            }
        }

        public string TitleOf(int i) { return titles[i]; }
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

    /// <summary>Pemilih pola layar: jendela popup sendiri (bukan ContextMenuStrip) agar klik pilihan selalu tertangkap.</summary>
    public static class LayoutPicker
    {
        public static void Show(Control anchor, int maxAreas, string currentId, Action<ScreenLayout> picked)
        {
            var items = new List<ScreenLayout>();
            foreach (ScreenLayout l in ScreenLayout.All) if (l.Count <= maxAreas) items.Add(l);
            int rowH = Asm.S(40), w = Asm.S(300);
            var f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.BackColor = Asm.Border;
            f.Padding = new Padding(1);
            var lb = new ListBox();
            lb.Tag = "nostyle";
            lb.Dock = DockStyle.Fill;
            lb.BorderStyle = BorderStyle.None;
            lb.BackColor = Asm.Panel;
            lb.ForeColor = Asm.Text;
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = rowH;
            lb.IntegralHeight = false;
            foreach (ScreenLayout l in items) lb.Items.Add(l);
            int cur = items.FindIndex(delegate(ScreenLayout l) { return l.Id == currentId; });
            var previews = new Dictionary<string, Bitmap>();
            lb.DrawItem += delegate(object o, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                ScreenLayout l = items[e.Index];
                bool hot = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(hot ? Asm.BarHot : Asm.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
                Bitmap bmp;
                if (!previews.TryGetValue(l.Id, out bmp)) { bmp = l.Preview(Asm.S(40), Asm.S(26)); previews[l.Id] = bmp; }
                e.Graphics.DrawImage(bmp, e.Bounds.X + Asm.S(10), e.Bounds.Y + (e.Bounds.Height - bmp.Height) / 2);
                string t = l.Id + "   (" + l.Count + " area)" + (l.Id == currentId ? "   \u2713" : "");
                TextRenderer.DrawText(e.Graphics, t, Asm.F(10f, false), new Rectangle(e.Bounds.X + Asm.S(60), e.Bounds.Y, e.Bounds.Width - Asm.S(60), e.Bounds.Height),
                    Asm.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            };
            bool done = false;
            lb.MouseMove += delegate(object o, MouseEventArgs e)
            {
                int i = lb.IndexFromPoint(e.Location);
                if (i >= 0 && lb.SelectedIndex != i) lb.SelectedIndex = i;
            };
            lb.MouseUp += delegate(object o, MouseEventArgs e)
            {
                int i = lb.IndexFromPoint(e.Location);
                if (e.Button != MouseButtons.Left || i < 0 || done) return;
                done = true;
                ScreenLayout l = items[i];
                f.Close();
                picked(l);
            };
            f.Deactivate += delegate { if (!done) { done = true; f.Close(); } };
            f.FormClosed += delegate { foreach (Bitmap b in previews.Values) b.Dispose(); f.Dispose(); };
            f.Controls.Add(lb);
            Point pt = anchor.PointToScreen(new Point(0, anchor.Height));
            Rectangle wa = Screen.FromControl(anchor).WorkingArea;
            int h = Math.Min(items.Count * rowH + 2, wa.Bottom - pt.Y - 10);
            if (h < rowH * 4) { h = Math.Min(items.Count * rowH + 2, wa.Height - 20); pt.Y = wa.Bottom - h - 5; }
            f.Bounds = new Rectangle(Math.Min(pt.X, wa.Right - w), pt.Y, w, h);
            f.Show(anchor.FindForm());
            if (cur >= 0) { lb.SelectedIndex = cur; lb.TopIndex = Math.Max(0, cur - 3); }
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Tampilan modern (flat) untuk seluruh aplikasi. Hanya mengubah warna, font, dan gaya kontrol;
    /// tidak mengubah fungsi apa pun. Jendela utama memakai tema terang, jendela Multiview/lihat kamera memakai tema gelap.</summary>
    public static class Theme
    {
        public class Palette
        {
            public Color Bg, Surface, Text, Muted, Accent, AccentHover, Border, Hover, HeaderBg, SelRow, AltRow, Console, ConsoleText, Danger, Tile;
        }

        public static readonly Palette Light = new Palette
        {
            Bg = Color.FromArgb(243, 245, 249),
            Surface = Color.White,
            Text = Color.FromArgb(30, 41, 59),
            Muted = Color.FromArgb(100, 116, 139),
            Accent = Color.FromArgb(37, 99, 235),
            AccentHover = Color.FromArgb(29, 78, 216),
            Border = Color.FromArgb(214, 219, 228),
            Hover = Color.FromArgb(232, 238, 250),
            HeaderBg = Color.FromArgb(241, 244, 249),
            SelRow = Color.FromArgb(219, 232, 254),
            AltRow = Color.FromArgb(249, 250, 252),
            Console = Color.FromArgb(17, 24, 39),
            ConsoleText = Color.FromArgb(209, 218, 230),
            Danger = Color.FromArgb(200, 40, 40),
            Tile = Color.FromArgb(24, 26, 32)
        };

        public static readonly Palette Dark = new Palette
        {
            Bg = Color.FromArgb(16, 17, 21),
            Surface = Color.FromArgb(30, 32, 39),
            Text = Color.FromArgb(226, 232, 240),
            Muted = Color.FromArgb(148, 163, 184),
            Accent = Color.FromArgb(59, 130, 246),
            AccentHover = Color.FromArgb(96, 165, 250),
            Border = Color.FromArgb(55, 60, 72),
            Hover = Color.FromArgb(44, 48, 60),
            HeaderBg = Color.FromArgb(36, 39, 48),
            SelRow = Color.FromArgb(37, 64, 112),
            AltRow = Color.FromArgb(34, 36, 44),
            Console = Color.FromArgb(10, 11, 14),
            ConsoleText = Color.FromArgb(190, 200, 215),
            Danger = Color.FromArgb(248, 113, 113),
            Tile = Color.FromArgb(24, 26, 32)
        };

        static Font _ui;
        static bool _hasConsolas;

        public static Font UiFont
        {
            get { return _ui ?? (_ui = new Font("Segoe UI", 9f)); }
        }

        /// <summary>Dipanggil sekali sebelum jendela pertama dibuat.</summary>
        public static void Init()
        {
            foreach (FontFamily ff in FontFamily.Families)
            {
                if (ff.Name == "Consolas") { _hasConsolas = true; break; }
            }
            ToolStripManager.Renderer = new ToolStripProfessionalRenderer(new ModernColors(Light));
        }

        public static void Apply(Form f, bool dark)
        {
            Palette p = dark ? Dark : Light;
            f.Font = UiFont;
            f.BackColor = p.Bg;
            f.ForeColor = p.Text;
            Walk(f, p);
        }

        /// <summary>Terapkan tema ke sub-pohon control (untuk control yang dibuat setelah jendela tampil).</summary>
        public static void ApplyTo(Control root, bool dark)
        {
            Palette p = dark ? Dark : Light;
            root.BackColor = p.Bg;
            root.ForeColor = p.Text;
            Style(root, p);
            Walk(root, p);
        }

        static void Walk(Control parent, Palette p)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is LiveTile) continue;                  // tile punya warna sendiri
                if ("nostyle".Equals(c.Tag)) continue;
                Style(c, p);
                Walk(c, p);
            }
        }

        static void Style(Control c, Palette p)
        {
            Button b = c as Button;
            if (b != null) { StyleButton(b, p); return; }
            TextBox t = c as TextBox;
            if (t != null) { StyleText(t, p); return; }
            ComboBox cb = c as ComboBox;
            if (cb != null)
            {
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = p.Surface;
                cb.ForeColor = p.Text;
                return;
            }
            NumericUpDown nu = c as NumericUpDown;
            if (nu != null)
            {
                nu.BorderStyle = BorderStyle.FixedSingle;
                nu.BackColor = p.Surface;
                nu.ForeColor = p.Text;
                return;
            }
            ListBox lb = c as ListBox;
            if (lb != null)
            {
                lb.BorderStyle = BorderStyle.FixedSingle;
                lb.BackColor = p.Surface;
                lb.ForeColor = p.Text;
                return;
            }
            DataGridView g = c as DataGridView;
            if (g != null) { StyleGrid(g, p); return; }
            ToolStrip ts = c as ToolStrip;
            if (ts != null) { StyleMenu(ts, p); return; }
            TabControl tc = c as TabControl;
            if (tc != null)
            {
                foreach (TabPage pg in tc.TabPages)
                {
                    pg.UseVisualStyleBackColor = false;
                    pg.BackColor = p.Bg;
                }
                return;
            }
        }

        static bool IsPrimary(string t)
        {
            return t.StartsWith("Mulai", StringComparison.Ordinal)
                || t == "Simpan / Registrasi" || t == "OK"
                || t.StartsWith("Tes Login", StringComparison.Ordinal)
                || t.StartsWith("Tes Koneksi", StringComparison.Ordinal)
                || t == "Isi Otomatis" || t == "Ambil Daftar Kamera"
                || t.StartsWith("Pakai Sebagai", StringComparison.Ordinal);
        }

        static bool IsDanger(string t)
        {
            return t.StartsWith("Stop", StringComparison.Ordinal) || t.StartsWith("Hapus", StringComparison.Ordinal)
                || t.StartsWith("Batal", StringComparison.Ordinal) || t.StartsWith("Kosongkan", StringComparison.Ordinal);
        }

        static void StyleButton(Button b, Palette p)
        {
            string t = b.Text ?? "";
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            if (IsPrimary(t))
            {
                b.BackColor = p.Accent;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = p.AccentHover;
                b.FlatAppearance.MouseDownBackColor = p.AccentHover;
            }
            else
            {
                b.BackColor = p.Surface;
                b.ForeColor = IsDanger(t) ? p.Danger : p.Text;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = p.Border;
                b.FlatAppearance.MouseOverBackColor = p.Hover;
                b.FlatAppearance.MouseDownBackColor = p.SelRow;
            }
            if (b.AutoSize)
            {
                b.Padding = new Padding(10, 2, 10, 2);
                b.MinimumSize = new Size(0, 30);
            }
        }

        static void StyleText(TextBox t, Palette p)
        {
            if (t.Multiline && t.ReadOnly)
            {
                // log / info: gaya konsol
                t.BorderStyle = BorderStyle.None;
                t.BackColor = p.Console;
                t.ForeColor = p.ConsoleText;
                if (_hasConsolas) t.Font = new Font("Consolas", 9f);
            }
            else
            {
                t.BorderStyle = BorderStyle.FixedSingle;
                t.BackColor = p.Surface;
                t.ForeColor = p.Text;
            }
        }

        static void StyleGrid(DataGridView g, Palette p)
        {
            g.EnableHeadersVisualStyles = false;
            g.BorderStyle = BorderStyle.None;
            g.BackgroundColor = p.Surface;
            g.GridColor = p.Border;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 34;
            g.RowTemplate.Height = 28;

            DataGridViewCellStyle h = g.ColumnHeadersDefaultCellStyle;
            h.BackColor = p.HeaderBg;
            h.ForeColor = p.Muted;
            h.Font = new Font(UiFont, FontStyle.Bold);
            h.SelectionBackColor = p.HeaderBg;
            h.SelectionForeColor = p.Muted;
            h.Padding = new Padding(6, 0, 0, 0);

            DataGridViewCellStyle d = g.DefaultCellStyle;
            d.BackColor = p.Surface;
            d.ForeColor = p.Text;
            d.SelectionBackColor = p.SelRow;
            d.SelectionForeColor = p.Text;
            d.Padding = new Padding(4, 0, 0, 0);

            g.AlternatingRowsDefaultCellStyle.BackColor = p.AltRow;
            g.AlternatingRowsDefaultCellStyle.ForeColor = p.Text;
            g.AlternatingRowsDefaultCellStyle.SelectionBackColor = p.SelRow;
            g.AlternatingRowsDefaultCellStyle.SelectionForeColor = p.Text;
        }

        // ---------- menu ----------

        public static void StyleMenu(ToolStrip ts, bool dark)
        {
            StyleMenu(ts, dark ? Dark : Light);
        }

        static void StyleMenu(ToolStrip ts, Palette p)
        {
            ts.Renderer = new ToolStripProfessionalRenderer(new ModernColors(p));
            ts.BackColor = p.Surface;
            ts.ForeColor = p.Text;
            ts.Font = UiFont;
            ts.Padding = new Padding(6, 2, 0, 2);
            StyleItems(ts.Items, p);
        }

        public static void StyleItems(ToolStripItemCollection items, bool dark)
        {
            StyleItems(items, dark ? Dark : Light);
        }

        static void StyleItems(ToolStripItemCollection items, Palette p)
        {
            foreach (ToolStripItem it in items)
            {
                it.ForeColor = it.Enabled ? p.Text : p.Muted;
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems) StyleItems(mi.DropDownItems, p);
            }
        }

        class ModernColors : ProfessionalColorTable
        {
            readonly Palette p;

            public ModernColors(Palette palette)
            {
                p = palette;
                UseSystemColors = false;
            }

            public override Color MenuItemSelected { get { return p.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return p.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return p.Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return p.Hover; } }
            public override Color MenuItemPressedGradientMiddle { get { return p.Hover; } }
            public override Color MenuItemPressedGradientEnd { get { return p.Hover; } }
            public override Color MenuItemBorder { get { return p.Hover; } }
            public override Color MenuBorder { get { return p.Border; } }
            public override Color MenuStripGradientBegin { get { return p.Surface; } }
            public override Color MenuStripGradientEnd { get { return p.Surface; } }
            public override Color ToolStripDropDownBackground { get { return p.Surface; } }
            public override Color ImageMarginGradientBegin { get { return p.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return p.Surface; } }
            public override Color ImageMarginGradientEnd { get { return p.Surface; } }
            public override Color SeparatorDark { get { return p.Border; } }
            public override Color SeparatorLight { get { return p.Surface; } }
            public override Color ToolStripBorder { get { return p.Border; } }
            public override Color CheckBackground { get { return p.SelRow; } }
            public override Color CheckSelectedBackground { get { return p.SelRow; } }
            public override Color CheckPressedBackground { get { return p.SelRow; } }
        }
    }

    /// <summary>TabControl datar: judul tab teks polos dengan garis aksen di tab aktif.</summary>
    public class ModernTabControl : TabControl
    {
        public ModernTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(150, 38);
            Padding = new Point(14, 4);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Palette p = Theme.Light;
            Graphics g = e.Graphics;
            g.Clear(p.Bg);
            int stripH = ItemSize.Height + 4;
            using (var strip = new SolidBrush(p.Surface))
                g.FillRectangle(strip, 0, 0, Width, stripH);
            using (var line = new Pen(p.Border))
                g.DrawLine(line, 0, stripH, Width, stripH);

            using (var bold = new Font(Theme.UiFont, FontStyle.Bold))
            {
                for (int i = 0; i < TabCount; i++)
                {
                    Rectangle r = GetTabRect(i);
                    bool sel = i == SelectedIndex;
                    TextRenderer.DrawText(g, TabPages[i].Text, sel ? bold : Theme.UiFont, r,
                        sel ? p.Accent : p.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    if (sel)
                    {
                        using (var ac = new SolidBrush(p.Accent))
                            g.FillRectangle(ac, r.X + 10, stripH - 3, Math.Max(10, r.Width - 20), 3);
                    }
                }
            }
        }
    }
}

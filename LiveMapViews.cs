using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Menjalankan sequence: pindah group tiap beberapa detik.</summary>
    public class SeqRunner
    {
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Action<GroupRec> apply;
        SeqRec seq;
        int idx;
        public bool Running { get { return seq != null; } }
        public string Name { get { return seq == null ? "" : seq.Name; } }

        public SeqRunner(Action<GroupRec> apply)
        {
            this.apply = apply;
            timer.Tick += delegate { Next(); };
        }

        public void Start(SeqRec s)
        {
            if (s.Steps.Count == 0) { MessageBox.Show("Sequence ini belum punya langkah."); return; }
            seq = s;
            idx = -1;
            Next();
        }

        void Next()
        {
            if (seq == null) return;
            idx = (idx + 1) % seq.Steps.Count;
            SeqStep st = seq.Steps[idx];
            GroupRec g = AsmDb.GetGroup(st.GroupId);
            if (g != null) apply(g);
            timer.Stop();
            timer.Interval = Math.Max(1, st.Seconds) * 1000;
            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            seq = null;
        }
    }

    /// <summary>Live window: hanya menampilkan gambar live (tanpa playback/kontrol), dengan Group dan Sequence di panel kiri.</summary>
    public class LiveView : UserControl
    {
        public readonly int Number;
        readonly ViewGrid grid = new ViewGrid();
        readonly FunctionPanel fp = new FunctionPanel();
        readonly CollapseHandle handle = new CollapseHandle(false);
        readonly Panel toolbar = new Panel();
        readonly FlowLayoutPanel flow = new FlowLayoutPanel();
        readonly Label lblInfo = new Label();
        readonly Label lblSeq = new Label();
        readonly SeqRunner runner;
        DeviceTreePanel dev;
        GroupPanel grp;
        SequencePanel seqp;

        public LiveView(int number)
        {
            Number = number;
            Dock = DockStyle.Fill;
            BackColor = Asm.Bg;
            Tag = "nostyle";

            grid.Dock = DockStyle.Fill;
            grid.ReadOnlyMode = true;
            grid.AllowRegisterLayout = false;

            toolbar.Dock = DockStyle.Top;
            toolbar.Height = Asm.S(38);
            toolbar.BackColor = Asm.Panel;
            toolbar.Tag = "nostyle";
            flow.Dock = DockStyle.Fill;
            flow.WrapContents = false;
            flow.Padding = new Padding(Asm.S(4), Asm.S(3), 0, 0);
            flow.Tag = "nostyle";
            var lay = new AsmButton(AsmIcon.Layout, "Pola layar", null);
            lay.Click += delegate { ShowLayoutMenu(lay); };
            flow.Controls.Add(lay);
            flow.Controls.Add(new AsmButton(AsmIcon.Full, "Layar penuh (Esc untuk keluar)", delegate { using (var f = new FullScreenForm(grid)) f.ShowDialog(this); }));
            flow.Controls.Add(new AsmButton(AsmIcon.Camera, "Still picture", delegate
            {
                ViewCell c = grid.Selected;
                if (c != null && c.CurrentImage != null && AsmApp.Can(Perm.StillPrint)) lblInfo.Text = "Tersimpan: " + Stills.Save(c.CurrentImage, Dir.Title(c.NvrId, c.Channel));
            }));
            flow.Controls.Add(new AsmButton(AsmIcon.Close, "Kosongkan semua area", delegate { StopSeq(); grid.ClearAll(); }));
            lblInfo.AutoSize = false;
            lblInfo.Width = Asm.S(500);
            lblInfo.Height = Asm.S(30);
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.ForeColor = Asm.Muted;
            lblInfo.Tag = "nostyle";
            lblSeq.Text = "";
            lblSeq.AutoSize = false;
            lblSeq.Width = Asm.S(50);
            lblSeq.Height = Asm.S(26);
            lblSeq.Margin = new Padding(8, 2, 0, 0);
            lblSeq.TextAlign = ContentAlignment.MiddleCenter;
            lblSeq.Font = Asm.F(9f, true);
            lblSeq.Tag = "nostyle";
            flow.Controls.Add(lblSeq);
            flow.Controls.Add(lblInfo);
            toolbar.Controls.Add(flow);

            runner = new SeqRunner(delegate(GroupRec g) { ApplyGroup(g); lblInfo.Text = "Sequence " + runner.Name + " : " + g.Name; });

            dev = new DeviceTreePanel();
            dev.CameraActivated += delegate(CamRef r)
            {
                StopSeq();
                int i = grid.SelectedIndex < 0 ? 0 : grid.SelectedIndex;
                grid.Assign(i, r.NvrId, r.Channel, true);
                int n = grid.FirstEmpty();
                if (n >= 0) grid.Select(n);
            };
            grp = new GroupPanel();
            grp.GroupChosen += delegate(GroupRec g) { StopSeq(); ApplyGroup(g); lblInfo.Text = "Group: " + g.Name; };
            seqp = new SequencePanel();
            seqp.StartRequested += delegate(SeqRec s) { runner.Start(s); UpdateSeq(); };
            seqp.StopRequested += delegate { StopSeq(); };
            fp.AddSection("grp", "Group", grp, true);
            fp.AddSection("seq", "Sequence", seqp, true);
            fp.AddSection("dev", "Device", dev, true);
            fp.Width = Asm.S(230);
            fp.Open("grp");
            fp.SearchTextChanged += delegate(string t)
            {
                bool a = grp.Filter(t), b = seqp.Filter(t), c = dev.Filter(t);
                if (t.Length == 0) return;
                if (a) fp.Open("grp"); else if (b) fp.Open("seq"); else if (c) fp.Open("dev");
            };
            fp.Dock = DockStyle.Left;
            handle.Dock = DockStyle.Left;
            handle.Click += delegate { fp.Visible = !fp.Visible; handle.Collapsed = !fp.Visible; handle.Invalidate(); };

            var center = new Panel();
            center.Dock = DockStyle.Fill;
            center.Controls.Add(grid);
            center.Controls.Add(toolbar);
            Controls.Add(center);
            Controls.Add(handle);
            Controls.Add(fp);

            grid.SetLayout(ScreenLayout.ById(AppSettings.Current.LiveLayout));
            grid.StateName = "live" + Number;
        }

        void ApplyGroup(GroupRec g)
        {
            grid.SetLayout(ScreenLayout.ById(g.Layout));
            grid.LoadSlots(g.Slots);
        }

        void UpdateSeq()
        {
            lblSeq.Text = runner.Running ? "SEQ" : "";
            lblSeq.BackColor = runner.Running ? Asm.Ok : Asm.Panel;
            lblSeq.ForeColor = runner.Running ? Color.Black : Asm.Text;
        }

        void StopSeq()
        {
            runner.Stop();
            UpdateSeq();
        }

        void ShowLayoutMenu(Control anchor)
        {
            ContextMenuStrip m = Asm.Menu();
            foreach (ScreenLayout l in ScreenLayout.All)
            {
                if (l.Count > AppSettings.Current.LiveMaxAreas) continue;
                ScreenLayout lay = l;
                ToolStripMenuItem it = Asm.Item(m, l.Id + "   (" + l.Count + " area)", delegate
                {
                    StopSeq();
                    grid.SetLayout(lay);
                    AppSettings.Current.LiveLayout = lay.Id;
                    try { AppSettings.Current.Save(); } catch { }
                });
                it.Image = l.Preview(Asm.S(30), Asm.S(20));
                it.Checked = lay.Id == grid.Layout.Id;
            }
            m.Show(anchor, new Point(0, anchor.Height));
        }

        /// <summary>Jalankan item awal dari pengaturan: "1=g:Nama;2=s:Nama".</summary>
        public void ApplyStartup()
        {
            string spec = AppSettings.Current.LiveStartItems ?? "";
            bool configured = false;
            foreach (string part in spec.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (part.StartsWith(Number + "=", StringComparison.Ordinal)) configured = true;
            if (!configured) { if (grid.RestoreState()) lblInfo.Text = "Tampilan terakhir dipulihkan"; return; }
            foreach (string part in spec.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                int n;
                if (!int.TryParse(part.Substring(0, eq), out n) || n != Number) continue;
                string v = part.Substring(eq + 1);
                if (v.Length < 3) continue;
                string name = v.Substring(2);
                try
                {
                    if (v.StartsWith("g:"))
                    {
                        foreach (GroupRec g in AsmDb.ListGroups()) if (g.Name == name) { ApplyGroup(g); lblInfo.Text = "Group: " + g.Name; }
                    }
                    else if (v.StartsWith("s:"))
                    {
                        foreach (SeqRec s in AsmDb.ListSequences()) if (s.Name == name) { runner.Start(s); UpdateSeq(); }
                    }
                }
                catch (Exception ex) { Log.Write("Live start item: " + ex.Message); }
            }
        }

        public void ReloadLists()
        {
            dev.Rebuild();
            grp.Reload();
            seqp.Reload();
        }

        public void Shutdown()
        {
            runner.Stop();
            grid.StopAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Shutdown();
            base.Dispose(disposing);
        }
    }

    /// <summary>Kanvas map: gambar denda + ikon kamera/ikon map. Mode edit: seret ikon, lepas kamera dari pohon untuk menambah.</summary>
    public class MapCanvas : Control
    {
        MapRec map;
        Image img;
        public bool EditMode;
        MapIconRec dragIcon;
        MapIconRec hoverIcon;
        public event Action<MapIconRec> IconActivated;
        public event Action<MapIconRec, Point> IconContext;
        public event Action Changed;
        public event Action<MapIconRec> IconSelected;
        public MapIconRec Selected;

        public MapCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Asm.Bg;
            Tag = "nostyle";
            AllowDrop = true;
            Dir.StatusChanged += OnStatus;
        }

        void OnStatus()
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Dir.StatusChanged -= OnStatus; if (img != null) img.Dispose(); }
            base.Dispose(disposing);
        }

        public MapRec Map
        {
            get { return map; }
            set
            {
                map = value;
                if (img != null) { img.Dispose(); img = null; }
                if (map != null && map.Image != null && map.Image.Length > 0)
                {
                    try { using (var ms = new MemoryStream(map.Image)) img = new Bitmap(ms); }
                    catch { img = null; }
                }
                Selected = null;
                Invalidate();
            }
        }

        RectangleF ImageRect()
        {
            float w = Width, h = Height;
            if (img == null) return new RectangleF(0, 0, w, h);
            float k = Math.Min(w / img.Width, h / img.Height);
            float iw = img.Width * k, ih = img.Height * k;
            return new RectangleF((w - iw) / 2f, (h - ih) / 2f, iw, ih);
        }

        PointF Pos(MapIconRec i)
        {
            RectangleF r = ImageRect();
            return new PointF(r.X + (float)i.X * r.Width, r.Y + (float)i.Y * r.Height);
        }

        MapIconRec Hit(Point p)
        {
            if (map == null) return null;
            int rad = Asm.S(14);
            for (int k = map.Icons.Count - 1; k >= 0; k--)
            {
                PointF c = Pos(map.Icons[k]);
                if ((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y) <= rad * rad) return map.Icons[k];
            }
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Asm.Bg);
            if (map == null)
            {
                TextRenderer.DrawText(g, "Pilih map di daftar.", Asm.F(10f, false), ClientRectangle, Asm.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            RectangleF r = ImageRect();
            if (img != null) g.DrawImage(img, r);
            else TextRenderer.DrawText(g, "(map belum punya gambar)", Asm.F(10f, false), ClientRectangle, Asm.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int rad = Asm.S(13);
            foreach (MapIconRec i in map.Icons)
            {
                PointF c = Pos(i);
                Color col;
                if (i.Kind == 1) col = Asm.Accent;
                else
                {
                    int st = Dir.Status(i.NvrId, i.Channel);
                    col = st == 2 ? Asm.Danger : (st == 0 ? Asm.Muted : Asm.Ok);
                }
                var rect = new RectangleF(c.X - rad, c.Y - rad, rad * 2, rad * 2);
                using (var b = new SolidBrush(Color.FromArgb(230, col))) g.FillEllipse(b, rect);
                using (var p = new Pen(i == Selected ? Asm.Yellow : Color.Black, i == Selected ? 3 : 1)) g.DrawEllipse(p, rect);
                AsmIcons.Draw(g, i.Kind == 1 ? AsmIcon.Map : AsmIcon.Camera,
                    new Rectangle((int)(c.X - rad + 4), (int)(c.Y - rad + 4), rad * 2 - 8, rad * 2 - 8), Color.Black);
                if (AppSettings.Current.ShowIconNames)
                {
                    string name = i.Kind == 1 ? MapName(i.TargetMapId) : Dir.CamName(i.NvrId, i.Channel);
                    Size ts = TextRenderer.MeasureText(name, Asm.F(8f, false));
                    var tr = new Rectangle((int)c.X - ts.Width / 2 - 2, (int)c.Y + rad + 1, ts.Width + 4, ts.Height);
                    using (var b = new SolidBrush(Color.FromArgb(190, 0, 0, 0))) g.FillRectangle(b, tr);
                    TextRenderer.DrawText(g, name, Asm.F(8f, false), tr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                }
            }
        }

        static string MapName(long id)
        {
            try { MapRec m = AsmDb.GetMap(id); return m == null ? "?" : m.Name; }
            catch { return "?"; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            MapIconRec h = Hit(e.Location);
            Selected = h;
            Invalidate();
            if (h != null && IconSelected != null) IconSelected(h);
            if (e.Button == MouseButtons.Left && EditMode) dragIcon = h;
            if (e.Button == MouseButtons.Right && h != null && IconContext != null) IconContext(h, e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (EditMode && dragIcon != null && (e.Button & MouseButtons.Left) != 0)
            {
                RectangleF r = ImageRect();
                dragIcon.X = Math.Max(0, Math.Min(1, (e.X - r.X) / r.Width));
                dragIcon.Y = Math.Max(0, Math.Min(1, (e.Y - r.Y) / r.Height));
                Invalidate();
            }
            else
            {
                MapIconRec h = Hit(e.Location);
                if (h != hoverIcon) { hoverIcon = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragIcon != null) { dragIcon = null; if (Changed != null) Changed(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            MapIconRec h = Hit(e.Location);
            if (h != null && IconActivated != null) IconActivated(h);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            e.Effect = EditMode && e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            if (!EditMode || map == null) return;
            string t = e.Data.GetData(DataFormats.StringFormat) as string;
            if (t == null || !t.StartsWith("asmcam:", StringComparison.Ordinal)) return;
            string[] p = t.Split(':');
            long nid; int ch;
            if (p.Length < 3 || !long.TryParse(p[1], out nid) || !int.TryParse(p[2], out ch)) return;
            Point pt = PointToClient(new Point(e.X, e.Y));
            AddIcon(0, nid, ch, 0, pt);
        }

        public void AddIcon(int kind, long nvrId, int channel, long targetMap, Point at)
        {
            if (map == null) return;
            RectangleF r = ImageRect();
            var i = new MapIconRec();
            i.Kind = kind; i.NvrId = nvrId; i.Channel = channel; i.TargetMapId = targetMap;
            i.X = Math.Max(0, Math.Min(1, (at.X - r.X) / r.Width));
            i.Y = Math.Max(0, Math.Min(1, (at.Y - r.Y) / r.Height));
            map.Icons.Add(i);
            Selected = i;
            Invalidate();
            if (Changed != null) Changed();
        }

        public void RemoveSelected()
        {
            if (map == null || Selected == null) return;
            map.Icons.Remove(Selected);
            Selected = null;
            Invalidate();
            if (Changed != null) Changed();
        }
    }

    /// <summary>Map window: daftar map di kiri, kanvas di tengah; klik ganda ikon kamera = tampilkan live, ikon map = pindah map.</summary>
    public class MapView : UserControl
    {
        readonly FunctionPanel fp = new FunctionPanel();
        readonly ListBox lb = new ListBox();
        readonly MapCanvas canvas = new MapCanvas();
        List<MapRec> maps = new List<MapRec>();
        readonly Label lblInfo = new Label();

        public MapView()
        {
            Dock = DockStyle.Fill;
            BackColor = Asm.Bg;
            Tag = "nostyle";
            lb.Dock = DockStyle.Fill;
            lb.BackColor = Asm.Panel;
            lb.ForeColor = Asm.Text;
            lb.BorderStyle = BorderStyle.None;
            lb.Font = Asm.F(9f, false);
            lb.Tag = "nostyle";
            lb.SelectedIndexChanged += delegate { ShowSelected(); };
            fp.AddSection("map", "Map", lb, true);
            fp.Width = Asm.S(220);
            fp.Open("map");
            fp.Dock = DockStyle.Left;
            fp.SearchTextChanged += delegate(string t) { Reload(t); };

            lblInfo.Dock = DockStyle.Top;
            lblInfo.Height = Asm.S(26);
            lblInfo.ForeColor = Asm.Muted;
            lblInfo.BackColor = Asm.Panel;
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.Tag = "nostyle";
            lblInfo.Text = "  Klik ganda ikon kamera = live di Operation window. Klik kanan = menu (Live / Playback / AUX).";

            canvas.Dock = DockStyle.Fill;
            canvas.IconActivated += OnActivate;
            canvas.IconContext += OnContext;
            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Controls.Add(canvas);
            host.Controls.Add(lblInfo);
            Controls.Add(host);
            Controls.Add(fp);
            Reload("");
        }

        public void Reload(string filter)
        {
            long keep = CurrentId();
            try { maps = AsmDb.ListMaps(true); }
            catch { maps = new List<MapRec>(); }
            lb.Items.Clear();
            var shown = new List<MapRec>();
            foreach (MapRec m in maps)
            {
                if (!string.IsNullOrEmpty(filter) && m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown.Add(m);
                lb.Items.Add(m.Name);
            }
            maps = shown;
            int sel = 0;
            for (int i = 0; i < maps.Count; i++) if (maps[i].Id == keep) sel = i;
            if (maps.Count > 0) lb.SelectedIndex = sel;
            else canvas.Map = null;
        }

        long CurrentId()
        {
            int i = lb.SelectedIndex;
            return i >= 0 && i < maps.Count ? maps[i].Id : 0;
        }

        void ShowSelected()
        {
            int i = lb.SelectedIndex;
            canvas.Map = i >= 0 && i < maps.Count ? maps[i] : null;
        }

        void OnActivate(MapIconRec i)
        {
            if (i.Kind == 1)
            {
                for (int k = 0; k < maps.Count; k++) if (maps[k].Id == i.TargetMapId) { lb.SelectedIndex = k; return; }
                MessageBox.Show("Map tujuan tidak ditemukan.");
                return;
            }
            NvrRecord nvr = Dir.Nvr(i.NvrId);
            CameraRecord cam = Dir.Cam(i.NvrId, i.Channel);
            if (nvr == null) return;
            if (cam == null) { cam = new CameraRecord(); cam.NvrId = nvr.Id; cam.Channel = i.Channel; }
            if (AsmApp.ShowCamera != null) AsmApp.ShowCamera(nvr, cam);
        }

        void OnContext(MapIconRec i, Point at)
        {
            if (i.Kind != 0) return;
            NvrRecord nvr = Dir.Nvr(i.NvrId);
            if (nvr == null) return;
            ContextMenuStrip m = Asm.Menu();
            Asm.Item(m, "Live (Operation window)", delegate { OnActivate(i); });
            ToolStripMenuItem pb = Asm.Item(m, "Playback 5 menit lalu", delegate
            {
                CameraRecord cam = Dir.Cam(i.NvrId, i.Channel);
                if (cam == null) { cam = new CameraRecord(); cam.NvrId = nvr.Id; cam.Channel = i.Channel; }
                if (AsmApp.PlayCameraAt != null) AsmApp.PlayCameraAt(nvr, cam, DateTime.UtcNow.AddMinutes(-5));
            });
            pb.Enabled = AsmApp.Can(Perm.Playback);
            if (AsmApp.Can(Perm.Camera))
            {
                m.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem aux = new ToolStripMenuItem("AUX");
                for (int k = 1; k <= 8; k++)
                {
                    string a = "AUX" + k;
                    ToolStripMenuItem sub = new ToolStripMenuItem(a);
                    Asm.Sub(sub, "Open", delegate { SendAux(nvr, i.Channel, a, "OPEN"); });
                    Asm.Sub(sub, "Close", delegate { SendAux(nvr, i.Channel, a, "CLOSE"); });
                    aux.DropDownItems.Add(sub);
                }
                m.Items.Add(aux);
                Asm.StyleMenu(m);
            }
            m.Show(canvas, at);
        }

        static void SendAux(NvrRecord nvr, int ch, string aux, string ctrl)
        {
            System.Threading.Tasks.Task.Run(delegate
            {
                try
                {
                    NvrSession s = Pool.Control(nvr);
                    s.EnsureLogin();
                    s.Cmd("camctl.cgi", "CH=" + ch + "&CMD=" + aux + "&CTRL=" + ctrl + "&PC=AS60");
                }
                catch (Exception ex) { Log.Write("AUX: " + ex.Message); }
            });
        }
    }
}

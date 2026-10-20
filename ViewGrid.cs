using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Simpan gambar diam dan cetak (menu klik kanan area gambar ASM300: Still picture / Print).</summary>
    public static class Stills
    {
        public static string Save(Image img, string title)
        {
            if (img == null) return null;
            string folder = AppSettings.Current.ResolveRecordFolder();
            try { Directory.CreateDirectory(folder); } catch { }
            string safe = title;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            string path = Path.Combine(folder, safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg");
            using (var bmp = new Bitmap(img)) bmp.Save(path, ImageFormat.Jpeg);
            Log.Write("Still picture: " + path);
            return path;
        }

        public static void Print(Image img, string title)
        {
            if (img == null) return;
            Image copy = new Bitmap(img);
            var doc = new PrintDocument();
            doc.DocumentName = title;
            doc.PrintPage += delegate(object s, PrintPageEventArgs e)
            {
                Rectangle m = e.MarginBounds;
                float k = Math.Min((float)m.Width / copy.Width, (m.Height - 40f) / copy.Height);
                float w = copy.Width * k, h = copy.Height * k;
                using (var f = new Font("Segoe UI", 11f))
                {
                    e.Graphics.DrawString(title + "   " + Asm.FormatTime(DateTime.Now), f, Brushes.Black, m.Left, m.Top);
                }
                e.Graphics.DrawImage(copy, m.Left, m.Top + 30, w, h);
            };
            using (var dlg = new PrintDialog())
            {
                dlg.Document = doc;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    try { doc.Print(); }
                    catch (Exception ex) { MessageBox.Show(ex.Message, "Print"); }
                }
            }
            copy.Dispose();
        }
    }

    /// <summary>Satu area gambar: live (LiveTile) atau playback (PictureBox).</summary>
    public class ViewCell : Panel
    {
        public int Index;
        public long NvrId;
        public int Channel;
        public bool Playback;
        public LiveTile Tile;
        public PictureBox Pic;
        public byte[] LastJpeg;
        public bool Selected;
        public string StatusText = "";

        // fisheye
        public FisheyeRec Fish;
        public FishMode FMode = FishMode.Off;
        double fYaw, fPitch, fHfov = 80;
        volatile int outW = 960, outH = 540;
        Point ptzLast;
        public event Action<ViewCell> FishReload;
        public bool SoftFish { get { FisheyeRec r = Fish; return r != null && r.Enabled && !r.CamStream && FMode != FishMode.Off; } }
        public bool PtzActive { get { return SoftFish && FMode == FishMode.Ptz; } }
        public byte[] RawJpeg { get { return Tile != null && Tile.LastJpeg != null ? Tile.LastJpeg : LastJpeg; } }

        NvrSession sess;
        readonly Label lblName = new Label();
        readonly Label lblEmpty = new Label();
        bool placed;

        public event Action<ViewCell> CellSelected;
        public event Action<ViewCell> CellDoubleClicked;
        public event Action<ViewCell, ViewCell> CellDropped;          // sumber, tujuan (sumber null = dari tree)
        public event Action<ViewCell, string> ExternalDrop;           // tujuan, "asmcam:nvr:ch"
        public event Action<ViewCell, PointF> ImageClicked;

        public bool HasCamera { get { return NvrId != 0 && Channel > 0; } }
        public CamRef Ref { get { return HasCamera ? new CamRef(NvrId, Channel) : null; } }

        public ViewCell()
        {
            BackColor = Asm.Bg;
            Padding = new Padding(2);
            AllowDrop = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); // Selectable: agar roda mouse (zoom PTZ virtual) sampai ke sel

            lblEmpty.Dock = DockStyle.Fill;
            lblEmpty.ForeColor = Asm.Muted;
            lblEmpty.BackColor = Color.Black;
            lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblEmpty.Tag = "nostyle";
            Controls.Add(lblEmpty);

            lblName.Dock = DockStyle.Top;
            lblName.Height = Asm.S(18);
            lblName.ForeColor = Color.White;
            lblName.BackColor = Color.FromArgb(40, 46, 60);
            lblName.Font = Asm.F(8.5f, false);
            lblName.TextAlign = ContentAlignment.MiddleLeft;
            lblName.AutoEllipsis = true;
            lblName.Tag = "nostyle";
            lblName.Visible = false;
            Controls.Add(lblName);

            HookMouse(this);
            HookMouse(lblEmpty);
            HookMouse(lblName);
            SizeChanged += delegate { UpdateOut(); };
            UpdateOut();
        }

        void UpdateOut()
        {
            int w = Math.Max(64, ClientSize.Width - Padding.Horizontal);
            int h = Math.Max(64, ClientSize.Height - Padding.Vertical - (lblName.Visible ? lblName.Height : 0));
            if (w > 1280) { h = h * 1280 / w; w = 1280; }
            outW = w & ~7;
            outH = h & ~7;
        }

        void LoadFish()
        {
            Fish = Fisheye.Get(NvrId, Channel);
            FMode = Fish != null && Fish.Enabled ? Fish.FMode : FishMode.Off;
            ResetView();
        }

        void ResetView()
        {
            fYaw = 0;
            fPitch = Fisheye.DefaultPitch(Fish == null ? 0 : Fish.Mount);
            fHfov = 80;
        }

        public void RequestFishReload()
        {
            if (FishReload != null) FishReload(this);
        }

        /// <summary>Ubah tampilan fisheye (Off/Panorama/Quad/PTZ virtual) dan simpan sebagai bawaan kamera ini.</summary>
        public void SetFishMode(FishMode m)
        {
            if (!HasCamera) return;
            FisheyeRec r = Fisheye.Get(NvrId, Channel);
            if (r == null)
            {
                if (m == FishMode.Off) return;
                r = new FisheyeRec();
                r.NvrId = NvrId;
                r.Channel = Channel;
            }
            r.Enabled = m != FishMode.Off;
            if (m != FishMode.Off) { r.Mode = (int)m; r.CamStream = false; }
            try { Fisheye.Save(r); } catch (Exception ex) { Log.Write("Fisheye simpan: " + ex.Message); }
            RequestFishReload();
        }

        /// <summary>Dipanggil dari thread dekode: kembalikan gambar hasil dewarp atau null bila tidak aktif.</summary>
        public Image FilterFrame(Image img)
        {
            FisheyeRec r = Fish;
            FishMode m = FMode;
            if (r == null || !r.Enabled || r.CamStream || m == FishMode.Off) return null;
            Bitmap b = img as Bitmap;
            if (b == null) return null;
            return Fisheye.Render(b, r, m, fYaw, fPitch, fHfov, outW, outH);
        }

        /// <summary>Gambar ulang frame terakhir (mis. setelah geser/zoom PTZ virtual).</summary>
        void Rerender()
        {
            byte[] raw = RawJpeg;
            PictureBox pb = Playback ? Pic : (Tile == null ? null : Tile.Picture);
            if (raw == null || pb == null) return;
            Bitmap b;
            try { using (var ms = new MemoryStream(raw)) b = new Bitmap(ms); }
            catch { return; }
            Image res = null;
            try { res = FilterFrame(b); } catch { }
            Image show = res ?? b;
            if (res != null) b.Dispose();
            Image old = pb.Image;
            pb.Image = show;
            if (old != null) old.Dispose();
        }

        void OnWheel(object s, MouseEventArgs e)
        {
            if (!PtzActive) return;
            fHfov = Math.Max(25, Math.Min(120, fHfov - e.Delta / 120.0 * 5));
            Rerender();
        }

        public void SetNumber(int n)
        {
            lblEmpty.Text = n.ToString();
            lblEmpty.Font = Asm.F(18f, true);
        }

        // ---------- mouse / drag & drop ----------

        Point down;
        bool dragReady;

        void HookMouse(Control c)
        {
            c.AllowDrop = true;
            c.MouseDown += OnDown;
            c.MouseMove += OnMove;
            c.MouseUp += delegate { dragReady = false; };
            c.MouseWheel += OnWheel;
            c.DragEnter += OnDragEnter;
            c.DragDrop += OnDragDrop;
            c.DoubleClick += delegate { if (CellDoubleClicked != null) CellDoubleClicked(this); };
        }

        void OnDown(object s, MouseEventArgs e)
        {
            Focus();
            if (CellSelected != null) CellSelected(this);
            if (e.Button == MouseButtons.Left && HasCamera)
            {
                if (PtzActive) ptzLast = Control.MousePosition;
                else { dragReady = true; down = Control.MousePosition; }
            }
            if (e.Button == MouseButtons.Right && ContextMenuStrip != null) { }
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (PtzActive && (Control.MouseButtons & MouseButtons.Left) != 0)
            {
                Point cur = Control.MousePosition;
                double k = fHfov / Math.Max(100.0, (double)outW);
                fYaw -= (cur.X - ptzLast.X) * k;
                fPitch -= (cur.Y - ptzLast.Y) * k;
                int mt = Fish == null ? 0 : Fish.Mount;
                fPitch = mt == 1 ? Math.Max(-80, Math.Min(80, fPitch)) : Math.Max(-10, Math.Min(89, fPitch));
                ptzLast = cur;
                Rerender();
                return;
            }
            if (!dragReady || (Control.MouseButtons & MouseButtons.Left) == 0) { dragReady = false; return; }
            Point p = Control.MousePosition;
            if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < 8) return;
            dragReady = false;
            DoDragDrop("asmcell:" + Index, DragDropEffects.Move);
        }

        void OnDragEnter(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.StringFormat)) { e.Effect = DragDropEffects.None; return; }
            // drag antar-area dimulai dengan AllowedEffects=Move: efek Copy akan ditolak OLE (kursor "dilarang", drop tidak terjadi)
            DragDropEffects eff = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : (e.AllowedEffect & DragDropEffects.Move);
            string t = e.Data.GetData(DataFormats.StringFormat) as string;
            if (t != null && t.StartsWith("asmcell:", StringComparison.Ordinal) && (e.AllowedEffect & DragDropEffects.Move) != 0) eff = DragDropEffects.Move;
            e.Effect = eff;
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            string t = e.Data.GetData(DataFormats.StringFormat) as string;
            if (t == null) return;
            if (t.StartsWith("asmcam:", StringComparison.Ordinal))
            {
                if (ExternalDrop != null) ExternalDrop(this, t);
            }
            else if (t.StartsWith("asmcell:", StringComparison.Ordinal))
            {
                int idx;
                if (int.TryParse(t.Substring(8), out idx) && CellDropped != null)
                {
                    ViewCell src = Parent == null ? null : FindSibling(idx);
                    if (src != null && src != this) CellDropped(src, this);
                }
            }
        }

        ViewCell FindSibling(int idx)
        {
            foreach (Control c in Parent.Controls)
            {
                ViewCell v = c as ViewCell;
                if (v != null && v.Index == idx) return v;
            }
            return null;
        }

        public void SetSelected(bool sel)
        {
            Selected = sel;
            BackColor = sel ? Asm.Yellow : Asm.Bg;
        }

        // ---------- isi ----------

        string TitleText()
        {
            return Dir.Title(NvrId, Channel);
        }

        void SetName()
        {
            lblName.Text = " " + TitleText() + (StatusText.Length > 0 ? "  -  " + StatusText : "");
            lblName.Visible = AppSettings.Current.ShowCamNames && HasCamera;
        }

        public void ClearContent()
        {
            StopLive();
            if (Pic != null) { Controls.Remove(Pic); if (Pic.Image != null) Pic.Image.Dispose(); Pic.Dispose(); Pic = null; }
            NvrId = 0;
            Channel = 0;
            LastJpeg = null;
            StatusText = "";
            if (lblMsg != null) lblMsg.Visible = false;
            lblEmpty.Visible = true;
            lblName.Visible = false;
        }

        public void StopLive()
        {
            LiveTile t = Tile;
            NvrSession s = sess;
            long nid = NvrId;
            int ch = Channel;
            Tile = null;
            sess = null;
            if (t != null)
            {
                Controls.Remove(t);
                t.Halt();
                t.Dispose();
                if (s != null)
                {
                    Task.Run(delegate
                    {
                        try { t.SendStop(); } catch { }
                        Pool.Release(nid, s, ch);
                    });
                }
            }
        }

        /// <summary>Tampilkan kamera secara live. useSub: pakai substream bila ada (multi-area).</summary>
        public void ShowLive(NvrRecord nvr, int channel, bool useSub, int decodeWidth, int delayMs)
        {
            StopLive();
            if (Pic != null) { Controls.Remove(Pic); if (Pic.Image != null) Pic.Image.Dispose(); Pic.Dispose(); Pic = null; }
            Playback = false;
            NvrId = nvr.Id;
            Channel = channel;
            LastJpeg = null;
            lblEmpty.Visible = false;
            LoadFish();
            if (SoftFish)
            {
                // dewarp butuh gambar besar: pakai stream utama dan lebar dekode minimal 1280
                useSub = false;
                if (decodeWidth > 0 && decodeWidth < 1280) decodeWidth = 1280;
            }
            sess = Pool.Acquire(nvr, channel);
            Tile = new LiveTile(channel, sess, LiveFormats.FromStored(AppSettings.Current.LiveFormat), decodeWidth);
            Tile.Caption = TitleText();
            if (useSub) Tile.Sources = StreamPlan.ForMultiview(nvr, Dir.Cam(nvr.Id, channel));
            if (Fish != null && Fish.Enabled && Fish.CamStream) Tile.Sources = CamStreamSources(nvr, channel);
            Tile.FrameFilter = FilterFrame;
            Tile.HideHeader();
            Tile.FillCell();
            Tile.Dock = DockStyle.Fill;
            Tile.BackColor = Color.Black;
            Tile.AllowDrop = true;
            Tile.Picture.AllowDrop = true;
            Tile.Picture.DragEnter += OnDragEnter;
            Tile.Picture.DragDrop += OnDragDrop;
            Tile.Picture.MouseDown += OnDown;
            Tile.Picture.MouseMove += OnMove;
            Tile.Picture.MouseUp += delegate { dragReady = false; };
            Tile.Picture.MouseWheel += OnWheel;
            Tile.Picture.DoubleClick += delegate { if (CellDoubleClicked != null) CellDoubleClicked(this); };
            Tile.ImageClicked += delegate(LiveTile t, PointF p) { if (ImageClicked != null) ImageClicked(this, p); };
            Tile.StatusChanged += delegate(LiveTile t, string text)
            {
                string st = text;
                int i = text.IndexOf(" - ", StringComparison.Ordinal);
                if (i >= 0) st = text.Substring(i + 3);
                StatusText = st;
                // dipanggil dari thread tile: sel bisa saja sudah dibuang -> jangan sampai melempar exception di thread itu
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)SetName); } catch { }
            };
            if (ContextMenuStrip != null) Tile.SetMenu(ContextMenuStrip);
            Controls.Add(Tile);
            // urutan dock: judul (Top) dilayout lebih dulu, gambar (Fill) mengisi sisa -> judul tidak menutupi bagian atas gambar (tanggal/jam)
            lblName.SendToBack();
            Tile.BringToFront();
            SetName();
            Tile.Start(delayMs);
        }

        List<StreamSource> CamStreamSources(NvrRecord nvr, int channel)
        {
            var l = new List<StreamSource>();
            string url = Fish.CamUrl ?? "";
            if (url.Length > 0)
            {
                var s = new StreamSource();
                s.Rtsp = true; s.Url = url; s.Label = "RTSP dewarp kamera";
                l.Add(s);
            }
            else
            {
                foreach (StreamSource x in StreamPlan.ForMultiview(nvr, Dir.Cam(nvr.Id, channel)))
                    if (x.Rtsp) { x.Label = "RTSP dewarp kamera"; l.Add(x); break; }
            }
            var main = new StreamSource();
            main.Label = "NVR utama";
            l.Add(main);
            return l;
        }

        /// <summary>Tandai area ini sebagai area playback untuk kamera tertentu (gambar datang dari PlaybackEngine).</summary>
        public void ShowPlaybackFor(long nvrId, int channel)
        {
            StopLive();
            Playback = true;
            NvrId = nvrId;
            Channel = channel;
            lblEmpty.Visible = false;
            LoadFish();
            if (Pic == null)
            {
                Pic = new PictureBox();
                Pic.Dock = DockStyle.Fill;
                Pic.SizeMode = PictureBoxSizeMode.Zoom;
                Pic.BackColor = Color.Black;
                Pic.AllowDrop = true;
                Pic.DragEnter += OnDragEnter;
                Pic.DragDrop += OnDragDrop;
                Pic.MouseDown += OnDown;
                Pic.MouseMove += OnMove;
                Pic.MouseUp += delegate { dragReady = false; };
                Pic.MouseWheel += OnWheel;
                Pic.DoubleClick += delegate { if (CellDoubleClicked != null) CellDoubleClicked(this); };
                if (ContextMenuStrip != null) Pic.ContextMenuStrip = ContextMenuStrip;
                Controls.Add(Pic);
                lblName.SendToBack();
                Pic.BringToFront();
            }
            StatusText = "playback";
            SetName();
        }

        /// <summary>Terima frame JPEG playback (panggil dari UI thread).</summary>
        Label lblMsg;
        /// <summary>Pesan di tengah area playback (menunggu / gagal / tidak ada rekaman); null = sembunyikan.</summary>
        public void SetPlaybackMessage(string text)
        {
            if (Pic == null) return;
            if (string.IsNullOrEmpty(text))
            {
                if (lblMsg != null) lblMsg.Visible = false;
                return;
            }
            if (lblMsg == null)
            {
                lblMsg = new Label();
                lblMsg.Dock = DockStyle.Fill;
                lblMsg.TextAlign = ContentAlignment.MiddleCenter;
                lblMsg.ForeColor = Color.Gainsboro;
                lblMsg.BackColor = Color.Transparent;
                lblMsg.UseMnemonic = false;
                lblMsg.Tag = "nostyle";
                lblMsg.Font = new Font(Font.FontFamily, 9f);
                Controls.Add(lblMsg);
            }
            lblMsg.Text = text;
            lblMsg.Visible = true;
            lblName.SendToBack();
            lblMsg.BringToFront();
        }

        public void ShowFrame(byte[] jpg)
        {
            if (Pic == null || jpg == null) return;
            if (lblMsg != null && lblMsg.Visible) lblMsg.Visible = false;
            LastJpeg = jpg;
            Image img;
            try { using (var ms = new MemoryStream(jpg)) img = new Bitmap(ms); }
            catch { return; }
            if (SoftFish)
            {
                Image d = null;
                try { d = FilterFrame(img); } catch { }
                if (d != null) { img.Dispose(); img = d; }
            }
            Image old = Pic.Image;
            Pic.Image = img;
            if (old != null) old.Dispose();
        }

        public Image CurrentImage
        {
            get
            {
                if (Playback) return Pic == null ? null : Pic.Image;
                return Tile == null ? null : Tile.CurrentImage;
            }
        }

        public void SetContextMenu(ContextMenuStrip m)
        {
            ContextMenuStrip = m;
            lblEmpty.ContextMenuStrip = m;
            lblName.ContextMenuStrip = m;
            if (Tile != null) Tile.SetMenu(m);
            if (Pic != null) Pic.ContextMenuStrip = m;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopLive();
                if (Pic != null && Pic.Image != null) { Image im = Pic.Image; Pic.Image = null; im.Dispose(); }
                if (lblMsg != null && lblMsg.Font != null) { try { lblMsg.Font.Dispose(); } catch { } }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Area gambar bertata letak (Operation window / Live window): sel-sel mengikuti pola layar ScreenLayout.</summary>
    public class ViewGrid : Panel
    {
        ScreenLayout layout = ScreenLayout.ById("4");
        readonly List<ViewCell> cells = new List<ViewCell>();
        int selected = -1;
        int maximized = -1;
        bool playbackMode;
        readonly ContextMenuStrip menu;

        public event Action SelectionChanged;
        public event Action CamerasChanged;
        public event Action<CamRef> QuickDownloadRequested;
        public event Action<ViewCell, PointF> CellImageClicked;
        public event Action RegisterLayoutRequested;
        public bool AllowRegisterLayout = true;
        public bool ReadOnlyMode;      // Live window: tidak ada menu playback

        public ScreenLayout Layout { get { return layout; } }
        public List<ViewCell> Cells { get { return cells; } }
        public bool PlaybackMode { get { return playbackMode; } }
        public int MaximizedIndex { get { return maximized; } }

        public ViewGrid()
        {
            BackColor = Asm.Bg;
            DoubleBuffered = true;
            menu = Asm.Menu();
            menu.Opening += OnMenuOpening;
            SetLayout(layout);
        }

        public ViewCell Selected { get { return selected >= 0 && selected < cells.Count ? cells[selected] : null; } }
        public int SelectedIndex { get { return selected; } }

        // ---------- tata letak ----------

        public void SetLayout(ScreenLayout l)
        {
            // simpan isi lama
            var old = new List<CamRef>();
            foreach (ViewCell c in cells) old.Add(c.Ref);
            maximized = -1;
            StopAll();
            foreach (ViewCell c in cells) { Controls.Remove(c); c.Dispose(); }
            cells.Clear();
            layout = l;
            for (int i = 0; i < l.Count; i++)
            {
                var c = new ViewCell();
                c.Index = i;
                c.SetNumber(i + 1);
                c.CellSelected += OnCellSelected;
                c.CellDoubleClicked += OnCellDouble;
                c.CellDropped += OnCellDropped;
                c.ExternalDrop += OnExternalDrop;
                c.ImageClicked += delegate(ViewCell vc, PointF p) { if (CellImageClicked != null) CellImageClicked(vc, p); };
                c.FishReload += delegate(ViewCell vc) { if (vc.HasCamera) Assign(vc.Index, vc.NvrId, vc.Channel, false); };
                c.SetContextMenu(menu);
                cells.Add(c);
                Controls.Add(c);
            }
            DoLayout();
            selected = cells.Count > 0 ? 0 : -1;
            if (selected >= 0) cells[0].SetSelected(true);
            // pulihkan kamera ke area yang sama
            for (int i = 0; i < old.Count && i < cells.Count; i++)
                if (old[i] != null) Assign(i, old[i].NvrId, old[i].Channel, false);
            if (SelectionChanged != null) SelectionChanged();
            if (CamerasChanged != null) CamerasChanged();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DoLayout();
        }

        void DoLayout()
        {
            if (cells.Count == 0) return;
            SuspendLayout();
            int W = Math.Max(1, ClientSize.Width), H = Math.Max(1, ClientSize.Height);
            for (int i = 0; i < cells.Count; i++)
            {
                ViewCell c = cells[i];
                if (maximized >= 0)
                {
                    c.Visible = (i == maximized);
                    if (i == maximized) c.Bounds = new Rectangle(0, 0, W, H);
                    continue;
                }
                Rectangle r = layout.Cells[i];
                int x0 = r.X * W / layout.Cols, x1 = (r.X + r.Width) * W / layout.Cols;
                int y0 = r.Y * H / layout.Rows, y1 = (r.Y + r.Height) * H / layout.Rows;
                c.Bounds = new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
                c.Visible = true;
            }
            ResumeLayout();
        }

        // ---------- pilih / drop ----------

        void OnCellSelected(ViewCell c)
        {
            Select(c.Index);
        }

        public void Select(int idx)
        {
            if (idx < 0 || idx >= cells.Count) return;
            if (selected >= 0 && selected < cells.Count) cells[selected].SetSelected(false);
            selected = idx;
            cells[idx].SetSelected(true);
            if (SelectionChanged != null) SelectionChanged();
        }

        void OnCellDouble(ViewCell c)
        {
            if (maximized >= 0) { maximized = -1; }
            else if (layout.Count > 1 && c.HasCamera) { maximized = c.Index; }
            else return;
            DoLayout();
            Select(c.Index);
            // area yang diperbesar memakai stream utama, kembali ke substream saat dikecilkan
            ViewCell cur = c;
            if (!cur.Playback && cur.HasCamera) Assign(cur.Index, cur.NvrId, cur.Channel, false);
        }

        void OnCellDropped(ViewCell src, ViewCell dst)
        {
            CamRef a = src.Ref, b = dst.Ref;
            if (a == null) return;
            Assign(dst.Index, a.NvrId, a.Channel, false);
            if (b != null) Assign(src.Index, b.NvrId, b.Channel, false);
            else Unassign(src.Index);
            Select(dst.Index);
            if (CamerasChanged != null) CamerasChanged();
        }

        void OnExternalDrop(ViewCell dst, string data)
        {
            string[] p = data.Split(':');
            long nid; int ch;
            if (p.Length >= 3 && long.TryParse(p[1], out nid) && int.TryParse(p[2], out ch))
            {
                if (!AsmApp.Can(Perm.Live) && !playbackMode) return;
                Assign(dst.Index, nid, ch, true);
                Select(dst.Index);
            }
        }

        // ---------- isi kamera ----------

        int DecodeW()
        {
            AppSettings s = AppSettings.Current;
            if (!s.AutoDecodeWidth) return s.DecodeWidth;
            if (maximized >= 0 || layout.Count == 1) return 1280;
            if (layout.Count <= 4) return 960;
            if (layout.Count <= 9) return 640;
            if (layout.Count <= 16) return 480;
            return 320;
        }

        public event Action<int, long, int> CellAssigned;    // idx, nvr, ch (mode playback: dipakai Operation window)
        public event Action<int> CellUnassigned;

        /// <summary>Taruh kamera di area idx. Bila mode playback, hanya menandai (gambar dari PlaybackEngine).</summary>
        public void Assign(int idx, long nvrId, int channel, bool raise)
        {
            if (idx < 0 || idx >= cells.Count) return;
            NvrRecord nvr = Dir.Nvr(nvrId);
            if (nvr == null) return;
            // satu kamera tidak boleh dobel dalam satu tampilan (batasan NVR per UID)
            for (int i = 0; i < cells.Count; i++)
                if (i != idx && cells[i].NvrId == nvrId && cells[i].Channel == channel) Unassign(i);
            ViewCell c = cells[idx];
            if (playbackMode)
            {
                c.ShowPlaybackFor(nvrId, channel);
            }
            else
            {
                bool multi = layout.Count > 1 && maximized < 0 && AppSettings.Current.UseSubstreamMulti;
                int delay = 150 * (idx % 16);
                c.ShowLive(nvr, channel, multi, DecodeW(), delay);
            }
            if (CellAssigned != null) CellAssigned(idx, nvrId, channel);
            if (raise && CamerasChanged != null) CamerasChanged();
        }

        public void Unassign(int idx)
        {
            if (idx < 0 || idx >= cells.Count) return;
            cells[idx].ClearContent();
            if (CellUnassigned != null) CellUnassigned(idx);
            if (CamerasChanged != null) CamerasChanged();
        }

        public void ClearAll()
        {
            for (int i = 0; i < cells.Count; i++) { cells[i].ClearContent(); if (CellUnassigned != null) CellUnassigned(i); }
            if (CamerasChanged != null) CamerasChanged();
        }

        public void StopAll()
        {
            foreach (ViewCell c in cells) c.StopLive();
        }

        /// <summary>Isi area menurut urutan slot (null = kosong).</summary>
        public void LoadSlots(CamRef[] slots)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                CamRef r = slots != null && i < slots.Length ? slots[i] : null;
                if (r == null) { cells[i].ClearContent(); if (CellUnassigned != null) CellUnassigned(i); }
                else Assign(i, r.NvrId, r.Channel, false);
            }
            if (CamerasChanged != null) CamerasChanged();
        }

        public CamRef[] Slots()
        {
            var a = new CamRef[cells.Count];
            for (int i = 0; i < a.Length; i++) a[i] = cells[i].Ref;
            return a;
        }

        public int FirstEmpty()
        {
            for (int i = 0; i < cells.Count; i++) if (!cells[i].HasCamera) return i;
            return -1;
        }

        public bool Contains(long nvrId, int ch)
        {
            foreach (ViewCell c in cells) if (c.NvrId == nvrId && c.Channel == ch) return true;
            return false;
        }

        /// <summary>Ganti seluruh area antara mode live dan playback (tanpa mengubah penempatan kamera).</summary>
        public void SetPlaybackMode(bool pb)
        {
            if (playbackMode == pb) return;
            playbackMode = pb;
            CamRef[] slots = Slots();
            for (int i = 0; i < cells.Count; i++)
            {
                CamRef r = slots[i];
                cells[i].ClearContent();
                if (r != null) Assign(i, r.NvrId, r.Channel, false);
            }
        }

        // ---------- menu klik kanan ----------

        void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var oldItems = new ToolStripItem[menu.Items.Count];
            menu.Items.CopyTo(oldItems, 0);
            menu.Items.Clear();
            foreach (ToolStripItem oi in oldItems) { try { oi.Dispose(); } catch { } }
            ViewCell c = null;
            Control src = menu.SourceControl;
            while (src != null && c == null) { c = src as ViewCell; src = src.Parent; }
            if (c == null) c = Selected;
            if (c == null) { e.Cancel = true; return; }
            Select(c.Index);
            ViewCell cell = c;
            bool has = cell.HasCamera;
            ToolStripMenuItem it;
            it = Asm.Item(menu, "Still picture", delegate
            {
                string p = Stills.Save(cell.CurrentImage, Dir.Title(cell.NvrId, cell.Channel));
                if (p != null) MessageBox.Show("Gambar disimpan:\n" + p, "Still picture");
            });
            it.Enabled = has && cell.CurrentImage != null && AsmApp.Can(Perm.StillPrint);
            it = Asm.Item(menu, "Print", delegate { Stills.Print(cell.CurrentImage, Dir.Title(cell.NvrId, cell.Channel)); });
            it.Enabled = has && cell.CurrentImage != null && AsmApp.Can(Perm.StillPrint);
            if (!ReadOnlyMode)
            {
                it = Asm.Item(menu, "Quick download", delegate { if (QuickDownloadRequested != null) QuickDownloadRequested(cell.Ref); });
                it.Enabled = has && AsmApp.Can(Perm.Download);
            }
            menu.Items.Add(new ToolStripSeparator());
            it = Asm.Item(menu, maximized >= 0 ? "Kembalikan ukuran" : "Perbesar area", delegate { OnCellDouble(cell); });
            it.Enabled = has && (layout.Count > 1);
            if (has)
            {
                ToolStripMenuItem fe = new ToolStripMenuItem("Fisheye");
                string[] names = { "Off (gambar bulat)", "Panorama", "Quad (4 sudut)", "PTZ virtual (geser mouse, roda = zoom)" };
                for (int k = 0; k < 4; k++)
                {
                    FishMode fm = (FishMode)k;
                    ToolStripMenuItem sub = Asm.Sub(fe, names[k], delegate { cell.SetFishMode(fm); });
                    sub.Checked = cell.FMode == fm && !(cell.Fish != null && cell.Fish.CamStream);
                }
                fe.DropDownItems.Add(new ToolStripSeparator());
                Asm.Sub(fe, "Kalibrasi lensa & sumber stream...", delegate
                {
                    using (var d = new FisheyeDialog(cell)) d.ShowDialog(FindForm());
                });
                menu.Items.Add(fe);
                Asm.StyleMenu(menu);
            }
            it = Asm.Item(menu, "No image display", delegate { Unassign(cell.Index); });
            it.Enabled = has;
            if (AllowRegisterLayout)
            {
                menu.Items.Add(new ToolStripSeparator());
                Asm.Item(menu, "Register layout to group...", delegate { if (RegisterLayoutRequested != null) RegisterLayoutRequested(); });
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopAll(); menu.Opening -= OnMenuOpening; }
            base.Dispose(disposing);
        }
    }

    /// <summary>Jendela layar penuh yang menampung ViewGrid (menu "Full screen"); tutup dengan Esc.</summary>
    public class FullScreenForm : Form
    {
        readonly Control content;
        readonly Control oldParent;
        readonly int oldIndex;
        readonly DockStyle oldDock;
        public FullScreenForm(Control c)
        {
            content = c;
            oldParent = c.Parent;
            oldDock = c.Dock;
            oldIndex = oldParent.Controls.GetChildIndex(c);
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.Black;
            KeyPreview = true;
            ShowInTaskbar = false;
            oldParent.Controls.Remove(c);
            c.Dock = DockStyle.Fill;
            Controls.Add(c);
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Controls.Remove(content);
            content.Dock = oldDock;
            oldParent.Controls.Add(content);
            oldParent.Controls.SetChildIndex(content, Math.Min(oldIndex, oldParent.Controls.Count - 1));
            base.OnFormClosed(e);
        }
    }
}

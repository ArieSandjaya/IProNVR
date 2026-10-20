using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Search window: Thumbnail search (gambar diam tiap interval waktu) dan Recording info (status HDD recorder).
    /// Pencarian berdasarkan event REC/VMD/wajah/heat map tidak tersedia karena CGI NVR tidak menyediakannya.</summary>
    public class SearchView : UserControl
    {
        readonly AsmTabControl tabs = new AsmTabControl();
        // thumbnail
        ComboBox cmbCam, cmbInterval, cmbCount;
        DateTimePicker dtp;
        FlowLayoutPanel thumbs;
        Label lblStatus;
        AsmButton btnGo, btnCancel;
        volatile bool cancel;
        readonly List<CamRef> camList = new List<CamRef>();
        // recording info
        ComboBox cmbNvr;
        ListView lvRec;
        Label lblRec;

        public SearchView()
        {
            Dock = DockStyle.Fill;
            BackColor = Asm.Panel;
            Tag = "nostyle";
            tabs.Dock = DockStyle.Fill;
            var t1 = new TabPage("Thumbnail search");
            var t2 = new TabPage("Recording info");
            t1.BackColor = Asm.Panel2;
            t2.BackColor = Asm.Panel2;
            tabs.TabPages.Add(t1);
            tabs.TabPages.Add(t2);
            Controls.Add(tabs);
            BuildThumb(t1);
            BuildRec(t2);
            Dir.Changed += OnDir;
            FillCameras();
        }

        void OnDir()
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)delegate { if (!IsDisposed) { FillCameras(); FillNvrs(); } });
            }
            catch (InvalidOperationException) { }
        }

        /// <summary>Buang semua thumbnail beserta bitmap-nya (Controls.Clear saja tidak membebaskan gambar).</summary>
        void ClearThumbs()
        {
            if (thumbs == null) return;
            var old = new List<Control>();
            foreach (Control c in thumbs.Controls) old.Add(c);
            thumbs.Controls.Clear();
            foreach (Control c in old)
            {
                foreach (Control ch in c.Controls)
                {
                    PictureBox pb = ch as PictureBox;
                    if (pb != null && pb.Image != null) { Image im = pb.Image; pb.Image = null; im.Dispose(); }
                }
                c.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Dir.Changed -= OnDir; cancel = true; ClearThumbs(); }
            base.Dispose(disposing);
        }

        static Label L(string t)
        {
            var l = new Label();
            l.Text = t;
            l.AutoSize = true;
            l.ForeColor = Asm.Muted;
            l.Margin = new Padding(8, 9, 2, 0);
            l.Tag = "nostyle";
            return l;
        }

        void BuildThumb(TabPage p)
        {
            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = Asm.S(80);
            bar.Padding = new Padding(Asm.S(6));
            bar.BackColor = Asm.Panel2;
            bar.Tag = "nostyle";

            bar.Controls.Add(L("Kamera"));
            cmbCam = new ComboBox();
            cmbCam.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCam.Width = Asm.S(260);
            cmbCam.Margin = new Padding(2, 6, 2, 0);
            bar.Controls.Add(cmbCam);
            bar.Controls.Add(L("Mulai"));
            dtp = new DateTimePicker();
            dtp.Format = DateTimePickerFormat.Custom;
            dtp.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
            dtp.Width = Asm.S(170);
            dtp.Margin = new Padding(2, 6, 2, 0);
            dtp.Value = DateTime.Now.AddHours(-1);
            bar.Controls.Add(dtp);
            bar.Controls.Add(L("Interval"));
            cmbInterval = new ComboBox();
            cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbInterval.Items.AddRange(new object[] { "10 detik", "30 detik", "1 menit", "5 menit", "10 menit", "30 menit", "1 jam" });
            cmbInterval.SelectedIndex = 3;
            cmbInterval.Width = Asm.S(90);
            cmbInterval.Margin = new Padding(2, 6, 2, 0);
            bar.Controls.Add(cmbInterval);
            bar.Controls.Add(L("Jumlah"));
            cmbCount = new ComboBox();
            cmbCount.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCount.Items.AddRange(new object[] { "6", "8", "12", "16", "20" });
            cmbCount.SelectedIndex = 2;
            cmbCount.Width = Asm.S(60);
            cmbCount.Margin = new Padding(2, 6, 2, 0);
            bar.Controls.Add(cmbCount);
            btnGo = new AsmButton(); btnGo.Icon = AsmIcon.Search; btnGo.Label = "Cari"; btnGo.Width = Asm.S(80); btnGo.Margin = new Padding(10, 3, 2, 0);
            btnGo.Click += delegate { StartSearch(); };
            btnCancel = new AsmButton(); btnCancel.Icon = AsmIcon.Close; btnCancel.Label = "Batal"; btnCancel.Width = Asm.S(80); btnCancel.Margin = new Padding(2, 3, 2, 0); btnCancel.Enabled = false;
            btnCancel.Click += delegate { cancel = true; };
            bar.Controls.Add(btnGo);
            bar.Controls.Add(btnCancel);
            lblStatus = new Label();
            lblStatus.AutoSize = true;
            lblStatus.ForeColor = Asm.Muted;
            lblStatus.Margin = new Padding(8, 9, 2, 0);
            lblStatus.Tag = "nostyle";
            lblStatus.Text = "Klik ganda gambar = putar rekaman dari waktu itu.";
            bar.Controls.Add(lblStatus);

            thumbs = new FlowLayoutPanel();
            thumbs.Dock = DockStyle.Fill;
            thumbs.AutoScroll = true;
            thumbs.BackColor = Asm.Bg;
            thumbs.Tag = "nostyle";
            p.Controls.Add(thumbs);
            p.Controls.Add(bar);
        }

        void FillCameras()
        {
            if (cmbCam == null) return;
            CamRef keep = cmbCam.SelectedIndex >= 0 && cmbCam.SelectedIndex < camList.Count ? camList[cmbCam.SelectedIndex] : null;
            camList.Clear();
            cmbCam.Items.Clear();
            foreach (NvrRecord n in Dir.Nvrs)
                foreach (CameraRecord c in Dir.Registered(n.Id))
                {
                    camList.Add(new CamRef(n.Id, c.Channel));
                    cmbCam.Items.Add(Dir.Title(n.Id, c.Channel));
                }
            int sel = 0;
            for (int i = 0; i < camList.Count; i++) if (keep != null && camList[i].Key == keep.Key) sel = i;
            if (cmbCam.Items.Count > 0) cmbCam.SelectedIndex = sel;
        }

        void StartSearch()
        {
            if (!AsmApp.Can(Perm.Playback)) { MessageBox.Show("Tidak ada hak playback."); return; }
            if (cmbCam.SelectedIndex < 0 || cmbCam.SelectedIndex >= camList.Count) { MessageBox.Show("Pilih kamera."); return; }
            CamRef r = camList[cmbCam.SelectedIndex];
            NvrRecord nvr = Dir.Nvr(r.NvrId);
            if (nvr == null) return;
            int[] secs = { 10, 30, 60, 300, 600, 1800, 3600 };
            int step = secs[cmbInterval.SelectedIndex];
            int count = int.Parse((string)cmbCount.SelectedItem);
            DateTime start = dtp.Value;
            ClearThumbs();
            cancel = false;
            btnGo.Enabled = false;
            btnCancel.Enabled = true;
            lblStatus.Text = "Mencari...";
            Task.Run(delegate
            {
                try
                {
                    for (int i = 0; i < count && !cancel; i++)
                    {
                        DateTime t = start.AddSeconds((double)step * i);
                        byte[] jpg = null;
                        try { jpg = PlaybackEngine.CaptureStill(nvr, r.Channel, t.ToUniversalTime(), 480); }
                        catch (Exception ex) { Log.Write("Thumbnail: " + ex.Message); }
                        int idx = i;
                        Ui.Post(this, delegate { AddThumb(r, nvr, t, jpg); lblStatus.Text = "Selesai " + (idx + 1) + " / " + count; });
                        for (int w = 0; w < 10 && !cancel; w++) Thread.Sleep(AppSettings.Current.PlaybackIntervalMs > 3000 ? 300 : 100);
                    }
                }
                finally
                {
                Ui.Post(this, delegate
                {
                    btnGo.Enabled = true;
                    btnCancel.Enabled = false;
                    lblStatus.Text = cancel ? "Dibatalkan." : "Selesai. Klik ganda gambar = putar.";
                });
                }
            });
        }

        void AddThumb(CamRef r, NvrRecord nvr, DateTime t, byte[] jpg)
        {
            var host = new Panel();
            host.Width = Asm.S(240);
            host.Height = Asm.S(170);
            host.Margin = new Padding(6);
            host.BackColor = Asm.Panel;
            host.Tag = "nostyle";
            var pic = new PictureBox();
            pic.Dock = DockStyle.Fill;
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.Black;
            if (jpg != null)
            {
                try { using (var ms = new MemoryStream(jpg)) pic.Image = new Bitmap(ms); } catch { }
            }
            var lbl = new Label();
            lbl.Dock = DockStyle.Bottom;
            lbl.Height = Asm.S(20);
            lbl.ForeColor = jpg != null ? Asm.Text : Asm.Danger;
            lbl.Text = Asm.FormatTime(t) + (jpg == null ? "  (tidak ada rekaman)" : "");
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.Tag = "nostyle";
            host.Controls.Add(pic);
            host.Controls.Add(lbl);
            pic.DoubleClick += delegate
            {
                CameraRecord cam = Dir.Cam(r.NvrId, r.Channel);
                if (cam == null) { cam = new CameraRecord(); cam.NvrId = nvr.Id; cam.Channel = r.Channel; }
                if (AsmApp.PlayCameraAt != null) AsmApp.PlayCameraAt(nvr, cam, t.ToUniversalTime());
            };
            thumbs.Controls.Add(host);
        }

        // ---------- recording info ----------

        void BuildRec(TabPage p)
        {
            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = Asm.S(46);
            bar.Padding = new Padding(Asm.S(6));
            bar.BackColor = Asm.Panel2;
            bar.Tag = "nostyle";
            bar.Controls.Add(L("NVR"));
            cmbNvr = new ComboBox();
            cmbNvr.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbNvr.Width = Asm.S(260);
            cmbNvr.Margin = new Padding(2, 6, 2, 0);
            bar.Controls.Add(cmbNvr);
            var go = new AsmButton(); go.Icon = AsmIcon.Search; go.Label = "Ambil info"; go.Width = Asm.S(110); go.Margin = new Padding(10, 3, 2, 0);
            go.Click += delegate { LoadRec(); };
            bar.Controls.Add(go);
            lblRec = new Label();
            lblRec.AutoSize = true; lblRec.ForeColor = Asm.Muted; lblRec.Margin = new Padding(8, 9, 2, 0); lblRec.Tag = "nostyle";
            bar.Controls.Add(lblRec);

            lvRec = new ListView();
            lvRec.Dock = DockStyle.Fill;
            lvRec.View = View.Details;
            lvRec.FullRowSelect = true;
            lvRec.BackColor = Asm.Panel;
            lvRec.ForeColor = Asm.Text;
            lvRec.BorderStyle = BorderStyle.None;
            lvRec.Tag = "nostyle";
            lvRec.Columns.Add("Disk", Asm.S(80));
            lvRec.Columns.Add("Status", Asm.S(90));
            lvRec.Columns.Add("Kapasitas (GB)", Asm.S(110));
            lvRec.Columns.Add("Sisa (GB)", Asm.S(90));
            lvRec.Columns.Add("Rekaman terlama", Asm.S(180));
            lvRec.Columns.Add("Rekaman terbaru", Asm.S(180));
            p.Controls.Add(lvRec);
            p.Controls.Add(bar);
            FillNvrs();
        }

        void FillNvrs()
        {
            if (cmbNvr == null) return;
            long keep = 0;
            int i0 = cmbNvr.SelectedIndex;
            List<NvrRecord> l = Dir.Nvrs;
            if (i0 >= 0 && i0 < l.Count) keep = l[i0].Id;
            cmbNvr.Items.Clear();
            int sel = 0;
            for (int i = 0; i < l.Count; i++) { cmbNvr.Items.Add(l[i].Name + "  (" + l[i].Host + ")"); if (l[i].Id == keep) sel = i; }
            if (l.Count > 0) cmbNvr.SelectedIndex = sel;
        }

        void LoadRec()
        {
            List<NvrRecord> l = Dir.Nvrs;
            int i = cmbNvr.SelectedIndex;
            if (i < 0 || i >= l.Count) return;
            NvrRecord nvr = l[i];
            lblRec.Text = "Mengambil...";
            lvRec.Items.Clear();
            Task.Run(delegate
            {
                try
                {
                    NvrEndpoint ep = nvr.ToEndpoint();
                    string text;
                    try { text = NvrSession.DownloadText(ep, "/cgi-bin/recordedtime.cgi", 15000); }
                    catch (Exception) { text = NvrSession.DownloadText(ep, "/cgi-bin//cgi-bin/recordedtime.cgi", 15000); }
                    List<DiskRecord> disks = HddParser.Parse(text);
                    Ui.Post(this, delegate
                    {
                        foreach (DiskRecord d in disks)
                        {
                            var it = new ListViewItem(d.Disk);
                            it.SubItems.Add(d.Recording ? "Merekam (R)" : "Siaga");
                            it.SubItems.Add(d.CapacityGb.HasValue ? d.CapacityGb.Value.ToString() : "");
                            it.SubItems.Add(d.RemainGb.HasValue ? d.RemainGb.Value.ToString() : "");
                            it.SubItems.Add(d.Oldest ?? "");
                            it.SubItems.Add(d.Newest ?? "");
                            lvRec.Items.Add(it);
                        }
                        lblRec.Text = disks.Count == 0 ? "Tidak ada HDD terbaca." : disks.Count + " HDD.";
                    });
                }
                catch (Exception ex)
                {
                    Ui.Post(this, delegate { lblRec.Text = "Gagal: " + ex.Message; });
                }
            });
        }
    }
}

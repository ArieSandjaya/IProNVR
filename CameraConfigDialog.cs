using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Detail konfigurasi satu kamera (klik dua kali baris kamera di Device setting): data yang bisa diatur, info dari NVR, tes RTSP sub/main, dan tombol lihat video.</summary>
    public class CameraConfigDialog : Form
    {
        readonly NvrRecord nvr;
        readonly CameraRecord cam;
        readonly TextBox txtName = new TextBox();
        readonly TextBox txtIp = new TextBox();
        readonly TextBox txtLoc = new TextBox();
        readonly TextBox txtNote = new TextBox();
        readonly TextBox txtRtsp = new TextBox();
        readonly TextBox txtInfo = new TextBox();
        readonly Label lblTest = new Label();
        readonly Label lblEff = new Label();
        Button btnTest;

        public CameraConfigDialog(NvrRecord nvr, CameraRecord cam)
        {
            this.nvr = nvr;
            this.cam = cam;
            Text = "Konfigurasi kamera - " + nvr.Name + " / CAM " + cam.Channel;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(Asm.S(640), Asm.S(560));
            BackColor = Asm.Panel;

            int y = Asm.S(12);
            Info("NVR", nvr.Name + "  (" + nvr.Host + ")", ref y);
            Info("Kanal", cam.Channel.ToString(), ref y);
            Info("Status di NVR", Db.StatusText(cam.RegStatus) + (string.IsNullOrEmpty(cam.LastSeenAt) ? "" : "   (terakhir normal " + Db.ToLocal(cam.LastSeenAt) + ")"), ref y);
            string res = (cam.Width.HasValue && cam.Height.HasValue) ? cam.Width + "x" + cam.Height : "-";
            string fps = cam.Fps.HasValue ? cam.Fps.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " fps" : "-";
            Info("Stream utama", (string.IsNullOrEmpty(cam.Codec) ? "-" : cam.Codec) + "  " + res + "  " + fps, ref y);
            FisheyeRec fr = null;
            try { fr = Fisheye.Get(nvr.Id, cam.Channel); } catch { }
            Info("Fisheye", fr == null || !fr.Enabled ? "tidak aktif (atur lewat klik kanan area gambar > Fisheye)" : (fr.CamStream ? "aktif - stream dewarp dari kamera" : "aktif - dewarp di aplikasi"), ref y);
            y += Asm.S(6);

            Field("Nama kamera", txtName, cam.Name, ref y);
            Field("IP kamera", txtIp, cam.IpAddress, ref y);
            Field("Lokasi", txtLoc, cam.Location, ref y);
            Field("Catatan", txtNote, cam.Note, ref y);
            Field("RTSP substream", txtRtsp, cam.RtspUrl, ref y);
            lblEff.SetBounds(Asm.S(150), y - Asm.S(4), Asm.S(480), Asm.S(34));
            lblEff.ForeColor = Asm.Muted;
            lblEff.UseMnemonic = false;
            Controls.Add(lblEff);
            y += Asm.S(38);

            btnTest = new Button(); btnTest.Text = "Tes RTSP (sub dan main)";
            btnTest.SetBounds(Asm.S(150), y, Asm.S(170), Asm.S(28));
            btnTest.Click += delegate { TestRtsp(); };
            Controls.Add(btnTest);
            var btnAuto = new Button(); btnAuto.Text = "Deteksi otomatis";
            btnAuto.SetBounds(Asm.S(150), y + Asm.S(34), Asm.S(170), Asm.S(28));
            btnAuto.Click += delegate { AutoDetect(btnAuto); };
            Controls.Add(btnAuto);
            lblTest.SetBounds(Asm.S(326), y + Asm.S(4), Asm.S(304), Asm.S(56));
            lblTest.ForeColor = Asm.Muted;
            lblTest.UseMnemonic = false;
            Controls.Add(lblTest);
            y += Asm.S(70);

            var l = new Label(); l.Text = "Hasil deteksi stream"; l.ForeColor = Asm.Muted;
            l.SetBounds(Asm.S(14), y + Asm.S(3), Asm.S(130), Asm.S(20));
            Controls.Add(l);
            txtInfo.SetBounds(Asm.S(150), y, Asm.S(480), Asm.S(56));
            txtInfo.Multiline = true; txtInfo.ReadOnly = true;
            txtInfo.Text = string.IsNullOrEmpty(cam.ScanDetail) ? "(belum ada; pakai tombol Deteksi di daftar kamera)" : cam.ScanDetail;
            Controls.Add(txtInfo);
            y += Asm.S(70);

            int by = ClientSize.Height - Asm.S(44);
            var save = new Button(); save.Text = "Simpan"; save.SetBounds(Asm.S(14), by, Asm.S(100), Asm.S(32));
            save.Click += delegate { Save(); };
            var cancel = new Button(); cancel.Text = "Batal"; cancel.SetBounds(Asm.S(122), by, Asm.S(100), Asm.S(32));
            cancel.DialogResult = DialogResult.Cancel;
            var view = new Button(); view.Text = "Lihat video"; view.SetBounds(Asm.S(230), by, Asm.S(110), Asm.S(32));
            view.Click += delegate
            {
                var f = new CameraViewForm(nvr, cam.Channel, txtName.Text.Trim());
                f.FormClosed += delegate { f.Dispose(); };
                f.Show(this);
            };
            Controls.AddRange(new Control[] { save, cancel, view });
            CancelButton = cancel;
            AcceptButton = save;

            txtRtsp.TextChanged += delegate { UpdateEffective(); };
            txtIp.TextChanged += delegate { UpdateEffective(); };
            Theme.ApplyTo(this, true);
            UpdateEffective();
            if (ServerLink.Enabled)
            {
                // mode server: konfigurasi kamera diubah di server (halaman admin), di sini hanya untuk melihat / mengetes
                foreach (Control c0 in Controls)
                {
                    var tb = c0 as TextBox;
                    if (tb != null && tb != txtInfo) tb.ReadOnly = true;
                }
                save.Enabled = false;
                Text += "  (dikelola di server)";
            }
        }

        void Info(string label, string value, ref int y)
        {
            var a = new Label(); a.Text = label; a.ForeColor = Asm.Muted;
            a.SetBounds(Asm.S(14), y, Asm.S(130), Asm.S(20));
            var b = new Label(); b.UseMnemonic = false; b.Text = value; b.ForeColor = Asm.Text; b.AutoEllipsis = true;
            b.SetBounds(Asm.S(150), y, Asm.S(480), Asm.S(20));
            Controls.Add(a); Controls.Add(b);
            y += Asm.S(24);
        }

        void Field(string label, TextBox t, string value, ref int y)
        {
            var a = new Label(); a.Text = label; a.ForeColor = Asm.Muted;
            a.SetBounds(Asm.S(14), y + Asm.S(3), Asm.S(130), Asm.S(20));
            t.SetBounds(Asm.S(150), y, Asm.S(480), Asm.S(24));
            t.Text = value ?? "";
            Controls.Add(a); Controls.Add(t);
            y += Asm.S(32);
        }

        string SubUrl()
        {
            string u = txtRtsp.Text.Trim();
            if (u.Length == 0) u = nvr.RtspTemplate;
            string cu, cp;
            Rtsp.Creds(nvr, cam, out cu, out cp);
            return Rtsp.Build(u, cu, cp, txtIp.Text.Trim());
        }

        void UpdateEffective()
        {
            string u = SubUrl();
            string shown = u.Length == 0 ? "tidak ada (isi IP + template RTSP NVR, atau URL RTSP di atas): live memakai stream utama NVR"
                : "dipakai untuk multi-area: " + MaskPass(u);
            lblEff.Text = (txtRtsp.Text.Trim().Length > 0 ? "URL kamera ini. " : "Dari template NVR. ") + shown;
        }

        static string MaskPass(string url)
        {
            return System.Text.RegularExpressions.Regex.Replace(url, @"(rtsp://[^:/@]+):[^@]*@", "$1:****@");
        }

        void AutoDetect(Button b)
        {
            string exe = FfmpegPipe.FindExe();
            if (exe == null) { lblTest.Text = "Butuh ffmpeg.exe."; return; }
            string ip = txtIp.Text.Trim();
            if (ip.Length == 0) { lblTest.Text = "Isi IP kamera dulu."; return; }
            b.Enabled = false; btnTest.Enabled = false;
            lblTest.Text = "Mencari URL RTSP (ONVIF, templat NVR, pola merk)... bisa sampai 1-2 menit";
            string cu, cp;
            Rtsp.Creds(nvr, cam, out cu, out cp);
            string tpl = nvr.RtspTemplate;
            Task.Run(delegate
            {
                return RtspDiscovery.Discover(exe, ip, cu, cp, tpl, System.Threading.CancellationToken.None);
            }).ContinueWith(task => Ui.Post(this, delegate
            {
                b.Enabled = true; btnTest.Enabled = true;
                if (task.IsFaulted) { lblTest.Text = "Gagal: " + task.Exception.GetBaseException().Message; return; }
                DiscoveryResult r = task.Result;
                lblTest.Text = r.Summary();
                if (r.SubTemplate != null)
                {
                    string viaTpl = Rtsp.Build(tpl, cu, cp, ip), built = Rtsp.Build(r.SubTemplate, cu, cp, ip);
                    txtRtsp.Text = (viaTpl.Length > 0 && viaTpl == built) ? "" : r.SubTemplate;
                    txtInfo.Text = r.Summary();
                }
            }));
        }

        void TestRtsp()
        {
            string exe = FfmpegPipe.FindExe();
            if (exe == null) { lblTest.Text = "Butuh ffmpeg.exe."; return; }
            string sub = SubUrl();
            if (sub.Length == 0) { lblTest.Text = "Isi IP kamera + template RTSP NVR (atau URL RTSP) dulu."; return; }
            string main = StreamProbe.MainFromSub(sub);
            btnTest.Enabled = false;
            lblTest.Text = "Menguji (hingga ~25 detik)...";
            Task.Run(delegate
            {
                string e1, e2 = null;
                StreamInfo s = StreamProbe.Probe(exe, sub, out e1);
                StreamInfo m = main != null ? StreamProbe.Probe(exe, main, out e2) : null;
                string t = "Sub: " + (s != null ? s.ToString() : "gagal (" + e1 + ")");
                if (main != null) t += "\nMain: " + (m != null ? m.ToString() : "gagal (" + e2 + ")");
                return t;
            }).ContinueWith(task => Ui.Post(this, delegate
            {
                btnTest.Enabled = true;
                lblTest.Text = task.IsFaulted ? "Gagal: " + task.Exception.GetBaseException().Message : task.Result;
            }));
        }

        void Save()
        {
            string rtsp = txtRtsp.Text.Trim();
            if (rtsp.Length > 0 && !rtsp.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("URL RTSP harus diawali rtsp://");
                return;
            }
            cam.Name = txtName.Text.Trim();
            cam.IpAddress = txtIp.Text.Trim();
            cam.Location = txtLoc.Text.Trim();
            cam.Note = txtNote.Text.Trim();
            cam.RtspUrl = rtsp;
            try { Db.UpdateCamerasManual(new System.Collections.Generic.List<CameraRecord> { cam }); }
            catch (Exception ex) { MessageBox.Show("Gagal menyimpan: " + ex.Message); return; }
            DialogResult = DialogResult.OK;
        }
    }
}

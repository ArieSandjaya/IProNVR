using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Kalibrasi lensa fisheye dan pilihan sumber (cara 1: dewarp di aplikasi, cara 2: stream dewarp dari kamera via RTSP).</summary>
    public class FisheyeDialog : Form
    {
        readonly ViewCell cell;
        readonly FisheyeRec rec;
        readonly bool existed;
        Bitmap raw;
        readonly PictureBox picRaw = new PictureBox();
        readonly PictureBox picOut = new PictureBox();
        readonly ComboBox cmbMount = new ComboBox();
        readonly ComboBox cmbMode = new ComboBox();
        readonly NumericUpDown numFov = new NumericUpDown();
        readonly NumericUpDown numCx = new NumericUpDown();
        readonly NumericUpDown numCy = new NumericUpDown();
        readonly NumericUpDown numR = new NumericUpDown();
        readonly NumericUpDown numRot = new NumericUpDown();
        readonly RadioButton rbApp = new RadioButton();
        readonly RadioButton rbCam = new RadioButton();
        readonly TextBox txtUrl = new TextBox();
        readonly Label lblHint = new Label();
        bool loading = true;

        public FisheyeDialog(ViewCell cell)
        {
            this.cell = cell;
            FisheyeRec cur = Fisheye.Get(cell.NvrId, cell.Channel);
            existed = cur != null;
            if (cur != null) rec = cur.Clone();
            else { rec = new FisheyeRec(); rec.NvrId = cell.NvrId; rec.Channel = cell.Channel; }
            rec.Enabled = true;

            Text = "Fisheye - " + Dir.Title(cell.NvrId, cell.Channel);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Asm.S(1120), Asm.S(660));
            BackColor = Asm.Panel;

            byte[] jpg = cell.RawJpeg;
            if (jpg != null)
            {
                try { using (var ms = new MemoryStream(jpg)) raw = new Bitmap(ms); } catch { raw = null; }
            }

            // kiri: gambar mentah + lingkaran lensa
            picRaw.SetBounds(Asm.S(10), Asm.S(10), Asm.S(500), Asm.S(380));
            picRaw.SizeMode = PictureBoxSizeMode.Zoom;
            picRaw.BackColor = Color.Black;
            picRaw.Image = raw;
            picRaw.Paint += PaintCircle;
            // kanan: hasil dewarp
            picOut.SetBounds(Asm.S(520), Asm.S(10), Asm.S(590), Asm.S(380));
            picOut.SizeMode = PictureBoxSizeMode.Zoom;
            picOut.BackColor = Color.Black;

            int y = Asm.S(404);
            cmbMount.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMount.Items.AddRange(new object[] { "Langit-langit (menghadap bawah)", "Dinding", "Meja / lantai (menghadap atas)" });
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.Items.AddRange(new object[] { "Panorama", "Quad (4 sudut)", "PTZ virtual" });
            SetupNum(numFov, 90, 360, 1, 0);
            SetupNum(numCx, 0, 1, 0.005m, 3);
            SetupNum(numCy, 0, 1, 0.005m, 3);
            SetupNum(numR, 0.1m, 1.5m, 0.005m, 3);
            SetupNum(numRot, -180, 180, 1, 0);

            AddRow("Posisi pasang", cmbMount, 10, y, 280);
            AddRow("Tampilan", cmbMode, 10, y + Asm.S(32), 280);
            AddRow("Sudut lensa (derajat)", numFov, 10, y + Asm.S(64), 90);
            AddRow("Pusat X (0-1)", numCx, 370, y, 90);
            AddRow("Pusat Y (0-1)", numCy, 370, y + Asm.S(32), 90);
            AddRow("Radius (x sisi pendek)", numR, 370, y + Asm.S(64), 90);
            AddRow("Putar gambar (derajat)", numRot, 730, y, 90);

            rbApp.Text = "Cara 1: dewarp di aplikasi (lensa dikalibrasi di atas)";
            rbApp.SetBounds(Asm.S(10), y + Asm.S(104), Asm.S(500), Asm.S(24));
            rbApp.ForeColor = Asm.Text;
            rbCam.Text = "Cara 2: pakai stream dewarp dari kamera (RTSP)";
            rbCam.SetBounds(Asm.S(10), y + Asm.S(130), Asm.S(500), Asm.S(24));
            rbCam.ForeColor = Asm.Text;
            txtUrl.SetBounds(Asm.S(30), y + Asm.S(158), Asm.S(1060), Asm.S(24));
            lblHint.SetBounds(Asm.S(30), y + Asm.S(186), Asm.S(1060), Asm.S(36));
            lblHint.ForeColor = Asm.Muted;
            lblHint.Text = "URL RTSP stream panorama/quad yang dikeluarkan kamera (atur dulu di web kamera). Kosong = pakai URL RTSP kamera/template NVR. " +
                           "Dengan cara 2 aplikasi tidak melakukan dewarp lagi.";

            var save = new Button(); save.Text = "Simpan"; save.SetBounds(Asm.S(10), ClientSize.Height - Asm.S(44), Asm.S(110), Asm.S(32));
            save.Click += delegate { Save(); };
            var cancel = new Button(); cancel.Text = "Batal"; cancel.SetBounds(Asm.S(130), ClientSize.Height - Asm.S(44), Asm.S(110), Asm.S(32));
            cancel.DialogResult = DialogResult.Cancel;
            var del = new Button(); del.Text = "Hapus pengaturan fisheye"; del.SetBounds(Asm.S(250), ClientSize.Height - Asm.S(44), Asm.S(200), Asm.S(32));
            del.Enabled = existed;
            del.Click += delegate
            {
                Fisheye.Delete(rec.NvrId, rec.Channel);
                cell.RequestFishReload();
                DialogResult = DialogResult.OK;
            };
            CancelButton = cancel;

            Controls.AddRange(new Control[] { picRaw, picOut, rbApp, rbCam, txtUrl, lblHint, save, cancel, del });

            cmbMount.SelectedIndex = Math.Max(0, Math.Min(2, rec.Mount));
            cmbMode.SelectedIndex = Math.Max(0, Math.Min(2, (rec.Mode == 0 ? 1 : rec.Mode) - 1));
            numFov.Value = Clamp(numFov, (decimal)rec.Fov);
            numCx.Value = Clamp(numCx, (decimal)rec.Cx);
            numCy.Value = Clamp(numCy, (decimal)rec.Cy);
            numR.Value = Clamp(numR, (decimal)rec.Radius);
            numRot.Value = Clamp(numRot, (decimal)rec.Rot);
            rbCam.Checked = rec.CamStream;
            rbApp.Checked = !rec.CamStream;
            txtUrl.Text = rec.CamUrl ?? "";
            loading = false;

            cmbMount.SelectedIndexChanged += delegate { Changed(); };
            cmbMode.SelectedIndexChanged += delegate { Changed(); };
            foreach (NumericUpDown n in new NumericUpDown[] { numFov, numCx, numCy, numR, numRot }) n.ValueChanged += delegate { Changed(); };
            rbApp.CheckedChanged += delegate { Changed(); };

            Theme.ApplyTo(this, true);
            if (raw == null)
            {
                lblHint.Text = "Belum ada gambar dari kamera ini: tunggu live tampil dulu, lalu buka lagi untuk kalibrasi. " + lblHint.Text;
            }
            Changed();
        }

        static decimal Clamp(NumericUpDown n, decimal v)
        {
            return Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        static void SetupNum(NumericUpDown n, decimal min, decimal max, decimal inc, int dec)
        {
            n.Minimum = min; n.Maximum = max; n.Increment = inc; n.DecimalPlaces = dec;
        }

        void AddRow(string text, Control c, int x, int y, int w)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = Asm.Muted;
            l.SetBounds(Asm.S(x), y + Asm.S(3), Asm.S(130), Asm.S(22));
            c.SetBounds(Asm.S(x + 134), y, Asm.S(Math.Min(w, 200)), Asm.S(24));
            Controls.Add(l);
            Controls.Add(c);
        }

        void Collect()
        {
            rec.Mount = cmbMount.SelectedIndex;
            rec.Mode = cmbMode.SelectedIndex + 1;
            rec.Fov = (double)numFov.Value;
            rec.Cx = (double)numCx.Value;
            rec.Cy = (double)numCy.Value;
            rec.Radius = (double)numR.Value;
            rec.Rot = (double)numRot.Value;
            rec.CamStream = rbCam.Checked;
            rec.CamUrl = txtUrl.Text.Trim();
        }

        void Changed()
        {
            if (loading) return;
            Collect();
            txtUrl.Enabled = rbCam.Checked;
            picRaw.Invalidate();
            if (raw == null || rbCam.Checked) { picOut.Image = null; return; }
            try
            {
                FishMode m = (FishMode)rec.Mode;
                double pitch = Fisheye.DefaultPitch(rec.Mount);
                Bitmap b = Fisheye.Render(raw, rec, m, 0, pitch, 80, 640, 384);
                Image old = picOut.Image;
                picOut.Image = b;
                if (old != null) old.Dispose();
            }
            catch (Exception ex) { Log.Write("Fisheye preview: " + ex.Message); }
        }

        void PaintCircle(object s, PaintEventArgs e)
        {
            if (raw == null) return;
            double k = Math.Min((double)picRaw.Width / raw.Width, (double)picRaw.Height / raw.Height);
            double ox = (picRaw.Width - raw.Width * k) / 2.0, oy = (picRaw.Height - raw.Height * k) / 2.0;
            double cx = ox + rec.Cx * raw.Width * k, cy = oy + rec.Cy * raw.Height * k;
            double r = rec.Radius * Math.Min(raw.Width, raw.Height) * k;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var p = new Pen(Asm.Yellow, 2))
            {
                e.Graphics.DrawEllipse(p, (float)(cx - r), (float)(cy - r), (float)(2 * r), (float)(2 * r));
                e.Graphics.DrawLine(p, (float)(cx - 8), (float)cy, (float)(cx + 8), (float)cy);
                e.Graphics.DrawLine(p, (float)cx, (float)(cy - 8), (float)cx, (float)(cy + 8));
            }
        }

        void Save()
        {
            Collect();
            rec.Enabled = true;
            if (rec.CamStream && rec.CamUrl.Length > 0 && !rec.CamUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("URL harus diawali rtsp://");
                return;
            }
            try { Fisheye.Save(rec); }
            catch (Exception ex) { MessageBox.Show(ex.Message); return; }
            cell.RequestFishReload();
            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (picOut.Image != null) picOut.Image.Dispose();
                if (raw != null) raw.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

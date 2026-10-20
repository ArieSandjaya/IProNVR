using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Jendela video langsung satu kamera, dibuka dengan klik dua kali pada Daftar Kamera.
    /// Memakai sesi (UID) sendiri ke NVR yang bersangkutan, jadi tidak mengganggu tab Live & PTZ
    /// dan tidak tergantung koneksi aktif. Format dari pilihan di tab Live. Tidak ada panel log; pesan masuk ke file log (lihat Log).</summary>
    public class CameraViewForm : Form
    {
        readonly NvrSession _sess;
        readonly LiveTile _tile;
        bool _closed;

        public CameraViewForm(NvrRecord nvr, int channel, string camName)
        {
            string title = nvr.Name + " - CAM " + channel + (string.IsNullOrEmpty(camName) ? "" : " (" + camName + ")");
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(960, 640);
            MinimumSize = new Size(360, 320);

            _sess = new NvrSession("lihat " + nvr.Name + " CAM " + channel, nvr.ToEndpoint());

            AppSettings s = AppSettings.Current;
            LiveFormat fmt = LiveFormats.FromStored(s.LiveFormat);
            _tile = new LiveTile(channel, _sess, fmt, 0);
            _tile.Dock = DockStyle.Fill;

            Controls.Add(_tile);
            Theme.Apply(this, true);

            Shown += delegate { _tile.Start(0); };
            FormClosing += delegate { Shutdown(); };
        }

        void Shutdown()
        {
            if (_closed) return;
            _closed = true;
            _tile.Halt(); // putus pembacaan lokal dulu (cepat)
            LiveTile tile = _tile;
            NvrSession sess = _sess;
            Task.Run(delegate
            {
                try { tile.SendStop(); } catch { }
                try { sess.Logout(); } catch { }
            });
        }
    }
}

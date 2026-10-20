using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace IproNvrTool
{
    /// <summary>Merekam live H.264 / H.265 / Audio dari NVR ke file.
    /// Transport: HTTP multipart (INTERNETMODE=ON) atau RTP/UDP (PORT=...).
    /// H.264/H.265 disimpan sebagai Annex-B (.h264/.h265), audio sebagai data mentah (.bin).</summary>
    public class RawRecorder
    {
        public long Bytes;
        public bool Running;
        public string FilePath;

        Thread _t;
        HttpWebRequest _req;
        UdpClient _udp;
        volatile bool _stop;
        NvrSession _sess;
        int _cam;
        string _codec;
        bool _udpMode;
        int _port;

        public void Start(NvrSession sess, int cam, string codec, bool udpMode, int port, string folder)
        {
            if (Running) return;
            _sess = sess;
            _cam = cam;
            _codec = codec;
            _udpMode = udpMode;
            _port = port;
            Directory.CreateDirectory(folder);
            string ext = codec == "H264" ? ".h264" : (codec == "H265" ? ".h265" : ".bin");
            FilePath = Path.Combine(folder, "cam" + cam + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext);
            _stop = false;
            Bytes = 0;
            Running = true;
            _t = new Thread(Run);
            _t.IsBackground = true;
            _t.Start();
        }

        void Run()
        {
            try
            {
                using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
                {
                    RtpDepacketizer dep = null;
                    if (_codec != "AUDIO") dep = new RtpDepacketizer(_codec == "H265", fs);
                    _sess.EnsureLiveSetup();
                    if (_udpMode) RunUdp(fs, dep);
                    else RunHttp(fs, dep);
                }
            }
            catch (Exception ex)
            {
                if (!_stop) Log.Write("Rekam CAM " + _cam + ": " + ex.Message);
            }
            finally
            {
                Running = false;
            }
        }

        void RunHttp(FileStream fs, RtpDepacketizer dep)
        {
            _req = _sess.Request("push.cgi", "CAM=" + _cam + "&CMD=START&COMP=" + _codec + "&INTERNETMODE=ON", 20000);
            using (WebResponse resp = _req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            {
                var mr = new MultipartReader(st);
                MultipartPart p;
                while (!_stop && (p = mr.ReadPart()) != null)
                {
                    if (dep != null)
                    {
                        dep.Feed(p.Body, p.Body.Length); // tiap part = 1 paket RTP
                    }
                    else if (p.Body.Length > 1)
                    {
                        fs.Write(p.Body, 1, p.Body.Length - 1); // audio: buang byte 0x00 di depan
                    }
                    Bytes = fs.Position;
                }
            }
        }

        void RunUdp(FileStream fs, RtpDepacketizer dep)
        {
            _udp = new UdpClient(_port);
            _sess.Cmd("push.cgi", "CAM=" + _cam + "&CMD=START&COMP=" + _codec + "&PORT=" + _port);
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                byte[] d;
                try { d = _udp.Receive(ref ep); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                if (dep != null)
                {
                    dep.Feed(d, d.Length);
                }
                else
                {
                    int off = RtpDepacketizer.PayloadOffset(d, d.Length);
                    if (off >= 0 && off < d.Length) fs.Write(d, off, d.Length - off);
                }
                Bytes = fs.Position;
            }
        }

        public void Stop()
        {
            if (!Running && _sess == null) return;
            _stop = true;
            try { if (_req != null) _req.Abort(); } catch { }
            try { if (_udp != null) _udp.Close(); } catch { }
            NvrSession s = _sess;
            int cam = _cam;
            string codec = _codec;
            string extra = _udpMode ? "&PORT=" + _port : "";
            if (s != null)
            {
                Task.Run(() =>
                {
                    try { s.Cmd("push.cgi", "CAM=" + cam + "&CMD=STOP&COMP=" + codec + extra); }
                    catch { }
                });
            }
        }
    }
}

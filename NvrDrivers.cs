using System;
using System.Collections.Generic;

namespace IproNvrTool
{
    /// <summary>Driver i-PRO: membungkus kode CGI yang sudah ada (NvrScanner, NvrDownloader, PlaybackEngine, NvrSession).
    /// Perilaku sama persis dengan sebelumnya; live/playback tetap lewat sesi NVR (push.cgi / hdrctl.cgi).</summary>
    public class IproDriver : INvrDriver
    {
        public string BrandId { get { return "ipro"; } }
        public string BrandName { get { return "i-PRO (Panasonic)"; } }
        public NvrCaps Capabilities
        {
            get { return NvrCaps.Live | NvrCaps.Playback | NvrCaps.Download | NvrCaps.Hdd | NvrCaps.Snapshot | NvrCaps.Ptz; }
        }

        public RegistrationResult Test(NvrEndpoint ep) { return NvrScanner.TestNvr(ep); }
        public CameraListResult ListCameras(NvrEndpoint ep) { return NvrScanner.FetchCameraList(ep); }
        public List<DiskRecord> ListDisks(NvrEndpoint ep) { return NvrScanner.FetchDisks(ep); }

        public NvrStreamSource LiveSource(NvrRecord nvr, int channel, bool subStream)
        {
            var s = new NvrStreamSource();
            s.Kind = NvrStreamKind.NvrSession;
            return s;
        }

        public NvrStreamSource PlaybackSource(NvrRecord nvr, int channel, DateTime startUtc, DateTime endUtc)
        {
            var s = new NvrStreamSource();
            s.Kind = NvrStreamKind.NvrSession;
            return s;
        }

        public void Download(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status)
        {
            NvrDownloader.Run(nvr, channel, startLocal, endLocal, kind, folder, job, status);
        }

        public byte[] Snapshot(NvrRecord nvr, int channel, DateTime utc, int width)
        {
            return PlaybackEngine.CaptureStill(nvr, channel, utc, width);
        }
    }

    /// <summary>Daftar driver merek. Merek baru: tulis kelas INvrDriver, daftarkan di sini; definisi endpoint ada di Knowledge/*.json.</summary>
    public static class NvrDrivers
    {
        static readonly Dictionary<string, INvrDriver> all = new Dictionary<string, INvrDriver>(StringComparer.OrdinalIgnoreCase);

        static NvrDrivers()
        {
            Register(new IproDriver());
            Register(new HikvisionDriver());
            Register(new DahuaDriver());
            Register(new AxisDriver());
        }

        public static void Register(INvrDriver d) { all[d.BrandId] = d; }

        public static IEnumerable<INvrDriver> All { get { return all.Values; } }

        /// <summary>Driver untuk merek tertentu; id kosong/tidak dikenal jatuh ke i-PRO (perilaku lama).</summary>
        public static INvrDriver ById(string id)
        {
            INvrDriver d;
            if (!string.IsNullOrEmpty(id) && all.TryGetValue(id, out d)) return d;
            return all["ipro"];
        }

        public static INvrDriver For(NvrRecord nvr) { return ById(nvr == null ? null : nvr.Brand); }
    }
}

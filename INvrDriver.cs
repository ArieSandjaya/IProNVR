using System;
using System.Collections.Generic;

namespace IproNvrTool
{
    /// <summary>Kemampuan yang didukung sebuah driver NVR (dipetakan dari Knowledge/*.json).</summary>
    [Flags]
    public enum NvrCaps { None = 0, Live = 1, Playback = 2, Search = 4, Download = 8, Ptz = 16, Hdd = 32, Snapshot = 64, Events = 128 }

    /// <summary>Cara video diambil: lewat sesi NVR sendiri (i-PRO push.cgi / hdrctl.cgi) atau URL RTSP standar.</summary>
    public enum NvrStreamKind { NvrSession, Rtsp }

    public class NvrStreamSource
    {
        public NvrStreamKind Kind;
        public string RtspUrl = "";     // terisi bila Kind = Rtsp
    }

    /// <summary>Antarmuka umum satu merek NVR. Satu instance = satu NVR (NvrRecord).
    /// Implementasi: IproDriver (CGI i-PRO), nanti HikvisionDriver, DahuaDriver, OnvifDriver.
    /// Metode yang tidak didukung merek tersebut melempar NotSupportedException (cek Capabilities dahulu).</summary>
    public interface INvrDriver
    {
        string BrandId { get; }
        string BrandName { get; }
        NvrCaps Capabilities { get; }

        /// <summary>Tes login + ambil HDD dan daftar kamera (dipakai registrasi NVR).</summary>
        RegistrationResult Test(NvrEndpoint ep);
        CameraListResult ListCameras(NvrEndpoint ep);
        List<DiskRecord> ListDisks(NvrEndpoint ep);

        NvrStreamSource LiveSource(NvrRecord nvr, int channel, bool subStream);
        NvrStreamSource PlaybackSource(NvrRecord nvr, int channel, DateTime startUtc, DateTime endUtc);

        /// <summary>kind: MP4, MP4VO, N3, N3VO (nama jenis data di UI). Waktu masukan = waktu lokal PC.</summary>
        void Download(NvrRecord nvr, int channel, DateTime startLocal, DateTime endLocal, string kind,
            string folder, DownloadJob job, Action<string> status);

        byte[] Snapshot(NvrRecord nvr, int channel, DateTime utc, int width);
    }
}

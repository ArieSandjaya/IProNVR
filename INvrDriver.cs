using System;
using System.Collections.Generic;

namespace IproNvrTool
{
    /// <summary>Kemampuan yang didukung sebuah driver NVR (dipetakan dari Knowledge/*.json).</summary>
    [Flags]
    public enum NvrCaps { None = 0, Live = 1, Playback = 2, Search = 4, Download = 8, Ptz = 16, Hdd = 32, Snapshot = 64, Events = 128 }

    public class NvrChannelInfo { public int Channel; public string Name = ""; public string Address = ""; public bool Online = true; }
    public class NvrRecSegment { public int Channel; public DateTime StartUtc, EndUtc; public string Type = ""; public string Token = ""; }

    /// <summary>Antarmuka umum untuk satu merek NVR. Implementasi i-PRO, Hikvision, Dahua, dan ONVIF generik.
    /// Belum dipasang ke UI: tahap berikutnya memindahkan kode i-PRO ke driver dan menambah driver lain.</summary>
    public interface INvrDriver
    {
        string Brand { get; }
        NvrCaps Capabilities { get; }
        void Login(string host, int port, string user, string pass);
        List<NvrChannelInfo> ListChannels();
        string LiveRtspUrl(int channel, bool subStream);
        string PlaybackRtspUrl(int channel, DateTime startUtc, DateTime endUtc);
        List<NvrRecSegment> Search(int channel, DateTime fromUtc, DateTime toUtc);
        void Download(NvrRecSegment seg, string destFile, Action<long> onBytes);
        byte[] Snapshot(int channel);
    }
}

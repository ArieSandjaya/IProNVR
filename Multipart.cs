using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IproNvrTool
{
    public class MultipartPart
    {
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body;

        public string H(string key)
        {
            string v;
            return Headers.TryGetValue(key, out v) ? v : "";
        }
    }

    /// <summary>Pembaca HTTP multipart ("--myboundary" + header + Content-Length + isi).</summary>
    public class MultipartReader
    {
        readonly Stream _s;

        public MultipartReader(Stream s)
        {
            _s = new BufferedStream(s, 64 * 1024);
        }

        string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = _s.ReadByte();
                if (b < 0) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
                if (sb.Length > 16384) throw new IOException("Baris header multipart terlalu panjang (bukan stream multipart?)");
            }
        }

        byte[] ReadExact(int len)
        {
            var buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = _s.Read(buf, got, len - got);
                if (n <= 0) return null;
                got += n;
            }
            return buf;
        }

        bool _boundaryConsumed;

        /// <summary>Tanpa Content-Length: baca sampai CRLF + "--" (awal boundary berikutnya).</summary>
        byte[] ReadUntilBoundary()
        {
            var ms = new MemoryStream();
            while (true)
            {
                int b = _s.ReadByte();
                if (b < 0) return ms.Length > 0 ? ms.ToArray() : null;
                ms.WriteByte((byte)b);
                if (ms.Length > 32 * 1024 * 1024) throw new IOException("Data multipart terlalu besar tanpa boundary.");
                if (b == '-' && ms.Length >= 4)
                {
                    byte[] buf = ms.GetBuffer();
                    long n = ms.Length;
                    if (buf[n - 2] == '-' && buf[n - 3] == '\n' && buf[n - 4] == '\r')
                    {
                        var body = new byte[n - 4];
                        Buffer.BlockCopy(buf, 0, body, 0, (int)(n - 4));
                        ReadLine(); // sisa baris boundary ("myboundary" / "myboundary--")
                        _boundaryConsumed = true;
                        return body;
                    }
                }
            }
        }

        string _boundary;   // mis. "--myboundary" (diambil dari baris boundary pertama)

        /// <summary>Baca boundary + header saja (isi belum dibaca). Return null jika stream habis.</summary>
        public MultipartPart ReadHeader()
        {
            string line;
            if (!_boundaryConsumed)
            {
                do
                {
                    line = ReadLine();
                    if (line == null) return null;
                } while (!line.StartsWith("--"));
                if (_boundary == null) _boundary = line.Trim();
            }
            _boundaryConsumed = false;

            var part = new MultipartPart();
            while (true)
            {
                line = ReadLine();
                if (line == null) return null;
                if (line.Length == 0)
                {
                    if (part.Headers.Count == 0) continue; // baris kosong ekstra sebelum header
                    break;
                }
                int i = line.IndexOf(':');
                if (i > 0) part.Headers[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return part;
        }

        /// <summary>Baris pembatas (mis. "--myboundary") yang dilihat pertama kali.</summary>
        string _boundaryLine;

        /// <summary>Baca pembatas + header satu bagian saja (isi belum dibaca; pakai CopyBody). Return null jika stream habis.</summary>
        public MultipartPart ReadPartHeaders()
        {
            string line;
            if (!_boundaryConsumed)
            {
                do
                {
                    line = ReadLine();
                    if (line == null) return null;
                } while (!line.StartsWith("--"));
                if (_boundaryLine == null) _boundaryLine = line.TrimEnd();
            }
            _boundaryConsumed = false;

            var part = new MultipartPart();
            while (true)
            {
                line = ReadLine();
                if (line == null) return null;
                if (line.Length == 0)
                {
                    if (part.Headers.Count == 0) continue; // baris kosong ekstra sebelum header
                    break;
                }
                int i = line.IndexOf(':');
                if (i > 0) part.Headers[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return part;
        }

        /// <summary>Alirkan isi bagian ke dest tanpa menampung semuanya di memori (download rekaman bisa ratusan MB).
        /// Dengan Content-Length: tepat sekian byte. Tanpa Content-Length: sampai pembatas berikutnya (CRLF + baris pembatas).
        /// onBytes dipanggil per potongan. Return jumlah byte, atau -1 bila stream putus.</summary>
        public long CopyBody(MultipartPart part, Stream dest, Action<int> onBytes)
        {
            var buf = new byte[64 * 1024];
            long total = 0;
            int len;
            if (int.TryParse(part.H("Content-Length"), out len) && len >= 0)
            {
                long left = len;
                while (left > 0)
                {
                    int n = _s.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) return -1;
                    dest.Write(buf, 0, n);
                    left -= n; total += n;
                    if (onBytes != null) onBytes(n);
                }
                return total;
            }
            byte[] delim = Encoding.ASCII.GetBytes("\r\n" + (_boundaryLine ?? "--"));
            int m = 0;                 // jumlah byte awal pembatas yang sudah cocok
            int fill = 0;
            Action flush = delegate
            {
                if (fill > 0) { dest.Write(buf, 0, fill); total += fill; if (onBytes != null) onBytes(fill); fill = 0; }
            };
            while (true)
            {
                int b = _s.ReadByte();
                if (b < 0) { flush(); return -1; }
                // cocokkan dengan pembatas; bila gagal, byte yang sempat cocok adalah data biasa
                while (true)
                {
                    if (b == delim[m]) { m++; break; }
                    if (m == 0) { buf[fill++] = (byte)b; break; }
                    // gagal: tulis byte pertama yang tadi cocok lalu cari ulang awal pembatas di sisa byte
                    buf[fill++] = delim[0];
                    int rest = m - 1;
                    var pend = new byte[rest];
                    Buffer.BlockCopy(delim, 1, pend, 0, rest);
                    m = 0;
                    foreach (byte pb in pend)
                    {
                        while (true)
                        {
                            if (pb == delim[m]) { m++; break; }
                            if (m == 0) { buf[fill++] = pb; break; }
                            buf[fill++] = delim[0];
                            // pembatas hanya punya satu CR di awal: sisa dianggap data
                            m = 0;
                        }
                        if (fill >= buf.Length - 8) flush();
                    }
                    continue;
                }
                if (fill >= buf.Length - 8) flush();
                if (m == delim.Length)
                {
                    flush();
                    ReadLine();    // sisa baris pembatas ("" atau "--" penutup)
                    _boundaryConsumed = true;
                    return total;
                }
            }
        }

        /// <summary>Return null jika stream habis. Seluruh isi bagian ditampung di memori (untuk data kecil: live / playback).</summary>
        public MultipartPart ReadPart()
        {
            MultipartPart part = ReadPartHeaders();
            if (part == null) return null;
            int len;
            if (int.TryParse(part.H("Content-Length"), out len) && len >= 0)
            {
                part.Body = ReadExact(len);
            }
            else
            {
                part.Body = ReadUntilBoundary();
            }
            if (part.Body == null) return null;
            return part;
        }
    }

    public class ParsedFrame
    {
        public List<byte[]> Video = new List<byte[]>(); // JPEG: 1 frame; H.264/H.265: Annex-B per picture
        public DateTime? UtcTime;                       // Rec info 0x0007
        public int Camera = -1;                         // Rec info 0x0004
        public string Codec = "";                       // JPEG / H264 / H265
        public DateTime? GopStartUtc;
        public DateTime? GopEndUtc;
    }

    /// <summary>Parser data video NVR (live JPEG multipart dan playback camera.cgi) sesuai bab 2.3.5, 3.3 dan 7.1.</summary>
    public static class FrameParser
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static uint BE32(byte[] d, int p)
        {
            return ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];
        }

        static int BE16(byte[] d, int p)
        {
            return (d[p] << 8) | d[p + 1];
        }

        static int IndexOf(byte[] d, byte[] pat, int from)
        {
            for (int i = from; i <= d.Length - pat.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pat.Length; j++)
                {
                    if (d[i + j] != pat[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>Awal data JPEG sebenarnya: FFD8 FF xx dengan xx bukan FE (FFD8FFFE = chunk info NVR).</summary>
        static int FindJpegStart(byte[] d)
        {
            for (int i = 0; i + 3 < d.Length; i++)
            {
                if (d[i] == 0xFF && d[i + 1] == 0xD8 && d[i + 2] == 0xFF && d[i + 3] != 0xFE) return i;
            }
            return -1;
        }

        static byte[] Slice(byte[] d, int a, int b, bool jpeg)
        {
            int end = b;
            if (jpeg)
            {
                for (int i = b - 2; i > a; i--)
                {
                    if (d[i] == 0xFF && d[i + 1] == 0xD9) { end = i + 2; break; }
                }
            }
            else
            {
                while (end > a && d[end - 1] == 0) end--;
            }
            var r = new byte[Math.Max(0, end - a)];
            Buffer.BlockCopy(d, a, r, 0, r.Length);
            return r;
        }

        static int NextPacket(byte[] d, int from)
        {
            for (int p = Math.Max(0, from); p + 28 <= d.Length; p++)
            {
                if (d[p] == 0xFF && d[p + 1] == 0x05 &&
                    d[p + 24] == 0 && d[p + 25] == 0 && d[p + 26] == 0 && d[p + 27] == 1) return p;
            }
            return -1;
        }

        static void ReadRecInfo(byte[] d, int bound, ParsedFrame f)
        {
            int p = (d[0] == 0xFF && d[1] == 0xD8) ? 6 : 4;
            while (p + 4 <= bound)
            {
                if (d[p] == 0xFF && d[p + 1] == 0xD8) { p += 6; continue; }
                int id = BE16(d, p);
                int len = BE16(d, p + 2);
                if (len < 4 || p + len > bound) break;
                if (id == 0x0007 && len >= 8 && f.UtcTime == null) f.UtcTime = Epoch.AddSeconds(BE32(d, p + 4));
                else if (id == 0x0004 && len >= 6 && f.Camera < 0) f.Camera = BE16(d, p + 4);
                p += len;
            }
        }

        public static ParsedFrame Parse(byte[] d)
        {
            var f = new ParsedFrame();
            if (d == null || d.Length < 8) return f;

            if (d[0] == 0xFF && d[1] == 0x15) f.Codec = "H264";
            else if (d[0] == 0xFF && d[1] == 0x16) f.Codec = "H265";
            else if (d[0] == 0xFF && d[1] == 0xD8) f.Codec = "JPEG";

            // Stream ID header (playback): FF 06 00 44
            int s = IndexOf(d, new byte[] { 0xFF, 0x06, 0x00, 0x44 }, 0);
            if (s >= 0)
            {
                ReadRecInfo(d, s, f);
                ReadPackets(d, s, f);
            }
            else if (f.Codec == "H264" || f.Codec == "H265")
            {
                ParseWithoutStreamId(d, f);
            }
            else
            {
                int js = FindJpegStart(d);
                ReadRecInfo(d, js >= 0 ? js : d.Length, f);
                if (js >= 0)
                {
                    f.Codec = "JPEG";
                    f.Video.Add(Slice(d, js, d.Length, true));
                }
            }
            if (f.Video.Count == 0 && (f.Codec == "H264" || f.Codec == "H265")) GenericFallback(d, f);
            return f;
        }

        static bool ValidNal(byte[] d, int i, bool h265)
        {
            if (i >= d.Length) return false;
            int b = d[i];
            if ((b & 0x80) != 0) return false;
            if (h265) { int t = (b >> 1) & 0x3F; return t <= 40; }
            int t2 = b & 0x1F;
            return t2 >= 1 && t2 <= 9 && (b & 0x60) != 0 || t2 == 6 || t2 == 9;
        }

        /// <summary>Cadangan terakhir bila susunan header tidak dikenal: cari paket "FF 05" dengan panjang yang masuk akal, atau start code Annex-B pertama.</summary>
        static void GenericFallback(byte[] d, ParsedFrame f)
        {
            bool h265 = f.Codec == "H265";
            int guard = 0;
            for (int p = 0; p + 24 < d.Length && guard < 100000; p++)
            {
                if (d[p] != 0xFF || d[p + 1] != 0x05) continue;
                long len = BE32(d, p + 4);
                long end = (long)p + 8 + len;
                if (len < 17 || end > d.Length) continue;
                bool chain = end == d.Length || (end + 1 < d.Length && d[end] == 0xFF && d[end + 1] == 0x05) || (end + 1 < d.Length && d[end] == 0 );
                if (!chain) continue;
                byte[] v = Slice(d, p + 24, (int)end, false);
                if (v.Length > 0) { f.Video.Add(v); guard++; }
                p = (int)end - 1;
            }
            if (f.Video.Count > 0) return;
            for (int i = 4; i + 4 < d.Length; i++)
            {
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1 && ValidNal(d, i + 3, h265))
                {
                    int st = (i > 0 && d[i - 1] == 0) ? i - 1 : i;
                    f.Video.Add(Slice(d, st, d.Length, false));
                    return;
                }
            }
        }

        /// <summary>Playback H.264/H.265 tanpa header "FF 06 00 44" (mis. awal data "FF 15 00 74 ..."): header 4 byte berisi panjang header, lalu info rekaman,
        /// lalu paket "FF 05" berisi Annex-B. Bila paket tak ditemukan, ambil mulai start code 00 00 00 01 pertama.</summary>
        static void ParseWithoutStreamId(byte[] d, ParsedFrame f)
        {
            int hl = BE16(d, 2);
            int bound = (hl >= 8 && hl <= d.Length) ? hl : Math.Min(d.Length, 256);
            ReadRecInfo(d, bound, f);
            int q = NextPacket(d, 0);
            int guard = 0;
            while (q >= 0 && q + 24 <= d.Length && guard++ < 100000)
            {
                uint length = BE32(d, q + 4);
                long ve = (long)q + 8 + length;
                if (ve > d.Length) ve = d.Length;
                int vs = q + 24;
                if (ve > vs)
                {
                    byte[] v = Slice(d, vs, (int)ve, false);
                    if (v.Length > 0) f.Video.Add(v);
                }
                q = NextPacket(d, (int)ve);
            }
            if (f.Video.Count == 0)
            {
                int sc = IndexOf(d, new byte[] { 0, 0, 0, 1 }, 0);
                if (sc >= 0) f.Video.Add(Slice(d, sc, d.Length, false));
            }
        }

        static void ReadPackets(byte[] d, int s, ParsedFrame f)
        {
            int g = s + 4; // GOP information (32 byte): reserved16, end(4)+supp(4), start(4)+supp(4)
            if (g + 32 <= d.Length)
            {
                uint end = BE32(d, g + 16);
                uint start = BE32(d, g + 24);
                if (start > 0) f.GopStartUtc = Epoch.AddSeconds(start);
                if (end > 0) f.GopEndUtc = Epoch.AddSeconds(end);
            }

            int q = s + 4 + 0x44; // Packet ID header: FF 05, padding(2), length(4), reserved(16), video...
            int guard = 0;
            bool jpeg = f.Codec == "JPEG";
            while (q >= 0 && q + 24 <= d.Length && d[q] == 0xFF && d[q + 1] == 0x05 && guard++ < 100000)
            {
                uint length = BE32(d, q + 4);
                long ve = (long)q + 8 + length;
                if (ve > d.Length) ve = d.Length;
                int vs = q + 24;
                if (ve > vs)
                {
                    byte[] v = Slice(d, vs, (int)ve, jpeg);
                    if (v.Length > 0) f.Video.Add(v);
                }
                if (jpeg) break;
                q = NextPacket(d, (int)ve);
            }
        }
    }

    /// <summary>Mengubah paket RTP H.264 / H.265 (live) menjadi stream Annex-B (.h264 / .h265)
    /// yang bisa diputar dengan VLC / ffplay.</summary>
    public class RtpDepacketizer
    {
        static readonly byte[] SC = { 0, 0, 0, 1 };
        readonly bool _h265;
        readonly Stream _out;

        public RtpDepacketizer(bool h265, Stream output)
        {
            _h265 = h265;
            _out = output;
        }

        /// <summary>Awal payload; header ekstensi RTP (info tambahan NVR) dilewati.</summary>
        public static int PayloadOffset(byte[] d, int len)
        {
            if (len < 12) return -1;
            int cc = d[0] & 0x0F;
            bool x = (d[0] & 0x10) != 0;
            int off = 12 + cc * 4;
            if (x)
            {
                if (len < off + 4) return -1;
                int words = (d[off + 2] << 8) | d[off + 3];
                off += 4 + words * 4;
            }
            return off <= len ? off : -1;
        }

        /// <summary>Marker bit RTP = paket terakhir dari satu gambar (saat itu aman men-flush ke dekoder).</summary>
        public static bool Marker(byte[] d)
        {
            return d != null && d.Length > 1 && (d[1] & 0x80) != 0;
        }

        /// <summary>Paket agregasi (STAP-A H.264 / AP H.265): [ukuran 2 byte][NAL] berulang.</summary>
        void WriteAggregate(byte[] d, int p, int end)
        {
            while (p + 2 <= end)
            {
                int sz = (d[p] << 8) | d[p + 1];
                p += 2;
                if (sz <= 0 || p + sz > end) break;
                _out.Write(SC, 0, 4);
                _out.Write(d, p, sz);
                p += sz;
            }
        }

        public void Feed(byte[] d, int len)
        {
            int off = PayloadOffset(d, len);
            if (off < 0 || off >= len) return;
            int n = len - off;

            if (!_h265)
            {
                int type = d[off] & 0x1F;
                if (type >= 1 && type <= 23)
                {
                    _out.Write(SC, 0, 4);
                    _out.Write(d, off, n);
                }
                else if (type == 28 && n >= 2) // FU-A
                {
                    byte fu = d[off + 1];
                    if ((fu & 0x80) != 0)
                    {
                        _out.Write(SC, 0, 4);
                        _out.WriteByte((byte)((d[off] & 0xE0) | (fu & 0x1F)));
                    }
                    _out.Write(d, off + 2, n - 2);
                }
                else if (type == 24 && n >= 3) // STAP-A
                {
                    WriteAggregate(d, off + 1, len);
                }
            }
            else
            {
                int type = (d[off] >> 1) & 0x3F;
                if (type < 48)
                {
                    _out.Write(SC, 0, 4);
                    _out.Write(d, off, n);
                }
                else if (type == 49 && n >= 3) // FU
                {
                    byte fu = d[off + 2];
                    if ((fu & 0x80) != 0)
                    {
                        _out.Write(SC, 0, 4);
                        _out.WriteByte((byte)((d[off] & 0x81) | ((fu & 0x3F) << 1)));
                        _out.WriteByte(d[off + 1]);
                    }
                    _out.Write(d, off + 3, n - 3);
                }
                else if (type == 48 && n >= 4) // AP
                {
                    WriteAggregate(d, off + 2, len);
                }
            }
        }
    }
}

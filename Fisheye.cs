using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IproNvrTool
{
    public enum FishMode { Off = 0, Panorama = 1, Quad = 2, Ptz = 3 }

    /// <summary>Pengaturan lensa fisheye per kamera. Sumber: dewarp di aplikasi (cara 1) atau stream dewarp dari kamera lewat RTSP (cara 2).</summary>
    public class FisheyeRec
    {
        public long NvrId;
        public int Channel;
        public bool Enabled = true;
        public int Mount;               // 0 langit-langit, 1 dinding, 2 meja/lantai (menghadap atas)
        public double Fov = 180;        // sudut pandang lensa (derajat)
        public double Cx = 0.5, Cy = 0.5; // pusat lingkaran (0..1 dari lebar/tinggi gambar)
        public double Radius = 0.5;     // jari-jari lingkaran (pecahan dari sisi terpendek gambar)
        public double Rot;              // putaran gambar (derajat)
        public int Mode = 1;            // FishMode bawaan
        public bool CamStream;          // true = pakai stream RTSP dari kamera (dewarp di kamera), tanpa dewarp di aplikasi
        public string CamUrl = "";

        public FisheyeRec Clone() { return (FisheyeRec)MemberwiseClone(); }
        public FishMode FMode { get { return (FishMode)Math.Max(0, Math.Min(3, Mode)); } }
    }

    /// <summary>Penyimpanan (cache) pengaturan fisheye + mesin dewarp perangkat lunak (proyeksi equidistant).</summary>
    public static class Fisheye
    {
        static readonly object lk = new object();
        static Dictionary<string, FisheyeRec> recs;

        static void Load()
        {
            if (recs != null) return;
            recs = new Dictionary<string, FisheyeRec>();
            try { foreach (FisheyeRec r in AsmDb.ListFisheye()) recs[r.NvrId + ":" + r.Channel] = r; }
            catch (Exception ex) { Log.Write("Fisheye: " + ex.Message); }
        }

        /// <summary>Pengaturan kamera (null bila tidak ada; cek Enabled sendiri).</summary>
        public static FisheyeRec Get(long nvrId, int channel)
        {
            lock (lk)
            {
                Load();
                FisheyeRec r;
                return recs.TryGetValue(nvrId + ":" + channel, out r) ? r : null;
            }
        }

        public static void Save(FisheyeRec r)
        {
            AsmDb.SaveFisheye(r);
            lock (lk) { Load(); recs[r.NvrId + ":" + r.Channel] = r; }
        }

        public static void Delete(long nvrId, int channel)
        {
            AsmDb.DeleteFisheye(nvrId, channel);
            lock (lk) { Load(); recs.Remove(nvrId + ":" + channel); }
        }

        public static double DefaultPitch(int mount) { return mount == 1 ? 0 : 45; }

        // ---------------- LUT ----------------

        class Lut
        {
            public int[] Idx;
            public byte[] Fx, Fy;
        }

        static readonly List<KeyValuePair<string, Lut>> cache = new List<KeyValuePair<string, Lut>>();

        static Lut GetLut(int sw, int sh, int ow, int oh, FisheyeRec r, FishMode m, double yaw, double pitch, double hfov)
        {
            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}x{1}>{2}x{3}|{4}|{5}|{6:F1}|{7:F3}|{8:F3}|{9:F3}|{10:F1}|{11:F1}|{12:F1}|{13:F1}",
                sw, sh, ow, oh, (int)m, r.Mount, r.Fov, r.Cx, r.Cy, r.Radius, r.Rot, m == FishMode.Ptz ? yaw : 0, m == FishMode.Ptz ? pitch : 0, m == FishMode.Ptz ? hfov : 0);
            lock (cache)
            {
                for (int i = 0; i < cache.Count; i++)
                {
                    if (cache[i].Key == key)
                    {
                        Lut hit = cache[i].Value;
                        if (i != cache.Count - 1) { cache.RemoveAt(i); cache.Add(new KeyValuePair<string, Lut>(key, hit)); }
                        return hit;
                    }
                }
            }
            Lut l = Build(sw, sh, ow, oh, r, m, yaw, pitch, hfov);
            lock (cache)
            {
                cache.Add(new KeyValuePair<string, Lut>(key, l));
                while (cache.Count > 10) cache.RemoveAt(0);
            }
            return l;
        }

        struct V3
        {
            public double X, Y, Z;
            public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
            public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
            public static V3 operator *(V3 a, double k) { return new V3(a.X * k, a.Y * k, a.Z * k); }
            public static V3 Cross(V3 a, V3 b) { return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
            public double Len { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
            public V3 Norm() { double l = Len; return l < 1e-12 ? new V3(1, 0, 0) : new V3(X / l, Y / l, Z / l); }
        }

        static V3 Gravity(int mount)
        {
            if (mount == 1) return new V3(0, 1, 0);   // dinding: sumbu optik horizontal, gravitasi = ke bawah gambar
            if (mount == 2) return new V3(0, 0, -1);  // meja: sumbu optik menghadap atas
            return new V3(0, 0, 1);                   // langit-langit: sumbu optik menghadap bawah
        }

        static V3 Horiz(int mount, double yawRad)
        {
            if (mount == 1) return new V3(Math.Sin(yawRad), 0, Math.Cos(yawRad));
            return new V3(Math.Cos(yawRad), Math.Sin(yawRad), 0);
        }

        static V3 Forward(int mount, double yawDeg, double pitchDeg, double rotDeg)
        {
            double yr = (yawDeg + (mount == 1 ? 0 : rotDeg)) * Math.PI / 180.0;
            double pr = pitchDeg * Math.PI / 180.0;
            V3 g = Gravity(mount);
            return Horiz(mount, yr) * Math.Cos(pr) + g * Math.Sin(pr);
        }

        [ThreadStatic] static double wallRot; // putaran gambar untuk pemasangan dinding (langit-langit/meja memakainya sebagai yaw)

        static void Project(Lut l, int p, V3 d, double cx, double cy, double R, double half, int sw, int sh)
        {
            double z = Math.Max(-1.0, Math.Min(1.0, d.Z));
            double theta = Math.Acos(z);
            if (theta > half) { l.Idx[p] = -1; return; }
            double dx = d.X, dy = d.Y;
            if (wallRot != 0)
            {
                double cr = Math.Cos(wallRot), sr = Math.Sin(wallRot);
                double nx = dx * cr - dy * sr; dy = dx * sr + dy * cr; dx = nx;
            }
            double rho = Math.Sqrt(dx * dx + dy * dy);
            double sx = cx, sy = cy;
            if (rho > 1e-9)
            {
                double rr = theta / half * R;
                sx = cx + dx / rho * rr;
                sy = cy + dy / rho * rr;
            }
            if (sx < 0 || sy < 0 || sx > sw - 1 || sy > sh - 1) { l.Idx[p] = -1; return; }
            int x0 = Math.Min((int)sx, sw - 2), y0 = Math.Min((int)sy, sh - 2);
            if (x0 < 0 || y0 < 0) { l.Idx[p] = -1; return; }
            l.Idx[p] = y0 * sw + x0;
            l.Fx[p] = (byte)Math.Max(0, Math.Min(255, (int)((sx - x0) * 256)));
            l.Fy[p] = (byte)Math.Max(0, Math.Min(255, (int)((sy - y0) * 256)));
        }

        static void Persp(Lut l, int ow, int rx, int ry, int rw, int rh, V3 f, int mount, double hfovDeg,
            double cx, double cy, double R, double half, int sw, int sh)
        {
            V3 g = Gravity(mount);
            V3 right = V3.Cross(g, f);
            right = right.Len < 1e-6 ? new V3(1, 0, 0) : right.Norm();
            V3 down = V3.Cross(f, right).Norm();
            double tx = Math.Tan(hfovDeg * 0.5 * Math.PI / 180.0);
            double ty = tx * rh / (double)rw;
            for (int j = 0; j < rh; j++)
            {
                double v = ((j + 0.5) / rh * 2.0 - 1.0) * ty;
                for (int i = 0; i < rw; i++)
                {
                    double u = ((i + 0.5) / rw * 2.0 - 1.0) * tx;
                    V3 d = (f + right * u + down * v).Norm();
                    Project(l, (ry + j) * ow + rx + i, d, cx, cy, R, half, sw, sh);
                }
            }
        }

        static Lut Build(int sw, int sh, int ow, int oh, FisheyeRec r, FishMode m, double yaw, double pitch, double hfov)
        {
            var l = new Lut();
            l.Idx = new int[ow * oh];
            l.Fx = new byte[ow * oh];
            l.Fy = new byte[ow * oh];
            double cx = r.Cx * sw, cy = r.Cy * sh, R = Math.Max(8, r.Radius * Math.Min(sw, sh));
            double half = Math.Max(10, r.Fov) * 0.5 * Math.PI / 180.0;
            int mt = r.Mount;
            wallRot = mt == 1 ? r.Rot * Math.PI / 180.0 : 0;
            if (m == FishMode.Panorama)
            {
                double y0, y1, p0, p1;
                if (mt == 1) { y0 = -85; y1 = 85; p0 = -40; p1 = 40; }
                else { y0 = 0; y1 = 360; p0 = 5; p1 = 80; }
                for (int j = 0; j < oh; j++)
                {
                    double p = p0 + (j + 0.5) / oh * (p1 - p0);
                    for (int i = 0; i < ow; i++)
                    {
                        double y = y0 + (i + 0.5) / ow * (y1 - y0);
                        Project(l, j * ow + i, Forward(mt, y, p, r.Rot), cx, cy, R, half, sw, sh);
                    }
                }
            }
            else if (m == FishMode.Quad)
            {
                double[] ys = mt == 1 ? new double[] { -45, 45, -45, 45 } : new double[] { 0, 90, 270, 180 };
                double[] ps = mt == 1 ? new double[] { -25, -25, 25, 25 } : new double[] { 35, 35, 35, 35 };
                int tw = ow / 2, th = oh / 2;
                for (int k = 0; k < 4; k++)
                {
                    int rx = (k % 2) * tw, ry = (k / 2) * th;
                    int rw = (k % 2 == 1) ? ow - tw : tw, rh = (k / 2 == 1) ? oh - th : th;
                    Persp(l, ow, rx, ry, rw, rh, Forward(mt, ys[k], ps[k], r.Rot), mt, 95, cx, cy, R, half, sw, sh);
                }
            }
            else
            {
                Persp(l, ow, 0, 0, ow, oh, Forward(mt, yaw, pitch, r.Rot), mt, hfov, cx, cy, R, half, sw, sh);
            }
            return l;
        }

        // ---------------- render ----------------

        /// <summary>Dewarp satu frame. src = gambar bulat mentah; hasil Bitmap baru berukuran ow x oh.</summary>
        [ThreadStatic] static int[] bufSrc, bufDst;

        public static Bitmap Render(Bitmap src, FisheyeRec r, FishMode mode, double yawDeg, double pitchDeg, double hfovDeg, int ow, int oh)
        {
            ow = Math.Max(16, ow & ~1);
            oh = Math.Max(16, oh & ~1);
            int sw = src.Width, sh = src.Height;
            Lut lut = GetLut(sw, sh, ow, oh, r, mode, yawDeg, pitchDeg, hfovDeg);

            // buffer dipakai ulang per thread (frame 1080p x puluhan kamera x 24 jam = tekanan GC besar bila dialokasi tiap frame)
            if (bufSrc == null || bufSrc.Length < sw * sh) bufSrc = new int[sw * sh];
            int[] sp = bufSrc;
            BitmapData sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (sd.Stride == sw * 4) Marshal.Copy(sd.Scan0, sp, 0, sw * sh);
                else for (int y = 0; y < sh; y++) Marshal.Copy(new IntPtr(sd.Scan0.ToInt64() + (long)y * sd.Stride), sp, y * sw, sw);
            }
            finally { src.UnlockBits(sd); }

            if (bufDst == null || bufDst.Length < ow * oh) bufDst = new int[ow * oh];
            int[] dp = bufDst;
            int[] idx = lut.Idx;
            byte[] fxs = lut.Fx, fys = lut.Fy;
            int total = ow * oh;
            for (int p = 0; p < total; p++)
            {
                int i = idx[p];
                if (i < 0) { dp[p] = unchecked((int)0xFF000000); continue; }
                int fx = fxs[p], fy = fys[p];
                int w00 = (256 - fx) * (256 - fy), w10 = fx * (256 - fy), w01 = (256 - fx) * fy, w11 = fx * fy;
                int a = sp[i], b = sp[i + 1], c = sp[i + sw], d = sp[i + sw + 1];
                int rr = (((a >> 16) & 255) * w00 + ((b >> 16) & 255) * w10 + ((c >> 16) & 255) * w01 + ((d >> 16) & 255) * w11) >> 16;
                int gg = (((a >> 8) & 255) * w00 + ((b >> 8) & 255) * w10 + ((c >> 8) & 255) * w01 + ((d >> 8) & 255) * w11) >> 16;
                int bb = ((a & 255) * w00 + (b & 255) * w10 + (c & 255) * w01 + (d & 255) * w11) >> 16;
                dp[p] = unchecked((int)0xFF000000) | (rr << 16) | (gg << 8) | bb;
            }
            var dst = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
            BitmapData dd = dst.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(dp, 0, dd.Scan0, total); }
            finally { dst.UnlockBits(dd); }
            return dst;
        }
    }
}

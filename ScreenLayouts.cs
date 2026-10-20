using System;
using System.Collections.Generic;
using System.Drawing;

namespace IproNvrTool
{
    /// <summary>Pola layar (screen layout) ala ASM300: 1, 2A/2B, 3A-3C, 4, 4A-4E, 5, 6, 6A, 7, 7A, 9, 10, 13, 16.
    /// Bentuk varian (A/B/C/D/E) adalah perkiraan; nomor area mengikuti urutan sel di sini.</summary>
    public class ScreenLayout
    {
        public string Id = "";
        public int Cols, Rows;
        public List<Rectangle> Cells = new List<Rectangle>();

        public int Count { get { return Cells.Count; } }

        static readonly List<ScreenLayout> all = new List<ScreenLayout>();

        static void Add(string id, int cols, int rows, params int[] r)
        {
            var l = new ScreenLayout();
            l.Id = id;
            l.Cols = cols;
            l.Rows = rows;
            for (int i = 0; i + 3 < r.Length; i += 4) l.Cells.Add(new Rectangle(r[i], r[i + 1], r[i + 2], r[i + 3]));
            all.Add(l);
        }

        static void Even(string id, int n)
        {
            var l = new ScreenLayout();
            l.Id = id;
            l.Cols = n;
            l.Rows = n;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) l.Cells.Add(new Rectangle(x, y, 1, 1));
            all.Add(l);
        }

        static ScreenLayout()
        {
            Add("1", 1, 1, 0, 0, 1, 1);
            Add("2A", 2, 1, 0, 0, 1, 1, 1, 0, 1, 1);
            Add("2B", 1, 2, 0, 0, 1, 1, 0, 1, 1, 1);
            Add("3A", 2, 2, 0, 0, 1, 2, 1, 0, 1, 1, 1, 1, 1, 1);
            Add("3B", 2, 2, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 2);
            Add("3C", 3, 1, 0, 0, 1, 1, 1, 0, 1, 1, 2, 0, 1, 1);
            Even("4", 2);
            Add("4A", 3, 3, 0, 0, 3, 2, 0, 2, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1);
            Add("4B", 3, 3, 0, 0, 2, 3, 2, 0, 1, 1, 2, 1, 1, 1, 2, 2, 1, 1);
            Add("4C", 4, 1, 0, 0, 1, 1, 1, 0, 1, 1, 2, 0, 1, 1, 3, 0, 1, 1);
            Add("4D", 1, 4, 0, 0, 1, 1, 0, 1, 1, 1, 0, 2, 1, 1, 0, 3, 1, 1);
            Add("4E", 3, 3, 0, 0, 1, 1, 0, 1, 1, 1, 0, 2, 1, 1, 1, 0, 2, 3);
            Add("5", 6, 2, 0, 0, 3, 1, 3, 0, 3, 1, 0, 1, 2, 1, 2, 1, 2, 1, 4, 1, 2, 1);
            Add("6", 3, 2, 0, 0, 1, 1, 1, 0, 1, 1, 2, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1);
            Add("6A", 3, 3, 0, 0, 2, 2, 2, 0, 1, 1, 2, 1, 1, 1, 0, 2, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1);
            Add("7", 4, 3, 0, 0, 2, 2, 2, 0, 1, 1, 3, 0, 1, 1, 2, 1, 1, 1, 3, 1, 1, 1, 0, 2, 2, 1, 2, 2, 2, 1);
            Add("7A", 3, 4, 0, 0, 3, 2, 0, 2, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1, 0, 3, 1, 1, 1, 3, 1, 1, 2, 3, 1, 1);
            Even("9", 3);
            Add("10", 4, 4, 0, 0, 2, 2, 2, 0, 2, 2,
                0, 2, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1, 3, 2, 1, 1,
                0, 3, 1, 1, 1, 3, 1, 1, 2, 3, 1, 1, 3, 3, 1, 1);
            Add("13", 4, 4, 0, 0, 2, 2, 2, 0, 1, 1, 3, 0, 1, 1, 2, 1, 1, 1, 3, 1, 1, 1,
                0, 2, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1, 3, 2, 1, 1,
                0, 3, 1, 1, 1, 3, 1, 1, 2, 3, 1, 1, 3, 3, 1, 1);
            Even("16", 4);
        }

        public static List<ScreenLayout> All { get { return all; } }

        public static ScreenLayout ById(string id)
        {
            foreach (ScreenLayout l in all) if (string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)) return l;
            return all[6]; // "4"
        }

        public static bool Exists(string id)
        {
            foreach (ScreenLayout l in all) if (string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Pola terkecil yang cukup untuk n kamera (mis. 5 kamera -> "6").</summary>
        public static ScreenLayout FitFor(int n)
        {
            string[] order = { "1", "4", "9", "16" };
            foreach (string id in order)
            {
                ScreenLayout l = ById(id);
                if (l.Count >= n) return l;
            }
            return ById("16");
        }

        public Bitmap Preview(int w, int h)
        {
            var bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30, 35, 46));
                using (var pen = new Pen(Color.FromArgb(150, 170, 200)))
                {
                    float cw = (w - 2f) / Cols, ch = (h - 2f) / Rows;
                    foreach (Rectangle c in Cells)
                        g.DrawRectangle(pen, 1 + c.X * cw, 1 + c.Y * ch, c.Width * cw - 1, c.Height * ch - 1);
                }
            }
            return bmp;
        }
    }
}

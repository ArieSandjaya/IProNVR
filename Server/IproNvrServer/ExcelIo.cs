using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace IproNvrServer
{
    /// <summary>Baca / tulis file Excel (.xlsx) sederhana dan CSV tanpa library tambahan (xlsx = zip berisi XML).</summary>
    public static class ExcelIo
    {
        // ---------------- baca ----------------

        /// <summary>Baris-baris sheet pertama (sel kosong = ""). Nama file .csv dibaca sebagai CSV.</summary>
        public static List<string[]> Read(byte[] data, string fileName)
        {
            bool zip = data.Length > 3 && data[0] == 0x50 && data[1] == 0x4B;
            if (!zip || (fileName ?? "").ToLowerInvariant().EndsWith(".csv")) return ReadCsv(data);
            return ReadXlsx(data);
        }

        static List<string[]> ReadCsv(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data).TrimStart('﻿');
            var rows = new List<string[]>();
            foreach (string line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                if (line.Trim().Length == 0) continue;
                char sep = line.IndexOf(';') >= 0 && line.IndexOf(',') < 0 ? ';' : (line.IndexOf('\t') >= 0 ? '\t' : ',');
                var cells = new List<string>();
                var sb = new StringBuilder();
                bool q = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
                    else if (c == '"') q = true;
                    else if (c == sep) { cells.Add(sb.ToString()); sb.Length = 0; }
                    else sb.Append(c);
                }
                cells.Add(sb.ToString());
                rows.Add(cells.ToArray());
            }
            return rows;
        }

        static List<string[]> ReadXlsx(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var shared = new List<string>();
                ZipArchiveEntry se = za.GetEntry("xl/sharedStrings.xml");
                if (se != null)
                {
                    var d = new XmlDocument();
                    using (Stream s = se.Open()) d.Load(s);
                    foreach (XmlNode si in d.DocumentElement.ChildNodes)
                    {
                        if (si.LocalName != "si") continue;
                        var sb = new StringBuilder();
                        foreach (XmlNode t in si.SelectNodes(".//*[local-name()='t']")) sb.Append(t.InnerText);
                        shared.Add(sb.ToString());
                    }
                }
                ZipArchiveEntry sheet = FirstSheet(za);
                if (sheet == null) throw new Exception("File Excel tidak berisi sheet.");
                var doc = new XmlDocument();
                using (Stream s = sheet.Open()) doc.Load(s);
                var rows = new List<string[]>();
                foreach (XmlNode row in doc.SelectNodes("//*[local-name()='row']"))
                {
                    var cells = new List<string>();
                    foreach (XmlNode c in row.ChildNodes)
                    {
                        if (c.LocalName != "c") continue;
                        string r = c.Attributes["r"] == null ? "" : c.Attributes["r"].Value;
                        int col = ColIndex(r);
                        if (col < 0) col = cells.Count;
                        while (cells.Count <= col) cells.Add("");
                        string t = c.Attributes["t"] == null ? "" : c.Attributes["t"].Value;
                        string val = "";
                        XmlNode v = c.SelectSingleNode("*[local-name()='v']");
                        if (t == "s" && v != null)
                        {
                            int idx;
                            if (int.TryParse(v.InnerText, out idx) && idx >= 0 && idx < shared.Count) val = shared[idx];
                        }
                        else if (t == "inlineStr")
                        {
                            var sb = new StringBuilder();
                            foreach (XmlNode tn in c.SelectNodes(".//*[local-name()='t']")) sb.Append(tn.InnerText);
                            val = sb.ToString();
                        }
                        else if (v != null) val = v.InnerText;
                        cells[col] = val.Trim();
                    }
                    bool any = false;
                    foreach (string x in cells) if (x.Length > 0) any = true;
                    if (any) rows.Add(cells.ToArray());
                }
                return rows;
            }
        }

        static ZipArchiveEntry FirstSheet(ZipArchive za)
        {
            ZipArchiveEntry best = null;
            foreach (ZipArchiveEntry e in za.Entries)
            {
                string n = e.FullName.Replace('\\', '/');
                if (!n.StartsWith("xl/worksheets/") || !n.EndsWith(".xml")) continue;
                if (n == "xl/worksheets/sheet1.xml") return e;
                if (best == null || string.CompareOrdinal(n, best.FullName) < 0) best = e;
            }
            return best;
        }

        static int ColIndex(string cellRef)
        {
            int n = 0, i = 0;
            while (i < cellRef.Length && char.IsLetter(cellRef[i])) { n = n * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1); i++; }
            return i == 0 ? -1 : n - 1;
        }

        // ---------------- tulis ----------------

        static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string ColName(int i) { return ((char)('A' + i)).ToString(); }

        /// <summary>Buat file .xlsx satu sheet. Kolom bertipe angka (numericCols) ditulis sebagai angka.</summary>
        public static byte[] Write(string sheetName, List<string[]> rows, int[] widths, int[] numericCols)
        {
            var num = new HashSet<int>(numericCols ?? new int[0]);
            using (var ms = new MemoryStream())
            {
                using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Put(za, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                    Put(za, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    Put(za, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"" + Esc(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    Put(za, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                    Put(za, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/></patternFill></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs></styleSheet>");
                    var sb = new StringBuilder();
                    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                    if (widths != null && widths.Length > 0)
                    {
                        sb.Append("<cols>");
                        for (int i = 0; i < widths.Length; i++) sb.Append("<col min=\"" + (i + 1) + "\" max=\"" + (i + 1) + "\" width=\"" + widths[i] + "\" customWidth=\"1\"/>");
                        sb.Append("</cols>");
                    }
                    sb.Append("<sheetData>");
                    for (int r = 0; r < rows.Count; r++)
                    {
                        sb.Append("<row r=\"" + (r + 1) + "\">");
                        string[] row = rows[r];
                        for (int c = 0; c < row.Length; c++)
                        {
                            string rf = ColName(c) + (r + 1);
                            string style = r == 0 ? " s=\"1\"" : "";
                            double dv;
                            if (r > 0 && num.Contains(c) && double.TryParse(row[c], NumberStyles.Any, CultureInfo.InvariantCulture, out dv))
                                sb.Append("<c r=\"" + rf + "\"" + style + "><v>" + dv.ToString(CultureInfo.InvariantCulture) + "</v></c>");
                            else
                                sb.Append("<c r=\"" + rf + "\"" + style + " t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Esc(row[c]) + "</t></is></c>");
                        }
                        sb.Append("</row>");
                    }
                    sb.Append("</sheetData></worksheet>");
                    Put(za, "xl/worksheets/sheet1.xml", sb.ToString());
                }
                return ms.ToArray();
            }
        }

        static void Put(ZipArchive za, string name, string xml)
        {
            ZipArchiveEntry e = za.CreateEntry(name);
            using (Stream s = e.Open())
            {
                byte[] b = new UTF8Encoding(false).GetBytes(xml);
                s.Write(b, 0, b.Length);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace IproNvrServer
{
    /// <summary>Penulis .xlsx minimal (tanpa library): beberapa sheet, teks/angka, gaya header dan warna status.</summary>
    public class XlsxCell
    {
        public string Text;
        public double? Num;
        public int Style;      // 0 biasa, 1 judul, 2 header tabel, 3 hijau (ok), 4 kuning (warning), 5 merah (error), 6 abu (unknown), 7 sub-judul grup
        public XlsxCell(string t, int style) { Text = t; Style = style; }
        public XlsxCell(double n, int style) { Num = n; Style = style; }
    }

    public class XlsxSheet
    {
        public string Name;
        public readonly List<List<XlsxCell>> Rows = new List<List<XlsxCell>>();
        public double[] Widths = new double[0];
        public int FreezeRow;   // jumlah baris teratas yang dibekukan (0 = tidak)

        public List<XlsxCell> Row(params XlsxCell[] cells)
        {
            var r = new List<XlsxCell>(cells);
            Rows.Add(r);
            return r;
        }
        public void Blank() { Rows.Add(new List<XlsxCell>()); }
    }

    public class XlsxBook
    {
        public readonly List<XlsxSheet> Sheets = new List<XlsxSheet>();

        public XlsxSheet Add(string name)
        {
            string n = Clean(name);
            string b = n; int k = 2;
            while (Sheets.Exists(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))) n = Trim31(b, " (" + (k++) + ")");
            var sh = new XlsxSheet { Name = n };
            Sheets.Add(sh);
            return sh;
        }

        static string Trim31(string s, string suffix) { return (s.Length + suffix.Length > 31 ? s.Substring(0, 31 - suffix.Length) : s) + suffix; }

        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if ("[]:*?/\\".IndexOf(c) < 0 && c >= ' ') sb.Append(c);
            string r = sb.ToString().Trim().Trim('\'');
            if (r.Length == 0) r = "Sheet";
            return r.Length > 31 ? r.Substring(0, 31) : r;
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '&') sb.Append("&amp;"); else if (c == '<') sb.Append("&lt;"); else if (c == '>') sb.Append("&gt;"); else if (c == '"') sb.Append("&quot;");
                else if (c < ' ' && c != '\t' && c != '\n' && c != '\r') continue;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string Col(int i) { string s = ""; i++; while (i > 0) { int m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }

        static void Put(ZipArchive z, string name, string content)
        {
            ZipArchiveEntry e = z.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }

        public byte[] ToBytes()
        {
            if (Sheets.Count == 0) Add("Sheet1");
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                for (int i = 0; i < Sheets.Count; i++) ct.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                ct.Append("</Types>");
                Put(z, "[Content_Types].xml", ct.ToString());
                Put(z, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                var rel = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (int i = 0; i < Sheets.Count; i++)
                {
                    wb.Append("<sheet name=\"" + Esc(Sheets[i].Name) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
                    rel.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
                }
                wb.Append("</sheets></workbook>");
                rel.Append("<Relationship Id=\"rId" + (Sheets.Count + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Put(z, "xl/workbook.xml", wb.ToString());
                Put(z, "xl/_rels/workbook.xml.rels", rel.ToString());
                Put(z, "xl/styles.xml", Styles());
                for (int i = 0; i < Sheets.Count; i++) Put(z, "xl/worksheets/sheet" + (i + 1) + ".xml", SheetXml(Sheets[i]));
            }
            return ms.ToArray();
        }

        static string Fill(string rgb) { return "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF" + rgb + "\"/><bgColor indexed=\"64\"/></patternFill></fill>"; }

        static string Styles()
        {
            // fonts: 0 normal, 1 tebal, 2 tebal putih, 3 tebal besar
            // fills: 0 none, 1 gray125, 2 emerald, 3 hijau muda, 4 kuning muda, 5 merah muda, 6 abu muda, 7 emerald muda
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
              "<fonts count=\"4\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"16\"/><name val=\"Calibri\"/></font></fonts>" +
              "<fills count=\"8\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" + Fill("059669") + Fill("D1FAE5") + Fill("FEF3C7") + Fill("FEE2E2") + Fill("E5E7EB") + Fill("A7F3D0") + "</fills>" +
              "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"><color rgb=\"FFD1D5DB\"/></left><right style=\"thin\"><color rgb=\"FFD1D5DB\"/></right><top style=\"thin\"><color rgb=\"FFD1D5DB\"/></top><bottom style=\"thin\"><color rgb=\"FFD1D5DB\"/></bottom><diagonal/></border></borders>" +
              "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
              "<cellXfs count=\"8\">" +
              "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\"/>" +
              "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
              "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"><alignment vertical=\"center\"/></xf>" +
              "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
              "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
              "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
              "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"6\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
              "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"7\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
              "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
        }

        static string SheetXml(XlsxSheet s)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (s.FreezeRow > 0)
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"" + s.FreezeRow + "\" topLeftCell=\"A" + (s.FreezeRow + 1) + "\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            if (s.Widths.Length > 0)
            {
                sb.Append("<cols>");
                for (int i = 0; i < s.Widths.Length; i++)
                    sb.Append("<col min=\"" + (i + 1) + "\" max=\"" + (i + 1) + "\" width=\"" + s.Widths[i].ToString(System.Globalization.CultureInfo.InvariantCulture) + "\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            for (int r = 0; r < s.Rows.Count; r++)
            {
                sb.Append("<row r=\"" + (r + 1) + "\">");
                for (int c = 0; c < s.Rows[r].Count; c++)
                {
                    XlsxCell cell = s.Rows[r][c];
                    if (cell == null) continue;
                    string rf = Col(c) + (r + 1);
                    if (cell.Num.HasValue) sb.Append("<c r=\"" + rf + "\" s=\"" + cell.Style + "\"><v>" + cell.Num.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "</v></c>");
                    else sb.Append("<c r=\"" + rf + "\" s=\"" + cell.Style + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Esc(cell.Text) + "</t></is></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }
    }
}

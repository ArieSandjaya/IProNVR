using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Tab Info Recorder: error log (loglist.cgi) dan info HDD (recordedtime.cgi).</summary>
    public class InfoTab : UserControl
    {
        DataGridView gridLog, gridHdd;
        TextBox txtRaw;
        Label lblStatus;

        public InfoTab()
        {
            Dock = DockStyle.Fill;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));

            var f1 = Ui.Flow();
            f1.Controls.Add(Ui.Btn("Ambil Error Log", delegate { LoadLog(); }));
            var f2 = Ui.Flow();
            f2.Controls.Add(Ui.Btn("Ambil Info HDD / Periode Rekaman", delegate { LoadHdd(); }));
            lblStatus = Ui.Lbl("");
            f2.Controls.Add(lblStatus);

            gridLog = MakeGrid(new string[] { "No", "Waktu (lokal NVR)", "Summertime", "Error", "Zona" });
            gridHdd = MakeGrid(new string[] { "Disk", "Kapasitas", "Sisa", "Rekaman tertua", "Rekaman terbaru" });

            txtRaw = new TextBox();
            txtRaw.Multiline = true;
            txtRaw.ReadOnly = true;
            txtRaw.ScrollBars = ScrollBars.Both;
            txtRaw.Dock = DockStyle.Fill;
            txtRaw.WordWrap = false;

            root.Controls.Add(f1, 0, 0);
            root.Controls.Add(f2, 1, 0);
            root.Controls.Add(gridLog, 0, 1);
            root.Controls.Add(gridHdd, 1, 1);
            root.Controls.Add(txtRaw, 0, 2);
            root.SetColumnSpan(txtRaw, 2);
            Controls.Add(root);
        }

        static DataGridView MakeGrid(string[] cols)
        {
            var g = new DataGridView();
            g.Dock = DockStyle.Fill;
            g.ReadOnly = true;
            g.AllowUserToAddRows = false;
            g.RowHeadersVisible = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            foreach (string c in cols) g.Columns.Add(c, c);
            return g;
        }

        void SetRaw(string text)
        {
            txtRaw.Text = text;
        }

        void LoadLog()
        {
            lblStatus.Text = "";
            Task.Run(() =>
            {
                try
                {
                    string text = Sessions.Util.Cmd("loglist.cgi", "KIND=ERR&HTML=as_errorlog.html");
                    Ui.Post(this, delegate
                    {
                        SetRaw(text);
                        gridLog.Rows.Clear();
                        int i0 = text.IndexOf("ERROR_LOG=", StringComparison.Ordinal);
                        string body = i0 >= 0 ? text.Substring(i0) : text;
                        MatchCollection m = Regex.Matches(body, "\"([^\"]*)\"");
                        for (int i = 0; i + 4 < m.Count; i += 5)
                        {
                            gridLog.Rows.Add(m[i].Groups[1].Value, m[i + 1].Groups[1].Value, m[i + 2].Groups[1].Value,
                                m[i + 3].Groups[1].Value, m[i + 4].Groups[1].Value);
                        }
                    });
                }
                catch (Exception ex) { Log.Write("Error log: " + ex.Message); }
            });
        }

        void LoadHdd()
        {
            Task.Run(() =>
            {
                try
                {
                    string text;
                    try
                    {
                        text = NvrSession.DownloadText("/cgi-bin/recordedtime.cgi", 15000);
                    }
                    catch (Exception)
                    {
                        // penulisan URL di dokumen spec: /cgi-bin//cgi-bin/recordedtime.cgi
                        text = NvrSession.DownloadText("/cgi-bin//cgi-bin/recordedtime.cgi", 15000);
                    }
                    Ui.Post(this, delegate
                    {
                        SetRaw(text);
                        gridHdd.Rows.Clear();
                        bool header = false;
                        foreach (string raw in text.Split('\n'))
                        {
                            string line = raw.Trim();
                            if (!header)
                            {
                                if (line.StartsWith("Disk,", StringComparison.OrdinalIgnoreCase)) header = true;
                                continue;
                            }
                            string[] p = line.Split(',');
                            if (p.Length < 3 || p[1].Trim().Length == 0) continue;
                            string oldest = p.Length > 5 ? (p[3].Trim() + " " + p[5].Trim()).Trim() : "";
                            string newest = p.Length > 8 ? (p[6].Trim() + " " + p[8].Trim()).Trim() : "";
                            gridHdd.Rows.Add(p[0].Trim(), p[1].Trim(), p[2].Trim(), oldest, newest);
                        }
                        lblStatus.Text = "(R) = HDD sedang merekam";
                    });
                }
                catch (Exception ex) { Log.Write("Info HDD: " + ex.Message); }
            });
        }
    }
}

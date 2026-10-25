using System;
using System.Drawing;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Halaman "HDD" panel bawah (kendali playback ala ASM300): dipakai bersama oleh Operation window dan Live window
    /// supaya tampilannya identik.</summary>
    public static class HddPage
    {
        public static Panel Build(ToolTip tips, Action<Action<PlaybackEngine>> each, Action<int> skipBy, Action<DateTime> playAt,
            Action exitPb, Action quickDl, out ComboBox cmbSkip, out DateTimePicker dtpGo)
        {
            DateTimePicker dtp;
            var h = new Panel();
            h.Dock = DockStyle.Fill;
            h.Tag = "nostyle";
            h.BackColor = Asm.Bg;
            Func<AsmIcon, string, string, int, int, Action, AsmButton> pb = delegate(AsmIcon ic, string label, string tip, int col, int row, Action act)
            {
                var b = new AsmButton();
                b.Icon = ic;
                b.SetBounds(Asm.S(10 + col * 100), Asm.S(row == 0 ? 11 : 104), Asm.S(80), Asm.S(56));
                b.Click += delegate { act(); };
                tips.SetToolTip(b, tip);
                h.Controls.Add(b);
                var l = new Label();
                l.Text = label; l.ForeColor = Asm.Label; l.Font = Asm.F(11f, false); l.Tag = "nostyle";
                l.TextAlign = ContentAlignment.MiddleCenter;
                l.SetBounds(b.Left - Asm.S(10), b.Bottom + Asm.S(1), Asm.S(100), Asm.S(20));
                h.Controls.Add(l);
                return b;
            };
            pb(AsmIcon.PrevChap, "PREV", "Mundur 5 menit", 0, 0, delegate { each(delegate(PlaybackEngine e) { e.Skip(-300); }); });
            pb(AsmIcon.FastRew, "REW", "Fast reverse", 1, 0, delegate { each(delegate(PlaybackEngine e) { e.FastReverse(); }); });
            pb(AsmIcon.Reverse, "REV PLAY", "Putar mundur", 2, 0, delegate { each(delegate(PlaybackEngine e) { e.Reverse(); }); });
            pb(AsmIcon.Play, "PLAY", "Putar maju", 3, 0, delegate { each(delegate(PlaybackEngine e) { e.Play(); }); });
            pb(AsmIcon.FastFwd, "FF", "Fast forward", 4, 0, delegate { each(delegate(PlaybackEngine e) { e.FastForward(); }); });
            pb(AsmIcon.NextChap, "NEXT", "Maju 5 menit", 5, 0, delegate { each(delegate(PlaybackEngine e) { e.Skip(300); }); });
            pb(AsmIcon.CurveBack, "REV SKIP", "Mundur sebesar SKIP TIME", 0, 1, delegate { skipBy(-1); });
            pb(AsmIcon.StepBack, "PREV IMAGE", "Frame sebelumnya", 1, 1, delegate { each(delegate(PlaybackEngine e) { e.Step(false); }); });
            pb(AsmIcon.Pause, "PAUSE", "Pause", 2, 1, delegate { each(delegate(PlaybackEngine e) { e.Pause(); }); });
            pb(AsmIcon.Stop, "STOP", "Stop", 3, 1, delegate { each(delegate(PlaybackEngine e) { e.Stop(); }); });
            pb(AsmIcon.StepFwd, "NEXT IMAGE", "Frame berikutnya", 4, 1, delegate { each(delegate(PlaybackEngine e) { e.Step(true); }); });
            pb(AsmIcon.CurveFwd, "SKIP", "Maju sebesar SKIP TIME", 5, 1, delegate { skipBy(1); });

            ComboBox skip;
            cmbSkip = skip = new ComboBox();
            cmbSkip.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSkip.FlatStyle = FlatStyle.Flat;
            cmbSkip.BackColor = Asm.Input; cmbSkip.ForeColor = Asm.Label;
            cmbSkip.Items.AddRange(new object[] { "10s", "30s", "1m", "5m", "10m", "30m", "1h" });
            cmbSkip.SelectedIndex = 1;
            cmbSkip.SetBounds(Asm.S(620), Asm.S(120), Asm.S(104), Asm.S(26));
            h.Controls.Add(cmbSkip);
            var lsk = new Label(); lsk.Text = "SKIP TIME"; lsk.ForeColor = Asm.Label; lsk.Font = Asm.F(11f, false); lsk.Tag = "nostyle";
            lsk.TextAlign = ContentAlignment.MiddleCenter; lsk.SetBounds(Asm.S(620), Asm.S(162), Asm.S(104), Asm.S(20)); h.Controls.Add(lsk);

            var spd1 = new AsmButton(); spd1.Icon = AsmIcon.Plus; spd1.SetBounds(Asm.S(750), Asm.S(11), Asm.S(86), Asm.S(50));
            spd1.Click += delegate { each(delegate(PlaybackEngine e) { e.SpeedUp(); }); }; tips.SetToolTip(spd1, "Percepat putaran (0.25x - 8x)"); h.Controls.Add(spd1);
            var sm = new AsmButton(); sm.Icon = AsmIcon.Minus; sm.SetBounds(Asm.S(750), Asm.S(78), Asm.S(86), Asm.S(50));
            sm.Click += delegate { each(delegate(PlaybackEngine e) { e.SpeedDown(); }); }; tips.SetToolTip(sm, "Perlambat putaran (0.25x - 8x)"); h.Controls.Add(sm);
            var lsp = new Label(); lsp.Text = "Speed"; lsp.ForeColor = Asm.Label; lsp.Font = Asm.F(11f, false); lsp.Tag = "nostyle";
            lsp.TextAlign = ContentAlignment.MiddleCenter; lsp.SetBounds(Asm.S(750), Asm.S(132), Asm.S(86), Asm.S(20)); h.Controls.Add(lsp);

            dtp = dtpGo = new DateTimePicker();
            dtpGo.Format = DateTimePickerFormat.Custom;
            dtpGo.CustomFormat = "yyyy/MM/dd  HH:mm:ss";
            dtpGo.SetBounds(Asm.S(870), Asm.S(8), Asm.S(190), Asm.S(26));
            dtpGo.Value = DateTime.Now.AddMinutes(-10);
            h.Controls.Add(dtpGo);
            var go = new AsmButton(); go.Icon = AsmIcon.Play; go.Label = "Go"; go.Font = Asm.F(10f, false);
            go.SetBounds(Asm.S(870), Asm.S(40), Asm.S(92), Asm.S(34)); go.Click += delegate { playAt(dtp.Value.ToUniversalTime()); }; h.Controls.Add(go);
            var lv = new AsmButton(); lv.Icon = AsmIcon.Live; lv.Label = "Live"; lv.Font = Asm.F(10f, false);
            lv.SetBounds(Asm.S(968), Asm.S(40), Asm.S(92), Asm.S(34)); lv.Click += delegate { exitPb(); }; h.Controls.Add(lv);
            var qd = new AsmButton(); qd.Icon = AsmIcon.Download; qd.Label = "Quick DL"; qd.Font = Asm.F(10f, false);
            qd.SetBounds(Asm.S(870), Asm.S(80), Asm.S(190), Asm.S(34)); qd.Click += delegate { quickDl(); }; h.Controls.Add(qd);
            return h;
        }
    }
}

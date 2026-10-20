using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Export / import database (NVR, kamera, folder, group, sequence, map, user, fisheye).
    /// Password NVR/kamera di database dilindungi DPAPI (terikat user Windows), jadi di file export diganti
    /// enkripsi passphrase (AES-256 + HMAC) atau dikosongkan; saat import dienkripsi ulang untuk PC ini.</summary>
    public static class DbTransfer
    {
        const string Prefix = "P1:";

        // ---------- kripto passphrase ----------

        static string Encrypt(string plain, string pass)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var salt = new byte[16];
            var iv = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) { rng.GetBytes(salt); rng.GetBytes(iv); }
            byte[] keys;
            using (var kdf = new Rfc2898DeriveBytes(pass, salt, 100000)) keys = kdf.GetBytes(64);
            var aesKey = new byte[32];
            var macKey = new byte[32];
            Array.Copy(keys, 0, aesKey, 0, 32);
            Array.Copy(keys, 32, macKey, 0, 32);
            byte[] ct;
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                byte[] data = Encoding.UTF8.GetBytes(plain);
                using (ICryptoTransform t = aes.CreateEncryptor()) ct = t.TransformFinalBlock(data, 0, data.Length);
            }
            var body = new byte[16 + 16 + ct.Length];
            Array.Copy(salt, 0, body, 0, 16);
            Array.Copy(iv, 0, body, 16, 16);
            Array.Copy(ct, 0, body, 32, ct.Length);
            byte[] mac;
            using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(body);
            var all = new byte[body.Length + 32];
            Array.Copy(body, 0, all, 0, body.Length);
            Array.Copy(mac, 0, all, body.Length, 32);
            return Prefix + Convert.ToBase64String(all);
        }

        /// <summary>Null bila passphrase salah / data rusak.</summary>
        static string Decrypt(string text, string pass)
        {
            try
            {
                byte[] all = Convert.FromBase64String(text.Substring(Prefix.Length));
                if (all.Length < 16 + 16 + 16 + 32) return null;
                var salt = new byte[16];
                var iv = new byte[16];
                Array.Copy(all, 0, salt, 0, 16);
                Array.Copy(all, 16, iv, 0, 16);
                byte[] keys;
                using (var kdf = new Rfc2898DeriveBytes(pass, salt, 100000)) keys = kdf.GetBytes(64);
                var aesKey = new byte[32];
                var macKey = new byte[32];
                Array.Copy(keys, 0, aesKey, 0, 32);
                Array.Copy(keys, 32, macKey, 0, 32);
                int bodyLen = all.Length - 32;
                var body = new byte[bodyLen];
                Array.Copy(all, 0, body, 0, bodyLen);
                byte[] mac;
                using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(body);
                int diff = 0;
                for (int i = 0; i < 32; i++) diff |= mac[i] ^ all[bodyLen + i];
                if (diff != 0) return null;
                var ct = new byte[bodyLen - 32];
                Array.Copy(all, 32, ct, 0, ct.Length);
                using (var aes = Aes.Create())
                {
                    aes.Key = aesKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (ICryptoTransform t = aes.CreateDecryptor())
                        return Encoding.UTF8.GetString(t.TransformFinalBlock(ct, 0, ct.Length));
                }
            }
            catch { return null; }
        }

        // ---------- util DB ----------

        static SQLiteConnection OpenFile(string path, bool readOnly)
        {
            var c = new SQLiteConnection("Data Source=\"" + path + "\";Version=3;Default Timeout=30;" + (readOnly ? "Read Only=True;" : ""));
            c.Open();
            return c;
        }

        static void VacuumInto(string dest)
        {
            if (File.Exists(dest)) File.Delete(dest);
            Db.Init();
            using (SQLiteConnection c = Db.Open())
            using (var cmd = new SQLiteCommand("VACUUM INTO '" + dest.Replace("'", "''") + "'", c))
                cmd.ExecuteNonQuery();
        }

        class SecretRow { public long Id; public string Pw = ""; public string CamPw = ""; }

        static List<SecretRow> ReadSecrets(SQLiteConnection c)
        {
            var l = new List<SecretRow>();
            using (var cmd = new SQLiteCommand("SELECT id, password_enc, cam_pass_enc FROM nvr", c))
            using (SQLiteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var s = new SecretRow();
                    s.Id = Convert.ToInt64(r.GetValue(0));
                    s.Pw = Convert.ToString(r.GetValue(1)) ?? "";
                    s.CamPw = Convert.ToString(r.GetValue(2)) ?? "";
                    l.Add(s);
                }
            }
            return l;
        }

        static void WriteSecrets(SQLiteConnection c, List<SecretRow> rows)
        {
            using (SQLiteTransaction t = c.BeginTransaction())
            {
                foreach (SecretRow s in rows)
                {
                    using (var cmd = new SQLiteCommand("UPDATE nvr SET password_enc = @p, cam_pass_enc = @c WHERE id = @id", c, t))
                    {
                        cmd.Parameters.AddWithValue("@p", s.Pw);
                        cmd.Parameters.AddWithValue("@c", s.CamPw);
                        cmd.Parameters.AddWithValue("@id", s.Id);
                        cmd.ExecuteNonQuery();
                    }
                }
                t.Commit();
            }
        }

        // ---------- export ----------

        /// <summary>Tulis salinan database ke dest. passphrase null = password NVR/kamera tidak ikut (dikosongkan).</summary>
        public static void Export(string dest, string passphrase)
        {
            string tmp = dest + ".tmp";
            VacuumInto(tmp);
            try
            {
                using (SQLiteConnection c = OpenFile(tmp, false))
                {
                    List<SecretRow> rows = ReadSecrets(c);
                    foreach (SecretRow s in rows)
                    {
                        string pw = Secret.Unprotect(s.Pw);
                        string cpw = Secret.Unprotect(s.CamPw);
                        s.Pw = passphrase == null ? "" : Encrypt(pw, passphrase);
                        s.CamPw = passphrase == null ? "" : Encrypt(cpw, passphrase);
                    }
                    WriteSecrets(c, rows);
                }
                SQLiteConnection.ClearAllPools();
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ---------- import ----------

        /// <summary>Periksa berkas: kembalikan null bila valid, selain itu pesan error. needsPass = berisi password terenkripsi passphrase.</summary>
        public static string Inspect(string src, out bool needsPass)
        {
            needsPass = false;
            try
            {
                using (SQLiteConnection c = OpenFile(src, true))
                {
                    using (var cmd = new SQLiteCommand("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('nvr','camera')", c))
                    {
                        if (Convert.ToInt32(cmd.ExecuteScalar()) < 2) return "Bukan berkas database i-PRO NVR Tool (tabel nvr/camera tidak ada).";
                    }
                    foreach (SecretRow s in ReadSecrets(c))
                        if (s.Pw.StartsWith(Prefix, StringComparison.Ordinal) || s.CamPw.StartsWith(Prefix, StringComparison.Ordinal)) needsPass = true;
                }
                return null;
            }
            catch (Exception ex) { return "Berkas tidak bisa dibaca: " + ex.Message; }
        }

        /// <summary>Ganti seluruh database dengan isi src. Database lama dicadangkan otomatis. Mengembalikan lokasi cadangan.</summary>
        public static string Import(string src, string passphrase)
        {
            string live = Db.FilePath;
            string dir = Path.GetDirectoryName(live);
            string tmp = Path.Combine(dir, "import_" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(src, tmp, true);
            try
            {
                using (SQLiteConnection c = OpenFile(tmp, false))
                {
                    List<SecretRow> rows = ReadSecrets(c);
                    foreach (SecretRow s in rows)
                    {
                        s.Pw = ToLocal(s.Pw, passphrase);
                        s.CamPw = ToLocal(s.CamPw, passphrase);
                    }
                    WriteSecrets(c, rows);
                }
                SQLiteConnection.ClearAllPools();

                string backup = Path.Combine(dir, "nvr_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db");
                try { VacuumInto(backup); } catch { backup = ""; }
                SQLiteConnection.ClearAllPools();
                foreach (string ext in new string[] { "-wal", "-shm", "-journal" })
                    try { if (File.Exists(live + ext)) File.Delete(live + ext); } catch { }
                // ganti file database sekaligus (File.Replace); salinan setengah jadi tidak boleh menimpa database aktif
                if (File.Exists(live)) File.Replace(tmp, live, null);
                else File.Move(tmp, live);
                Db.ResetInit();
                Db.Init(); // migrasi skema bila berkas berasal dari versi lama
                Log.Write("Import database dari " + src + (backup.Length > 0 ? " (cadangan: " + backup + ")" : ""));
                return backup;
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                try { File.Delete(tmp); } catch { }
            }
        }

        /// <summary>Nilai password di berkas -> nilai untuk PC ini (DPAPI). Melempar bila passphrase salah.</summary>
        static string ToLocal(string v, string pass)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (!v.StartsWith(Prefix, StringComparison.Ordinal)) return v; // sudah DPAPI (export dari PC/user yang sama)
            if (pass == null) throw new InvalidOperationException("Passphrase diperlukan.");
            string plain = Decrypt(v, pass);
            if (plain == null) throw new InvalidOperationException("Passphrase salah atau berkas rusak.");
            return Secret.Protect(plain);
        }
    }

    /// <summary>Dialog passphrase (karakter disamarkan).</summary>
    public class PassphraseDialog : Form
    {
        readonly TextBox t1 = new TextBox();
        readonly TextBox t2 = new TextBox();
        readonly bool confirm;
        public string Value = "";

        public PassphraseDialog(string title, string message, bool confirm)
        {
            this.confirm = confirm;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(Asm.S(420), Asm.S(confirm ? 210 : 160));
            BackColor = Asm.Panel;
            var l = new Label(); l.Text = message; l.SetBounds(Asm.S(14), Asm.S(12), Asm.S(392), Asm.S(50)); l.ForeColor = Asm.Text;
            t1.SetBounds(Asm.S(14), Asm.S(68), Asm.S(392), Asm.S(24)); t1.UseSystemPasswordChar = true;
            Controls.Add(l); Controls.Add(t1);
            int y = 100;
            if (confirm)
            {
                var l2 = new Label(); l2.Text = "Ulangi passphrase:"; l2.SetBounds(Asm.S(14), Asm.S(98), Asm.S(392), Asm.S(20)); l2.ForeColor = Asm.Muted;
                t2.SetBounds(Asm.S(14), Asm.S(120), Asm.S(392), Asm.S(24)); t2.UseSystemPasswordChar = true;
                Controls.Add(l2); Controls.Add(t2);
                y = 156;
            }
            var ok = new Button(); ok.Text = "OK"; ok.SetBounds(Asm.S(14), Asm.S(y), Asm.S(100), Asm.S(30));
            ok.Click += delegate
            {
                if (t1.Text.Length < 6) { MessageBox.Show("Passphrase minimal 6 karakter."); return; }
                if (this.confirm && t1.Text != t2.Text) { MessageBox.Show("Passphrase tidak sama."); return; }
                Value = t1.Text;
                DialogResult = DialogResult.OK;
            };
            var cancel = new Button(); cancel.Text = "Batal"; cancel.SetBounds(Asm.S(124), Asm.S(y), Asm.S(100), Asm.S(30));
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            Theme.ApplyTo(this, true);
        }

        public static string Ask(IWin32Window owner, string title, string message, bool confirm)
        {
            using (var d = new PassphraseDialog(title, message, confirm))
                return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Ikon aplikasi (6296844.ico, tertanam di exe) untuk semua jendela: bilah judul, taskbar, Alt+Tab.
    /// Install() dipasang sekali; form apa pun yang tampil dan belum punya ikon ini otomatis diberi.</summary>
    public static class AppIcon
    {
        static Icon icon;
        static bool loaded;
        static readonly List<WeakReference> done = new List<WeakReference>();

        public static Icon Get()
        {
            if (loaded) return icon;
            loaded = true;
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("IproNvrTool.app.ico"))
                    if (st != null) icon = new Icon(st);
            }
            catch { }
            if (icon == null)
            {
                try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            }
            return icon;
        }

        public static void Install()
        {
            if (Get() == null) return;
            Application.Idle += delegate
            {
                try
                {
                    foreach (Form f in Application.OpenForms)
                    {
                        if (f.IsDisposed || f.Icon == icon || IsDone(f)) continue;
                        f.Icon = icon;
                        done.Add(new WeakReference(f));
                    }
                    done.RemoveAll(delegate(WeakReference w) { return !w.IsAlive; });
                }
                catch { }
            };
        }

        static bool IsDone(Form f)
        {
            foreach (WeakReference w in done) if (w.Target == f) return true;
            return false;
        }
    }
}

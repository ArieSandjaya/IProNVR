using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IproNvrTool
{
    /// <summary>Penghalusan tampilan: double buffer + tema "Explorer" untuk TreeView/ListView, double buffer untuk panel/list.
    /// Dipasang sekali pada form; control yang dibuat belakangan ikut otomatis (ControlAdded).</summary>
    public static class UiSmooth
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        const int TVM_SETEXTENDEDSTYLE = 0x1100 + 44, TVS_EX_DOUBLEBUFFER = 0x0004;
        const int LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1000 + 54, LVS_EX_DOUBLEBUFFER = 0x00010000;

        static readonly PropertyInfo DoubleBufferedProp =
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Attach(Control root)
        {
            Apply(root);
            root.ControlAdded += OnAdded;
        }

        static void OnAdded(object sender, ControlEventArgs e)
        {
            Attach(e.Control);
        }

        static void Apply(Control c)
        {
            try
            {
                if (c is LiveTile) return;                 // tile video diatur sendiri
                if (c is Panel || c is TreeView || c is ListView || c is ListBox || c is TabControl || c is FlowLayoutPanel || c is TableLayoutPanel)
                {
                    if (DoubleBufferedProp != null) DoubleBufferedProp.SetValue(c, true, null);
                }
                if (c is TreeView || c is ListView)
                {
                    Action native = delegate
                    {
                        try
                        {
                            SetWindowTheme(c.Handle, "explorer", null);
                            if (c is TreeView) SendMessage(c.Handle, TVM_SETEXTENDEDSTYLE, (IntPtr)TVS_EX_DOUBLEBUFFER, (IntPtr)TVS_EX_DOUBLEBUFFER);
                            else SendMessage(c.Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)LVS_EX_DOUBLEBUFFER);
                        }
                        catch { }                          // bukan Windows / uxtheme tidak ada
                    };
                    if (c.IsHandleCreated) native(); else c.HandleCreated += delegate { native(); };
                }
            }
            catch { }
            foreach (Control ch in c.Controls) Attach(ch);
        }
    }
}

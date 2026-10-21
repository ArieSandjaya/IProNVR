using System.IO;
using System.Reflection;

namespace IproNvrServer
{
    /// <summary>Halaman admin web (admin.html tertanam di exe).</summary>
    public static class AdminPage
    {
        static string html;
        public static string Html
        {
            get
            {
                if (html == null)
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IproNvrServer.admin.html"))
                    using (var r = new StreamReader(s)) html = r.ReadToEnd();
                }
                return html;
            }
        }
    }
}

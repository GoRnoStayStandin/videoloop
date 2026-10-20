using System;
using System.Threading;
using System.Windows.Forms;

namespace VideoDisplayChecker
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var instance = new Mutex(true, @"Local\VideoDisplayChecker.Main", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Программа уже открыта. Переключитесь в неё через Alt+Tab.", "VideoLoop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                }
                finally { instance.ReleaseMutex(); }
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Diagnostics
{
    internal static void Run()
    {
        string artifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        string exe = Path.Combine(artifacts, "VideoDisplayChecker", "VideoDisplayChecker.exe");
        using (Process process = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false }))
        {
            try
            {
                Tests.Assert(process.WaitForInputIdle(10000), "release UI is ready");
                Thread.Sleep(1500);
                process.Refresh();
                Tests.Assert(!process.HasExited && process.MainWindowTitle == "VideoLoop", "release main window");
                IntPtr window = process.MainWindowHandle;
                RectangleNative rect;
                Tests.Assert(GetWindowRect(window, out rect), "window dimensions");
                using (var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        IntPtr dc = graphics.GetHdc();
                        try { Tests.Assert(PrintWindow(window, dc, 0), "render window"); }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    bitmap.Save(Path.Combine(artifacts, "app-preview.png"), ImageFormat.Png);
                }
                TimeSpan before = process.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                Thread.Sleep(10000);
                process.Refresh();
                double cpu = (process.TotalProcessorTime - before).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
                Console.WriteLine("Release EXE size: {0:N1} KiB", new FileInfo(exe).Length / 1024.0);
                Console.WriteLine("Package size: {0:N1} KiB", new FileInfo(Path.Combine(artifacts, "VideoDisplayChecker-win-x86.zip")).Length / 1024.0);
                Console.WriteLine("Idle working set: {0:N1} MiB", process.WorkingSet64 / 1048576.0);
                Console.WriteLine("Idle private memory: {0:N1} MiB", process.PrivateMemorySize64 / 1048576.0);
                Console.WriteLine("Idle CPU over 10 seconds (one-core basis): {0:N3}%", cpu);
                Tests.Assert(process.CloseMainWindow() && process.WaitForExit(5000), "release UI closes normally");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectangleNative { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RectangleNative rectangle);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}

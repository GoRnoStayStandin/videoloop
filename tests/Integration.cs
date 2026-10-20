using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VideoDisplayChecker;

internal static class Integration
{
    internal static void Run(string vlcPath)
    {
        Tests.Assert(File.Exists(vlcPath), "VLC executable exists");
        string first = Path.Combine(Tests.Scratch, "Первый #1 100% & плюс+.mp4");
        string second = Path.Combine(Tests.Scratch, "Второй ролик.mp4");
        MakeVideo(first, 5, "testsrc2=size=320x180:rate=10");
        MakeVideo(second, 7, "color=c=blue:size=320x180:rate=10");

        var sessions = new List<VlcSession>();
        object gate = new object();
        var options = new MonitorOptions { ProbeMilliseconds = 750, ConfirmationMilliseconds = 1300, RetryMilliseconds = 500,
            MaxRetryMilliseconds = 2000, StartupMilliseconds = 30000, StartupPollMilliseconds = 300 };
        var watchdog = new Watchdog(r =>
        {
            var session = new VlcSession(r, 2000);
            lock (gate) sessions.Add(session);
            return session;
        }, options);
        int healthy = 0;
        watchdog.Updated += (s, e) =>
        {
            Console.WriteLine("VLC: " + e.Message.Replace("\r\n", " / "));
            if (e.Healthy) Interlocked.Increment(ref healthy);
        };

        Process unrelated = null;
        Task<RunResult> task = null;
        try
        {
            unrelated = Process.Start(new ProcessStartInfo
            {
                FileName = vlcPath, UseShellExecute = false, CreateNoWindow = true,
                Arguments = "--ignore-config --intf=dummy --no-one-instance --no-media-library"
            });
            task = watchdog.Start(new PlaybackRequest(vlcPath, new[] { first, second }, 900));
            Tests.Eventually(() => Volatile.Read(ref healthy) >= 1 || task.IsCompleted, 40000, "initial VLC startup");
            Tests.Assert(!task.IsCompleted, "startup must succeed: " + (task.IsCompleted ? task.Result.Error : ""));
            VlcSession active;
            lock (gate) active = sessions[sessions.Count - 1];
            Console.WriteLine("VLC version: " + active.Read(CancellationToken.None).Version);
            PlayerStatus initial = active.Read(CancellationToken.None);
            Tests.Assert(initial.Loop && !initial.Repeat && !initial.Random, "ordered whole-playlist loop");
            Tests.Assert(initial.Fullscreen, "VLC starts in fullscreen");
            Tests.Assert(unrelated != null && !unrelated.HasExited, "independent VLC is alive");

            var seen = new HashSet<string>();
            int transitions = 0;
            string previous = null;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 16000)
            {
                var sample = active.Read(CancellationToken.None);
                if (sample.State == "playing" && sample.ItemId != "-1")
                {
                    seen.Add(sample.Name);
                    if (previous != null && previous != sample.ItemId) transitions++;
                    previous = sample.ItemId;
                }
                Thread.Sleep(400);
            }
            Tests.Assert(seen.Contains(Path.GetFileName(first)) && seen.Contains(Path.GetFileName(second)) && transitions >= 2,
                "both Unicode paths play and playlist wraps: " + string.Join(", ", seen));

            int count;
            lock (gate) count = sessions.Count;
            int checkpoint = Volatile.Read(ref healthy);
            active.Command("pl_forcepause", CancellationToken.None);
            Tests.Eventually(() => Volatile.Read(ref healthy) > checkpoint && active.Read(CancellationToken.None).State == "playing", 15000, "pause resumed");
            lock (gate) Tests.Assert(sessions.Count == count, "pause does not restart the process");

            checkpoint = Volatile.Read(ref healthy);
            active.Command("pl_stop", CancellationToken.None);
            Tests.Eventually(() => Volatile.Read(ref healthy) > checkpoint && active.Read(CancellationToken.None).State == "playing", 15000, "stopped playback resumed");

            // Leave fullscreen and minimize. A subsequent watchdog cycle must not restore the window.
            if (active.Read(CancellationToken.None).Fullscreen) active.Command("fullscreen", CancellationToken.None);
            Thread.Sleep(700);
            using (var player = Process.GetProcessById(active.ProcessId))
            {
                player.Refresh();
                IntPtr window = player.MainWindowHandle;
                Tests.Assert(window != IntPtr.Zero, "VLC main window available");
                ShowWindow(window, 6); // SW_MINIMIZE
                Tests.Eventually(() => IsIconic(window), 2000, "window actually minimizes");
                int baseline = Volatile.Read(ref healthy);
                Tests.Eventually(() => Volatile.Read(ref healthy) > baseline, 10000, "healthy while minimized");
                Tests.Assert(IsIconic(window), "watchdog leaves VLC minimized");
            }

            using (var player = Process.GetProcessById(active.ProcessId)) { player.Kill(); player.WaitForExit(5000); }
            Tests.Eventually(() => { lock (gate) return sessions.Count > count; }, 15000, "crash creates a replacement");
            VlcSession replacement;
            lock (gate) replacement = sessions[sessions.Count - 1];
            Tests.Eventually(() =>
            {
                try { return replacement.Read(CancellationToken.None).State == "playing"; }
                catch (System.Net.WebException) { return false; }
            }, 15000, "replacement plays");

            watchdog.RequestStop();
            Tests.Assert(task.Wait(12000), "STOP completes");
            Tests.Assert(task.Result.Error == null, "clean stop: " + task.Result.Error);
            Tests.Assert(!unrelated.HasExited, "STOP preserves unrelated VLC");
            int afterStop;
            lock (gate) afterStop = sessions.Count;
            Thread.Sleep(2000);
            lock (gate) Tests.Assert(sessions.Count == afterStop, "no resurrection after STOP");

            // A single file must wrap too. Reuse the watchdog to check a second START/STOP cycle.
            task = watchdog.Start(new PlaybackRequest(vlcPath, new[] { first }, 900));
            VlcSession single = null;
            Tests.Eventually(() => { lock (gate) { if (sessions.Count <= afterStop) return false; single = sessions[sessions.Count - 1]; return true; } }, 10000, "single-file launch");
            double lastPosition = -1;
            bool wrapped = false;
            clock.Restart();
            while (clock.ElapsedMilliseconds < 15000 && !wrapped)
            {
                try
                {
                    var sample = single.Read(CancellationToken.None);
                    if (sample.State == "playing")
                    {
                        if (lastPosition > 0.65 && sample.Position < 0.3) wrapped = true;
                        lastPosition = sample.Position;
                    }
                }
                catch (System.Net.WebException) { }
                Thread.Sleep(250);
            }
            Tests.Assert(wrapped, "single file loops");
            watchdog.RequestStop();
            Tests.Assert(task.Wait(12000) && task.Result.Error == null, "second STOP succeeds");
        }
        finally
        {
            watchdog.RequestStop();
            if (task != null) task.Wait(12000);
            if (unrelated != null)
            {
                if (!unrelated.HasExited) { unrelated.Kill(); unrelated.WaitForExit(5000); }
                unrelated.Dispose();
            }
        }
    }

    private static void MakeVideo(string path, int seconds, string source)
    {
        using (var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg", UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            Arguments = "-hide_banner -loglevel error -f lavfi -i " + VlcSession.Quote(source)
                + " -t " + seconds + " -an -c:v mpeg4 -q:v 6 -y " + VlcSession.Quote(path)
        }))
        {
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Tests.Assert(process.ExitCode == 0, "ffmpeg: " + error);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
}

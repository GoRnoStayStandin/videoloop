using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using VideoDisplayChecker;

internal static class Tests
{
    private static int passed;
    internal static string Scratch;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Scratch = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        try
        {
            if (args.Length > 0 && args[0] == "--measure")
                Check("Release executable smoke test and idle resources", Diagnostics.Run);
            else if (args.Length > 0 && args[0] == "--integration")
                Check("Real VLC integration", () => Integration.Run(args[1]));
            else
            {
                Check("VLC XML parsing and hostile XML rejection", ParseStatus);
                Check("Loop boundaries and short clips do not look frozen", Progress);
                Check("Playlist URI escaping and Windows argument quoting", PlaylistEncoding);
                Check("Atomic settings, normalization and backup recovery", Settings);
                Check("Resume + ordered whole-playlist loop", Resume);
                Check("Pause between samples resumes without a restart", PauseDuringProbe);
                Check("Crash recovery closes only the owned session", CrashRecovery);
                Check("STOP during process creation cannot restart VLC", StopDuringCreation);
                Check("STOP aborts an in-flight request", StopDuringRequest);
                Check("Frozen playback reaches a bounded recovery limit", FrozenPlayback);
                Check("Missing videos fail before launching VLC", MissingFile);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Check("UI removal, locking, STOP, clear and persistence", () => Ui(false));
                Check("Closing the UI cancels playback and waits for cleanup", () => Ui(true));
            }
            Console.WriteLine("PASS: " + passed + " checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(Scratch, true); }
            catch (IOException) { }
        }
    }

    private static void Check(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("OK: " + name);
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("ASSERT: " + message);
    }

    internal static void Eventually(Func<bool> condition, int timeout, string message)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeout)
        {
            if (condition()) return;
            Thread.Sleep(25);
        }
        throw new Exception("TIMEOUT: " + message);
    }

    private static PlayerStatus Parse(string xml)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml))) return PlayerStatus.Parse(stream);
    }

    private static void ParseStatus()
    {
        var value = Parse("<root><state>playing</state><version>3.0.21</version><currentplid>7</currentplid><time>4</time><position>0.25</position><loop>true</loop><repeat>false</repeat><random>false</random><information><category><info name='filename'>Ролик &amp; тест.mp4</info></category></information><stats><decodedvideo>120</decodedvideo><playedabuffers>51</playedabuffers></stats></root>");
        Assert(value.Name == "Ролик & тест.mp4" && value.DecodedVideo == 120 && value.Position == 0.25 && value.Loop, "XML fields");
        bool rejected = false;
        try { Parse("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><root><state>&x;</state></root>"); }
        catch (XmlException) { rejected = true; }
        Assert(rejected, "DTD must be rejected");
        rejected = false;
        try { Parse("<html><body>Error</body></html>"); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "HTML is not a valid VLC status");
    }

    internal static PlayerStatus Playing(double time)
    {
        return new PlayerStatus { State = "playing", Version = "3.0.21", ItemId = "1", Name = "test.mp4", Time = time,
            Position = time / 100, DecodedVideo = (long)(time * 10), PlayedAudio = -1, Loop = true };
    }

    private static void Progress()
    {
        Assert(!PlayerStatus.HasProgress(Playing(5), Playing(5)), "unchanged playback is suspect");
        Assert(PlayerStatus.HasProgress(Playing(59), Playing(1)), "same-item loop is progress");
        var next = Playing(5); next.ItemId = "2";
        Assert(PlayerStatus.HasProgress(Playing(5), next), "next item with identical time");
        var frames = Playing(0); frames.DecodedVideo = 7;
        Assert(PlayerStatus.HasProgress(Playing(0), frames), "short clip frames");
        var paused = Playing(8); paused.State = "paused";
        Assert(!PlayerStatus.HasProgress(Playing(7), paused), "pause cannot be healthy");
    }

    private static void PlaylistEncoding()
    {
        string path = Path.Combine(Scratch, "Видео #1 100% & плюс+.mp4");
        string playlist = VlcSession.MakePlaylist(new[] { path });
        string uri = playlist.Split('\n')[2];
        Assert(new Uri(uri).LocalPath == path, "URI round trip with Cyrillic and reserved characters");
        Assert(uri.Contains("%23") && uri.Contains("%25"), "escape fragment and percent");
        Assert(VlcSession.Quote(@"C:\folder with spaces\") == "\"C:\\folder with spaces\\\\\"", "trailing backslash quoting");
        Assert(VlcSession.Quote("a\"b") == "\"a\\\"b\"", "embedded quote escaping");
        string[] files = { path };
        var request = new PlaybackRequest("vlc.exe", files, 60000);
        files[0] = "changed";
        Assert(request.Videos[0] == path, "playlist snapshot is independent");
    }

    private static void Settings()
    {
        string dir = Path.Combine(Scratch, "settings");
        var store = new SettingsStore(dir);
        var settings = new AppSettings { CheckMinutes = 99, VlcPath = "vlc" };
        string video = Path.Combine(Scratch, "Тест.mp4");
        settings.Videos.Add(video);
        settings.Videos.Add(video.ToUpperInvariant());
        settings.Videos.Add("");
        store.Save(settings);
        string warning;
        var loaded = store.Load(out warning);
        Assert(loaded.CheckMinutes == 1 && loaded.Videos.Count == 1 && warning == null, "normalize invalid settings");
        loaded.CheckMinutes = 5;
        store.Save(loaded);
        Assert(store.Load(out warning).CheckMinutes == 5, "replace primary settings");
        File.WriteAllText(Path.Combine(dir, "settings.xml"), "broken XML");
        loaded = store.Load(out warning);
        Assert(loaded.CheckMinutes == 1 && loaded.Videos.Count == 1 && warning != null, "recover previous settings");
    }

    internal static MonitorOptions FastOptions()
    {
        return new MonitorOptions { StartupMilliseconds = 1000, StartupPollMilliseconds = 5, ProbeMilliseconds = 10,
            ConfirmationMilliseconds = 15, RetryMilliseconds = 20, MaxRetryMilliseconds = 80,
            RestartWindowMilliseconds = 10000, MaxRestarts = 2 };
    }

    private static PlaybackRequest FakeRequest(int interval)
    {
        string vlc = Path.Combine(Scratch, "vlc.exe");
        string video = Path.Combine(Scratch, "test.mp4");
        File.WriteAllText(vlc, "test fixture");
        File.WriteAllText(video, "test fixture");
        return new PlaybackRequest(vlc, new[] { video }, interval);
    }

    private static RunResult Stop(Watchdog watchdog, Task<RunResult> task)
    {
        watchdog.RequestStop();
        Assert(task.Wait(3000), "worker stops promptly");
        Assert(task.Result.Error == null, "clean STOP: " + task.Result.Error);
        return task.Result;
    }

    private static void Resume()
    {
        var session = new FakeSession { State = "paused", Loop = false, Repeat = true, Random = true };
        var watchdog = new Watchdog(r => session, FastOptions());
        int healthy = 0;
        watchdog.Updated += (s, e) => { if (e.Healthy) Interlocked.Increment(ref healthy); };
        Task<RunResult> task = watchdog.Start(FakeRequest(15));
        try
        {
            Eventually(() => Volatile.Read(ref healthy) >= 2, 2000, "resume playback");
            Stop(watchdog, task);
            Assert(session.Commands.Count(c => c == "pl_forceresume") == 1, "idempotent resume, not pause toggle");
            Assert(session.Commands.Count(c => c == "pl_loop") == 1 && !session.Repeat && !session.Random, "loop policy");
            Assert(session.Disposed, "owned session disposed");
        }
        finally { watchdog.RequestStop(); task.Wait(3000); }
    }

    private static void CrashRecovery()
    {
        var first = new FakeSession();
        var second = new FakeSession();
        var unrelated = new FakeSession();
        int created = 0;
        int healthy = 0;
        var watchdog = new Watchdog(r => Interlocked.Increment(ref created) == 1 ? first : second, FastOptions());
        watchdog.Updated += (s, e) => { if (e.Healthy) Interlocked.Increment(ref healthy); };
        Task<RunResult> task = watchdog.Start(FakeRequest(50));
        try
        {
            Eventually(() => Volatile.Read(ref healthy) >= 1, 2000, "initial healthy state");
            first.Exited = true;
            Eventually(() => Volatile.Read(ref created) == 2 && Volatile.Read(ref healthy) >= 2, 2000, "crash recovery");
            Stop(watchdog, task);
            Assert(first.Disposed && second.Disposed && !unrelated.Disposed, "only owned sessions cleaned up");
            Thread.Sleep(80);
            Assert(created == 2, "no restart after STOP");
        }
        finally { watchdog.RequestStop(); task.Wait(3000); }
    }

    private static void PauseDuringProbe()
    {
        var session = new FakeSession { PauseOnRead = 4 };
        int created = 0;
        int healthy = 0;
        var watchdog = new Watchdog(r => { Interlocked.Increment(ref created); return session; }, FastOptions());
        watchdog.Updated += (s, e) => { if (e.Healthy) Interlocked.Increment(ref healthy); };
        Task<RunResult> task = watchdog.Start(FakeRequest(100));
        try
        {
            Eventually(() => Volatile.Read(ref healthy) >= 1, 2000, "resume during probe");
            Stop(watchdog, task);
            Assert(created == 1 && session.Commands.Contains("pl_forceresume"), "pause is not a frozen process");
        }
        finally { watchdog.RequestStop(); task.Wait(3000); }
    }

    private static void StopDuringCreation()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            var session = new FakeSession();
            int created = 0;
            var watchdog = new Watchdog(r => { Interlocked.Increment(ref created); entered.Set(); release.WaitOne(2000); return session; }, FastOptions());
            Task<RunResult> task = watchdog.Start(FakeRequest(50));
            try
            {
                Assert(entered.WaitOne(2000), "factory entered");
                watchdog.RequestStop();
                release.Set();
                Stop(watchdog, task);
                Assert(session.Disposed && session.Reads == 0 && created == 1, "cancelled launch cleaned up before any request");
            }
            finally { release.Set(); watchdog.RequestStop(); task.Wait(3000); }
        }
    }

    private static void StopDuringRequest()
    {
        var session = new FakeSession { BlockRead = true };
        var watchdog = new Watchdog(r => session, FastOptions());
        Task<RunResult> task = watchdog.Start(FakeRequest(50));
        try
        {
            Eventually(() => Volatile.Read(ref session.Reads) > 0, 2000, "HTTP request entered");
            Stop(watchdog, task);
            Assert(session.Disposed, "in-flight cancellation cleanup");
        }
        finally { watchdog.RequestStop(); task.Wait(3000); }
    }

    private static void FrozenPlayback()
    {
        var sessions = new List<FakeSession>();
        var watchdog = new Watchdog(r => { var s = new FakeSession { FreezeAfterRead = 3 }; sessions.Add(s); return s; }, FastOptions());
        Task<RunResult> task = watchdog.Start(FakeRequest(10));
        try
        {
            Assert(task.Wait(5000), "bounded retry completion");
            Assert(task.Result.Error != null && sessions.Count == 3 && sessions.All(s => s.Disposed), "two restarts then explicit failure");
        }
        finally { watchdog.RequestStop(); task.Wait(3000); }
    }

    private static void MissingFile()
    {
        int created = 0;
        var watchdog = new Watchdog(r => { created++; return new FakeSession(); }, FastOptions());
        var request = FakeRequest(10);
        var missing = new PlaybackRequest(request.VlcPath, new[] { Path.Combine(Scratch, "missing.mp4") }, 10);
        bool rejected = false;
        try { watchdog.Start(missing); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && created == 0, "missing input rejected before process start");
    }

    private static async Task WaitUi(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3000)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new Exception("UI TIMEOUT: " + message);
    }

    private static void Ui(bool closeDuringPlayback)
    {
        var request = FakeRequest(60000);
        var store = new SettingsStore(Path.Combine(Scratch, "ui-" + Guid.NewGuid().ToString("N")));
        store.Save(new AppSettings { VlcPath = request.VlcPath, Videos = new List<string> { request.Videos[0], request.Videos[0] + ".second" } });
        File.WriteAllText(request.Videos[0] + ".second", "fixture");
        var session = new FakeSession();
        var watchdog = new Watchdog(r => session, FastOptions());
        Exception failure = null;
        using (var form = new MainForm(store, watchdog))
        {
            form.Shown += async delegate
            {
                try
                {
                    var grid = (DataGridView)form.Controls.Find("Playlist", true)[0];
                    var start = (Button)form.Controls.Find("StartPlayback", true)[0];
                    var stop = (Button)form.Controls.Find("StopPlayback", true)[0];
                    var clear = (Button)form.Controls.Find("ClearPlaylist", true)[0];
                    Assert(grid.Rows.Count == 2 && start.Enabled && !stop.Enabled, "initial UI");
                    typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(grid, new object[] { new DataGridViewCellEventArgs(1, 0) });
                    Assert(grid.Rows.Count == 1 && File.Exists(request.Videos[0]), "per-item deletion leaves video file");
                    start.PerformClick();
                    await WaitUi(() => session.Reads > 2, "started");
                    Assert(!start.Enabled && stop.Enabled && !grid.Enabled && !clear.Enabled, "queue is locked");
                    if (closeDuringPlayback) { form.Close(); return; }
                    stop.PerformClick();
                    await WaitUi(() => start.Enabled, "stopped");
                    Assert(session.Disposed && grid.Enabled && !stop.Enabled, "STOP cleans up and unlocks UI");
                    clear.PerformClick();
                    Assert(grid.Rows.Count == 0 && !start.Enabled, "clear disables START");
                    string warning;
                    Assert(store.Load(out warning).Videos.Count == 0, "clear persisted");
                }
                catch (Exception ex) { failure = ex; }
                form.Close();
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
        Assert(session.Disposed, "form closing finishes cleanup");
    }

    internal sealed class FakeSession : IPlayerSession
    {
        public string State = "playing";
        public bool Loop = true;
        public bool Repeat;
        public bool Random;
        public int FreezeAfterRead;
        public bool BlockRead;
        public int PauseOnRead;
        public volatile bool Exited;
        public volatile bool Disposed;
        public int Reads;
        public readonly List<string> Commands = new List<string>();
        public bool HasExited { get { return Exited; } }

        public PlayerStatus Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int read = Interlocked.Increment(ref Reads);
            if (read == PauseOnRead) State = "paused";
            if (BlockRead) { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            var result = Playing(FreezeAfterRead > 0 && read >= FreezeAfterRead ? 0 : read);
            result.State = State; result.Loop = Loop; result.Repeat = Repeat; result.Random = Random;
            return result;
        }

        public PlayerStatus Command(string command, CancellationToken token)
        {
            Commands.Add(command);
            if (command == "pl_forceresume" || command == "pl_play") State = "playing";
            if (command == "pl_loop") Loop = !Loop;
            if (command == "pl_repeat") Repeat = !Repeat;
            if (command == "pl_random") Random = !Random;
            return Read(token);
        }

        public bool Wait(int milliseconds, CancellationToken token)
        {
            if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
            return Exited;
        }

        public void Dispose() { Disposed = true; }
    }
}

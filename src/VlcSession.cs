using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VideoDisplayChecker
{
    internal interface IPlayerSession : IDisposable
    {
        bool HasExited { get; }
        PlayerStatus Read(CancellationToken token);
        PlayerStatus Command(string command, CancellationToken token);
        bool Wait(int milliseconds, CancellationToken token);
    }

    internal sealed class PlaybackRequest
    {
        public readonly string VlcPath;
        public readonly string[] Videos;
        public readonly int IntervalMilliseconds;

        public PlaybackRequest(string vlcPath, string[] videos, int intervalMilliseconds)
        {
            VlcPath = vlcPath;
            Videos = (string[])videos.Clone();
            IntervalMilliseconds = intervalMilliseconds;
        }

        public void Validate()
        {
            if (!File.Exists(VlcPath) || !string.Equals(Path.GetFileName(VlcPath), "vlc.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Укажите существующий файл vlc.exe.");
            if (Videos.Length == 0) throw new InvalidOperationException("Добавьте хотя бы одно видео.");
            if (IntervalMilliseconds < 1) throw new InvalidOperationException("Некорректный интервал проверки.");
            foreach (string video in Videos)
                if (!File.Exists(video)) throw new InvalidOperationException("Видео недоступно: " + video);
        }
    }

    internal sealed class VlcSession : IPlayerSession
    {
        private readonly Process process;
        private readonly ManualResetEvent exited = new ManualResetEvent(false);
        private readonly string playlistPath;
        private readonly VlcClient client;
        private bool disposed;
        internal int ProcessId { get { return process.Id; } }

        public VlcSession(PlaybackRequest request, int requestTimeout)
        {
            Directory.CreateDirectory(SettingsStore.AppDirectory);
            playlistPath = Path.Combine(SettingsStore.AppDirectory, "playlist-" + Guid.NewGuid().ToString("N") + ".m3u8");
            int port = FindPort();
            string password = Guid.NewGuid().ToString("N");
            client = new VlcClient(port, password, requestTimeout);
            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = request.VlcPath,
                Arguments = Arguments(port, password, playlistPath),
                WorkingDirectory = Path.GetDirectoryName(request.VlcPath),
                UseShellExecute = false
            };
            process.EnableRaisingEvents = true;
            process.Exited += OnExited;
            try
            {
                File.WriteAllText(playlistPath, MakePlaylist(request.Videos), new UTF8Encoding(false));
                if (!process.Start()) throw new InvalidOperationException("Не удалось запустить VLC.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static string MakePlaylist(string[] videos)
        {
            var text = new StringBuilder("#EXTM3U\n");
            foreach (string video in videos)
            {
                text.Append("#EXTINF:-1,").Append(Path.GetFileName(video)).Append('\n');
                text.Append(new Uri(Path.GetFullPath(video)).AbsoluteUri).Append('\n');
            }
            return text.ToString();
        }

        internal static string Arguments(int port, string password, string playlist)
        {
            return "--intf=qt --extraintf=http --http-host=127.0.0.1 --http-port=" + port.ToString(CultureInfo.InvariantCulture)
                + " --http-password=" + password
                + " --no-one-instance --no-one-instance-when-started-from-file"
                + " --fullscreen --no-video-on-top --no-video-wallpaper"
                + " --loop --no-repeat --no-random --playlist-autostart --no-play-and-exit --no-play-and-stop --no-start-paused"
                + " --no-qt-pause-minimized --qt-auto-raise=0 --no-qt-start-minimized --no-qt-system-tray"
                + " --no-qt-video-autoresize --no-qt-privacy-ask --no-qt-updates-notif --no-qt-error-dialogs"
                + " --qt-continue=0 --no-qt-recentplay --no-qt-autoload-extensions --no-video-title-show --no-media-library"
                + " " + Quote(playlist);
        }

        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                slashes = 0;
                result.Append(c);
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static int FindPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private void OnExited(object sender, EventArgs args)
        {
            try { exited.Set(); }
            catch (ObjectDisposedException) { }
        }

        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public PlayerStatus Read(CancellationToken token) { return client.Read(token); }
        public PlayerStatus Command(string command, CancellationToken token) { return client.Send(command, token); }

        public bool Wait(int milliseconds, CancellationToken token)
        {
            int result = WaitHandle.WaitAny(new WaitHandle[] { token.WaitHandle, exited }, milliseconds);
            token.ThrowIfCancellationRequested();
            return result == 1;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (!HasExited)
                {
                    // Only our Process handle is ever closed/killed, never all vlc.exe processes.
                    try { process.CloseMainWindow(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex) { Log.Write("Закрытие окна VLC: " + ex.Message); }
                    if (!process.WaitForExit(4000))
                    {
                        try { process.Kill(); }
                        catch (InvalidOperationException) { }
                        if (!process.WaitForExit(4000)) throw new IOException("Не удалось завершить управляемый VLC.");
                    }
                }
            }
            finally
            {
                process.Exited -= OnExited;
                process.Dispose();
                exited.Dispose();
                try { if (File.Exists(playlistPath)) File.Delete(playlistPath); }
                catch (IOException ex) { Log.Write(ex.Message); }
                catch (UnauthorizedAccessException ex) { Log.Write(ex.Message); }
            }
        }
    }
}

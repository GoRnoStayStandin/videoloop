using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VideoDisplayChecker
{
    internal sealed class MonitorOptions
    {
        public int StartupMilliseconds = 45000;
        public int StartupPollMilliseconds = 1000;
        public int ProbeMilliseconds = 3000;
        public int ConfirmationMilliseconds = 4700;
        public int RetryMilliseconds = 5000;
        public int MaxRetryMilliseconds = 60000;
        public int RestartWindowMilliseconds = 600000;
        public int MaxRestarts = 5;
    }

    internal sealed class MonitorUpdate : EventArgs
    {
        public readonly string Message;
        public readonly bool Healthy;
        public MonitorUpdate(string message, bool healthy) { Message = message; Healthy = healthy; }
    }

    internal sealed class RunResult
    {
        public readonly string Error;
        public RunResult(string error) { Error = error; }
    }

    internal sealed class Watchdog
    {
        private readonly object gate = new object();
        private readonly Func<PlaybackRequest, IPlayerSession> createSession;
        private readonly MonitorOptions options;
        private CancellationTokenSource cancellation;
        private Task<RunResult> worker;
        public event EventHandler<MonitorUpdate> Updated;

        public Watchdog() : this(r => new VlcSession(r, 3000), new MonitorOptions()) { }

        internal Watchdog(Func<PlaybackRequest, IPlayerSession> factory, MonitorOptions options)
        {
            createSession = factory;
            this.options = options;
        }

        public Task<RunResult> Start(PlaybackRequest request)
        {
            request.Validate();
            lock (gate)
            {
                if (worker != null && !worker.IsCompleted) throw new InvalidOperationException("Показ уже запущен.");
                var source = new CancellationTokenSource();
                cancellation = source;
                // One sleeping background thread; no polling timers on the UI thread.
                worker = Task.Factory.StartNew(() => Run(request, source), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return worker;
            }
        }

        public void RequestStop()
        {
            lock (gate)
                if (cancellation != null) cancellation.Cancel();
        }

        private RunResult Run(PlaybackRequest request, CancellationTokenSource source)
        {
            CancellationToken token = source.Token;
            var restarts = new Queue<long>();
            var clock = Stopwatch.StartNew();
            string finalError = null;
            try
            {
                SetThreadExecutionState(0x80000003); // System + display, for this worker only.
                Log.Write("Показ запущен. Видео: " + request.Videos.Length + ".");
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    IPlayerSession session = null;
                    Exception failure = null;
                    try
                    {
                        Publish("Запуск VLC…", false);
                        session = createSession(request);
                        // STOP may have arrived while Process.Start was running.
                        token.ThrowIfCancellationRequested();
                        WaitForReady(session, token);
                        while (true)
                        {
                            Check(session, token);
                            if (session.Wait(request.IntervalMilliseconds, token)) throw new IOException("VLC завершился.");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (UnsupportedVlcException) { throw; }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        // Cleanup must finish before any new instance can be created.
                        if (session != null) session.Dispose();
                    }

                    token.ThrowIfCancellationRequested();
                    long now = clock.ElapsedMilliseconds;
                    while (restarts.Count > 0 && now - restarts.Peek() > options.RestartWindowMilliseconds) restarts.Dequeue();
                    if (restarts.Count >= options.MaxRestarts)
                        throw new IOException("Слишком много сбоев подряд. Проверьте видео и VLC, затем нажмите СТАРТ. Последняя ошибка: " + failure.Message);
                    restarts.Enqueue(now);
                    int delay = (int)Math.Min((long)options.MaxRetryMilliseconds, (long)options.RetryMilliseconds << Math.Min(restarts.Count - 1, 8));
                    Log.Write("Восстановление VLC: " + failure.Message);
                    Publish("Восстановление через " + Math.Max(1, delay / 1000) + " сек. " + failure.Message, false);
                    if (token.WaitHandle.WaitOne(delay)) token.ThrowIfCancellationRequested();
                    // A disappeared drive/file is reported instead of repeatedly opening an empty playlist.
                    request.Validate();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                finalError = ex.Message;
                Log.Write("Показ остановлен с ошибкой: " + ex);
            }
            finally
            {
                SetThreadExecutionState(0x80000000);
                lock (gate)
                {
                    if (ReferenceEquals(cancellation, source)) cancellation = null;
                    source.Dispose();
                }
                Log.Write("Контроль остановлен.");
            }
            return new RunResult(finalError);
        }

        private void WaitForReady(IPlayerSession session, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            Exception last = null;
            PlayerStatus previous = null;
            do
            {
                token.ThrowIfCancellationRequested();
                if (session.HasExited) throw new IOException("VLC закрылся во время запуска.");
                try
                {
                    PlayerStatus status = session.Read(token);
                    last = null;
                    if (!status.Version.StartsWith("3.", StringComparison.Ordinal))
                        throw new UnsupportedVlcException("Для этой версии программы требуется VLC 3.x. Установите VLC 3.x и повторите запуск.");
                    // The HTTP endpoint can become ready before VLC has parsed the
                    // M3U8. pl_play during that window can interrupt playlist loading.
                    // Give decoding its own startup grace period as well.
                    if (status.State == "paused" && status.ItemId != "-1") return;
                    if (previous != null && PlayerStatus.HasProgress(previous, status)) return;
                    previous = status;
                }
                catch (OperationCanceledException) { throw; }
                catch (UnsupportedVlcException) { throw; }
                catch (Exception ex) { last = ex; }
                if (session.Wait(options.StartupPollMilliseconds, token)) throw new IOException("VLC закрылся во время запуска.");
            } while (clock.ElapsedMilliseconds < options.StartupMilliseconds);
            throw new IOException(last == null ? "VLC не начал воспроизведение. Проверьте видео."
                : "VLC не отвечает на команды. Проверьте установку VLC. " + last.Message);
        }

        private void Check(IPlayerSession session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (session.HasExited) throw new IOException("VLC завершился.");
            PlayerStatus before = ReadWithRetry(session, token);
            // These commands toggle flags: issue only when the reported flag needs changing.
            if (!before.Loop) before = session.Command("pl_loop", token);
            if (before.Repeat) before = session.Command("pl_repeat", token);
            if (before.Random) before = session.Command("pl_random", token);
            int stagnant = 0;
            int corrections = 0;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                // The state may change while a probe is sleeping. In particular, a
                // user pause between samples must be resumed, not treated as a hang.
                if (before.State == "paused")
                {
                    if (++corrections > 3) break;
                    stagnant = 0;
                    Publish("Возобновление после паузы…", false);
                    Log.Write("Снятие паузы.");
                    before = session.Command("pl_forceresume", token);
                }
                else if (before.State == "stopped")
                {
                    // Allow a normal track transition to finish before issuing play.
                    WaitAlive(session, options.StartupPollMilliseconds, token);
                    before = ReadWithRetry(session, token);
                    if (before.State == "stopped")
                    {
                        if (++corrections > 3) break;
                        stagnant = 0;
                        Publish("Возобновление воспроизведения…", false);
                        Log.Write("Запуск остановленного воспроизведения.");
                        before = session.Command("pl_play", token);
                    }
                }
                WaitAlive(session, attempt == 0 ? options.ProbeMilliseconds : options.ConfirmationMilliseconds, token);
                PlayerStatus after = ReadWithRetry(session, token);
                if (PlayerStatus.HasProgress(before, after))
                {
                    Publish("Воспроизведение" + (string.IsNullOrEmpty(after.Name) ? "." : ": " + after.Name)
                        + "\r\nПоследняя проверка: " + DateTime.Now.ToString("HH:mm:ss"), true);
                    return;
                }
                // Commands are asynchronous in VLC. A stopped -> playing response
                // establishes a fresh baseline, rather than consuming the stall budget.
                if (before.State == "playing" && after.State == "playing") stagnant++;
                else stagnant = 0;
                if (stagnant >= 3) break;
                before = after;
                Publish("Проверка продвижения видео…", false);
            }
            throw new IOException("Воспроизведение не продвигается.");
        }

        private PlayerStatus ReadWithRetry(IPlayerSession session, CancellationToken token)
        {
            try { return session.Read(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                Publish("Нет ответа VLC. Повторная проверка…", false);
                WaitAlive(session, options.ConfirmationMilliseconds, token);
                return session.Read(token);
            }
        }

        private static void WaitAlive(IPlayerSession session, int milliseconds, CancellationToken token)
        {
            if (session.Wait(milliseconds, token)) throw new IOException("VLC завершился.");
        }

        private void Publish(string message, bool healthy)
        {
            EventHandler<MonitorUpdate> handler = Updated;
            if (handler != null) handler(this, new MonitorUpdate(message, healthy));
        }

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        private sealed class UnsupportedVlcException : Exception
        {
            public UnsupportedVlcException(string message) : base(message) { }
        }
    }
}

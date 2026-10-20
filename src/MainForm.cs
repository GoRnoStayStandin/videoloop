using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoDisplayChecker
{
    internal sealed class MainForm : Form
    {
        private readonly SettingsStore store;
        private readonly AppSettings settings;
        private readonly Watchdog watchdog;
        private readonly DataGridView playlist = new DataGridView();
        private readonly Button add = new Button();
        private readonly Button clear = new Button();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Button browse = new Button();
        private readonly TextBox vlcPath = new TextBox();
        private readonly ComboBox interval = new ComboBox();
        private readonly Label status = new Label();
        private readonly Label count = new Label();
        private readonly ToolTip toolTip = new ToolTip();
        private bool running;
        private bool stopping;
        private bool closing;
        private bool initialized;
        private bool saveWarningShown;

        public MainForm() : this(new SettingsStore(SettingsStore.AppDirectory), new Watchdog()) { }

        internal MainForm(SettingsStore store, Watchdog watchdog)
        {
            this.store = store;
            this.watchdog = watchdog;
            string warning;
            settings = store.Load(out warning);
            if (!File.Exists(settings.VlcPath)) settings.VlcPath = VlcLocator.Find();
            Text = "VideoLoop";
            Name = "MainForm";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 530);
            MinimumSize = new Size(610, 470);
            BackColor = SystemColors.Window;
            BuildLayout();
            vlcPath.Text = settings.VlcPath;
            interval.SelectedIndex = settings.CheckMinutes == 5 ? 1 : settings.CheckMinutes == 10 ? 2 : 0;
            RefreshPlaylist();
            SetStatus(warning ?? "Добавьте видео и нажмите СТАРТ.", false);
            watchdog.Updated += OnMonitorUpdate;
            FormClosing += OnClosing;
            initialized = true;
            UpdateControls();
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 3; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label { Text = "VideoLoop", AutoSize = true, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) };
            layout.Controls.Add(title, 0, 0);

            var queueButtons = FlowRow();
            ConfigureButton(add, "Добавить видео…", 150);
            ConfigureButton(clear, "Очистить очередь", 155);
            add.Name = "AddVideos";
            clear.Name = "ClearPlaylist";
            add.Click += AddVideos;
            clear.Click += delegate { settings.Videos.Clear(); QueueChanged(); };
            queueButtons.Controls.Add(add);
            queueButtons.Controls.Add(clear);
            layout.Controls.Add(queueButtons, 0, 1);

            playlist.Name = "Playlist";
            playlist.Dock = DockStyle.Fill;
            playlist.BackgroundColor = SystemColors.Window;
            playlist.BorderStyle = BorderStyle.FixedSingle;
            playlist.AutoGenerateColumns = false;
            playlist.AllowUserToAddRows = false;
            playlist.AllowUserToDeleteRows = false;
            playlist.AllowUserToResizeRows = false;
            playlist.RowHeadersVisible = false;
            playlist.ReadOnly = true;
            playlist.MultiSelect = false;
            playlist.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            playlist.RowTemplate.Height = 32;
            playlist.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            playlist.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            playlist.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "VideoName", HeaderText = "Видео — в порядке воспроизведения", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            playlist.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "RemoveVideo", HeaderText = "", Text = "Удалить", UseColumnTextForButtonValue = true,
                Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            playlist.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (running || e.RowIndex < 0 || e.ColumnIndex != 1) return;
                settings.Videos.RemoveAt(e.RowIndex);
                QueueChanged();
            };
            layout.Controls.Add(playlist, 0, 2);

            count.AutoSize = true;
            count.ForeColor = SystemColors.GrayText;
            count.Margin = new Padding(0, 5, 0, 8);
            layout.Controls.Add(count, 0, 3);

            var pathRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(new Label { Text = "VLC:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
            vlcPath.Name = "VlcPath";
            vlcPath.Dock = DockStyle.Fill;
            vlcPath.ReadOnly = true;
            vlcPath.BackColor = SystemColors.Window;
            vlcPath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            ConfigureButton(browse, "Обзор…", 90);
            browse.Click += BrowseVlc;
            pathRow.Controls.Add(vlcPath, 1, 0);
            pathRow.Controls.Add(browse, 2, 0);
            layout.Controls.Add(pathRow, 0, 4);

            var intervalRow = FlowRow();
            intervalRow.Margin = new Padding(0, 5, 0, 8);
            intervalRow.Controls.Add(new Label { Text = "Проверять каждые:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            interval.Name = "CheckInterval";
            interval.DropDownStyle = ComboBoxStyle.DropDownList;
            interval.Width = 125;
            interval.Items.AddRange(new object[] { "1 минуту", "5 минут", "10 минут" });
            interval.SelectedIndexChanged += delegate
            {
                if (!initialized) return;
                settings.CheckMinutes = interval.SelectedIndex == 1 ? 5 : interval.SelectedIndex == 2 ? 10 : 1;
                SaveSettings();
            };
            intervalRow.Controls.Add(interval);
            layout.Controls.Add(intervalRow, 0, 5);

            var playbackButtons = FlowRow();
            ConfigureButton(start, "СТАРТ", 145);
            ConfigureButton(stop, "СТОП", 145);
            start.Name = "StartPlayback";
            stop.Name = "StopPlayback";
            start.Height = stop.Height = 40;
            start.Font = new Font(Font, FontStyle.Bold);
            stop.Font = new Font(Font, FontStyle.Bold);
            start.Click += async delegate { await StartPlayback(); };
            stop.Click += delegate { StopPlayback(); };
            playbackButtons.Controls.Add(start);
            playbackButtons.Controls.Add(stop);
            layout.Controls.Add(playbackButtons, 0, 6);

            status.Name = "PlaybackStatus";
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.Height = 52;
            status.Margin = new Padding(0, 8, 0, 4);
            layout.Controls.Add(status, 0, 7);
            layout.Controls.Add(new Label
            {
                Text = "Alt+Tab — перейти к СТОП.  F в VLC — выйти из полного экрана.\r\nОчередь можно менять после остановки. Закрытие программы останавливает показ.",
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 0)
            }, 0, 8);
            Controls.Add(layout);
            toolTip.SetToolTip(clear, "Удаляет записи из очереди. Видеофайлы остаются на диске.");
            toolTip.SetToolTip(interval, "После вылета VLC восстановление начнётся раньше, по событию завершения процесса.");
        }

        private static FlowLayoutPanel FlowRow()
        {
            return new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        }

        private static void ConfigureButton(Button button, string text, int width)
        {
            button.Text = text;
            button.Size = new Size(width, 31);
            button.Margin = new Padding(0, 0, 8, 0);
            button.UseVisualStyleBackColor = true;
        }

        private void AddVideos(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Добавить видео", Multiselect = true, CheckFileExists = true,
                Filter = "Видеофайлы|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts;*.webm;*.flv;*.vob;*.ogv;*.3gp|Все файлы|*.*"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (string file in dialog.FileNames)
                    if (!settings.Videos.Contains(file, StringComparer.OrdinalIgnoreCase)) settings.Videos.Add(file);
                QueueChanged();
            }
        }

        private void BrowseVlc(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Title = "Выберите vlc.exe", Filter = "VLC player (vlc.exe)|vlc.exe", CheckFileExists = true })
            {
                if (File.Exists(settings.VlcPath)) dialog.FileName = settings.VlcPath;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                settings.VlcPath = dialog.FileName;
                vlcPath.Text = settings.VlcPath;
                toolTip.SetToolTip(vlcPath, settings.VlcPath);
                SaveSettings();
            }
        }

        private void QueueChanged()
        {
            RefreshPlaylist();
            UpdateControls();
            SaveSettings();
        }

        private void RefreshPlaylist()
        {
            playlist.Rows.Clear();
            foreach (string file in settings.Videos)
            {
                int row = playlist.Rows.Add(Path.GetFileName(file));
                playlist.Rows[row].Cells[0].ToolTipText = file;
            }
            count.Text = "Видео в очереди: " + settings.Videos.Count + "   •   Повтор всей очереди";
            toolTip.SetToolTip(vlcPath, settings.VlcPath);
        }

        private async Task StartPlayback()
        {
            if (running) return;
            var request = new PlaybackRequest(settings.VlcPath, settings.Videos.ToArray(), settings.CheckMinutes * 60000);
            try { request.Validate(); }
            catch (Exception ex)
            {
                SetStatus(ex.Message, false);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SaveSettings();
            running = true;
            stopping = false;
            UpdateControls();
            SetStatus("Запуск VLC…", false);
            try
            {
                RunResult result = await watchdog.Start(request);
                SetStatus(result.Error == null ? "Показ остановлен. Контроль отключён." : "Ошибка: " + result.Error, false);
            }
            catch (Exception ex)
            {
                Log.Write("Ошибка запуска: " + ex);
                SetStatus("Ошибка: " + ex.Message, false);
            }
            finally
            {
                running = false;
                stopping = false;
                UpdateControls();
                if (closing) Close();
            }
        }

        private void StopPlayback()
        {
            if (!running || stopping) return;
            stopping = true;
            SetStatus("Остановка VLC…", false);
            UpdateControls();
            watchdog.RequestStop();
        }

        private void OnMonitorUpdate(object sender, MonitorUpdate update)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (running && !stopping) SetStatus(update.Message, update.Healthy);
                });
            }
            catch (InvalidOperationException) { }
        }

        private void UpdateControls()
        {
            add.Enabled = clear.Enabled = browse.Enabled = interval.Enabled = playlist.Enabled = !running;
            clear.Enabled = !running && settings.Videos.Count > 0;
            start.Enabled = !running && settings.Videos.Count > 0;
            stop.Enabled = running && !stopping;
        }

        private void SetStatus(string message, bool healthy)
        {
            status.Text = message;
            status.ForeColor = healthy ? Color.FromArgb(25, 112, 55) : SystemColors.ControlText;
            toolTip.SetToolTip(status, message);
        }

        private void SaveSettings()
        {
            try { store.Save(settings); saveWarningShown = false; }
            catch (Exception ex)
            {
                if (!SettingsStore.IsStorageError(ex)) throw;
                Log.Write("Не удалось сохранить настройки: " + ex.Message);
                if (saveWarningShown) return;
                saveWarningShown = true;
                MessageBox.Show(this, "Не удалось сохранить очередь и настройки:\r\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (running)
            {
                e.Cancel = true;
                closing = true;
                StopPlayback();
                return;
            }
            SaveSettings();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                watchdog.RequestStop();
                watchdog.Updated -= OnMonitorUpdate;
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

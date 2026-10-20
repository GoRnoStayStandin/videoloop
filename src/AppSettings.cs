using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace VideoDisplayChecker
{
    public sealed class AppSettings
    {
        public string VlcPath { get; set; }
        public int CheckMinutes { get; set; }
        public List<string> Videos { get; set; }

        public AppSettings()
        {
            VlcPath = "";
            CheckMinutes = 1;
            Videos = new List<string>();
        }

        public void Normalize()
        {
            if (CheckMinutes != 1 && CheckMinutes != 5 && CheckMinutes != 10) CheckMinutes = 1;
            VlcPath = VlcPath ?? "";
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var valid = new List<string>();
            foreach (string path in Videos ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    string fullPath = Path.GetFullPath(path);
                    if (unique.Add(fullPath)) valid.Add(fullPath);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            Videos = valid;
        }
    }

    internal sealed class SettingsStore
    {
        internal static readonly string AppDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoDisplayChecker");
        private readonly string directory;
        private string SettingsPath { get { return Path.Combine(directory, "settings.xml"); } }

        public SettingsStore(string directory) { this.directory = directory; }

        public AppSettings Load(out string warning)
        {
            warning = null;
            if (!File.Exists(SettingsPath)) return new AppSettings();
            try { return Read(SettingsPath); }
            catch (Exception ex)
            {
                if (!IsStorageError(ex)) throw;
                Log.Write("Не удалось прочитать настройки: " + ex.Message);
                try
                {
                    AppSettings backup = Read(SettingsPath + ".bak");
                    warning = "Настройки восстановлены из резервной копии.";
                    return backup;
                }
                catch (Exception backupError)
                {
                    if (!IsStorageError(backupError)) throw;
                    warning = "Не удалось прочитать настройки. Добавьте видео заново.";
                    return new AppSettings();
                }
            }
        }

        private static AppSettings Read(string path)
        {
            var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2097152 };
            using (var reader = XmlReader.Create(path, options))
            {
                var settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(reader);
                settings.Normalize();
                return settings;
            }
        }

        public void Save(AppSettings settings)
        {
            Directory.CreateDirectory(directory);
            string temporary = SettingsPath + ".tmp";
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
                new XmlSerializer(typeof(AppSettings)).Serialize(writer, settings);
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, SettingsPath + ".bak");
            else File.Move(temporary, SettingsPath);
        }

        internal static bool IsStorageError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is XmlException;
        }
    }

    internal static class VlcLocator
    {
        public static string Find()
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = root.OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
                        {
                            if (key == null) continue;
                            string direct = key.GetValue(null) as string;
                            if (!string.IsNullOrEmpty(direct) && File.Exists(direct)) return direct;
                            string folder = key.GetValue("InstallDir") as string;
                            if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "vlc.exe")))
                                return Path.Combine(folder, "vlc.exe");
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            foreach (string variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string folder = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(folder)) continue;
                string path = Path.Combine(folder, "VideoLAN", "VLC", "vlc.exe");
                if (File.Exists(path)) return path;
            }
            return "";
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(SettingsStore.AppDirectory);
                    string path = Path.Combine(SettingsStore.AppDirectory, "events.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 262144)
                    {
                        string previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

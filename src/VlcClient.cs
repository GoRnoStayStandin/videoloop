using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace VideoDisplayChecker
{
    internal sealed class PlayerStatus
    {
        public string State;
        public string Version;
        public string ItemId;
        public string Name;
        public double Time;
        public double Position;
        public long DecodedVideo;
        public long PlayedAudio;
        public bool Loop;
        public bool Repeat;
        public bool Random;
        public bool Fullscreen;

        public static PlayerStatus Parse(Stream stream)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 };
            using (var reader = XmlReader.Create(stream, settings))
            {
                XElement root = XDocument.Load(reader).Root;
                if (root == null || root.Name != "root") throw new InvalidDataException("Некорректный ответ VLC.");
                string state = Text(root, "state", "");
                if (state != "playing" && state != "paused" && state != "stopped")
                    throw new InvalidDataException("VLC вернул неизвестное состояние.");
                string name = "";
                foreach (XElement info in root.Descendants("info"))
                    // VLC 3 status.xml escapes metadata once in xmlString and again in
                    // convert_xml_special_chars. XML parsing removes only the outer layer.
                    if ((string)info.Attribute("name") == "filename") { name = WebUtility.HtmlDecode(info.Value); break; }
                XElement stats = root.Element("stats");
                return new PlayerStatus
                {
                    State = state,
                    Version = Text(root, "version", ""),
                    ItemId = Text(root, "currentplid", "-1"),
                    Name = name,
                    Time = Number(root, "time", -1),
                    Position = Number(root, "position", -1),
                    DecodedVideo = (long)Number(stats, "decodedvideo", -1),
                    PlayedAudio = (long)Number(stats, "playedabuffers", -1),
                    Loop = Text(root, "loop", "false") == "true",
                    Repeat = Text(root, "repeat", "false") == "true",
                    Random = Text(root, "random", "false") == "true",
                    Fullscreen = Text(root, "fullscreen", "false") == "true"
                };
            }
        }

        private static string Text(XElement parent, string name, string fallback)
        {
            XElement node = parent == null ? null : parent.Element(name);
            return node == null ? fallback : node.Value;
        }

        private static double Number(XElement parent, string name, double fallback)
        {
            double value;
            return double.TryParse(Text(parent, name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;
        }

        public static bool HasProgress(PlayerStatus before, PlayerStatus after)
        {
            if (before.State != "playing" || after.State != "playing") return false;
            // A new item, a rewind at the loop boundary, or reset counters are all progress.
            // Samples are taken a few seconds apart, never an entire polling interval apart.
            return before.ItemId != after.ItemId
                || Changed(before.Time, after.Time)
                || Changed(before.Position, after.Position)
                || Changed(before.DecodedVideo, after.DecodedVideo)
                || Changed(before.PlayedAudio, after.PlayedAudio);
        }

        private static bool Changed(double before, double after)
        {
            return before >= 0 && after >= 0 && before != after;
        }
    }

    internal sealed class VlcClient
    {
        private readonly string address;
        private readonly string authorization;
        private readonly int timeout;

        public VlcClient(int port, string password, int timeoutMilliseconds)
        {
            address = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/requests/status.xml";
            authorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + password));
            timeout = timeoutMilliseconds;
        }

        public PlayerStatus Read(CancellationToken token) { return Send(null, token); }

        public PlayerStatus Send(string command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(address + (command == null ? "" : "?command=" + Uri.EscapeDataString(command)));
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.Headers[HttpRequestHeader.Authorization] = authorization;
            using (token.Register(request.Abort))
            {
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                        return PlayerStatus.Parse(stream);
                }
                catch (WebException)
                {
                    token.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }
    }
}

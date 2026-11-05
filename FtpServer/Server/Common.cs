using System;
using System.Collections.Generic;
using Windows.Storage;

namespace FtpServer.Server
{
    public static class Logger
    {
        public static event Action<string> OnLog;

        public static void Log(string message)
        {
            var handler = OnLog;
            if (handler != null)
            {
                try { handler(DateTime.Now.ToString("HH:mm:ss") + "  " + message); }
                catch { }
            }
        }
    }

    // Snapshot of the user's settings, taken when the servers start so a
    // running session never sees half-edited values.
    public class ServerSettings
    {
        public int FtpPort = 2221;
        public int WebPort = 8080;
        public bool Anonymous = true;
        public string Username = "lumia";
        public string Password = "";
        public bool AllowWrites = true;
        public bool KeepScreenOn = true;

        public static ServerSettings Load()
        {
            var s = new ServerSettings();
            var v = ApplicationData.Current.LocalSettings.Values;
            if (v.ContainsKey("FtpPort")) s.FtpPort = (int)v["FtpPort"];
            if (v.ContainsKey("WebPort")) s.WebPort = (int)v["WebPort"];
            if (v.ContainsKey("Anonymous")) s.Anonymous = (bool)v["Anonymous"];
            if (v.ContainsKey("Username")) s.Username = (string)v["Username"];
            if (v.ContainsKey("Password")) s.Password = (string)v["Password"];
            if (v.ContainsKey("AllowWrites")) s.AllowWrites = (bool)v["AllowWrites"];
            if (v.ContainsKey("KeepScreenOn")) s.KeepScreenOn = (bool)v["KeepScreenOn"];
            if (s.FtpPort <= 0 || s.FtpPort > 65535) s.FtpPort = 2221;
            if (s.WebPort <= 0 || s.WebPort > 65535) s.WebPort = 8080;
            if (string.IsNullOrEmpty(s.Username)) s.Username = "lumia";
            if (s.Password == null) s.Password = "";
            return s;
        }

        public void Save()
        {
            var v = ApplicationData.Current.LocalSettings.Values;
            v["FtpPort"] = FtpPort;
            v["WebPort"] = WebPort;
            v["Anonymous"] = Anonymous;
            v["Username"] = Username;
            v["Password"] = Password ?? "";
            v["AllowWrites"] = AllowWrites;
            v["KeepScreenOn"] = KeepScreenOn;
        }

        // True when the supplied credentials are accepted as the
        // configured user. An empty configured password accepts any
        // (including empty) password for that user; the password is
        // only checked when one is set.
        public bool CheckUser(string user, string pass)
        {
            if (!string.Equals(user ?? "", Username, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(Password)) return true;
            return string.Equals(pass ?? "", Password, StringComparison.Ordinal);
        }

        public static bool IsAnonymousUser(string user)
        {
            return string.Equals(user, "anonymous", StringComparison.OrdinalIgnoreCase)
                || string.Equals(user, "ftp", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class ContentTypes
    {
        static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".txt", "text/plain" }, { ".html", "text/html" }, { ".htm", "text/html" },
            { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".png", "image/png" },
            { ".gif", "image/gif" }, { ".bmp", "image/bmp" }, { ".svg", "image/svg+xml" },
            { ".mp3", "audio/mpeg" }, { ".flac", "audio/flac" }, { ".wav", "audio/wav" },
            { ".m4a", "audio/mp4" }, { ".ogg", "audio/ogg" }, { ".wma", "audio/x-ms-wma" },
            { ".mp4", "video/mp4" }, { ".mkv", "video/x-matroska" }, { ".avi", "video/x-msvideo" },
            { ".mov", "video/quicktime" }, { ".wmv", "video/x-ms-wmv" }, { ".3gp", "video/3gpp" },
            { ".pdf", "application/pdf" }, { ".zip", "application/zip" },
            { ".doc", "application/msword" }, { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            { ".xls", "application/vnd.ms-excel" }, { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            { ".lrc", "text/plain" }, { ".md", "text/plain" }, { ".json", "application/json" },
        };

        public static string For(string fileName)
        {
            int dot = fileName.LastIndexOf('.');
            if (dot >= 0)
            {
                string ct;
                if (Map.TryGetValue(fileName.Substring(dot), out ct)) return ct;
            }
            return "application/octet-stream";
        }

        public static string HumanSize(ulong bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }
    }
}

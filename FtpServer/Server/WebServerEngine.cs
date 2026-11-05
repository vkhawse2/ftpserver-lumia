using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage;

namespace FtpServer.Server
{
    public class WebServerEngine
    {
        StreamSocketListener _listener;
        readonly List<StreamSocket> _sockets = new List<StreamSocket>();

        public bool IsRunning { get { return _listener != null; } }

        public async Task StartAsync(StorageFolder home, ServerSettings settings)
        {
            Stop();
            var listener = new StreamSocketListener();
            listener.ConnectionReceived += (sender, args) =>
            {
                var socket = args.Socket;
                lock (_sockets) _sockets.Add(socket);
                var handler = new WebRequestHandler(socket, home, settings);
                var ignored = handler.RunAsync().ContinueWith(t =>
                {
                    lock (_sockets) _sockets.Remove(socket);
                    try { socket.Dispose(); } catch { }
                });
            };
            await listener.BindServiceNameAsync(settings.WebPort.ToString());
            _listener = listener;
            Logger.Log("Web UI listening on port " + settings.WebPort);
        }

        public void Stop()
        {
            if (_listener != null)
            {
                try { _listener.Dispose(); } catch { }
                _listener = null;
            }
            lock (_sockets)
            {
                foreach (var s in _sockets) { try { s.Dispose(); } catch { } }
                _sockets.Clear();
            }
        }
    }

    class WebRequestHandler
    {
        readonly StreamSocket _socket;
        readonly StorageFolder _home;
        readonly ServerSettings _settings;
        Stream _in;
        Stream _out;

        public WebRequestHandler(StreamSocket socket, StorageFolder home, ServerSettings settings)
        {
            _socket = socket;
            _home = home;
            _settings = settings;
        }

        public async Task RunAsync()
        {
            try
            {
                _in = _socket.InputStream.AsStreamForRead(8192);
                _out = _socket.OutputStream.AsStreamForWrite();

                var headerBytes = await ReadHeaderBytes();
                if (headerBytes == null) return;
                string headerText = Encoding.UTF8.GetString(headerBytes, 0, headerBytes.Length);
                var lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;
                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0].ToUpperInvariant();
                string rawTarget = parts[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
                }
                long contentLength = 0;
                string cl;
                if (headers.TryGetValue("Content-Length", out cl)) long.TryParse(cl, out contentLength);

                if (!CheckAuth(headers))
                {
                    await SendStatus(401, "Unauthorized", "text/plain", Encoding.UTF8.GetBytes("Authentication required"),
                        "WWW-Authenticate: Basic realm=\"WiFi FTP Server\"");
                    return;
                }

                string rawPath = rawTarget;
                var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int q = rawTarget.IndexOf('?');
                if (q >= 0)
                {
                    rawPath = rawTarget.Substring(0, q);
                    ParseQuery(rawTarget.Substring(q + 1), query);
                }
                string targetPath = UrlDecode(rawPath);

                Logger.Log("Web " + method + " " + rawTarget + " from " + _socket.Information.RemoteAddress);

                if (method == "POST" && targetPath == "/api/upload") { await HandleUpload(query, contentLength); return; }
                if (method == "POST" && targetPath == "/api/mkdir") { await HandleMkdir(contentLength); return; }
                if (method == "POST" && targetPath == "/api/delete") { await HandleDelete(contentLength); return; }
                if (method == "GET") { await HandleGet(query, targetPath); return; }
                await SendStatus(405, "Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("Method not allowed"), null);
            }
            catch (Exception ex)
            {
                Logger.Log("Web error: " + ex.Message);
            }
        }

        bool CheckAuth(Dictionary<string, string> headers)
        {
            if (_settings.Anonymous) return true;
            string auth;
            if (!headers.TryGetValue("Authorization", out auth)) return false;
            if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Substring(6).Trim()));
                int c = decoded.IndexOf(':');
                string user = c >= 0 ? decoded.Substring(0, c) : decoded;
                string pass = c >= 0 ? decoded.Substring(c + 1) : "";
                return _settings.CheckUser(user, pass);
            }
            catch { return false; }
        }

        async Task<byte[]> ReadHeaderBytes()
        {
            var bytes = new List<byte>();
            int state = 0; // progress matching \r\n\r\n
            while (bytes.Count < 16384)
            {
                int b = _in.ReadByte();
                if (b < 0) return bytes.Count == 0 ? null : bytes.ToArray();
                bytes.Add((byte)b);
                if ((state == 0 || state == 2) && b == '\r') state++;
                else if ((state == 1 || state == 3) && b == '\n') state++;
                else state = (b == '\r') ? 1 : 0;
                if (state == 4) return bytes.ToArray();
            }
            return null;
        }

        async Task SendRaw(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _out.WriteAsync(bytes, 0, bytes.Length);
            await _out.FlushAsync();
        }

        async Task SendStatus(int code, string reason, string contentType, byte[] body, string extraHeader)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 " + code + " " + reason + "\r\n");
            if (contentType != null) sb.Append("Content-Type: " + contentType + "\r\n");
            if (body != null) sb.Append("Content-Length: " + body.Length + "\r\n");
            if (extraHeader != null) sb.Append(extraHeader + "\r\n");
            sb.Append("Connection: close\r\n\r\n");
            await SendRaw(sb.ToString());
            if (body != null && body.Length > 0) { await _out.WriteAsync(body, 0, body.Length); await _out.FlushAsync(); }
        }

        async Task HandleGet(Dictionary<string, string> query, string requestPath)
        {
            string vpath;
            if (!query.TryGetValue("path", out vpath) || string.IsNullOrEmpty(vpath))
                vpath = string.IsNullOrEmpty(requestPath) ? "/" : requestPath;
            var segs = PathHelper.Normalize("/", vpath);
            var item = await PathHelper.GetItemAsync(_home, segs);
            if (item == null) { await SendStatus(404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found"), null); return; }
            var file = item as StorageFile;
            if (file != null) { await SendFile(file); return; }
            var folder = item as StorageFolder;
            string html = await BuildListing(folder, PathHelper.ToVirtual(segs));
            await SendStatus(200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null);
        }

        async Task SendFile(StorageFile file)
        {
            var props = await file.GetBasicPropertiesAsync();
            string safeName = file.Name.Replace("\"", "");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: " + ContentTypes.For(file.Name) + "\r\n");
            sb.Append("Content-Length: " + props.Size + "\r\n");
            sb.Append("Content-Disposition: attachment; filename=\"" + safeName + "\"\r\n");
            sb.Append("Connection: close\r\n\r\n");
            await SendRaw(sb.ToString());
            using (var src = await file.OpenStreamForReadAsync())
            {
                var buf = new byte[65536];
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    await _out.WriteAsync(buf, 0, n);
                await _out.FlushAsync();
            }
            Logger.Log("Web download " + file.Name + " (" + props.Size + " bytes)");
        }

        async Task HandleUpload(Dictionary<string, string> query, long contentLength)
        {
            if (!_settings.AllowWrites) { await SendStatus(403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("Read-only mode"), null); return; }
            string dirPath, name;
            if (!query.TryGetValue("path", out dirPath)) dirPath = "/";
            if (!query.TryGetValue("name", out name) || string.IsNullOrEmpty(name)) { await SendStatus(400, "Bad Request", "text/plain", Encoding.UTF8.GetBytes("Missing name"), null); return; }
            name = name.Replace("/", "_").Replace("\\", "_");
            var segs = PathHelper.Normalize("/", dirPath);
            var folder = await PathHelper.GetFolderAsync(_home, segs, true);
            if (folder == null) { await SendStatus(500, "Error", "text/plain", Encoding.UTF8.GetBytes("Cannot open folder"), null); return; }
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            long remaining = contentLength;
            using (var dst = await file.OpenStreamForWriteAsync())
            {
                var buf = new byte[65536];
                while (remaining > 0)
                {
                    int want = (int)Math.Min(buf.Length, remaining);
                    int n = await _in.ReadAsync(buf, 0, want);
                    if (n <= 0) break;
                    await dst.WriteAsync(buf, 0, n);
                    remaining -= n;
                }
                await dst.FlushAsync();
            }
            Logger.Log("Web upload " + name + " (" + (contentLength - remaining) + " bytes)");
            await SendStatus(200, "OK", "text/plain", Encoding.UTF8.GetBytes("OK"), null);
        }

        async Task<string> ReadBodyString(long contentLength)
        {
            if (contentLength <= 0 || contentLength > 65536) return "";
            var buf = new byte[contentLength];
            int read = 0;
            while (read < buf.Length)
            {
                int n = await _in.ReadAsync(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(buf, 0, read);
        }

        async Task HandleMkdir(long contentLength)
        {
            if (!_settings.AllowWrites) { await SendStatus(403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("Read-only mode"), null); return; }
            string body = await ReadBodyString(contentLength);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ParseQuery(body, fields);
            string dirPath, name;
            if (!fields.TryGetValue("path", out dirPath)) dirPath = "/";
            if (!fields.TryGetValue("name", out name) || string.IsNullOrEmpty(name)) { await SendStatus(400, "Bad Request", "text/plain", Encoding.UTF8.GetBytes("Missing name"), null); return; }
            var segs = PathHelper.Normalize("/", dirPath);
            segs.Add(name.Replace("/", "_").Replace("\\", "_"));
            var parent = await PathHelper.GetParentAsync(_home, segs, true);
            if (parent == null) { await SendStatus(500, "Error", "text/plain", Encoding.UTF8.GetBytes("Cannot open folder"), null); return; }
            await parent.CreateFolderAsync(segs[segs.Count - 1], CreationCollisionOption.OpenIfExists);
            Logger.Log("Web mkdir " + PathHelper.ToVirtual(segs));
            await SendStatus(200, "OK", "text/plain", Encoding.UTF8.GetBytes("OK"), null);
        }

        async Task HandleDelete(long contentLength)
        {
            if (!_settings.AllowWrites) { await SendStatus(403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("Read-only mode"), null); return; }
            string body = await ReadBodyString(contentLength);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ParseQuery(body, fields);
            string target;
            if (!fields.TryGetValue("path", out target) || string.IsNullOrEmpty(target)) { await SendStatus(400, "Bad Request", "text/plain", Encoding.UTF8.GetBytes("Missing path"), null); return; }
            var segs = PathHelper.Normalize("/", target);
            if (segs.Count == 0) { await SendStatus(403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("Cannot delete home folder"), null); return; }
            var item = await PathHelper.GetItemAsync(_home, segs);
            if (item == null) { await SendStatus(404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found"), null); return; }
            await item.DeleteAsync();
            Logger.Log("Web deleted " + PathHelper.ToVirtual(segs));
            await SendStatus(200, "OK", "text/plain", Encoding.UTF8.GetBytes("OK"), null);
        }

        async Task<string> BuildListing(StorageFolder folder, string vpath)
        {
            var items = await folder.GetItemsAsync();
            var folders = new List<StorageFolder>();
            var files = new List<StorageFile>();
            foreach (var item in items)
            {
                var f = item as StorageFolder;
                if (f != null) folders.Add(f); else files.Add((StorageFile)item);
            }
            folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>AirTile — " + Html(vpath) + "</title>");
            sb.Append("<style>");
            sb.Append("body{background:#181818;color:#eee;font-family:sans-serif;margin:0;padding:12px;}");
            sb.Append("a{color:#6db3f2;text-decoration:none;}");
            sb.Append(".row{display:block;padding:14px 10px;border-bottom:1px solid #333;font-size:17px;}");
            sb.Append(".meta{color:#999;font-size:13px;}");
            sb.Append("button,.btn{background:#2d7dd2;color:#fff;border:0;padding:12px 16px;font-size:16px;border-radius:4px;min-height:48px;}");
            sb.Append(".del{background:#a33;float:right;padding:8px 12px;min-height:40px;font-size:14px;}");
            sb.Append("input[type=text]{padding:12px;font-size:16px;width:60%;background:#222;color:#eee;border:1px solid #444;}");
            sb.Append("#barwrap{background:#333;height:18px;display:none;margin:8px 0;}#bar{background:#2d7dd2;height:18px;width:0%;}");
            sb.Append("h1{font-size:20px;} .box{border:1px solid #333;padding:12px;margin:14px 0;}");
            sb.Append("</style></head><body>");
            sb.Append("<h1>&#128193; " + Html(vpath) + "</h1>");

            // breadcrumb
            sb.Append("<div>");
            sb.Append("<a href=\"/?path=/\">Home</a>");
            var segs = PathHelper.Normalize("/", vpath);
            string acc = "";
            foreach (var seg in segs)
            {
                acc += "/" + seg;
                sb.Append(" / <a href=\"/?path=" + Url(acc) + "\">" + Html(seg) + "</a>");
            }
            sb.Append("</div>");

            if (segs.Count > 0)
            {
                var parentSegs = segs.GetRange(0, segs.Count - 1);
                sb.Append("<a class=\"row\" href=\"/?path=" + Url(PathHelper.ToVirtual(parentSegs)) + "\">&#11013; .. (parent)</a>");
            }

            foreach (var f in folders)
            {
                string child = (vpath == "/" ? "" : vpath) + "/" + f.Name;
                sb.Append("<div class=\"row\"><a href=\"/?path=" + Url(child) + "\">&#128193; " + Html(f.Name) + "</a>");
                if (_settings.AllowWrites)
                    sb.Append("<button class=\"del\" onclick=\"del('" + Js(PathHelper.ToVirtual(PathHelper.Normalize("/", child))) + "')\">Delete</button>");
                sb.Append("<div class=\"meta\">Folder &middot; " + f.DateCreated.ToString("yyyy-MM-dd HH:mm") + "</div></div>");
            }
            foreach (var f in files)
            {
                string child = (vpath == "/" ? "" : vpath) + "/" + f.Name;
                ulong size = (await f.GetBasicPropertiesAsync()).Size;
                sb.Append("<div class=\"row\"><a href=\"/?path=" + Url(child) + "\">&#128196; " + Html(f.Name) + "</a>");
                if (_settings.AllowWrites)
                    sb.Append("<button class=\"del\" onclick=\"del('" + Js(PathHelper.ToVirtual(PathHelper.Normalize("/", child))) + "')\">Delete</button>");
                sb.Append("<div class=\"meta\">" + ContentTypes.HumanSize(size) + " &middot; " + f.DateCreated.ToString("yyyy-MM-dd HH:mm") + "</div></div>");
            }
            if (folders.Count == 0 && files.Count == 0) sb.Append("<p class=\"meta\">This folder is empty.</p>");

            if (_settings.AllowWrites)
            {
                sb.Append("<div class=\"box\"><h3>Upload files here</h3>");
                sb.Append("<input type=\"file\" id=\"files\" multiple style=\"font-size:16px;\"><br><br>");
                sb.Append("<button onclick=\"upload()\">Upload</button>");
                sb.Append("<div id=\"barwrap\"><div id=\"bar\"></div></div><div id=\"status\" class=\"meta\"></div></div>");

                sb.Append("<div class=\"box\"><h3>New folder</h3>");
                sb.Append("<input type=\"text\" id=\"newname\" placeholder=\"Folder name\"> ");
                sb.Append("<button onclick=\"mkdir()\">Create</button></div>");
            }
            else
            {
                sb.Append("<p class=\"meta\">Read-only mode — uploads, new folders and delete are disabled.</p>");
            }

            sb.Append("<script>");
            sb.Append("var cur='" + Js(vpath) + "';");
            sb.Append("function post(url,body,done){var x=new XMLHttpRequest();x.open('POST',url,true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onload=function(){done(x.status);};x.onerror=function(){done(0);};x.send(body);}");
            sb.Append("function del(p){if(!confirm('Delete '+p+'?'))return;post('/api/delete','path='+encodeURIComponent(p),function(s){if(s==200)location.reload();else alert('Delete failed ('+s+')');});}");
            sb.Append("function mkdir(){var n=document.getElementById('newname').value;if(!n)return;post('/api/mkdir','path='+encodeURIComponent(cur)+'&name='+encodeURIComponent(n),function(s){if(s==200)location.reload();else alert('Create failed ('+s+')');});}");
            sb.Append("function upload(){var inp=document.getElementById('files');if(!inp.files.length)return;var i=0;function next(){if(i>=inp.files.length){location.reload();return;}var f=inp.files[i];var x=new XMLHttpRequest();x.open('POST','/api/upload?path='+encodeURIComponent(cur)+'&name='+encodeURIComponent(f.name),true);x.upload.onprogress=function(e){if(e.lengthComputable){var pc=Math.round(e.loaded*100/e.total);document.getElementById('barwrap').style.display='block';document.getElementById('bar').style.width=pc+'%';document.getElementById('status').innerHTML='Uploading '+f.name+' — '+pc+'%';}};x.onload=function(){i++;next();};x.onerror=function(){alert('Upload failed: '+f.name);};x.send(f);}next();}");
            sb.Append("</script>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        static void ParseQuery(string qs, Dictionary<string, string> dict)
        {
            foreach (var pair in qs.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq < 0) dict[UrlDecode(pair)] = "";
                else dict[UrlDecode(pair.Substring(0, eq))] = UrlDecode(pair.Substring(eq + 1));
            }
        }

        static string UrlDecode(string s)
        {
            if (s == null) return "";
            var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%' && i + 2 < s.Length + 1 && i + 2 <= s.Length - 1)
                {
                    int hi = HexVal(s[i + 1]), lo = HexVal(s[i + 2]);
                    if (hi >= 0 && lo >= 0) { bytes.Add((byte)(hi * 16 + lo)); i += 2; continue; }
                }
                if (c == '+') { bytes.Add((byte)' '); continue; }
                foreach (var b in Encoding.UTF8.GetBytes(new string(c, 1))) bytes.Add(b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static string Url(string path)
        {
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        static string Html(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string Js(string s)
        {
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage;

namespace FtpServer.Server
{
    class FtpSession
    {
        readonly StreamSocket _socket;
        readonly StorageFolder _home;
        readonly ServerSettings _settings;
        StreamReader _reader;
        StreamWriter _writer;
        string _cwd = "/";
        string _pendingUser;
        bool _loggedIn;
        string _renameFrom; // virtual path
        StreamSocketListener _pasvListener;
        TaskCompletionSource<StreamSocket> _pasvAccept;

        public FtpSession(StreamSocket socket, StorageFolder home, ServerSettings settings)
        {
            _socket = socket;
            _home = home;
            _settings = settings;
        }

        async Task Send(string line)
        {
            await _writer.WriteLineAsync(line);
            await _writer.FlushAsync();
        }

        public async Task RunAsync()
        {
            try
            {
                var input = _socket.InputStream.AsStreamForRead(8192);
                var output = _socket.OutputStream.AsStreamForWrite();
                _reader = new StreamReader(input, new UTF8Encoding(false));
                _writer = new StreamWriter(output, new UTF8Encoding(false));
                _writer.NewLine = "\r\n";

                await Send("220 WiFi FTP Server (Lumia) ready");
                string line;
                while ((line = await _reader.ReadLineAsync()) != null)
                {
                    if (line.Length == 0) continue;
                    string cmd, arg;
                    int sp = line.IndexOf(' ');
                    if (sp < 0) { cmd = line.Trim().ToUpperInvariant(); arg = ""; }
                    else { cmd = line.Substring(0, sp).Trim().ToUpperInvariant(); arg = line.Substring(sp + 1); }

                    bool keepGoing = await Handle(cmd, arg);
                    if (!keepGoing) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("FTP session error: " + ex.Message);
            }
            finally
            {
                ClosePasv();
            }
        }

        async Task<bool> Handle(string cmd, string arg)
        {
            switch (cmd)
            {
                case "USER":
                    _pendingUser = arg;
                    _loggedIn = false;
                    if (ServerSettings.IsAnonymousUser(arg) && _settings.Anonymous)
                        await Send("331 Anonymous access allowed, send any password");
                    else
                        await Send("331 Password required");
                    return true;
                case "PASS":
                    if (_pendingUser == null) { await Send("503 Login with USER first"); return true; }
                    if (ServerSettings.IsAnonymousUser(_pendingUser) && _settings.Anonymous) _loggedIn = true;
                    else if (_settings.CheckUser(_pendingUser, arg)) _loggedIn = true;
                    if (_loggedIn)
                    {
                        Logger.Log("FTP login ok: " + _pendingUser + " from " + _socket.Information.RemoteAddress);
                        await Send("230 Login successful");
                    }
                    else
                    {
                        Logger.Log("FTP login failed: " + _pendingUser);
                        await Send("530 Login incorrect");
                    }
                    return true;
                case "QUIT":
                    await Send("221 Goodbye");
                    return false;
                case "NOOP": await Send("200 OK"); return true;
                case "SYST": await Send("215 UNIX Type: L8"); return true;
                case "FEAT":
                    await Send("211-Features:");
                    await Send(" UTF8");
                    await Send("211 End");
                    return true;
                case "OPTS":
                    await Send("200 OK");
                    return true;
            }

            if (!_loggedIn) { await Send("530 Not logged in"); return true; }

            switch (cmd)
            {
                case "TYPE": await Send("200 Type set to " + (arg.StartsWith("A") ? "A" : "I")); break;
                case "PWD":
                case "XPWD": await Send("257 \"" + _cwd + "\" is the current directory"); break;
                case "CWD": await CmdCwd(arg); break;
                case "CDUP":
                case "XCUP":
                    _cwd = PathHelper.ToVirtual(PathHelper.Normalize(_cwd, ".."));
                    await Send("250 Directory changed to " + _cwd);
                    break;
                case "PASV": await CmdPasv(false); break;
                case "EPSV": await CmdPasv(true); break;
                case "PORT":
                case "EPRT": await Send("502 Active mode not supported, use passive (PASV/EPSV)"); break;
                case "LIST": await CmdList(arg, false); break;
                case "NLST": await CmdList(arg, true); break;
                case "MLSD": await CmdMlsd(arg); break;
                case "RETR": await CmdRetr(arg); break;
                case "STOR": await CmdStor(arg); break;
                case "DELE": await CmdDelete(arg, false); break;
                case "RMD": await CmdDelete(arg, true); break;
                case "MKD": await CmdMkd(arg); break;
                case "RNFR": await CmdRnfr(arg); break;
                case "RNTO": await CmdRnto(arg); break;
                case "SIZE": await CmdSize(arg); break;
                case "MDTM": await CmdMdtm(arg); break;
                default: await Send("502 Command not implemented"); break;
            }
            return true;
        }

        async Task CmdCwd(string arg)
        {
            var segs = PathHelper.Normalize(_cwd, arg);
            var folder = await PathHelper.GetFolderAsync(_home, segs, false);
            if (folder == null) { await Send("550 Folder not found"); return; }
            _cwd = PathHelper.ToVirtual(segs);
            await Send("250 Directory changed to " + _cwd);
        }

        void ClosePasv()
        {
            if (_pasvListener != null) { try { _pasvListener.Dispose(); } catch { } _pasvListener = null; }
            _pasvAccept = null;
        }

        async Task CmdPasv(bool extended)
        {
            ClosePasv();
            var listener = new StreamSocketListener();
            var tcs = new TaskCompletionSource<StreamSocket>();
            listener.ConnectionReceived += (s, e) => tcs.TrySetResult(e.Socket);
            await listener.BindServiceNameAsync("0");
            _pasvListener = listener;
            _pasvAccept = tcs;
            int port = int.Parse(listener.Information.LocalPort);
            if (extended)
            {
                await Send("229 Entering Extended Passive Mode (|||" + port + "|)");
            }
            else
            {
                string ip = _socket.Information.LocalAddress.ToString().Replace('.', ',');
                await Send("227 Entering Passive Mode (" + ip + "," + (port / 256) + "," + (port % 256) + ")");
            }
        }

        async Task<StreamSocket> AcceptDataAsync()
        {
            var tcs = _pasvAccept;
            if (tcs == null) return null;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(15000));
            ClosePasv();
            if (done != tcs.Task) return null;
            return await tcs.Task;
        }

        // Resolve the listing target: arg may be empty, a folder, or a file.
        // Returns the folder to list (the parent folder if arg was a file).
        async Task<StorageFolder> ListTargetFolder(string arg)
        {
            var segs = PathHelper.Normalize(_cwd, string.IsNullOrEmpty(arg) ? "" : arg);
            var item = await PathHelper.GetItemAsync(_home, segs);
            if (item is StorageFile)
            {
                segs = segs.GetRange(0, segs.Count - 1);
                return await PathHelper.GetFolderAsync(_home, segs, false);
            }
            return item as StorageFolder;
        }

        static string UnixLine(IStorageItem item, ulong size)
        {
            bool dir = item is StorageFolder;
            string date = item.DateCreated.ToString("MMM dd yyyy", CultureInfo.InvariantCulture);
            return (dir ? "drwxr-xr-x" : "-rw-r--r--") + " 1 owner group "
                + (dir ? 0 : (long)size) + " " + date + " " + item.Name;
        }

        async Task CmdList(string arg, bool namesOnly)
        {
            string singleFile = null;
            if (!string.IsNullOrEmpty(arg))
            {
                var checkSegs = PathHelper.Normalize(_cwd, arg);
                var checkItem = await PathHelper.GetItemAsync(_home, checkSegs);
                if (checkItem is StorageFile) singleFile = checkItem.Name;
            }
            var folder = await ListTargetFolder(arg);
            if (folder == null) { await Send("550 Folder not found"); return; }
            await Send("150 Opening data connection for listing");
            var data = await AcceptDataAsync();
            if (data == null) { await Send("425 No data connection"); return; }
            try
            {
                var w = new StreamWriter(data.OutputStream.AsStreamForWrite(), new UTF8Encoding(false));
                w.NewLine = "\r\n";
                if (singleFile != null)
                {
                    var f = await folder.GetFileAsync(singleFile);
                    var props = await f.GetBasicPropertiesAsync();
                    await w.WriteLineAsync(namesOnly ? f.Name : UnixLine(f, props.Size));
                }
                else
                {
                    var items = await folder.GetItemsAsync();
                    foreach (var item in items)
                    {
                        if (namesOnly) { await w.WriteLineAsync(item.Name); continue; }
                        ulong size = 0;
                        var file = item as StorageFile;
                        if (file != null) size = (await file.GetBasicPropertiesAsync()).Size;
                        await w.WriteLineAsync(UnixLine(item, size));
                    }
                }
                await w.FlushAsync();
            }
            catch (Exception ex) { Logger.Log("FTP LIST error: " + ex.Message); }
            try { data.Dispose(); } catch { }
            await Send("226 Directory listing sent");
        }

        async Task CmdMlsd(string arg)
        {
            var folder = await ListTargetFolder(arg);
            if (folder == null) { await Send("550 Folder not found"); return; }
            await Send("150 Opening data connection for MLSD");
            var data = await AcceptDataAsync();
            if (data == null) { await Send("425 No data connection"); return; }
            try
            {
                var w = new StreamWriter(data.OutputStream.AsStreamForWrite(), new UTF8Encoding(false));
                w.NewLine = "\r\n";
                var items = await folder.GetItemsAsync();
                foreach (var item in items)
                {
                    string modify = item.DateCreated.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                    if (item is StorageFolder)
                        await w.WriteLineAsync("Type=dir;Modify=" + modify + "; " + item.Name);
                    else
                    {
                        var file = (StorageFile)item;
                        ulong size = (await file.GetBasicPropertiesAsync()).Size;
                        await w.WriteLineAsync("Type=file;Size=" + size + ";Modify=" + modify + "; " + item.Name);
                    }
                }
                await w.FlushAsync();
            }
            catch (Exception ex) { Logger.Log("FTP MLSD error: " + ex.Message); }
            try { data.Dispose(); } catch { }
            await Send("226 Directory listing sent");
        }

        async Task CmdRetr(string arg)
        {
            var segs = PathHelper.Normalize(_cwd, arg);
            var file = await PathHelper.GetItemAsync(_home, segs) as StorageFile;
            if (file == null) { await Send("550 File not found"); return; }
            await Send("150 Opening data connection for " + file.Name);
            var data = await AcceptDataAsync();
            if (data == null) { await Send("425 No data connection"); return; }
            long total = 0;
            try
            {
                using (var src = await file.OpenStreamForReadAsync())
                {
                    var dst = data.OutputStream.AsStreamForWrite();
                    var buf = new byte[65536];
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        total += n;
                    }
                    await dst.FlushAsync();
                }
            }
            catch (Exception ex) { Logger.Log("FTP RETR error: " + ex.Message); }
            try { data.Dispose(); } catch { }
            Logger.Log("FTP download " + file.Name + " (" + total + " bytes)");
            await Send("226 Transfer complete");
        }

        async Task CmdStor(string arg)
        {
            if (!_settings.AllowWrites) { await Send("550 Read-only mode: uploads disabled"); return; }
            var segs = PathHelper.Normalize(_cwd, arg);
            if (segs.Count == 0) { await Send("550 Bad file name"); return; }
            var parent = await PathHelper.GetParentAsync(_home, segs, true);
            if (parent == null) { await Send("550 Cannot create folder"); return; }
            string name = segs[segs.Count - 1];
            await Send("150 Ready to receive " + name);
            var data = await AcceptDataAsync();
            if (data == null) { await Send("425 No data connection"); return; }
            long total = 0;
            try
            {
                var file = await parent.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                using (var dst = await file.OpenStreamForWriteAsync())
                {
                    var src = data.InputStream.AsStreamForRead(65536);
                    var buf = new byte[65536];
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        total += n;
                    }
                    await dst.FlushAsync();
                }
            }
            catch (Exception ex) { Logger.Log("FTP STOR error: " + ex.Message); }
            try { data.Dispose(); } catch { }
            Logger.Log("FTP upload " + name + " (" + total + " bytes)");
            await Send("226 Transfer complete");
        }

        async Task CmdDelete(string arg, bool folderOnly)
        {
            if (!_settings.AllowWrites) { await Send("550 Read-only mode"); return; }
            var segs = PathHelper.Normalize(_cwd, arg);
            if (segs.Count == 0) { await Send("550 Cannot delete the home folder"); return; }
            var item = await PathHelper.GetItemAsync(_home, segs);
            if (item == null) { await Send("550 Not found"); return; }
            if (folderOnly && !(item is StorageFolder)) { await Send("550 Not a folder"); return; }
            if (!folderOnly && !(item is StorageFile)) { await Send("550 Not a file"); return; }
            try
            {
                await item.DeleteAsync();
                Logger.Log("FTP deleted " + PathHelper.ToVirtual(segs));
                await Send("250 Deleted");
            }
            catch (Exception ex) { await Send("550 Delete failed: " + ex.Message); }
        }

        async Task CmdMkd(string arg)
        {
            if (!_settings.AllowWrites) { await Send("550 Read-only mode"); return; }
            var segs = PathHelper.Normalize(_cwd, arg);
            if (segs.Count == 0) { await Send("550 Bad folder name"); return; }
            var parent = await PathHelper.GetParentAsync(_home, segs, true);
            if (parent == null) { await Send("550 Cannot create folder"); return; }
            try
            {
                await parent.CreateFolderAsync(segs[segs.Count - 1], CreationCollisionOption.FailIfExists);
                await Send("257 \"" + PathHelper.ToVirtual(segs) + "\" created");
            }
            catch (Exception ex) { await Send("550 Create failed: " + ex.Message); }
        }

        async Task CmdRnfr(string arg)
        {
            if (!_settings.AllowWrites) { await Send("550 Read-only mode"); return; }
            var segs = PathHelper.Normalize(_cwd, arg);
            var item = await PathHelper.GetItemAsync(_home, segs);
            if (item == null) { await Send("550 Not found"); return; }
            _renameFrom = PathHelper.ToVirtual(segs);
            await Send("350 Ready for RNTO");
        }

        async Task CmdRnto(string arg)
        {
            if (!_settings.AllowWrites) { await Send("550 Read-only mode"); return; }
            if (_renameFrom == null) { await Send("503 RNFR first"); return; }
            var fromSegs = PathHelper.Normalize("/", _renameFrom);
            var item = await PathHelper.GetItemAsync(_home, fromSegs);
            _renameFrom = null;
            if (item == null) { await Send("550 Not found"); return; }
            var toSegs = PathHelper.Normalize(_cwd, arg);
            if (toSegs.Count == 0) { await Send("550 Bad name"); return; }
            try
            {
                // Rename in place (same folder). Cross-folder move is not
                // supported by the storage API for arbitrary folders.
                await item.RenameAsync(toSegs[toSegs.Count - 1], NameCollisionOption.FailIfExists);
                Logger.Log("FTP renamed to " + toSegs[toSegs.Count - 1]);
                await Send("250 Renamed");
            }
            catch (Exception ex) { await Send("550 Rename failed: " + ex.Message); }
        }

        async Task CmdSize(string arg)
        {
            var segs = PathHelper.Normalize(_cwd, arg);
            var file = await PathHelper.GetItemAsync(_home, segs) as StorageFile;
            if (file == null) { await Send("550 File not found"); return; }
            var props = await file.GetBasicPropertiesAsync();
            await Send("213 " + props.Size);
        }

        async Task CmdMdtm(string arg)
        {
            var segs = PathHelper.Normalize(_cwd, arg);
            var item = await PathHelper.GetItemAsync(_home, segs);
            if (item == null) { await Send("550 Not found"); return; }
            await Send("213 " + item.DateCreated.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        }
    }
}

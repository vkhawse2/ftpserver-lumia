using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage;

namespace FtpServer.Server
{
    public class FtpServerEngine
    {
        StreamSocketListener _listener;
        readonly List<StreamSocket> _sessions = new List<StreamSocket>();

        public bool IsRunning { get { return _listener != null; } }

        public async Task StartAsync(StorageFolder home, ServerSettings settings)
        {
            Stop();
            var listener = new StreamSocketListener();
            listener.ConnectionReceived += (sender, args) =>
            {
                var socket = args.Socket;
                lock (_sessions) _sessions.Add(socket);
                Logger.Log("FTP connection from " + socket.Information.RemoteAddress);
                var session = new FtpSession(socket, home, settings);
                var ignored = session.RunAsync().ContinueWith(t =>
                {
                    lock (_sessions) _sessions.Remove(socket);
                    try { socket.Dispose(); } catch { }
                });
            };
            await listener.BindServiceNameAsync(settings.FtpPort.ToString());
            _listener = listener;
            Logger.Log("FTP server listening on port " + settings.FtpPort);
        }

        public void Stop()
        {
            if (_listener != null)
            {
                try { _listener.Dispose(); } catch { }
                _listener = null;
            }
            lock (_sessions)
            {
                foreach (var s in _sessions) { try { s.Dispose(); } catch { } }
                _sessions.Clear();
            }
        }
    }
}

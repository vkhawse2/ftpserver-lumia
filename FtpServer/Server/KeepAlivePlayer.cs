using System;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Core;

namespace FtpServer.Server
{
    // VLC-style background keep-alive.
    //
    // With the backgroundMediaPlayback capability declared, Windows keeps
    // a media-playing app's process alive in the background and under the
    // lock screen — that is the whole of VLC's background trick on
    // Windows 10 Mobile (its package declares only that capability).
    // AirTile is not a media app, but while a file transfer is running it
    // "plays" a 2-second silent WAV on loop through a MediaPlayer, so the
    // FTP/web servers in the same process keep running too.
    //
    // Playback starts when the first transfer begins and stops 30 s
    // after the last one ends (the grace period covers the gaps between
    // files of a multi-file copy). Every failure is caught and logged —
    // a keep-alive failure must never break the servers.
    public static class KeepAlivePlayer
    {
        public static event Action<string> StateChanged;

        static readonly object LockObj = new object();
        static MediaPlayer _player;
        static CoreDispatcher _dispatcher;
        static bool _enabled;
        static bool _serverRunning;
        static bool _playing;
        static int _activeTransfers;
        static Timer _graceTimer;

        // Called once from MainPage, with the UI dispatcher. All
        // MediaPlayer calls are marshalled onto it.
        public static void Initialize(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public static void SetEnabled(bool enabled)
        {
            lock (LockObj) _enabled = enabled;
        }

        public static void ServerStarted()
        {
            bool enabled;
            lock (LockObj)
            {
                _serverRunning = true;
                _activeTransfers = 0;
                CancelGraceLocked();
                enabled = _enabled;
            }
            Report(enabled ? "On — waiting for a transfer" : "Off");
        }

        public static void ServerStopped()
        {
            lock (LockObj)
            {
                _serverRunning = false;
                _activeTransfers = 0;
                CancelGraceLocked();
            }
            PauseNow("Off", false);
        }

        public static void TransferStarted()
        {
            bool shouldPlay = false;
            lock (LockObj)
            {
                CancelGraceLocked();
                _activeTransfers++;
                if (_enabled && _serverRunning && _activeTransfers == 1 && !_playing)
                    shouldPlay = true;
            }
            if (shouldPlay) PlayNow();
        }

        public static void TransferEnded()
        {
            lock (LockObj)
            {
                if (_activeTransfers > 0) _activeTransfers--;
                if (_activeTransfers == 0 && _enabled && _serverRunning && _playing)
                {
                    CancelGraceLocked();
                    _graceTimer = new Timer(GraceExpired, null, 30000, Timeout.Infinite);
                    Report("On — grace period");
                }
            }
        }

        static void GraceExpired(object state)
        {
            bool stop = false;
            lock (LockObj)
            {
                _graceTimer = null;
                if (_activeTransfers == 0 && _playing) stop = true;
            }
            if (stop)
            {
                Logger.Log("Background mode OFF");
                PauseNow("On — idle", false);
            }
        }

        static void CancelGraceLocked()
        {
            if (_graceTimer != null)
            {
                try { _graceTimer.Dispose(); } catch { }
                _graceTimer = null;
            }
        }

        static void PlayNow()
        {
            RunOnUi(() =>
            {
                lock (LockObj)
                {
                    if (!_enabled || !_serverRunning || _playing) return;
                }
                try
                {
                    if (_player == null)
                    {
                        _player = new MediaPlayer();
                        _player.Source = MediaSource.CreateFromUri(new Uri("ms-appx:///Assets/silence.wav"));
                        _player.IsLoopingEnabled = true;
                        _player.Volume = 0;
                        _player.AudioCategory = MediaPlayerAudioCategory.Other;
                        // Not a real media app: never take over the
                        // lock-screen / volume media controls.
                        _player.CommandManager.IsEnabled = false;
                    }
                    _player.Play();
                    lock (LockObj) _playing = true;
                    Logger.Log("Background mode ON (transfer active)");
                    Report("On — transfer active");
                }
                catch (Exception ex)
                {
                    Logger.Log("Background mode failed to start: " + ex.Message);
                    Report("On — failed to start (see log)");
                }
            });
        }

        static void PauseNow(string report, bool log)
        {
            RunOnUi(() =>
            {
                try { if (_player != null) _player.Pause(); }
                catch (Exception ex) { Logger.Log("Background mode pause failed: " + ex.Message); }
                lock (LockObj) _playing = false;
                if (report != null) Report(report);
            });
        }

        static void RunOnUi(Action action)
        {
            var d = _dispatcher;
            if (d == null)
            {
                try { action(); }
                catch (Exception ex) { Logger.Log("Background mode error: " + ex.Message); }
                return;
            }
            var ignored = d.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { action(); }
                catch (Exception ex) { Logger.Log("Background mode error: " + ex.Message); }
            });
        }

        static void Report(string state)
        {
            var handler = StateChanged;
            if (handler != null)
            {
                try { handler(state); }
                catch { }
            }
        }
    }
}

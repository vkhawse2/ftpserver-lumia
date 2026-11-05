using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.ApplicationModel.ExtendedExecution.Foreground;
using Windows.Foundation.Metadata;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.System.Display;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using FtpServer.Server;

namespace FtpServer
{
    public sealed partial class MainPage : Page
    {
        readonly FtpServerEngine _ftp = new FtpServerEngine();
        readonly WebServerEngine _web = new WebServerEngine();
        readonly List<string> _logLines = new List<string>();
        StorageFolder _homeFolder;
        bool _running;
        DisplayRequest _displayRequest;
        bool _displayActive;
        ExtendedExecutionSession _eeSession;
        object _eeForegroundSession;

        public MainPage()
        {
            InitializeComponent();
            Logger.OnLog += OnLogLine;
            Loaded += MainPage_Loaded;
        }

        async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            var s = ServerSettings.Load();
            FtpPortBox.Text = s.FtpPort.ToString();
            WebPortBox.Text = s.WebPort.ToString();
            AnonToggle.IsOn = s.Anonymous;
            UserBox.Text = s.Username;
            PassBox.Password = s.Password;
            WriteToggle.IsOn = s.AllowWrites;
            ScreenOnToggle.IsOn = s.KeepScreenOn;
            LockToggle.IsOn = s.RunUnderLock;

            _homeFolder = await RestoreHomeFolderAsync();
            UpdateHomeText();
        }

        async Task<StorageFolder> RestoreHomeFolderAsync()
        {
            try
            {
                var fal = StorageApplicationPermissions.FutureAccessList;
                if (fal.ContainsItem("homeFolder"))
                    return await fal.GetFolderAsync("homeFolder");
            }
            catch (Exception ex)
            {
                Logger.Log("Home folder restore failed: " + ex.Message);
            }
            return ApplicationData.Current.LocalFolder;
        }

        bool HasPickedHome
        {
            get { return StorageApplicationPermissions.FutureAccessList.ContainsItem("homeFolder"); }
        }

        void UpdateHomeText()
        {
            if (_homeFolder == null) { HomeText.Text = "(none)"; return; }
            string path = string.IsNullOrEmpty(_homeFolder.Path) ? _homeFolder.Name : _homeFolder.Path;
            if (HasPickedHome)
                HomeText.Text = _homeFolder.Name + "\n" + path;
            else
                HomeText.Text = "No folder picked yet — serving app folder. Pick your SD card / phone folder to share real files.\n(" + path + ")";
        }

        async void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            StorageApplicationPermissions.FutureAccessList.AddOrReplace("homeFolder", folder);
            _homeFolder = folder;
            UpdateHomeText();
            Logger.Log("Home folder set to " + folder.Name);
            if (_running) Logger.Log("Restart the server to use the new home folder");
        }

        void Setting_Toggled(object sender, RoutedEventArgs e) { }

        ServerSettings ReadSettingsFromUi()
        {
            var s = new ServerSettings();
            int port;
            s.FtpPort = int.TryParse(FtpPortBox.Text, out port) ? port : 2221;
            s.WebPort = int.TryParse(WebPortBox.Text, out port) ? port : 8080;
            s.Anonymous = AnonToggle.IsOn;
            s.Username = string.IsNullOrWhiteSpace(UserBox.Text) ? "lumia" : UserBox.Text.Trim();
            s.Password = PassBox.Password ?? "";
            s.AllowWrites = WriteToggle.IsOn;
            s.KeepScreenOn = ScreenOnToggle.IsOn;
            s.RunUnderLock = LockToggle.IsOn;
            s.Save();
            return s;
        }

        static List<string> GetLocalIps()
        {
            var ips = new List<string>();
            foreach (var hn in NetworkInformation.GetHostNames())
            {
                if (hn.Type != HostNameType.Ipv4) continue;
                string ip = hn.CanonicalName;
                if (string.IsNullOrEmpty(ip)) continue;
                if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                if (!ips.Contains(ip)) ips.Add(ip);
            }
            return ips;
        }

        async void StartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                _ftp.Stop();
                _web.Stop();
                StopProtection();
                _running = false;
                StatusText.Text = "Stopped";
                StartStopButton.Content = "Start server";
                AddrText.Text = "";
                Logger.Log("Servers stopped");
                return;
            }

            if (_homeFolder == null) _homeFolder = ApplicationData.Current.LocalFolder;
            var settings = ReadSettingsFromUi();
            try
            {
                await _ftp.StartAsync(_homeFolder, settings);
                await _web.StartAsync(_homeFolder, settings);
            }
            catch (Exception ex)
            {
                _ftp.Stop();
                _web.Stop();
                StopProtection();
                Logger.Log("Start failed: " + ex.Message);
                StatusText.Text = "Start failed";
                return;
            }
            _running = true;
            AcquireDisplayRequest(settings);
            await StartExtendedExecutionAsync(settings);
            StatusText.Text = "Running";
            StartStopButton.Content = "Stop server";
            var ips = GetLocalIps();
            if (ips.Count == 0)
            {
                AddrText.Text = "No Wi-Fi address found — connect the phone to Wi-Fi, then check Settings > Wi-Fi for its address.";
            }
            else
            {
                var lines = new List<string>();
                foreach (var ip in ips)
                {
                    lines.Add("ftp://" + ip + ":" + settings.FtpPort);
                    lines.Add("http://" + ip + ":" + settings.WebPort);
                }
                AddrText.Text = string.Join("\n", lines.ToArray());
            }
            Logger.Log("Sharing folder: " + _homeFolder.Name);
        }

        // ---- Lock-screen protection (v1.1) ----

        void AcquireDisplayRequest(ServerSettings settings)
        {
            if (!settings.KeepScreenOn) { Logger.Log("Keep screen on: off (setting)"); return; }
            try
            {
                if (_displayRequest == null) _displayRequest = new DisplayRequest();
                if (!_displayActive)
                {
                    _displayRequest.RequestActive();
                    _displayActive = true;
                    Logger.Log("Keep screen on: active");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Keep screen on failed: " + ex.Message);
            }
        }

        void ReleaseDisplayRequest()
        {
            if (!_displayActive || _displayRequest == null) return;
            try { _displayRequest.RequestRelease(); }
            catch (Exception ex) { Logger.Log("Keep screen on release failed: " + ex.Message); }
            _displayActive = false;
        }

        async Task StartExtendedExecutionAsync(ServerSettings settings)
        {
            if (!settings.RunUnderLock)
            {
                LockText.Text = "Lock-screen mode: Off";
                Logger.Log("Run under lock screen: off (setting)");
                return;
            }

            // Preferred: the foreground extended-execution session, which is
            // the one designed to keep a foreground app running under the
            // lock screen. Guarded — the type may not exist on this OS build.
            if (ApiInformation.IsTypePresent("Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundSession"))
            {
                ExtendedExecutionForegroundSession fg = null;
                try
                {
                    fg = new ExtendedExecutionForegroundSession();
                    fg.Reason = ExtendedExecutionForegroundReason.Unspecified;
                    fg.Description = "WiFi FTP Server transfers";
                    fg.Revoked += ForegroundSession_Revoked;
                    var result = await fg.RequestExtensionAsync();
                    Logger.Log("Extended execution (foreground) result: " + result);
                    if (result == ExtendedExecutionForegroundResult.Allowed)
                    {
                        _eeForegroundSession = fg;
                        LockText.Text = "Lock-screen mode: Allowed";
                        return;
                    }
                    LockText.Text = "Lock-screen mode: Denied — keep screen on instead";
                    fg.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log("Extended execution (foreground) failed: " + ex.Message);
                    try { if (fg != null) fg.Dispose(); } catch { }
                }
            }
            else
            {
                Logger.Log("Extended execution (foreground) not available on this OS — using basic session");
            }

            // Fallback: a plain extended-execution session.
            try
            {
                var session = new ExtendedExecutionSession();
                session.Reason = ExtendedExecutionReason.Unspecified;
                session.Description = "WiFi FTP Server transfers";
                session.Revoked += EeSession_Revoked;
                var result = await session.RequestExtensionAsync();
                Logger.Log("Extended execution result: " + result);
                if (result == ExtendedExecutionResult.Allowed)
                {
                    _eeSession = session;
                    LockText.Text = "Lock-screen mode: Allowed";
                }
                else
                {
                    LockText.Text = "Lock-screen mode: Denied — keep screen on instead";
                    session.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Extended execution failed: " + ex.Message);
                LockText.Text = "Lock-screen mode: Denied — keep screen on instead";
            }
        }

        async void OnLockProtectionLost(string reason)
        {
            Logger.Log("Extended execution revoked: " + reason + " — transfers may stop when the phone is locked");
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    LockText.Text = "Lock protection lost: " + reason;
                });
            }
            catch { }
        }

        void ForegroundSession_Revoked(object sender, ExtendedExecutionForegroundRevokedEventArgs args)
        {
            _eeForegroundSession = null;
            OnLockProtectionLost(args.Reason.ToString());
        }

        void EeSession_Revoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            _eeSession = null;
            OnLockProtectionLost(args.Reason.ToString());
        }

        void StopProtection()
        {
            ReleaseDisplayRequest();
            try { if (_eeSession != null) _eeSession.Dispose(); } catch { }
            _eeSession = null;
            try { var fg = _eeForegroundSession as IDisposable; if (fg != null) fg.Dispose(); } catch { }
            _eeForegroundSession = null;
            LockText.Text = "";
        }

        async void OnLogLine(string line)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                _logLines.Add(line);
                while (_logLines.Count > 100) _logLines.RemoveAt(0);
                LogBox.Text = string.Join("\n", _logLines.ToArray());
            });
        }
    }
}

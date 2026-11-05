# AirTile — WiFi FTP Server for Lumia 540 (Windows 10 Mobile)

An FTP server + web file browser for Vishal's Lumia 540 (Windows 10
Mobile build 14393, ARM), modelled on MedhaApps' "WiFi FTP Server Pro"
for Android: configurable port, anonymous access, home folder and
username/password, accessed from FileZilla / Windows Explorer over
Wi-Fi or hotspot. Personal sideload.

## Features (v1)

- **FTP server** (passive mode, UTF-8 filenames): USER/PASS auth
  (anonymous toggle, or a configured username/password), LIST/NLST/MLSD,
  RETR/STOR streamed in chunks, DELE/RMD/MKD/RNFR/RNTO, SIZE/MDTM.
  Default port **2221**.
- **Web file browser** on port **8080**: responsive page (works in a PC
  browser and the phone's Edge 38) with folder browsing, downloads,
  multi-file upload with a progress bar, new-folder and delete.
  Same home folder, same auth (HTTP Basic when anonymous is off),
  same read-only toggle.
- **Phone UI**: big Start/Stop, the `ftp://` and `http://` addresses to
  copy, home-folder picker (FolderPicker, remembered via
  FutureAccessList — pick the SD card root or a Music folder), settings
  saved to LocalSettings, and a 100-line log.

## v1.1 — keep screen on

v1 stopped transfers when the phone locked (W10M suspends the app).
v1.1 adds **Keep screen on while running** (toggleable in Settings,
persisted, default ON): a single `DisplayRequest`, active only while
the server runs, so the phone doesn't lock by itself mid-transfer.

## v1.4 — background features removed

At the user's request, v1.4 removes the two background experiments
completely — toggles, settings, capabilities and all related code:
**Run under lock screen** (v1.1/v1.3, extended execution) and
**Background mode (VLC style)** (v1.3, silent-audio keep-alive).
The server is back to the v1.2 behaviour: foreground app, with
Keep screen on as the only lock protection.

## Honest limitations

- **Plain FTP only.** UWP gives no server-side TLS listener, so there
  is no FTPS in v1. Home Wi-Fi only.
- Windows 10 Mobile suspends background apps: the server runs in
  the foreground only (v1.4 removed the background experiments);
  Keep screen on stops the phone locking by itself while it runs,
  but locking the phone or switching away suspends the app.
- The app can only share folders it has access to (libraries + the
  folder you pick), per the UWP sandbox.
- Not yet tested on the real phone — first install on the Lumia is
  the real test.

## Build

GitHub Actions (`.github/workflows/build.yml`, windows-2022):
self-signed cert (CN=Vishal) → MSBuild ARM Release, AppxBundle=Always →
published as a Release asset (`FtpServer-W10M.zip`) containing the
`.appxbundle`, dependency `.appx` files and the matching `.cer`.

## Install

See `INSTALL.txt`: install the `.cer` first, then dependencies if
needed, then the `.appxbundle`. Developer Mode must be ON.

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

## v1.1 — lock-screen fix

v1 stopped transfers when the phone locked (W10M suspends the app).
v1.1 adds, both toggleable in Settings (persisted, both default ON):
**Keep screen on while running** (a single `DisplayRequest`, active
only while the server runs) and **Run under lock screen** (a
runtime-guarded `ExtendedExecutionForegroundSession` requested on
Start, falling back to a plain `ExtendedExecutionSession`; the result
is shown in the status area and the log, revocations are logged
without stopping the listeners).

## Honest limitations

- **Plain FTP only.** UWP gives no server-side TLS listener, so there
  is no FTPS in v1. Home Wi-Fi only.
- The server runs **only while the app is in the foreground** — Windows
  10 Mobile suspends background apps.
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

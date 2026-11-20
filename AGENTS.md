# Velocity — project memory

Written for any agent (or human) picking this repo up. Read this first, then
`DEVELOPMENT_LOG.md` for the round-by-round history.

## What it is

Velocity is a native **Windows download manager**: WPF + .NET 10, with a Chromium/Firefox
extension and a native-messaging host that hands downloads from the browser to the app.

- Solution: `Velocity.slnx` — `Velocity.App` (WPF), `Velocity.Core` (models/contracts),
  `Velocity.Data` (SQLite), `Velocity.DownloadEngine`, `Velocity.Services`,
  `Velocity.BrowserHost` (native messaging), `tests/Velocity.Core.Tests`.
- Database: `%LOCALAPPDATA%\Velocity\velocity.db`.
- Docs: `ARCHITECTURE.md`, `README.md`, `USER_MANUAL.md`, `QA_CHECKLIST.md`,
  `EXTENSION-NOTES.md`, `VELOCITY_BROWSER_GUIDE.txt`, `PROJECT_PLAN.md`.

## Build, test, install

```powershell
dotnet build Velocity.slnx
dotnet test Velocity.slnx
powershell -ExecutionPolicy Bypass -File artifacts/build-installer.ps1   # ~6 min
```

The build writes `artifacts/velocity-portable/`, `artifacts/Velocity-1.0.0-portable.zip`
and `artifacts/installer/Velocity-Setup-1.0.0.exe`. Silent install / uninstall:

```bash
MSYS_NO_PATHCONV=1 timeout 220 ./artifacts/installer/Velocity-Setup-1.0.0.exe /S
./artifacts/installer/Velocity-Setup-1.0.0.exe /uninstall /S
```

Installed app: `%LOCALAPPDATA%\Programs\Velocity\Velocity.App.exe`, extension copied to
`%LOCALAPPDATA%\Programs\Velocity\browser-extension`.
**The Chrome extension the user actually runs is the unpacked folder in this repo**
(`browser-extension/chromium`) — edit here, then reload it in `chrome://extensions`
(or fully quit and reopen Chrome).

## Browser extension

- Files in `browser-extension/chromium/`: `background.js` (service worker: offer queue,
  download interception/`takeOver`, per-profile ledger), `detector.js` (page-side sniffing,
  interaction gating, dismissal), `content.js` (in-page prompt), `capture-decision.js`
  (pure "offer or ignore" rule), `manifest.json`, `options.*`, `popup.*`.
- Unpacked, no manifest `key`, so the extension ID derives from the folder path:
  `loopmagklpfneecglcankfihcephdbld`, shared by every Chrome profile that loads this folder.
- Native host manifest (only that ID is allowed):
  `%LOCALAPPDATA%\Velocity\native-host\com.velocity.download_manager.json` →
  `artifacts\browser-host\Velocity.BrowserHost.exe`.
- State lives in `chrome.storage.local`: `downloadedFiles` (the ledger, 1000 entries,
  statuses `velocity` | `chrome` | `offered` | `declined`), `cancelledDownloads`
  (300 Chrome item ids), `dismissedOffers` (500).

### Behaviour rules — do not regress these

1. Never offer a download on browser start, page load, page refresh or passive browsing.
2. Only offer when the user explicitly clicks a downloadable link or starts a download.
3. A file already downloaded, offered or declined is never offered again; completed
   downloads must not be recaptured after restarting Chrome or Velocity.
4. Chrome replays interrupted downloads from its own list at startup. Those must be
   cancelled silently: same item id as a cancelled capture, start time older than
   `RESTORED_MS` (2 min), or a known file with no tab behind it.
5. A deliberate fresh download started from a page is always still offered.
6. Normal navigation and user-started Chrome downloads must keep working.

Fix causes in the detection/decision logic. Do not paper over symptoms by hiding the
popup, muting notifications or adding timers.

## Tests

`artifacts/e2e/` holds real-Chrome harnesses (Python-free, Node + CDP). They launch a
throwaway Chrome profile each run.

```bash
node artifacts/e2e/server.js                                  # test server, port 8123
node artifacts/e2e/capture-decision.js                        # unit: offer/cancel rule
node artifacts/e2e/declined-downloads.js     "<chrome.exe>" 9476
node artifacts/e2e/popup-regression.js       "<chrome.exe>" 9475
node artifacts/e2e/detect.js                 "<chrome.exe>" 9477 Chrome <profile-name>
```

Chrome lives at `C:/Program Files/Google/Chrome/Application/chrome.exe`. `headless.js` is
the shared launcher; in headless CDP options-page targets `chrome.tabs`/`chrome.runtime`
are unavailable, so use `chrome.storage` only there. Stop the test server by the PID
listening on 8123 — never by process-name pattern.

## Conventions

- Append a round to `DEVELOPMENT_LOG.md` for every substantive change, including what was
  verified and how (that file is the project's memory of record).
- Keep commits local unless the user asks to push.
- Nothing here is a web app; there is no preview server to register for the .NET side.

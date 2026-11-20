# Browser integration (Chrome and Microsoft Edge)

Velocity ships one extension, `chromium/`, for Chromium browsers. It targets **Google Chrome** and **Microsoft Edge** only: both read the same Manifest V3 extension, the same native-messaging manifest, and the same registration script. Other browsers are not supported.

## Install

1. Publish the native host once:

   ```powershell
   .\publish-browser-host.ps1
   ```

2. Load `chromium/` unpacked in Chrome (`chrome://extensions` → Developer mode → Load unpacked) and in Edge (`edge://extensions` → Developer mode → Load unpacked). The ID is fixed by the public key in `chromium/manifest.json` — `jnfpmdnbiomjnelmlaohkpifaacandjg` — in every browser, profile and folder, so the host registration never has to be redone after moving or renaming anything. Load the installed copy (`%LOCALAPPDATA%\Programs\Velocity\browser-extension`) if you want a path that never changes.

3. Register the native host for the current user:

   ```powershell
   .\register-native-host.ps1 -HostExecutable "C:\path\to\Velocity.BrowserHost.exe" -ChromiumExtensionId "your-extension-id"
   ```

   The script writes the host manifest to `%LOCALAPPDATA%\Velocity\native-host\` and adds the registry keys Chrome and Edge read.

   After the first run this step is automatic: Velocity rewrites the manifest whenever the host executable moves and
   keeps the same `HKCU` keys for Chrome, Edge, Brave, Vivaldi and Chromium pointed at it. Nothing listens on a port,
   which is why a VPN, a proxy or a network change cannot break the hand-off.

4. Open the extension's options page to choose the download folder behaviour, the theme, and whether Velocity intercepts downloads. It also shows whether the native host answers.

## What interception does

A click on a link that looks like a file (`.zip`, `.exe`, `.msi`, `.pdf`, `.docx`, `.mp4`, …) is stopped and probed before anything reaches the browser's download manager. The probe asks the server for the response headers — `HEAD` first, falling back to `GET` for servers that reject it — so the offer shows the real file name (including `Content-Disposition` names), size, type, and whether the server supports resuming.

The offer shows **Download** (queue it in Velocity), **Download later** (queue it without starting), and **Keep in Chrome** (Chrome finishes the very transfer it had already started — or a fresh one, when the offer came from a click Chrome never turned into a download). Closing the offer, pressing Escape, or dismissing the popup is the cancel path: the held browser copy is cancelled, so cancelling downloads nothing anywhere. Accepting sends the link to the native host, which appends it to `%LOCALAPPDATA%\Velocity\browser-inbox.jsonl`; the running desktop app picks it up. Nothing is queued while the offer is open.

The offer is a dialog, so it appears in the exact centre of the window — and therefore of the screen — instead of a corner, at any resolution, scaling or monitor layout.

Renderable content stays with the browser: a PDF, image, audio or video that the server serves inline keeps Chrome's viewer, and the extension only offers it when the response says `Content-Disposition: attachment` or the page marks the link as a download.

### Downloads the browser started on its own

When Chrome or Edge starts a download that did not come from an intercepted click (a script, a redirect chain, a form post), the extension **pauses** it immediately — the copy stops transferring at once, and it waits, paused, for your decision — and only then verifies the response and shows the same offer. The browser's list entry is **not deleted**, and the browser is never left downloading behind the offer. "Keep in Chrome" resumes that same transfer, so it works for links that only answer once (signed URLs, form posts, POST redirects); taking the file in Velocity cancels the paused copy; an offer nobody answers cancels it when the offer's lifetime ends. Erasing it destroys Chrome's own download item while the download UI still points at it, and Chrome 157 then writes through that dead pointer and closes the whole browser (five crash dumps, all at `chrome.dll+0x4bf8da9`, a null write). Cancelling is what matters: a cancelled download cannot be resumed, finished or saved by the browser. Its row stays in Chrome's list marked interrupted, which is a cosmetic cost paid once instead of a browser crash. The browser therefore never keeps a copy and never finishes a transfer behind Velocity's back. Cancelling the offer cannot fall back to the browser's download manager because a cancelled item cannot be resumed.

Several captures that arrive together are queued, not merged: every one of them gets its own prompt in turn, and a download is never dropped because another was being answered, because the page reported a dismissal for a prompt it was never shown, or because the tab it came from closed (its prompt moves to the standalone window instead). If the file cannot be handed to Velocity (the helper is missing, or the desktop app refuses it), Chrome is asked for a brand new download with an explicit note in the offer, so a captured file is never lost. Two clicks on the same link produce one offer: the second browser download is cancelled and no second prompt appears.

### When interception is off

Turning interception off, or listing a host under excluded sites, makes the extension a no-op: clicks navigate normally, and downloads run in Chrome or Edge exactly as they would without Velocity. Re-enabling takes effect immediately, without reloading the extension or the page.

## Automatic file detection

Independently of clicks, the extension looks at what a page actually offers and draws a small Velocity button next to it. Detection is driven by the server response, not the file name:

* **Links** — hovering a link (or focusing it with the keyboard) asks the service worker for the response headers. A button appears only when the answer is a file: `Content-Disposition: attachment`, a download MIME type, or a known file type that the browser would not render. Links with a file extension are checked ahead of time while the page is idle, so the button is already there on hover. Links with no extension (`/download?id=42`, signed URLs) are checked on hover, with `HEAD` only — a page's `GET` handlers are never fired by a hover.
* **Media** — a visible `<video>` or `<audio>` whose source is a real file gets an IDM-style strip in its upper-right corner: a compact **Download this video** button carrying the Velocity mark. Clicking it hands that file to Velocity. When the player points at several files at once — alternate qualities in `<source>` elements, subtitles in `<track>` elements — the strip's chevron opens a list with every detected file, IDM-style: its name, its quality (480p, 720p, 1080p … when the player or the URL reports one) or container, and its size in KB/MB/GB — the chevron is there even for a single file, so its properties are always one click away — plus a **Download all** action when there are several; alternate sources appear in the list once the server confirms them, and a subtitle track is listed as it is, because the player itself points at it. Each quality row has a star: picking one remembers that file for the site (stored in the extension's local settings), the row is marked, and the strip's own click then downloads that file directly instead of opening the list — so the same source is used every time until the star is cleared or another quality is picked. A choice only appears when the player offers several video files with distinct labels (a `label`/`res` attribute, a resolution in the URL, otherwise the container). The strip follows the video while the page scrolls, disappears when the player leaves the viewport or is removed, never duplicates itself for the same element, and lives in a closed shadow root so it cannot touch the page. An HLS player is offered as the **stream itself**, not as a fragment: the playlist is read in the worker, the best variant's quality (720p, 1080p …) and — from its bitrate and duration — the size of the whole video appear in the row, and Velocity joins the segments into one playable file. A DASH manifest is identified too and says plainly that it cannot be joined into one file; clicking it explains that instead of sending a fragment. Individual segments (`.ts`, `.m4s`, `.ism`) are never offered as the video, and `blob:` sources are ignored because their bytes exist only inside the page. A stream that is live (no end) or DRM protected is marked as not downloadable rather than half-saved.

Detection recognises a video rather than a URL: a player that reloads, seeks or switches quality re-requests the same file with a fresh signature, and the volatile parameters are ignored so one entry is updated instead of a second icon appearing, while the properties already known (name, type, size) are kept and improved as the page's own responses reveal them.
* **Files the page fetches itself** — a script-built link, a token URL or an “prepare download” XHR is noticed in the actual response headers and offered as a card in the bottom-right corner. Only responses that are real files qualify; HTML, JSON and images are not offered.

Clicking a button hands that exact URL and its metadata (name from `Content-Disposition`, size, type, resumability) to the native host through the same inbox the offer popup uses — nothing is queued until the button is clicked. Each file is offered once: repeated hovers reuse the same button, a file already sent is not sent again, and a dismissed file stays dismissed.

The detector can be switched off, or limited per site, in the options page (**Automatic detection → Show a Velocity button for files on the page** / excluded sites).

What it deliberately does not do: no DRM bypass, no `blob:` fetching, no guessing a page's hidden stream URL, and no change to the page's markup, layout or styles — every button lives in a closed shadow root positioned over the page.

## Resume behaviour

The download engine stores each transfer as `<file>.part` with a `<file>.part.meta` sidecar (URL, final URL, name, destination, total size, bytes written, `ETag`, `Last-Modified`, resume support, and segment checkpoints). Pause keeps both files; resume continues from the last byte with a `Range` request and `If-Range`, so a changed remote file is never appended to an old partial file. The state survives an app restart.

* Server answers `206` — the transfer continues from the requested offset.
* Server answers `200` — the server ignored the range (a weak `ETag` sent as `If-Range` always makes it do so, so a weak validator is never sent). When that response is the same length as the bytes already on disk and the validator has not changed, those bytes are kept and the matching prefix of the full response is skipped, so the transfer continues instead of starting over.
* Server answers `416` — the file is shorter than what is already on disk. A single-stream download is validated and finalised if the bytes match; otherwise the partial file is discarded and the download restarts.
* Multi-connection downloads checkpoint every segment and resume each range separately, and a resume never depends on the one-byte probe: if the probe is refused or fails, the checkpoint's total and validator are used and the real range requests decide whether the server still honours ranges.

## Limitations

* Links generated inside the page (`blob:`, `data:`) cannot be fetched by the extension; the browser has to handle them.
* Content behind DRM or a player that streams in its own protocol is out of scope.
* A server without range support cannot be asked for only the missing part. Velocity keeps the bytes it already has and skips the repeated prefix, so the download still finishes correctly, but that resume costs a full transfer.
* Chrome allows one automatic download per origin per session before it shows its "multiple downloads" prompt. Later navigation-triggered downloads can be blocked by the browser itself, and Velocity cannot intercept what the browser never starts.
* A download that finishes before it can be paused stays in the browser; the extension deliberately does not offer it a second time.
* Filenames are taken from `Content-Disposition`, then the URL; the desktop app still resolves folder and on-disk name collisions.

## Verification

`artifacts/e2e/` contains the loopback harness used to check this behaviour in both browsers (`server.js` for the test files, `intercept.js` for the CDP-driven scenarios, `headless.js` for launching an isolated profile with the unpacked extension). Run it with:

```bash
node artifacts/e2e/server.js
node artifacts/e2e/detect.js "C:/Program Files/Google/Chrome/Application/chrome.exe" 9340 Chrome profile-detect
node artifacts/e2e/detect.js "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" 9343 Edge profile-edge-detect
node artifacts/e2e/intercept.js "C:/Program Files/Google/Chrome/Application/chrome.exe" 9334 Chrome profile-headless
node artifacts/e2e/intercept.js "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" 9337 Edge profile-edge
MODE=takeover node artifacts/e2e/intercept.js "C:/Program Files/Google/Chrome/Application/chrome.exe" 9335 Chrome profile-takeover
```

The harness writes test settings to `%LOCALAPPDATA%\Velocity\browser-settings.json`, so back that file up before running it and restore it afterwards.

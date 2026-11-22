# Development Log

## 2026-11-06 — Phase 1

- Assessed the Windows 11, .NET 10, Visual Studio, Git, Chrome, and Edge environment.
- Created the .NET solution and layered project structure.
- Added the initial dark WPF shell and navigation layout.
- Added dependency injection, debug logging, and SQLite database initialization.
- `dotnet build Velocity.slnx` passed with 0 errors.
- `dotnet test Velocity.slnx --no-build` passed: 1 test passed.
- Restore reports NU1903 for the transitive `SQLitePCLRaw.lib.e_sqlite3` package; this requires review before release.

## 2026-11-06 — Phases 2–3

- Added a validated Add Download dialog with URL, filename, destination-folder selection, and browser folder picker.
- Added a real `HttpClient` streaming download engine using temporary `.part` files and atomic finalization.
- Added actual byte progress, total size, speed reporting, completion/failure states, and completed-download persistence.
- Added download history loading on application startup.
- Verification: build passed with 0 errors; 2 tests passed, including a loopback HTTP integration download test.
- Known limitation: pause/resume, retries, queues, range requests, and segmented downloading are not implemented yet.

## 2026-11-06 — Phases 4–5

- Added a three-download active queue with queued, downloading, paused, completed, failed, and cancelled states.
- Added pause, resume, retry, and cancel actions in the download table.
- Preserved `.part` files during pause and resumed with HTTP Range requests when supported.
- Added concurrent segmented HTTP Range downloads with configurable connection count and normal-download fallback.
- Added final-size validation before a download is finalized.
- Verification: build passed with 0 errors; 2 automated tests passed.
- Known limitation: the UI currently uses a fixed maximum of three active downloads and does not yet expose connection-count or retry-limit settings.

## 2026-11-06 — Phase 6 and browser integration

- Added persisted application settings for download folder, active-download limit, connection count, retry limit, and theme preference storage.
- Added Settings UI and seeded SQLite categories for common file types.
- Added Chromium Manifest V3 extension support for Chrome, Edge, and Brave.
- Added Firefox WebExtension support with native messaging.
- Added a validated .NET native-messaging host and desktop inbox polling.
- Browser capture is explicit through context menu or extension button to prevent duplicate downloads.
- Verification: build passed with 0 errors; 2 automated tests passed.
- Remaining browser work: package signed extensions, register per-browser native-host manifests, and perform end-to-end tests on each installed browser.

## 2026-11-06 — Phase 7 continuation

- Added a PowerShell native-host registration script for Chrome, Edge, Brave, and Firefox.
- Documented unpacked-extension loading, extension-ID registration, and explicit browser capture behavior.
- Confirmed the downloader remains extension-agnostic; it streams ordinary HTTP/HTTPS response bytes and does not filter audio, video, PDF, document, archive, image, or installer extensions.
- Verification: build passed with 0 errors; 2 automated tests passed.
- Browser end-to-end verification still requires loading each extension and running the registration script on the user's machine.

## 2026-11-06 — Phase 7 completion package

- Added extension options for Manual and Automatic capture modes.
- Added self-contained win-x64 native-host publishing script.
- Published the native host successfully to `artifacts/browser-host`.
- Validated both browser manifests as JSON.
- Full solution build passed with 0 errors; 2 automated tests passed.
- Manual browser-store/extension-page verification remains environment-specific and must be performed in Chrome, Edge, Brave, and Firefox.

## 2026-11-07 — Bug fixes and interface overhaul

- Fixed downloads being saved as `download`: the manager passed a placeholder name that overrode the URL-derived file name.
- Fixed the library sections and search: navigation was inert and the search box only toggled its placeholder. Sections now filter live with per-section counts.
- Fixed row actions appearing for every status; Pause, Resume, Retry, Open and Folder are now contextual, and queued items can be paused.
- Fixed deleting a queued download leaving it queued to start later.
- Fixed a failed segmented download leaving a full-length `.part` file that resumed as a corrupt file; partial segment files are now discarded.
- Fixed the theme manager skipping borders, which left dark borders in the Light theme; the full palette is applied for Midnight, Ember and Light.
- Fixed scheduled and browser-captured downloads never appearing in the list; the manager now publishes every registration.
- Fixed the scheduler dialog remaining open until the scheduled time because it awaited the full delay.
- Fixed completion notifications blocking the UI with a message box; they now use non-blocking tray notifications.
- Fixed stale "Downloading" rows after a restart; they load as Paused and can be resumed.
- Implemented the retry-limit setting, which was stored but never used. Argument errors are not retried.
- Throttled engine progress reporting to ten updates per second.
- Rebuilt the interface: new palette and icon set, stat cards, live search, filter sidebar with counts, status pills, redesigned download cards, themed dialogs, keyboard shortcuts (Ctrl+N, Ctrl+F, Delete), and a compact layout verified down to 1000x620.
- Verification: build succeeded with 0 warnings and 0 errors; 9 tests pass (3 added for file naming, queued deletion and retry limits); every window rendered offscreen in all three themes (previews in `artifacts/ui-preview`).

## 2026-11-09 — Chrome/Edge interception and resumable downloads

- Scoped the browser integration to Chrome and Microsoft Edge: removed the Firefox extension and its native-host example, and rewrote the registration script and README for the two Chromium browsers.
- Replaced explicit-only capture with real interception. A click on a download-looking link is probed (HEAD first, GET as fallback) and offered in the Velocity popup with the server's name, size, type and resume capability; nothing is queued until the user chooses Download or Download later, and Cancel leaves the page untouched.
- Renderable responses (PDF, image, audio, video) are left to the browser unless the server marks them as an attachment or the link is marked as a download.
- Browser-started downloads are paused and offered through `downloads.onCreated`. Pausing is retried until the browser confirms it; accepting cancels the browser copy so the file is fetched once, and "Keep in Chrome" resumes it. If the pause cannot be confirmed the offer is skipped.
- Turning interception off, or excluding a host, makes the extension a no-op so Chrome and Edge behave exactly as without it; re-enabling applies immediately.
- Rewrote the download engine as resumable-first: `.part` plus a `.part.meta` sidecar (URL, final URL, name, destination, size, bytes, `ETag`, `Last-Modified`, resume support, segment checkpoints), `If-Range` on resume, offset validation for `206`, truncate-and-restart for `200`, validate-or-restart for `416`, and a stall watchdog independent of pause. Multi-connection downloads checkpoint per segment. The engine also resolves `Content-Disposition` names when the caller has none and reports the resolved path so the manager renames the item.
- Persisted the resume metadata (final URL, `ETag`, `Last-Modified`, resume support) in the download record with an in-place schema migration, and made restore correct the byte count from the partial file on disk.
- The native host now writes `browser-inbox.jsonl` without a byte-order mark, and the desktop app tolerates a legacy mark on the first line.
- Added `DownloadResumeTests` with a range-capable loopback server: pause then resume with the exact `bytes=<offset>-`, restart when the server ignores ranges, complete on `416`, restart when the file changed (`If-Range`), `416` against an untrusted partial, segmented checkpoint resume, disposition naming, segmented fallback, and restart from a stored record. `dotnet test` passes 18 tests; `dotnet build` succeeds with 0 warnings.
- Extended the click-detection list to the remaining documented formats (TS, AAC, SVG, TXT) and verified all 35 extensions named in the requirements are recognised by both the click heuristic and the probe heuristic.
- Added resumable-download tests that pause at 30%, 60% and 90% of the payload: each asserts the partial file still holds the bytes written, that resume requests exactly `bytes=<pausedBytes>-`, and that the finished file matches the source byte for byte. `dotnet test` now passes 21 tests.
- End-to-end checks run headless against Chrome 157 and Edge 157 with the unpacked extension: offer contents, accept/cancel/later against the native host, inline PDF left alone, attachment PDF offered, no interception and normal browser downloads while disabled, interception working again after re-enabling, and takeover of a browser-started download (paused at `in_progress`, cancelled after acceptance, no duplicate file). All checks pass in both browsers.

## 2026-11-09 — Download engine reliability: resume matrix, four parallel downloads, restart recovery

Root causes found by driving the real engine and manager against a range-capable loopback server:

- **Corrupt restart path.** When a resume response carried a changed validator, `SingleAsync` deleted the partial file but did not restart the request, so the `206` body (which begins at the old offset) was appended to an empty file. The download then failed with "ended early" and the bytes on disk were from the wrong offset.
- **Resume threw away progress for validators of different kinds.** The comparison used `etag ?? lastModified` against a single stored validator, so a server that sends an ETag on the full response but only `Last-Modified` on `206` was judged "changed" on every resume. Validators are now compared like with like, and a `206` without `Content-Range` is treated as untrustworthy and restarted.
- **Scheduler could strand a resumed download.** `ProcessQueue` skipped items still present in `_running`; a resume issued while the paused task was unwinding removed the item from the queue and nothing retried it. Deferred items are now kept and re-scanned when the task ends.
- **Active limit was 3**, so a fourth download sat in the queue until another was paused. The default is now 4.
- **Paused progress was stale.** Progress reports are throttled, so the byte count after a pause lagged the file. Pausing and restoring now read the partial file (single stream) or the checkpoint (multi-connection, whose file is preallocated to full length).
- **A stale progress save could overwrite completion.** History writes are now serialised per download and read the item's current state, so a row can no longer be left "Downloading" for a finished file.
- **Cancel did not leave a terminal state** for queued or paused downloads (`Paused` stayed `Paused`), and a retry held an active slot during its backoff. Both fixed; `Resuming` was added so a waiting resume is distinguishable from a queued download.

Shared code: the resume sidecar moved to `Velocity.Core.Models.DownloadCheckpoint`, read by both the engine and the manager.

Verification actually run:

- `dotnet build Velocity.slnx` — 0 warnings, 0 errors.
- `dotnet test` — 44 passed, 0 failed. New suites: `ResumeCompatibilityTests` (206, 200 after Range, 416 complete/rejected, no ranges yet ranges advertised, ranges hidden, missing validators, changed ETag, changed Last-Modified, dropped connection, redirect, segmented pause/resume, changed segmented file) and `ConcurrentDownloadTests` (four at once, pause one, pause two, cancel one, paused slots, four-download recovery, segmented pause/resume, segmented recovery progress, resuming state, checkpoint round-trip).
- New console harness `tests/Velocity.RestartHarness` (in the solution) drives the real manager across processes. Hard-killed a process during four downloads (`kill -9`), reopened with a second process, resumed all four: sizes and SHA-256 matched the source files, history stayed at four rows, no duplicates. Repeated for the preallocated 4-connection path, and for the close-while-paused path (all rows end `Completed`).
- File-type coverage through the harness: MP4, EXE, TXT and an extensionless `application/octet-stream` file all completed with matching hashes, including Content-Disposition renaming.
- The desktop app was started from the debug build and stayed healthy for six seconds; UI-driven pause/resume was not automated.

## 2026-11-10 — Automatic file detection (IDM-style floating button): audit and gap fixes

The detection layer (header probes, hover buttons, media offers, passive network offers, per-site switch) was
already in the extension, so this pass verified it end to end and closed the remaining gaps against the feature
requirements rather than rewriting it.

Gaps found and fixed:

- **BMP was never probed.** `image/bmp` links without a `Content-Disposition` got no button because `.bmp` was
  missing from the file-hint lists in `background.js`, `detector.js` and `content.js`, and from the click
  interception hint in `background.js`.
- **An attachment with an unknown MIME type was ignored.** `classifyFile` only recognised a fixed type table, so
  `Content-Disposition: attachment` with a type outside that table produced no offer — even though the server had
  explicitly said "this is a download". Attachments are now accepted for any type except HTML/JSON/XML pages.
- **Extensionless download links were only detected passively.** `/download?id=42`-style links were offered only
  when the page itself fetched them, or when the anchor carried a `download` attribute. Hovering now verifies any
  http(s) link whose headers can prove it is a file, using `HEAD` only for links without a file extension so a
  hover can never fire a page's `GET` side effects. Idle prefetch stays limited to links that already look like
  files, so a page full of ordinary links causes no request burst.
- **Attachments with no `Content-Type` at all** are now recognised from `Content-Disposition` plus a known size.

Verification actually run (loopback harness, unpacked extension, isolated profiles):

- `node artifacts/e2e/detect.js` in Chrome — all checks passed (46 assertions; 45 executed in the last run, because one inbox-timing check is skipped when the running desktop app consumes the line first). New cases: a `.bmp` link, an extensionless
  `/dynamic-download?id=9` link named by `Content-Disposition`, an unknown-type attachment
  (`odd-package.vel`), and a redirect that resolves to a signed URL. The same run still proves ordinary page
  links, missing files, `.m3u8` playlists and `blob:` players are never offered, that JSON/image XHRs are
  ignored, that a JS-added link is found, that clicking sends the real URL/name to the native inbox, that
  repeated hovers do not duplicate a button, and that the toggle and site exclusions work.
- `node artifacts/e2e/detect.js` in Microsoft Edge — the same suite passed, with no failures.
- `node artifacts/e2e/intercept.js` (Chrome) — all checks passed; the existing click interception, offer card,
  Cancel/Download later paths and the "interception off" behaviour are unchanged.
- `MODE=takeover node artifacts/e2e/intercept.js` (Chrome) — a browser-started download is paused, offered, and
  released to Velocity; all checks passed.
- `dotnet build Velocity.slnx` — 0 warnings, 0 errors. `dotnet test` — 44 passed, 0 failed (the 2026-11-09 engine
  work is still intact).

Test residue removed: loopback profiles deleted, the 12 test history rows and their files under
`Downloads\Video` deleted, the test server stopped.

## 2026-11-11 — Unlimited concurrency, crash-safe resume, themed dialogs, per-file speed limit

Six requested changes, none of which touch the layout or the existing download flow:

- **No artificial speed cap existed.** The only ceiling in the codebase was the user-configured
  `GlobalSpeedLimitKilobytesPerSecond` (0 = unlimited). Nothing else throttles a transfer, so a flat ~5 MB/s
  reading is the server/CDN side (or a single-connection fallback), not Velocity.
- **Concurrency is no longer capped.** `MaximumActiveDownloads` defaults to 0 = start queued items immediately;
  `ProcessQueue` only enforces a ceiling when it is above zero, and Settings accepts 0–64.
- **Crash-safe resume.** `DownloadCheckpoint.Write` now flushes to the disk (and keeps a `.prev` copy that is read
  as a fallback), progress is persisted with the byte count and segment ranges, and `DownloadManager.ResumeInterrupted`
  continues every transfer that was active at shutdown when the app starts — from its last byte, never from zero.
  A single shared `RateLimiter` per download now paces the combined throughput of all connections, which also fixed
  limited segmented downloads failing to resume.
- **Deleting an active download asks first.** `DeleteConfirmWindow` gained a warning variant ("Download still running"
  with the byte count and a note that partial data and resume data go away); cancelling leaves the transfer running.
- **Themed dialogs.** `VelocityDialog` (info / warning / danger) replaces every system `MessageBox`, so the popup uses
  the same brushes, type, buttons and `small.png` icon as the rest of the app and follows a live theme switch.
- **Per-download speed limit (IDM style).** New gauge button on each active row opens `SpeedLimitWindow`; the value is
  stored in `Downloads.SpeedLimitBytesPerSecond` (0 = follow the app-wide setting) and survives a restart. Changing it
  during a transfer restarts that transfer in place, keeping the partial file, because the ceiling is applied at start.

Verification actually run:

- `dotnet test tests/Velocity.Core.Tests` — 50 passed, 0 failed (6 new tests: unlimited parallel downloads, checkpoint
  `.prev` fallback, deleting an active download removes partials and metadata, a per-file limit survives a restart,
  a limited segmented download resumes from its checkpoint, a per-file limit slows only that file).
- Hard-kill recovery, real processes: four simultaneous downloads (4 connections, limit 256 KB/s), `taskkill /F` at
  2.17 MB of 8 MB, then a brand-new process resumed automatically. Every final file's SHA-256 matched a fresh download
  (one earlier attempt at 1 MB/s finished before the kill, so the kill point was lowered until it landed mid-transfer).
- `dotnet build Velocity.slnx` — 0 warnings, 0 errors.
- `artifacts/ui-harness` now renders `VelocityDialog` (info/warning), `SpeedLimitWindow` and the active-download
  `DeleteConfirmWindow` in Midnight, Ember and Light; the screenshots were inspected and all three themes applied.

## 2026-11-11 — Follow-up: resumes that still started over

The earlier crash test exercised a range-resuming server with a strong ETag, which hid three ways a real resume
still restarted from zero. All three were in the engine:

- **A weak ETag was sent as `If-Range`.** A weak validator is invalid for `If-Range` (RFC 9110 13.1.5), so a
  conforming server ignores the range and answers `200` with the whole file, which discarded the partial and
  started over — every time, for every host that emits `W/"..."` (nginx and many CDNs do). A weak ETag is now
  never sent: a strong ETag is preferred, then `Last-Modified`, and if neither exists no header is sent.
- **A `200` response during a resume threw the bytes away.** Now, when the response is the same length as the
  known total and the validator did not change, the bytes on disk are kept and the matching prefix of the full
  response is skipped, so the transfer continues even against a server that ignores ranges completely.
- **A resume depended on the one-byte probe.** If the probe was refused (`416`/`200` for `bytes=0-0`) or threw,
  the segmented path bailed out and the fallback mistook a preallocated `.part` for a finished file (`416`), which
  deleted the partial and restarted. A resume now synthesises the probe from its own checkpoint and only the real
  segment requests decide whether ranges work.

Verification actually run:

- `tests/Velocity.Core.Tests/ResumeRobustnessTests.cs` (3 new tests: ranges ignored, weak ETag only, probe
  refused): all pass. Proven to be real regressions — with the three fixes individually reverted the tests fail
  with "the resumed transfer reported 0 bytes after pausing at 2097152: it started over" (and 524288…1966080).
- `dotnet test` — 53 passed, 0 failed. `dotnet build Velocity.slnx` — 0 warnings, 0 errors.

## 2026-11-11 — IDM-style floating download strip over videos

The detector already found media elements and probed them, but it showed the same 296 px card the network offers
use. Videos now get what IDM shows: a compact strip in the video's own upper-right corner.

- `mediaItems()` collects every file the player points at — `currentSrc`, the `src` attribute, each `<source>` and
  each subtitle `<track>` — ignoring playlists (`m3u8`, `mpd`, `m4s`, `ism`) and `blob:` sources. A `.ts` source is
  a real MPEG-TS file here, so it stays a candidate.
- `mediaBar()` renders the strip: Velocity mark + "Download this video", plus a chevron and a close button. With one
  file the click downloads it; with several the chevron opens the list (name, type, size, "Download all"), alternate
  sources appear once the server confirms them, and a subtitle track is listed as the player references it.
- Position comes from the live layout: `left = video.right - width - 10`, `top = video.top + 10`, clamped to the
  viewport, and the list flips above the strip when the player sits at the bottom. Every element stays inside a
  closed shadow root with `pointer-events` only on the strip, so playback, seeking, controls and the page's own
  listeners are untouched; a fullscreen player hides the strip automatically because the overlay lives in `<body>`.
- Clicking sends through `postToVelocity()`, the same `velocity-action` native-host message the link and pill buttons
  use, so nothing about capture changed.

Verification actually run (loopback harness, unpacked extension, isolated profiles, real files served with ranges):

- `node artifacts/e2e/detect.js` (Chrome) — all checks passed, including four new ones: the strip is the compact
  "Download this video" bar; it carries the server's name and size; its rect sits inside the video's upper-right
  corner (`bar {x:268,y:399,w:173.7,h:30}` inside `video {x:32,y:389,w:420,h:150}`); clicking it reaches the native
  inbox with `filename: clip.mp4`; a multi-source player (two qualities + a VTT track) lists both extras and
  "Download all". The pre-existing cases (links, hover, blob/HLS players left alone, dedupe, toggle, exclusions,
  network offers) still pass.
- One earlier run failed the new cases for an environmental reason, not a code one: an old `server.js` from an
  earlier session still held port 8123 and was serving the previous page. The stale process was stopped and the run
  repeated from a clean server.

Test residue: `%LOCALAPPDATA%\Velocity\browser-inbox.jsonl` entries produced by the clicks were consumed by the app
as designed; the loopback profiles are isolated per run.

## 2026-11-11 — Remembered quality for the floating video strip

Players that offer several files now remember which one the user wants: each video row in the media list has a
star, the choice is stored in the extension's local settings per site (`preferredMedia`), and the strip's own click
downloads that file directly instead of opening the list.

- A row label comes from the player (`label`/`res`/`data-res`/`size`), then a resolution in the URL
  (240…2160 → "720p"), then `4K`/`HD`/`SD`, then the container. The star only appears when at least two video
  files have distinct labels, because an unlabelled choice would be a guess.
- `togglePreferred()` writes `{ "<host>": "<label>" }` to `chrome.storage.local`; a change in another tab is picked
  up by the existing storage listener, so every strip on the page updates. Clicking the filled star clears it.
- The strip's tooltip names the remembered file, so it is obvious what a click will download.

Verification actually run (loopback harness, unpacked extension, isolated profile):

- `node artifacts/e2e/detect.js` (Chrome) — all checks passed, including the four new ones: each quality has its own
  row; clicking the star marks the row (`data-preferred="true"`, star `★`, `aria-pressed="true"`); the choice is
  stored as `{"127.0.0.1:8123":"720p"}`; after a full page reload the strip is back and one click on it sends
  `multi-720.mp4` (not `multi.mp4`) to the native inbox.
- The same suite was run in Edge below; the preference lives in extension storage, not in the page, so it behaves
  identically there.

## 2026-11-11 — IDM-style properties in the video list (quality + size)

The list that the video strip opens now carries what IDM shows for each file: a quality label and a size, not just
a name. The chevron is always present, so even a single-file video shows its properties one click away.

- `qualityLabel()` reads the player's own attributes first (`label`, `res`, `data-res`, `data-quality`, `quality`,
  `size`), then a resolution in the URL (`480p`, `720` inside `quality=`/`res=`/`height=`), then `4K`/`HD`/`SD`,
  then the height the element reports for the stream it is playing (`videoHeight`), and only then the container.
- A row reads like IDM's: name, then `720p · MP4 · 900 KB` (the container is dropped when it duplicates the
  quality), with the size coming from the server probe. A subtitle track reads `VTT · subtitle`.
- `watchMedia()` now refreshes the strip on `loadedmetadata`, `loadeddata`, `resize`, `play`, `playing`,
  `emptied` and `durationchange`, so the label appears once the player knows its real resolution and disappears
  when the source is unloaded.

Verification actually run: `node artifacts/e2e/detect.js` in Chrome and Edge against the loopback page — all checks
passed, including a new one that opens the list of the single-file video and requires both a format and a size in it,
plus the existing ones for the multi-quality list and the remembered quality.

## 2026-11-11 — Unlimited concurrency, runtime stream recognition, real tray icon

Three requests, all checked against the running code rather than assumed.

**1. Unlimited simultaneous downloads.** The cap was already gone: `ProcessQueue` reads
`AppSettings.MaximumActiveDownloads`, and `0` (the shipped default) means no ceiling at all, so every
download the user starts gets its own task, connection set, cancellation token, partial file and checkpoint.
`SpeedLimitAndConcurrencyTests.EveryDownloadRunsAtOnceWhenNoCeilingIsConfigured` already proved six at once.
Added `tests/Velocity.Core.Tests/UnlimitedConcurrencyTests.cs`: eight downloads of 3–16 MB started at the
same moment (four connections each) must all be transferring at once, all complete, every file match its
SHA-256, no `.part` left behind and no row left `Queued`. The first version of the test was flaky because its
smallest file could finish before all eight were in flight; it now samples the peak instead of one instant
(three consecutive runs passed).

**2. Runtime video recognition (extension + engine).** The browser side used to ignore playlists entirely and
identified a video by its exact URL, so a reload with a fresh token produced a second entry with no
properties. Now:

- `identityOf()` drops signature/expiry parameters (`token`, `expires`, `sig`, `X-Amz-*`, `hdnts`, …) and
  sorts the rest, so the same video re-requested is one entry; `state.known` keeps the best properties ever
  seen per identity and `mergeDetails()` never lets a weaker answer (204, HEAD without length, stripped
  headers) erase a known name, type or size — `verify()` also rebinds the *new* URL, so an old token is never
  sent to Velocity.
- The service worker now forwards real response properties for `video/*` and `audio/*` (including `206`
  responses) to the page, so a size the first probe could not see still appears on the strip.
- Manifests are read, not guessed: HLS masters are parsed for variants (BANDWIDTH/RESOLUTION), the best
  variant's media playlist gives its duration, and the row shows `720p · HLS · ~1.3 MB`; DASH is parsed for
  representations and is listed as `720p · DASH · not downloadable` with the reason, because Velocity cannot
  join its fragments. Segments (`.ts`, `.m4s`, `.ism`) are never offered as the video.
- `HlsPlaylistDownloader` in the engine is what makes an offered stream honest: it joins the chosen variant's
  segments (with `EXT-X-MAP` init parts), decrypts AES-128 transport encryption, replaces an old partial
  instead of appending to it, honours the per-download speed ceiling over the whole file, and refuses a live
  stream or SAMPLE-AES DRM with a message that says why.

**3. Tray icon.** The cause was concrete: the tray used `SystemIcons.Application` and the project had no
`ApplicationIcon` at all. `icon.ico` is now generated from `icon.png` (the V mark, black keyed out, 16–256 px),
set as `ApplicationIcon` and embedded as a resource; `LoadTrayIcon()` takes the executable's own icon and falls
back to the embedded copy. A hidden `NativeWindow` hears `TaskbarCreated`, so the icon comes back after Explorer
restarts instead of disappearing for good.

Verification actually run:

- `dotnet build` — 0 warnings, 0 errors; `dotnet test tests/Velocity.Core.Tests` — 58 passed, 0 failed, including
  the four new HLS tests (best variant joined byte-exact, AES-128 decrypted, live stream refused, DRM refused).
- `node artifacts/e2e/detect.js` in Chrome **and** Edge — all checks passed, including: the HLS strip shows
  `720p · HLS · ~1.3 MB`, the DASH strip shows `720p · DASH · not downloadable`, clicking the DASH row reports
  the reason and sends nothing, a token change keeps one entry and its size, and no `.ts` segment is offered.
- The built executable's icon was extracted back out to `artifacts/tray-icon-from-exe.png` and inspected.

## 2026-11-11 — Brand icon in the extension and a legible tray icon

Both places showed the wrong thing: the extension had no `icons` entry at all, so Chrome drew its default
letter tile, and the tray used a hairline gold mark on transparency that faded into the notification area.

- `icon.ico` and `browser-extension/chromium/icons/icon{16,32,48,128}.png` are now rendered from the same
  brand artwork: the V mark taken from `icon.png` (black keyed out, strokes thickened so they survive 16 px)
  drawn dark on a gold rounded plate, each pixel size rendered separately rather than downscaled, so the
  small sizes stay crisp. The ICO is written directly with one PNG frame per size (16–256).
- `manifest.json` gained `icons`, `action.default_icon` (16/24/32) and a `web_accessible_resources` entry for
  `icons/*.png`, so the toolbar button, the extensions menu, the profile menu and `chrome://extensions` all
  show the Velocity mark.
- The in-page marks (media strip and detection pill) no longer render a "V" glyph: they use the same icon
  through `api.runtime.getURL("icons/icon32.png")` in their closed shadow roots.

Verification actually run: `node artifacts/e2e/detect.js` in Chrome and Edge — all checks passed with the new
icon and styles; the built app was relaunched and its tray button captured from the Windows 11 overflow flyout.

## 2026-11-11 — One click on the tray icon opens Velocity

`NotifyIcon` only had a `DoubleClick` handler, so a single left click did nothing. It now opens the window on
the first left click (`MouseClick` with `MouseButtons.Left`), which is IDM's behaviour; `ShowMainWindow()`
restores a minimised window, so a click after the window was hidden into the tray brings it back. The
redundant `DoubleClick` handler was removed so one click is one activation, and right-click still opens the
context menu.

Verification actually run: the app was rebuilt (0 warnings, 0 errors) and restarted; the window was minimised
into the tray, then the tray button's own click action was invoked through the shell's UI Automation provider
(the same notification a left click delivers) — the hidden window came back visible. A synthetic
`mouse_event` click sent to the tray button did nothing until the Windows 11 overflow flyout was brought to the
foreground first (the flyout lands on `TopLevelWindowForOverflowXamlIsland`, which ignores input while it is not
focused). After that, one left click restored the hidden window: `shown=False` before, `shown=True` after.

## 2026-11-11 — Clipboard offers, batch links, tray progress and mirror downloads

Four additions, all in the desktop app; the extension was not touched.

- **Clipboard monitor.** A 900 ms timer reads the clipboard while the main window is visible and offers an
  http(s) link once, in the themed dialog, with a `CLIPBOARD` caption. It never adds anything on its own, does
  not run while Velocity sits in the tray, skips links already on the list, remembers what it already offered
  for the session, and is switchable in Settings (on by default, like IDM).
- **Batch links.** The link box in Add download is now multi-line: one link per line, each becoming its own
  request. Duplicates inside the paste are dropped, a file name is applied only when there is one link, and
  the dialog reports per-link failures instead of failing the whole paste.
- **Tray progress.** `App.UpdateTrayStatus` draws a progress ring around the brand plate in 5% buckets (each
  rendered icon cached and its HICON destroyed) and sets a tooltip of count, speed and percentage, capped at
  the 63 characters `NotifyIcon` accepts. The idle icon is the untouched brand icon again.
- **Mirror downloads.** `DownloadRequest.Mirrors` feeds `HttpDownloadEngine`: every mirror is probed first and
  joins the transfer only when it reports the same length *and* the same validator (ETag, else
  Last-Modified); a mirror that disagrees is dropped, because joining two builds produces a file that opens and
  is silently corrupt. Verified hosts then take segments round-robin, and the segment count grows so no
  verified mirror sits idle.

Verification actually run: `dotnet build Velocity.slnx` (0 warnings, 0 errors) and 60 tests pass, including
`MirrorDownloadTests` (two range servers serve one 6 MB file byte-exactly; a mirror with a different length is
probed but never asked for a range). Against the running app, driven through UI Automation and real input:
copying a link produced exactly one offer, accepting it downloaded the 8 MB file through the tray mid-transfer
(`1 downloading · 1.3 MB/s · 48% → 97%`), the tray returned to `Velocity` when it finished, and the same link
was not offered again. Pasting two links in one dialog added both files with the entered file name correctly
ignored. A mirror download from `artifacts/e2e/slow-server.js` (`/mirror-a.bin` + `/mirror-b.bin`, same ETag)
finished byte-exact with the server log showing ranges served by both hosts (primary 3, mirror 2).

## 2026-11-11 — Copy the original download link from the library

Every row now carries the address the file was fetched from, and nothing else: `DownloadItem.Url` is the
request's own link, so a copied value is always the original URL and never the local path.

- Hovering the file name opens a small tooltip card: a `DOWNLOAD LINK` caption, the full URL wrapped to at
  most 440 px, a `Copy link` button, and the destination path in muted text underneath so the two are never
  confused. It replaces the old tooltip that stacked name, URL, path and error in one block.
- A compact copy-link icon (the existing `IconCopy` geometry, `IconButtonStyle`) sits beside the name and
  copies with a single click, for rows where hovering is slower than clicking.
- Both entry points run `CopyLinkClick`, which puts `item.Url` on the clipboard and, if Windows refuses the
  clipboard, explains it in the themed dialog instead of failing silently. The icon uses the same size and
  hover treatment as the other row actions, so the layout and column widths are untouched.

Verification actually run: built with 0 warnings and 0 errors, the app was restarted, and the row's copy icon
was invoked through UI Automation — the clipboard then held the row's real
`https://cryptonewz.online/dl.php?link=…` URL, not a file path. Hovering the file name through injected mouse
movement opened the tooltip with `DOWNLOAD LINK` and the URL exposed to UI Automation, and invoking its
`Copy link` button put exactly that URL on the clipboard. The rendered card was captured to
`artifacts/tooltip-hover.png` to confirm spacing and that the muted path line stays secondary.

## 2026-11-11 — The copy icon confirms itself

A copy that silently succeeds is hard to trust, so the row's double-square icon now becomes a checkmark the
moment the link reaches the clipboard and returns to the copy glyph two seconds later. `CopyLinkClick` swaps
the `Path.Data` of the clicked icon from `IconCopy` to the existing `IconCheck` geometry and starts a
`DispatcherTimer`; `RestoreCopyIcon` puts the original geometry back, is idempotent, and also runs when the
window detaches, so a row recycled mid-flash cannot keep the wrong glyph. Only the glyph changes: the button's
size, position, style and tooltip are untouched, and the tooltip card's own Copy button is unaffected.

Verification actually run: built with 0 warnings and 0 errors and restarted. Invoking the row's copy icon
through UI Automation put the original URL on the clipboard; a capture 250 ms later shows the checkmark in that
button (`artifacts/copy-icon-flash.png`) and a capture 2.85 s later shows the double-square again
(`artifacts/copy-icon-after.png`).

## 2026-11-11 — Authenticated links and queue control

Two features, both in the desktop app; the browser extension was not touched.

- **Request headers.** `DownloadRequest.Headers` carries whatever a host insists on — `Referer`, a session
  `Cookie`, an `Authorization` token, an `X-Api-Key` — and the engine applies it to *every* request it makes:
  the capability probe, each ranged segment, the mirror probe and each resume attempt. Skipping any one of
  those would make a protected download fail after it looked healthy. Reserved headers (`Range`, `If-Range`,
  `Host`, `Content-Length`, `Connection`) are ignored so a pasted value can never break the transfer itself.
  The Add dialog gained a compact `AUTHENTICATION (OPTIONAL)` block; headers are stored as JSON in a new
  `Downloads.Headers` column, so a protected link resumes after a restart without retyping anything. Cookies
  are therefore on disk in Velocity's local SQLite file, at the same trust level as a browser profile.
- **Queue control.** `PauseAll`/`ResumeAll` reuse the single-item pause path, so partial files and checkpoints
  survive; `Prioritize` raises the selected downloads above the rest of the queue while keeping their own
  order, and `ProcessQueue` now starts the highest `Priority` first (persisted in a new `Downloads.Priority`
  column, default 0 = "in the order added"). The toolbar gained `Pause all`, `Resume all` and `Start first`
  (enabled only with a selection); no per-row chrome and no new columns were added.

Verification actually run: `dotnet build` 0 warnings, 0 errors; 65 tests pass, including headers reaching the
probe and all segments (and reserved names being ignored), headers/priority surviving a repository round-trip,
a higher-priority item starting before an earlier queued one, and PauseAll/ResumeAll taking transfers to Paused
and back to Completed. Live, with the app driven through UI Automation against a slow local server: the
dialog's Referer, Cookie and `X-Api-Key` all arrived in the server log; with a ceiling of one, three links
started `/big.bin`, and after `Start first` on the third the order became `/big.bin` -> `/mirror-b.bin` ->
`/mirror-a.bin`; on a single transfer, `Pause all` moved it to Paused with nothing downloading and
`Resume all` completed it.

One bug was found by that live run and fixed: the extra-headers box was split on spaces as well as newlines, so
`X-Api-Key: key-42` was rejected. Header lines are now split on newlines only.

Known gap: HLS (`HlsPlaylistDownloader`) still requests segments without the caller's headers, so a protected
playlist needs its cookies added there before it behaves like a plain file.

## 2026-11-11 — A compact Add download dialog and truly simultaneous transfers

- **Scrollable link box.** The theme's text-box template hosts its content in a `ScrollViewer` but never bound
  `ScrollViewer.VerticalScrollBarVisibility` from the templated `TextBox`, so every multi-line box silently
  refused to scroll. The template now binds both scrollbar visibilities; the URL, mirror and header boxes ask
  for a visible vertical bar and wrap long values instead of hiding them past the right edge.
- **Compact dialog.** It used to consume nearly the whole screen. The controls are now paired into two-column
  rows (file name with destination, referer with cookies, mirrors with extra headers), spacing and field
  heights were reduced, long captions wrap, and the window caps itself at 80% of the work area with an inner
  scroll viewer as a safety net. Measured on this machine: 729 px tall against a 1020 px work area (0.71),
  down from ~0.98; every control is still present.
- **Simultaneous downloads.** The active-download ceiling was set to 3 by an earlier test of mine, and that
  setting is what queued the extra files. It is now 0 (no limit) — the same value a fresh profile uses — so a
  pasted batch starts every file at once, each with its own connections, progress, speed and pause/resume
  state. Nothing else serialised transfers: `ProcessQueue` only ever held the slot counter, and the engine's
  locks are per-download checkpoint gates.

Verification actually run: built with 0 warnings, 0 errors; the dialog was driven through UI Automation with
20 links pasted, its URL box reported `VerticallyScrollable = True`, and the window measured 0.71 of the work
area with the capture in `artifacts/add-dialog-compact.png`. Three links submitted in one dialog produced 15
interleaved requests in the test server's log (collapsed sequence `/mirror-b.bin -> /big.bin -> /mirror-a.bin`
repeating, 5 interleavings rather than three separate blocks) and all three 8 MB files completed.

## 2026-11-12 — An IDM-style video bar that stays, with a real quality menu

The strip existed but only sometimes: it was built for a `<video src="file.mp4">` and it could be deleted by the
first event that did not fit. Against a real player (the Plex capture in `one.png`/`two.png`) it either never
appeared or appeared and vanished. The bar, its menu and the way it reaches the desktop app were all reworked.

**Why it disappeared.** Four separate causes, all fixed:

- An MSE/blob player publishes no address, so `mediaItems()` returned nothing and the code called `hideFor()`.
  The manifest the page itself fetched is now the video for that player: the worker remembers the last one per
  tab and the strip attaches to the playing element, which is how 360p/720p rows appear for a player that only
  has a `blob:` source.
- The `IntersectionObserver` deleted the strip whenever the player left the viewport, including for a frame while
  a player was being laid out. Visibility is now a position question only: the strip is parked off-screen, never
  destroyed.
- `place()` removed the record when the element it was pinned to was momentarily detached (React re-render, source
  swap). It now adopts the new player element and keeps the bar where it was.
- Properties arrive continuously while playing, and every arrival rebuilt the bar. An unchanged list keeps its
  node, an open menu stays open, and a page that loads its playlist before the content script starts is covered by
  a `velocity-streams` query at start-up (the earlier `velocity-detected` message was simply lost).

**Click opens the qualities, like IDM.** The bar no longer downloads "the one file" or a remembered quality on a
click: it opens the menu. A master playlist is expanded into one row per quality (`720p · HLS · ~1.3 MB`,
`360p · HLS · ~586 KB`), each row sending that variant's own playlist so the engine joins exactly the selected
quality. Direct sources, alternate `<source>` files and subtitle tracks stay in the same menu, and the star still
remembers a default for the site. DASH is listed with the honest "not downloadable" note instead of being hidden.

**Fullscreen.** The strip is moved into `document.fullscreenElement`, because anything left in `<body>` is not
painted inside the browser's top layer; a containing-block-creating fullscreen element is compensated for.

**Communication that a VPN cannot break.** There is no socket anywhere: the extension hands a download to the
helper over native messaging, the helper appends one line to `%LOCALAPPDATA%\Velocityrowser-inbox.jsonl`, and the
app consumes it. The page a request came from is now forwarded as `Referer` and stored with the download
(`Headers`), which is what a video host needs. Velocity also registers the native host itself (manifest preserved,
`HKCU` keys for Chrome, Edge, Brave, Vivaldi, Chromium) and sets its own `Run` entry, so no script and no manual
launch is required. The HTTP client uses a two-minute pooled connection lifetime: a VPN switch, a new adapter or a
new lease closes the old route, and the next retry or segment opens a connection on the new one instead of waiting
for a dead socket.

Verification actually run:

- `node artifacts/e2e/detect.js` in Chrome and in Edge: **84 checks, all passing** in both. New cases: the bar is
  still there after repeated sampling, and survives play, seek, `waiting` and `emptied`; a blob player gets the bar
  from its sniffed manifest, placed inside the player; clicking the bar opens the menu without queuing anything;
  both declared qualities are listed; selecting 360p queues `stream-360.m3u8` with the page as `Referer`; the bar
  is inside the fullscreen element and visible in fullscreen. The existing cases (hover buttons, takeover, PDFs,
  DASH, exclusions, detection switch) still pass.
- `artifacts/velocity-video-bar.png` shows the bar and its opened menu over the player.
- Desktop hand-off with the published host: `{"ok":true,...}`, the playlist was joined into a 65 536-byte `.ts`,
  and the row stored `{"Referer":"http://127.0.0.1:8123/detect-stream"}`.
- The `Run` value points at the app and the host manifest kept its registered extension origin after the app
  rewrote it.
- `dotnet build` — 0 warnings, 0 errors; `dotnet test` — 65 passed.

## Round 10 — live speed limits, only the requested file, and a real installer

**Removing a speed limit no longer touches the transfer.** The old design applied a new limit by cancelling and
requeueing the item, which is why lifting a limit could look like the download had frozen. A `SpeedLimit` object
(`src/Velocity.Core/Models/SpeedLimit.cs`) is now shared between the item and the engine: the rate limiter reads it
before every buffer, slices its own waits into 250 ms steps and ticks a stall guard, so a new value takes effect on
the next read and "unlimited" simply stops waiting. The setting in Settings writes through to every running item
(`DownloadManager.SetSpeedLimit`, `RefreshSpeeds`), and HLS segment downloads are paced the same way.

**Only the file the user picked.** The media bar's "Download all" action is gone from `detector.js` and its message
handler with it: the bar sends exactly one URL, the selected variant (or the direct file when there is only one).

**Nothing in Downloads unless something is downloading.** Startup no longer resumes interrupted transfers behind the
user's back (`ResumeOnStartup`, default off, with a checkbox in Settings) and sweeps `.part`, `.part.meta`,
`.meta.prev` and `.meta.tmp` files that no download row owns. Category organization also stopped nesting a category
inside a folder that already was that category (`Downloads\Video\Videos`).

**Shader-safe JSON in the trimmed host.** The portable build trims the native messaging host to 13.8 MB; its replies
are now built with `JsonObject` instead of the reflection serializer, so trimming cannot strip response properties
(trimming the old code silently produced `{}`).

Verification actually run:

- `node artifacts/e2e/detect.js` in Chrome: **83 checks, all passing**, including the new
  "the list offers no bulk download of supporting files". The fullscreen case reloads the page first, because a
  successful download now closes the bar on purpose.
- `dotnet test tests/Velocity.Core.Tests/Velocity.Core.Tests.csproj` — **68 passed**, including
  `SpeedLimitRemovalTests` (limit removed mid-transfer finishes without going backwards, no second copy or leftover,
  an app-wide change reaches a running transfer).
- `dotnet build Velocity.slnx` — 0 warnings, 0 errors.
- Trimmed host: `{"ok":true,"running":true,"version":"2.1.0"}` for `ping` and a complete `settings` reply.
- Installer: silent install created the program folder, both shortcuts, the *Apps & features* entry and launched the
  installed app; `/uninstall /S` removed the folder, both shortcuts and the uninstall and `Run` values.
- Real app startup with a planted `orphan-test.bin.part` + `.part.meta`: both were deleted and no file or folder was
  created in `Downloads`.

## Round 11 — the installed build opened with "Velocity could not start"

`open.png` showed the packaged app dying before its window, with the detail
`Provide value on 'System.Windows.Baml2006.TypeConverterMarkupExtension' threw an exception.` That converter is what
WPF runs while parsing a BAML attribute, and the culprit was the window icon: five windows declared
`Icon="pack://siteoforigin:,,,/icon.png"`. A `siteoforigin` URI points at a loose file next to the executable,
which exists in a `bin` build (the project copies `icon.png` to output) but not in a single-file publish — so the
XAML parser threw and `App.OnStartup`'s catch turned it into the dialog in the screenshot.

All five declarations (main, Add download, Settings, Scheduler, About) now use the embedded artwork, which the
project already ships as a resource: `pack://application:,,,/Velocity.App;component/icon.ico`. `siteoforigin` no
longer appears anywhere in the app, so a packaged build cannot depend on a file that was never deployed.

Verification actually run:

- Rebuilt single-file publish started and reported a live window: `title: [Velocity]`, non-zero window handle.
- Rebuilt the portable folder, portable zip and installer with `artifacts/build-installer.ps1`.
- The user's exact flow, reproduced: run `Velocity-Setup-1.0.0.exe` with no flags → "Velocity 1.0.0 is installed"
  box appears → `Velocity.App` is running with `title: [Velocity]` while the box is up.
- The same exe started directly from `%LOCALAPPDATA%\Programs\Velocity` stays alive with its window for 10 s.
- `dotnet publish` — no errors or warnings for the app.

## Round 12 — interception that never leaves a second copy, and a centered offer

`lefttoright.png` showed Velocity's offer in the corner of the browser and Chrome still downloading the same file.
The corner was the standalone offer window: `windows.create` was called without coordinates, so Chrome put it at the
top-left of the screen. The second download came from the old hand-off design, which *paused* Chrome's download and
**resumed** it whenever the offer was cancelled — the exact behaviour that produced repeated browser downloads.

**Interception.** `chrome.downloads.onCreated` now cancels the browser's copy and erases its list entry first, and
probes the URL afterwards. The decision to cancel reads an in-memory options cache, so nothing is awaited before the
cancel; a HEAD-only probe then fills in the offer. A download Velocity cannot hand over (helper missing, app refuses)
is started again in Chrome deliberately, with a note in the offer — a new request, never a resumed one. Two clicks on
one link cancel the second browser download and open a single prompt. `downloads.download` calls made by the extension
carry `byExtensionId`, so "Keep in Chrome" is never intercepted again.

- "Keep in Chrome" is now an explicit action (`velocity-action` with `action: "browser"`) that starts a fresh
  browser download. Closing the offer, pressing Escape, or closing the standalone window is the cancel path
  (`action: "cancel"`), which releases the offer and downloads nothing anywhere.
- An unanswered offer expires from the live-offer map after ten minutes, so a closed prompt can never block the
  same link forever.

**Centering.** The in-page offer is wrapped in a full-viewport flex layer, so the card is centered horizontally and
vertically at any window size, zoom or resolution; content taller than the window scrolls. The standalone window is
placed at the centre of the focused browser window (and therefore its screen) instead of Chrome's default corner.
In the desktop app, **Add download** uses `CenterScreen` and Velocity restores, raises and focuses itself first, so a
prompt cannot open behind another window or beside a minimized taskbar button.

Verification actually run:

- `node artifacts/e2e/detect.js` (Chrome, unpacked extension): **92 checks, all passing**. New cases: clicking a file
  link opens the offer and it is centered within 2 px on both axes; Escape cancels it and nothing is downloaded in
  either place; a page that starts a download by itself (a scripted `a[download]` click, bypassing the page offer) is
  offered by Velocity, **Chrome keeps no copy** (`chrome.downloads` has no entry for it, no new entry at all), and
  cancelling the offer never resumes the browser download.
- A visible-window run (`VELOCITY_E2E_VISIBLE=1`) reached the same interception results in a real download manager;
  that mode is also where the old pause/resume design failed, and it is noisier for unrelated layout checks, so the
  headless run is the one quoted above.
- `dotnet build` — 0 warnings, 0 errors; `dotnet test` — 68 passed; `node --check` on every changed script.

## Round 13 — two downloads, two prompts

Two detections arriving together used to fight over one card: the second `present` replaced the first, and since the
browser copy is cancelled the moment a download is captured, the first file was lost without ever being offered.

The offer is now a queue. `offerQueue` in the worker holds everything waiting; `pumpOffers` presents one at a time and
`finishOffer` (decision, dismissal, closed tab or expiry) moves to the next, so a prompt can never be replaced while it
is unanswered. The content script keeps its own short queue as well, for offers that arrive while a card is on screen,
and closes the card before telling the worker, because the answer to that call is the next prompt.

Escape now also works after focus moved out of the card (a click on the page used to swallow it), and a closed prompt
still releases its link after ten minutes at most.

Verification actually run (`node artifacts/e2e/detect.js`, Chrome, unpacked extension): **99 checks, all passing**,
with the new sequence: a page starts a download → its offer appears; a second download starts while that offer is open
→ **the open prompt is not replaced**; dismissing it reveals the **second file's own prompt** (`setup.exe` after
`e2e-archive.zip`); neither file is lost and the browser keeps neither download.

Installing over a running Velocity was also fixed while verifying this: the setup never stopped the app, so replacing
`Velocity.App.exe` failed with an exception and the installer exited with code 1 after copying only part of the files
(Velocity starts with Windows, so the app is nearly always running). The setup now closes the running app first, exactly
like the uninstaller, and retries each file for a few seconds while the process shuts down. Verified with the app
running: install exits 0, the installed extension carries the queue fix, and the relaunched app opens its window.

The retry loop also halves the connections after a failed attempt now: a host that refuses (or rate-limits) many
parallel connections used to leave several downloads as failed rows even though the same file arrives over one
connection. Attempts are still capped by the existing retry limit.

What was actually measured for "only two download at a time": the app has no active-download cap (`MaximumActiveDownloads`
is 0, i.e. unlimited, in the user's settings and in `ProcessQueue`), and six simultaneous browser hand-offs all completed;
through the extension, four downloads captured at once produced four prompts and all four reached Velocity. A test-only
burst script (`artifacts/e2e/burst-downloads.js`) reports how many of four answered prompts arrive, and the background
now exposes `{type:"velocity-stats"}` so a captured-but-not-offered download is visible instead of silent.

## Round 14 — the three stuck downloads, the tab-close data loss, and Chrome's crash dumps

Evidence first. The user's own database had four rows for two episodes: `409`/`410` (r2.dev) and `411` (the
googleusercontent redirect) all targeting `VisionQuest.S01E01…mkv`, two of them running at the same time on the
same destination path. Chrome's profile also held three real crash dumps (`0xc0000005`) written at 18:47, 18:50 and
18:51 while those downloads were being intercepted.

Root causes found and fixed:

* **Two downloads could own one file.** `DownloadManager` queued and started items without claiming a destination
  path, so two rows with one file name wrote the same `.part` (the segmented engine opens it with `FileShare.ReadWrite`,
  so instead of failing they interleaved bytes into one file, and the loser moved a file that no longer existed).
  The manager now claims the path of every starting download and renames a later one to `name (2).ext`;
  `CategoryOrganizer` picks a free name with a non-overwriting move (numbered, not a per-second timestamp that could
  itself collide). This is what made several simultaneous downloads "fail" or vanish.
* **A closed tab destroyed a queued download.** `tabs.onRemoved` deleted every queued offer belonging to that tab.
  Since `activeTabId()` could return an extension popup's own tab (the prompt window that closes when answered),
  real downloads were dropped silently. Queued offers are now re-pointed at the standalone prompt instead of removed,
  `activeTabId()` only accepts http(s) tabs, and `finishOffer` removes a queued offer only for an explicit decision.
* **A page-side dismissal could delete a download it was never shown.** `finishOffer`'s non-active branch removed
  matching queue entries for any caller; it now requires `{ drop: true }`, which only a real user decision passes.
* **Chrome's copy is cancelled first, its entry erased later.** `cancel`+`erase` inside the `onCreated` dispatch
  touched a download item Chrome was still creating. Cancel stays in the same task, the erase now waits until
  Chrome reports the item as interrupted (with `chrome.downloads.search` polling), and `takeOver` can no longer
  produce an unhandled rejection (probe failures fall back to an unverified offer, presenter failures finish the
  offer and move the queue on).

Verification actually run (not compilation): 70 unit tests pass, including new `SameNameConcurrencyTests` and
`ConcurrentControlTests` (pause/resume/cancel/retry across five running downloads with byte-exact hashes);
`artifacts/e2e/detect.js` passes fully; `artifacts/e2e/burst-downloads.js` passes 4/4 on three consecutive runs
(before the tab-close fix it reported 3/4 with `droppedByTabClose: 1`); and through the installed app, ten
simultaneous downloads — six different types/sizes plus four links sharing one file name — all completed, the four
same-name files saved as `episode2.mkv`, `episode2 (2).mkv`, `episode2 (3).mkv`, `episode2 (4).mkv` and every one
of them byte-exact against the source.

## Round 15 — the Chrome crash: a null write in Chrome's own download UI

Five crash dumps from the user's Chrome profile, all identical: exception `0xc0000005` (access violation) with
`ExceptionInformation = [8, 0]` — a **write to address 0x0** — at `chrome.dll+0x4bf8da9`, on the UI thread of the
**browser** process (`ptype=browser`). Timestamps 18:47, 18:50, 18:51, 02:25 and 02:42, each during a download the
user retried two or three times; every dump contains a `https://pub-….r2.dev/…` download URL. A fixed instruction at a
fixed offset with a null write is a deterministic code path on Chrome's side, not a random crash.

The one thing Velocity does that can put a null into Chrome's download UI is `chrome.downloads.erase`: it destroys
Chrome's own `DownloadItem` while the download bubble still holds it, and Chrome 157 then writes through the dead
pointer. So erasing is gone. `cancelBrowserDownload` now only cancels (issued in the same task as `onCreated`, as
before) and waits until Chrome itself reports the item as interrupted; nothing is deleted from Chrome's list, and
the startup cleanup of older paused ids cancels instead of erasing. A cancelled item can never be resumed, finished
or saved by the browser, so the guarantee is unchanged; its row simply stays in Chrome's list marked interrupted, and
no `.crdownload` is left behind (verified: the Downloads folder stays clean).

Verified with real files (not the local test fixtures), headful Chrome, the real extension:
* `artifacts/e2e/real-downloads.js` — 6 real URLs (1 GB OVH, 400 MB Cloudflare, 200 MB Cloudflare, 100 MB OVH, a
  GitHub release asset), cancelled mid-transfer, plus a second round of 6: no crash, no dumps, and Chrome's list shows
  the captured items as `interrupted` with no completed copy.
* the same script run three times against **one link repeated** (`?bytes=400000000` three times), which is the user's
  "same file two or three times" sequence: no crash, no dumps.
* `detect.js` full suite passes (its "no copy kept" checks now count only non-interrupted entries, because the
  interrupted row is intentional), and `burst-downloads.js` passes 4/4 with four prompts.

Chrome's own crash sequence (Chromium issue 566820676, a download-bubble use-after-free after an abnormal exit) was
also reproduced as a control: `artifacts/e2e/chrome-crash-repro.js`. It did not crash this build on its own, so the
trigger remains a download Chrome is handling while its bubble is live — which is exactly where `erase` was removed.

## Round 16 — no folder tree around the user's downloads

`CategoryOrganizer` moved every finished file into a subfolder named after its type ("Videos", "Archives",
"Documents", "Other", "Music", "Images", "Software") inside the chosen download folder, using a count from
`FileTypeInfo.Category(extension)` — and `Directory.CreateDirectory` there is what produced the empty folders the user
saw. That is the exact code path: `DownloadManager.RunAsync` did
`OrganizeCompletedDownloads ? organizer.Organize(result.FilePath) : result.FilePath`.

Removed: `CategoryOrganizer`, its DI registration, the `OrganizeCompletedDownloads` setting (load, save, model, the
"Sort completed files into type folders" checkbox) and the call in `RunAsync`. A download is now saved exactly in the
folder the user pointed it at, and the only directory Velocity creates is that destination folder, when a transfer
needs it (`HttpDownloadEngine.DownloadAsync`). The UI badge still shows a type label; it is display only and touches
no disk.

Verified: new `DownloadFolderTests` runs a real transfer for `.mkv`, `.pdf`, `.zip`, an extension-less file and `.exe`
into its own empty folder and asserts the file sits directly there, that `Directory.GetDirectories` is empty and that
the folder holds exactly one entry. Full suite: 75 passed.

## Round 17 — the speed ceiling now holds on the real transfer

Measured first instead of trusting the code. A new `speed` mode in `tests/Velocity.RestartHarness` runs a real
transfer through `DownloadManager` + `HttpDownloadEngine` (local fast server and the real internet) and prints the
bytes actually written per second, with the ceiling removed or changed mid-flight. Two real defects showed up:

1. **The limiter allowed a burst of half a second's allowance.** `RateLimiter.Refill` capped credit at `rate / 2`,
   so a 1 MB/s ceiling delivered 1.15–1.5 MB/s over short windows. Credit is now capped at `rate / 20` clamped to
   4–64 KB — a few percent, not half the rate.
2. **"No limit" on a file fell back to the app-wide ceiling.** `SpeedLimitKilobytesPerSecond == 0` meant "follow
   Settings", so removing a limit while a global limit existed changed nothing. `DownloadItem.SpeedLimitIsExplicit`
   now records that the user chose a value for this file (persisted as `-1` for "no limit", `0` for "not chosen"),
   and `DownloadManager.EffectiveLimitBytesPerSecond` honours it. Choosing "No limit" means full speed.

The dialogs are in **MB/s** now, since that is the unit the limit is chosen in: `SpeedLimitWindow` takes a decimal
MB/s value (presets 0.5/1/2/5/10/25, "No limit"), and Settings' app-wide box is MB/s too. Both convert to KB/s
internally at 1024 KB per MB.

Measured results (4 connections unless noted):

| Source | Setting | Measured |
| --- | --- | --- |
| local 40 MB, no delay | 1024 KB/s | 10.76 MB in 10.1 s ≈ 1042 KB/s |
| local, two files at once | 512 KB/s each | ~1024 KB/s total, each file its own ceiling |
| local, change while running | 512 → 2048 KB/s | next sample 2176 KB/s, transfer never restarted |
| local, global 512 then "No limit" on the file | 512 → none | 40 MB completed instantly |
| real internet (OVH 100 MB) | 1024 KB/s | ~1003 KB/s average |
| same, limit removed mid-transfer | none | 5–6 MB/s (the line's own speed) |

New `SpeedLimitAccuracyTests` (2 tests, measuring real throughput and 4 connections) pins both fixes. Full suite:
77 passed.

One honest limit: per-second samples still swing ±10–15% because Windows and the network buffer bytes in flight;
the average the transfer achieves tracks the chosen ceiling, which is what a rate limiter can promise.

## Round 18 — the extension only offers what the user asks for

The reports were: opening Chrome, opening a site or refreshing a page brought Velocity's download offer up again for
files already downloaded. Two mechanisms did it, both in the extension:

1. **The page re-presented a saved offer on every load.** `content.js` sent `velocity-hello` on load and the worker
   answered with whatever offer it still held for that tab (`storage.session`, key `pending`). So every reload — and
   every restored tab at browser start — rendered the card again with no click. Removed the whole handshake: the
   message case, `readPending`/`writePending`/`rememberPending`/`forgetPending`, the `pending` key, and the calls in
   `pumpOffers`, `finishOffer` and `takeOver`. A download is offered once, by the worker, when it is captured.
2. **Passive detection had no memory.** `inspectNetworkResponse` told the page about every file-like response on
   every page load, and nothing recorded that the file had already been downloaded (or already offered), so a page
   that fetches a document, or a player that reloads its playlist, produced the same offer each time. Added a
   persistent ledger in `storage.local` (`downloadedFiles`, 1000 entries, signature parameters dropped from the
   identity) written when Velocity takes a download, when Chrome finishes one on its own, and when a network file is
   first offered. Passive detection consults it before sending anything to a page; a deliberate click or a download
   the user starts is never filtered, so nothing became un-downloadable.

Verified in a real Chrome with the real extension (`artifacts/e2e/popup-regression.js`): opening and refreshing a
page shows no card and no pill, a file the page fetches is still offered once, refreshing does not offer it again,
the record survives a browser restart (same profile, fresh process) so it is not offered again, no card appears after
that restart, and the handshake is gone from the source. `artifacts/e2e/detect.js` still passes every check with the
changed extension — hover buttons, network offer, click-to-send, stream strip, fullscreen, exclusions.

## Round 19 — passive offers need a gesture, and a dismissal sticks

Reported again on a second Chrome profile: offers came back on every browser start. Chrome's own `History` database
for that profile showed why — the entries are *passive* detections (`video-downloads.googleusercontent.com/ADGPM…`
attachment responses of 400–540 MB), not captured downloads (the newest `downloads` row predates the previous fix).
The page a restored tab loads fetches those files by itself, every URL carries a fresh token, so neither a
per-navigation dedupe nor a URL-shaped ledger could stop it.

Two changes in `detector.js`:

1. **A passive offer now requires an interaction on the page.** `pointerdown`, `mousedown`, `keydown` or
   `touchstart` unlocks offers for that document; scrolling deliberately does not. A page that fetches a file while
   it loads — a restored tab, a preview handler, a player preloading the next item — is no longer offered anything,
   which is exactly the "popups when Chrome starts" case.
2. **Dismissing an offer is remembered in `storage.local`** (`dismissedOffers`, 500 newest), not in the page. Closing
   a pill now really ends it: a reload, a fresh page or a browser restart does not bring that file back.

Verified: `artifacts/e2e/popup-regression.js` (13 checks) — no card or pill on load and reload, a file fetched before
any interaction is not offered, scrolling does not unlock offers, a click does unlock them, the offered file is
remembered and is not offered again after a refresh or a browser restart. `artifacts/e2e/detect.js`: 99 checks, all
passed (hover buttons, network offer after interaction, click-to-send, stream strip, fullscreen, exclusions).

## Round 20 — a download Chrome replays at startup is not a new download

The screenshot (`pop.png`) showed the offer in the extension's standalone window with a blank new tab behind it, for
`pub-d888f9ac9b9f48ec9cfd894703c927c5.r2.dev/979569a9838fd8cd906226c7b1b51ecc` — the item Chrome has kept in its own
download list since Nov 9. Chrome retries downloads it still holds when it starts; each retry was captured, offered,
cancelled and left interrupted, so the next start offered the whole queue again, one prompt after another.

`takeOver` now treats a captured download whose own `startTime` is more than two minutes old as a replay, not a new
request: the browser's copy is still cancelled, the file is recorded as declined, and nothing is offered. And
declining an offer is now recorded for the captured-download path too (`downloadedFiles` … `how: "declined"`), so a
later retry of that exact file is cancelled silently instead of asking again. Both rules only affect the interception
path; clicking a link or starting a download yourself is unchanged.

Verified: `artifacts/e2e/declined-downloads.js` (5 checks) — the decline is recorded, the same download started again
is not offered and opens no offer window, while Chrome's copy is still cancelled. `artifacts/e2e/popup-regression.js`
13/13 and `artifacts/e2e/detect.js` all passed with the same build.

## Round 21 — "downloaded it, restarted, it asked again": one rule, tested

The remaining case was a file the user *did* download: restart Chrome once and the offer appeared one time, then not
again once it was cancelled. The previous rule only skipped captures the user had *declined*; a file already taken by
Velocity was still offered if Chrome replayed it.

The decision now lives in its own file, `capture-decision.js`, loaded by the worker with `importScripts` and asserted
directly by `artifacts/e2e/capture-decision.js`:

* **Same download item** — every id the extension cancels is stored (`cancelledDownloads`, 300 ids, `storage.local`).
  Chrome reuses an item's id when it retries an interrupted download, so a capture with a known id is a replay: it is
  cancelled and never offered, on every future start.
* **Old start time, or no page behind it** — a capture that is more than two minutes old, or has no tab, cannot be a
  fresh choice, so it is not offered.
* **Known file** — a URL already recorded as taken, offered or declined is not offered again when it arrives without a
  page. A fresh download started from a page is still offered, so re-downloading on purpose keeps working.

Verified: `artifacts/e2e/capture-decision.js` 8/8; `artifacts/e2e/declined-downloads.js` all checks passed;
`artifacts/e2e/popup-regression.js` all checks passed; `artifacts/e2e/detect.js` all checks passed.

## Round 22 — a fixed identity, an app that repairs itself, and a browser that waits

The user moved the Velocity folder, and the extension disappeared from every Chrome profile; reloading it unpacked
then produced *"The Velocity native host is not installed."* Three separate things in the same chain:

1. The manifest had no `key`, so an unpacked extension's id was derived from the folder path. New folder → new id.
2. Chrome still pointed at the old absolute path, so the extension was dropped from the profile.
3. The native-host manifest authorised exactly one id — the old one — so the reloaded copy was forbidden from
   messaging the host, while the message told the user to run a script that lives inside the folder they had moved.

**Fixed identity.** `browser-extension/chromium/manifest.json` now carries a public `key`, so the id is
`jnfpmdnbiomjnelmlaohkpifaacandjg` everywhere: any folder, any profile, any machine, any reinstall. The matching
private key stays out of version control (`artifacts/keys/`, gitignored). Reloading from a moved folder now keeps the
same id, the same `chrome.storage` and the same host permission.

**Self-repairing registration.** `StartupIntegration` always authorises the shipped id — it no longer depends on
`browser-settings.json` having an `extensionId`, which a fresh profile or a moved folder never wrote — and appends any
id already granted, so an older install keeps working during the transition. `register-native-host.ps1` defaults to
the same id and takes `-AdditionalExtensionIds`. The extension's error text no longer names a script: it says to open
Velocity once, which is what actually repairs it.

**Chrome waits for the decision.** A captured download is now *paused*, not cancelled, the moment it appears, and the
offer is made while it waits. "Keep in Chrome" resumes that exact transfer, so a signed, POST or one-time-only link
still delivers — the case a fresh `downloads.download` could not. Taking the file in Velocity cancels the paused copy;
a failed hand-off resumes it instead of starting a second request; an unanswered offer cancels it when its lifetime
ends. Only an offer Chrome never turned into a download (a bare page click) still starts a fresh request for
"Keep in Chrome". Interception off or an excluded site is untouched: Chrome downloads normally.

The agent moved into `holdBrowserDownload` / `releaseHeldDownload` in `background.js`; Firefox (no pause in its API)
keeps the cancel path.

Verified on this build with real Chrome:

- `artifacts/e2e/declined-downloads.js` — all checks passed (the declined file stays `interrupted`, never in progress).
- `artifacts/e2e/popup-regression.js` — all checks passed.
- `artifacts/e2e/detect.js` — all checks passed (including "Chrome keeps no copy" after a pause is cancelled, and the
  native-host hand-offs, which first failed only because the test copy's new id was not yet authorised).
- `artifacts/e2e/capture-decision.js` — 8/8.
- `dotnet build Velocity.slnx` — 0 warnings, 0 errors. Installer rebuilt, installed, app started; it rewrote the host
  manifest by itself with the fixed id, and the installed extension carries the same `key` and hold logic.

Loading instructions in `EXTENSION-NOTES.md` and `browser-extension/README.md` now point at the installed copy,
`%LOCALAPPDATA%\Programs\Velocity\browser-extension`, whose path never changes.

## Round 23 — an XDM-style download window

The library showed a transfer only as a row. The user asked for the window XDM opens beside a download, in both of
its sizes, keeping Velocity's own theme and adding the speed limit that XDM's small window does not have.

**`DownloadWindow.xaml`** is one window with two layouts. The big one carries the drawn caption `[ 5% ] name`, a
ring, the name, the status line, `Downloaded n%` against `bytes / total`, the progress bar, and `Speed …` against the
time left, then `Less` on the left and `Speed limit`, `Hide`, `Pause` on the right, over the connection table. `Less`
switches to the small layout (narrower window, wrapped name, tighter rows, `More`/`Hide`/`Pause`) at the same
information density as the reference screenshot, and resizes the window with it. Everything else keeps the existing
brushes and button styles, so all themes work unchanged; the caption is a `WindowChrome` bar so a dark window does
not grow a light system title bar.

**`ProgressRing`** is a small `FrameworkElement` that draws the track circle and the finished arc itself. It takes
`Value` plus track/fill brushes as dependency properties, so it repaints on every progress report and follows a
theme switch like any other control.

**Connection table.** `DownloadProgress` now carries `ConnectionBytes` — the bytes each segment has written — which
the segmented engine reports inside its existing 100 ms report gate. `DownloadManager` copies it onto the item, which
clears it at the start of every attempt so a retry that falls back to one connection cannot leave stale rows. The
window builds one row per connection (# / Status / Downloaded), with `Downloading`, `Completed`, `Waiting`, `Paused`,
`Stopped` or `Failed` derived from the item's status and the bytes written against each connection's share.

**Reaching it.** Double-clicking a download in the library opens its window, and a new window icon on each card does
the same; a second click brings the existing window back instead of opening a duplicate. Deleting a download closes
its window. Nothing opens on its own — capture still never throws a window at the user.

Verified: `dotnet build Velocity.slnx` — 0 warnings, 0 errors; both layouts rendered from a throwaway harness
(`artifacts/window-preview/`) and checked against the two reference screenshots. The window was not driven against a
real active transfer in the running app.

## Round 24 — capture end to end: offer, then window, then library

Round 23 built the window but only opened it from the library, so a link captured from the browser still started
silently: the in-page offer card asked, the transfer began, and nothing else happened. That is not the XDM flow the
user described (ask → downloading window → progress visible in the main window).

**Captures now finish the flow.** When the browser inbox delivers an accepted `download` entry,
`ProcessBrowserInboxAsync` starts it, raises the main window (`BringToFront`) and opens that transfer's
`DownloadWindow` immediately, so the window is on screen while the bytes arrive instead of after the fact. Entries the
user sent with "Download later" still queue quietly, and nothing opens on its own for history, startup or a plain
page load — the extension's offer card is the ask, and this reacts only to its accepted answer.

**The library always shows it.** `RevealInLibrary` selects the new row and scrolls the grid to it, clearing an active
filter or search that would otherwise hide the transfer. The window and the library read the same `DownloadItem`, so
speed, progress, pause/resume and the speed limit stay in step in both places.

**Theme.** No colour was added: the window, the ring, the table and all buttons still use the brushes in
`Resources/Theme.xaml`, so Midnight, Ember and Light look like the rest of Velocity rather than XDM's blue palette.

Verified: `dotnet build Velocity.slnx` — 0 warnings, 0 errors; installer rebuilt and installed so the running app is
this build. Not verified: a real capture driven from Chrome in this session.

## Round 25 — the two windows are independent, and the confirmation matches the transfer window

The user's list exposed three structural mistakes, not cosmetic ones. The download window was created with the
library as its `Owner`, so Windows closed it with the library and the pair could never be closed independently. The
library hid itself from the desktop whenever it was minimized, so it left the taskbar and could not be restored from
it. And the confirmation dialog was still 580 wide with `SizeToContent="Height"`, so accepting a download swapped a
small window for a 900x560 one.

**One owner for both windows.** `DownloadCoordinator` now owns the window of every transfer and the browser-inbox
listener. A `DownloadWindow` is a top-level window with no owner: closing the library leaves it open and the transfer
running, and closing it never touches the library. Because the listener moved up to the application, a capture that
arrives while the library is closed still starts and still gets its window. Deleting a download closes only that
download's window, through the same coordinator.

**The application decides when to exit.** `ShutdownMode` is now `OnExplicitShutdown`:
`App.NotifyWindowClosed` keeps the process alive while another window is visible or `DownloadManager.HasActiveTransfers`
is true, and shuts down on the last window of an idle application — the same exit the user gets today. A closed WPF
window cannot be shown again, so the tray icon builds a new library window, and `DownloadManager.Items` plus the
history load fills it without a second row for a transfer that is already running; the abandoned-partial sweep now
sees those live rows too.

**Minimize means minimize.** The `StateChanged` handler that called `Hide()` is gone. The library stays a normal
taskbar window, leaves the desktop when minimized and comes back from its taskbar button; the tray icon still opens
it as well.

**One size for the confirmation.** `AddDownloadWindow` is 900x560 like `DownloadWindow`, `ResizeMode="NoResize"`, with
its content in the scroll viewer it already had. Accepting it closes it and opens the transfer's window; a batch keeps
only its first window and the rest of the rows in the library, and the library is no longer raised in front of the
window that the user actually asked for. Starting from the clipboard prompt opens that window too.

Verified with `artifacts/window-flow-check.ps1` against the Debug build and a real 8 MB transfer from
`artifacts/e2e/slow-server.js` — all five scenarios pass: the confirmation window and the download window have the
same 900x560 DIP outer size (1125x700 device px at 125%); accepting closes the dialog and leaves exactly the library
and the one download window; minimizing the library parks only it off the desktop and the transfer window keeps
running; closing the transfer window leaves the library and the process untouched; closing the library mid-transfer
leaves the process and the download window alive, the partial keeps receiving writes, and the file completes to
8 388 608 bytes with no library window open. The last window of an idle application still ends the process.
Not verified: a click on the extension's card in Chrome in this session.

## Round 26 — resume is fast and honest, and Velocity stays in the tray

**Resume no longer validates with a separate request.** A paused download already knows its address, length and
validator from the checkpoint beside the partial file, so `SegmentedAsync` goes straight to its per-segment `Range`
requests instead of asking the server for the length first — one whole round trip (and the server work behind it)
removed from every resume. The range responses are the validation: each one either starts at the exact saved byte or
the resume is refused. Mirror probes, when mirrors are configured, now run concurrently instead of one after another.
On the device the first progress report after Resume arrives in 24–34 ms, and the server log shows four segment
requests resuming exactly the bytes in the checkpoint, with no `bytes=0-0` probe at all.

**A resume never restarts or deletes anything.** `HttpDownloadEngine` used to treat `200 OK`, an unexpected `206`, a
changed `ETag`/`Last-Modified`, or `416` as "start over", truncating or deleting the partial file. That is exactly the
silent restart the user forbade, so every one of those answers now raises the new
`DownloadLinkExpiredException` (message: "Download link is broken or expired. You need to download this file again.")
and leaves both the `.part` and its checkpoint untouched. `401/403/404/410` on a resume are treated the same way; a
`5xx` or timeout keeps the ordinary retry path. A `416` whose total matches the saved bytes still completes the file
without a transfer. The download's original URL is what is stored and reused (the resolved `FinalUrl` stays only as a
record), so a download keeps its own link across pause, close and restart.

**Broken links have their own state and their own action.** `DownloadItem.LinkExpired` drives a "Link expired" status,
keeps "Retry" hidden, and adds a "Download again" row action (and the transfer window's primary button). That action
is the only place a partial file is deleted, and it reuses the original link, headers, name and folder. The library
shows the exact sentence in a themed dialog once per download, with the server's reason underneath and a note that the
partial file was kept. The dialog is queued after the status change rather than shown inside it, so a modal window can
never stall the transfer's own completion; the first attempt at showing it from inside `Dispatcher.Invoke` never
appeared, which is why it is now posted to the dispatcher.

**The tray keeps unfinished downloads.** Closing the library with anything running, queued, paused or failed hides the
window into the notification area instead of destroying it (`App.HideMainWindowInsteadOfClosing`), keeps the process
alive (`App.NotifyWindowClosed` → `DownloadManager.HasUnfinishedDownloads`), and the tray icon brings the very same
window back with its rows. The tray menu now reads "Open Velocity" and "Force quit Velocity", and force quit bypasses
the hide rule (`App.ForceQuit`). History rows are registered with the manager, so a paused download from an earlier
session counts as unfinished and a second copy of it can never appear when the library is rebuilt.

**A browser hand-off stays out of the user's way.** The native host writes the inbox entry and then starts the app, so
startup can tell why it was launched: with a pending capture the library is created with `ShowActivated=false` and
`Minimized`, and the inbox is processed immediately instead of waiting for the first two-second tick. The only window
on screen is the transfer's; the library appears when the user clicks its taskbar button or the tray icon.

**The confirmation window grew.** `AddDownloadWindow` is now 960x640 (was 900x560) and `DownloadWindow` matches it, so
the two windows keep the same size while the form's file name, folder, referer, cookie, mirror and header fields all
fit without scrolling.

Verified with `artifacts/resume-check.ps1` (new, `artifacts/e2e/resume-server.js` as the 24 MB range server) against
the Debug build, all checks passing: the confirmation window measures 960x640 DIP (1184x792 px at 125%); a download
paused at 25% resumes with its first progress report after 29 ms and four range requests that continue exactly the
6 553 600 bytes in the checkpoint, without a probe request, and finishes byte-identical (SHA-256); a link turned into
a 404 while paused produces the exact required message, keeps `.part` + `.part.meta` at the same byte count, offers
"Download again", and that action re-downloads from the original link to a byte-identical file; closing the library
with a paused download hides the window while the process lives, one click on the notification-area icon restores it,
double-clicking the paused row restores its transfer window, Resume finishes the file, and Force quit from the tray
menu ends the process; a browser hand-off started the app with the library minimized and only the transfer window
visible. `dotnet test` passes 77/77 after the resume tests were rewritten to the new contract (a refused resume must
throw, keep the partial bytes and never write the destination file). Re-run against the rebuilt installer's build
(`%LOCALAPPDATA%\Programs\Velocity\Velocity.App.exe`) it reports 0 failures: every check above passes on the installed
build, including Force quit from the tray menu.

Test note: the notification-area icon lives in Windows 11's overflow flyout, which ignores scripted mouse input, so
`artifacts/resume-check.ps1` opens the tray menu the way the shell does — by sending the app's NotifyIcon window the
right-click callback — and then invokes "Force quit Velocity" through UI Automation. The tray icon's own left-click
action is exercised through its `Invoke` pattern. Both are the application's real handlers, not stand-ins.

## Round 27 — a finished transfer's window leaves the screen

`DownloadWindow` closed only when the user closed it, so a download that reached 100% left its window sitting on
screen. `LeaveOnCompletion` now watches the item's status: when it becomes `Completed` the window closes itself once,
posted at `Background` dispatcher priority so the final 100% frame paints first. Paused, failed and cancelled
downloads keep their windows — the rule is only about a finished transfer — and the library is untouched behind it.

Verified with `artifacts/window-close-check.ps1` (new, `artifacts/e2e/close-test-server.js` as a ~1 MB/s range server)
against the real Debug build, 0 failures: the transfer window opens, is still on screen at 28% and 78% while bytes
move, disappears once the file completes, the library window is still there, and the 4 194 304-byte file is complete
on disk. `dotnet build Velocity.slnx` is clean (0 warnings, 0 errors). The installer was rebuilt and silently
installed, and the same check reports 0 failures against `%LOCALAPPDATA%\Programs\Velocity\Velocity.App.exe`
(28% → 78% → window gone, library window still present, file complete). The library is often minimized at that point,
which the check accepts: the point is that the transfer window left and nothing else did.

## Round 28 — Velocity for Android

The phone app now lives in `android/`: Kotlin, one Gradle module (`com.velocity.downloader`, minSdk 26, target 35,
compileSdk 36, AGP 8.13.2, Kotlin 2.2.21), no Compose and no Room, so the whole APK is under 4 MB and builds offline
from the installed SDK.

**Paste, tap Download.** `MainActivity` takes any http/https link (typed, pasted, or shared to Velocity through
`ACTION_SEND`), shows the Download button directly under the field, and sends the row to the service. Analysis happens
in the service — HEAD first, then `Range: bytes=0-0` when HEAD is refused or says nothing useful — and fills in the
name (`Content-Disposition`, then the redirected path, then the link), the length, the content type and whether ranges
work. A missing length or content type never blocks the download. The folder comes from Android's own folder picker
(`ACTION_OPEN_DOCUMENT_TREE`, persisted), and without a choice the file lands in the shared Downloads collection
through MediaStore.

**The engine** (`core/DownloadEngine.kt`) preallocates the file and splits a known, range-capable download over four
connections that each own a byte range and can be retried on their own; a server that answers a ranged request with
`200 OK` drops the transfer to one stream instead of failing. Range offsets are validated against the saved total and
`ETag`, each connection resumes from its own written bytes after a drop, and a checkpoint file (`<id>.meta`) is
rewritten every half second so a process death continues from where it stopped rather than from zero. `429` and `408`
retry with backoff; `401/403/404/410` stop with the server's actual reason and keep the partial file. Transfers never
overwrite: a finished file gets a unique name in the chosen folder, and the same link cannot be queued twice while it
is running or paused.

**Background and lifetime.** `DownloadService` is a `dataSync` foreground service with a progress notification
(pause/resume/cancel actions), a partial wake lock while bytes move, a queue of three concurrent transfers, and
`START_STICKY`; anything the system stops is re-queued on the next start, and opening the app puts a stalled queue back
to work. Pause stops the streams and keeps the bytes, resume continues from them, cancel deletes the partial file.

Verified on the connected Realme RMX3785 (Android 15) with the debug APK installed by adb:
`gradle testDebugUnitTest` passes; a real GitHub archive download followed the redirect, used the server's name, and
produced a valid 12 849 721-byte zip with 4 625 entries; a 200 MB throttled local file paused at 136 429 568 bytes and
stayed there, resumed from exactly that byte to completion; the same file grew from 71 MB to 122 MB with the app in
the background and had its notification present; a force-stop plus reopen continued past 153 MB and finished at
209 715 200 bytes; `404` produced "The server has no file at that address (HTTP 404). The link may have expired."; a
chunked 8.4 MB response with no length completed and was saved under its `Content-Disposition` name; a 4 MB
no-range server completed over a single connection; the same link queued twice produced one row; `logcat` showed no
crashes.

Known limits, stated rather than hidden: Android has no FTP client, so `ftp://` and `magnet:` links are refused with an
explanation instead of a fake attempt; DRM, paywalls and sign-in walls are not bypassed — cookies from the browser's
cookie store are sent when the platform has them; the APK is debug-signed because no release keystore exists in the
repository.

## Round 29 — the Android settings experience, in two themes

The phone app now wears the reference designs instead of the first-pass look. Every colour in the app comes from two
themes — **Dark Jet Black** and **Beige** — declared as attributes (`values/attrs.xml`) and resolved through
`Theme.Velocity.Dark` / `Theme.Velocity.Beige`. Layouts, drawables, switches, radios, sliders, dialogs, the status bar
and the notification-free surfaces all read those attributes, so no screen can drift into a third palette.
`ThemedActivity` applies the saved theme before inflation and recreates a screen when the theme changes underneath it;
the Home screen also flips its status-bar icons to dark in Beige so they stay legible.

**Settings** (`ui/SettingsActivity`) follows `mob/setting.png`: slate/brown app bar, a card with the theme dropdown
("Dark Jet Black" / "Beige"), then one card with exactly four rows — Download Settings, Traffic Limits, About, Quit.

**Download Settings** (`ui/DownloadSettingsActivity`) reproduces `mob/donw.png`: the five switches (start without
confirmation, remove deleted files, remove completed downloads, do not download web pages, server time for file
creation) and the Default download folder block — automatic folder radio with the two indented "suggest folders"
switches (by file type, by URL), fixed-folder radio with the picker field, folder button and the chosen path shown in
accent underneath. The switches are not decoration: a confirmation dialog appears before a transfer when the first
switch is off, finished rows vanish when the third is on, `text/html` links are refused with an explanation when the
fourth is on, MediaStore files take the server's `Last-Modified` when the fifth is on, and the folder radios decide
where `OutputStore` writes. Suggested folders create `Download/Velocity/Images` (or Videos, Audio, Documents, Archives,
Apps, Other) and add the host when the URL switch is on; with no suggestion the file still lands directly in Downloads.

**Traffic Limits** (`ui/TrafficLimitsActivity`) reproduces `mob/traffic.png`: two expandable sections with rotating
chevrons. "Download speed" holds a slider over Unlimited, 64 KB/s … 32 MB/s, and "Maximum number of simultaneous
downloads" holds a −/+ stepper (1–8). Both persist and apply live: `BandwidthLimiter` is one shared token bucket used
by every connection in the app, and `DownloadService` reads the concurrency value on each pump (and re-pumps when it
changes).

**About** reads the real package version (1.0.0) instead of a hard-coded string, names Shahrukh Rais Khan as developer,
and uses the actual Velocity emblem. **Quit** asks "Do you want to exit from Velocity?" and only closes the app on Yes.

**Icon.** `icon.png` in the repository root is the source: the V emblem was cropped and written to all five legacy
densities (`mipmap-mdpi…xxxhdpi`, 48–192 px), round variants, and adaptive-icon foreground layers (108–432 px) over a
black background, with `mipmap-anydpi-v26` XML for both launcher icons. The old placeholder vector was deleted.

Verified on the connected Realme RMX3785 (Android 15): `gradle assembleDebug testDebugUnitTest` passes; the debug APK
was installed by adb and each screen was inspected in both themes (Settings, Downloads settings, Traffic limits, About,
the theme dialog and the Quit dialog; the status bar now shares the app bar colour). Functional checks: setting the
slider to 256 KB/s made a 4 MB single-connection transfer take 15 s (≈280 KB/s measured from the store); with the
concurrency stepper at 1, a second queued link stayed `QUEUED` at 0 bytes while the first was `DOWNLOADING`; settings
survived a force-stop (theme, limit and concurrency all restored); Quit with Yes removed every Velocity activity from
the task. All test downloads, partial files, `downloads.json` and the `Download/Velocity` test folder were removed, the
speed limit was returned to Unlimited, concurrency to 3, and the theme to Dark Jet Black.

Known limits, stated rather than hidden: "Use server time" can only stamp the file through MediaStore — SAF has no API
to set a document's modification time, so the fixed-folder path keeps the device's clock; the two "suggest folders"
switches belong to automatic mode, exactly as the reference nests them, and are disabled while a fixed folder is
chosen; the speed limit is per app, not per transfer, because that is what the reference screen describes.

## Round 30 — pasted links that were dropped or called dead

Three real failures were reproduced on the phone before any code was touched. **A link pasted without its scheme**
(`samplelib.com/mp4/sample-5s.mp4`) or **shared together with its title** ("Check this video https://… nice") never
reached the box at all: both the share intent and the clipboard prefill only accepted text that began with `http`, so
the field stayed empty and Download answered "Paste a download link first". **A `http://` link that redirects to
`https://`** failed with "The server redirected the request somewhere that could not be followed (HTTP 301)", because
`HttpURLConnection` only follows redirects inside one protocol; a 308 was not followed at all and left the row
`ANALYZING`. Direct `https` file links did work, which is why the bug looked intermittent.

`core/Links` now turns pasted text into a fetchable address: it finds an `http(s)` link inside surrounding words,
accepts a bare host (with or without `www.`, with a port, or an IPv4 address) and assumes `https://`, drops quotes,
brackets and sentence punctuation around it, percent-encodes spaces and non-Latin characters, and refuses text with
no link at all or a scheme Android cannot fetch (`ftp:`, `magnet:` still get their explanation). `MainActivity` uses
it for Download, the share intent and the clipboard prefill.

`Http.open` now follows redirects itself — 301, 302, 303, 307 and 308, absolute, protocol-relative and relative
`Location`, across `http`↔`https`, up to ten hops, with the `Range` header preserved on every hop and a clear message
for a redirect with no destination or an endless loop. Because some CDNs answer 403 only to a client that is not a
browser, a 403 is retried once with a browser User-Agent before it is reported (a caller-supplied agent is never
overridden). `UrlAnalyzer` also retries its probe without a range when a server answers 416/400/405 to `bytes=0-0`,
and its probe timeouts grew to 20 s so a slow mobile network is not mistaken for a dead link. `FileNames` no longer
turns a literal `+` in a path into a space.

Verified on the Realme RMX3785 (Android 15) with the rebuilt APK: `gradle assembleDebug testDebugUnitTest` passes,
including new `LinksTest` cases for every paste shape. On the device, all of these completed with the right name and
byte count: scheme-less `samplelib.com/mp4/sample-5s.mp4`, share text with words around the link, a quoted URL,
`http://` → `https://` 301, an mp3 reached through a 301 to another host, and — against a local server — 308, 307 and
302 with relative, absolute and same-origin `Location`s. A local endpoint that returns 403 unless the agent looks like
a browser completed through the fallback, and a normal direct mp4 still completed after the change. Every test file,
`downloads.json` and the temporary servers were removed and `adb reverse` cleared afterwards.

Known limits, stated rather than hidden: a link to a **web page** (YouTube, Instagram, a file host's share page and
similar) is still refused while "Do not download web pages" is on, exactly as that setting says; Velocity does not
extract media out of page HTML, JavaScript players, DRM or sign-in walls, so such links must be the direct file
address. Sites that require a sign-in cookie can still refuse the request.

## Round 31 — the mobile app and the desktop work published

The repository on GitHub held only the Windows build and the browser extension, while this working copy carried two
finished pieces that had never left the machine: the Android app (rounds 22–30, `android/`) and the desktop round that
replaced the old inline progress logic with `DownloadCoordinator`, `DownloadWindow` and `ProgressRing` and made the
engine's resume checkpoints tolerant of link expiry (`DownloadLinkExpiredException`).

Everything under `android/` is now tracked — 76 source, resource and Gradle files, with `.gradle/`, `build/` and
`local.properties` still ignored, so no SDK path or build output is published. `README.md` gained an **Android app**
section: what it does, the `assembleDebug` / `testDebugUnitTest` commands, and where the installable APK is attached.
The screenshots in `mob/` and the loose captures in the repository root stay untracked; they are working files, not
source.

Release assets are separate from source. A new release, `mobile-v1.0.0`, carries `Velocity-Mobile-1.0.0.apk`
(4.2 MB, `com.velocity.downloader`, minSdk 26, targetSdk 35) built from this tree; `v1.0.0` keeps the Windows installer
and portable zip untouched. The APK is the debug-signed build, because the project has no release keystore and an
unsigned release APK cannot be installed — it installs and updates in place with `adb install -r`, as verified on the
Realme RMX3785, but it is not a Play-Store-signed artifact.

Verified before publishing: `gradle assembleDebug testDebugUnitTest` and `dotnet build Velocity.slnx` both succeed on
this tree, so the published source is the source that was built and tested.

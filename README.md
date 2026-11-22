# Velocity

Velocity is a native Windows download manager built with WPF and .NET 10.

Velocity provides a native WPF download manager, real asynchronous HTTP downloads, progress reporting, pause/resume, retry, queue concurrency, persistent status, HTTP Range segmentation, browser integration, speed limits, scheduling, tray support, notifications, and optional category organization.

## Android app

A native Android build of Velocity lives in `android/` (Kotlin, `com.velocity.downloader`, Android 8.0+). It downloads
the link you paste — video, audio or any file — with pause/resume, an active-download queue, a speed limit, a
background service with notifications and share-sheet/clipboard paste.

The ready-to-install APK is attached to the release as `Velocity-Mobile-1.0.0.apk`. To build it yourself:

```bash
cd android
./gradlew assembleDebug        # -> app/build/outputs/apk/debug/app-debug.apk
./gradlew testDebugUnitTest    # unit tests
```

`android/local.properties` must point `sdk.dir` at your Android SDK.

## Releases

```powershell
powershell -ExecutionPolicy Bypass -File artifacts/build-installer.ps1
```

This publishes the app, the native messaging host and the browser extension, then writes:

- `artifacts/velocity-portable/` — the runnable folder (no installation, no admin rights)
- `artifacts/Velocity-1.0.0-portable.zip` — the same folder as a portable archive
- `artifacts/installer/Velocity-Setup-1.0.0.exe` — a per-user installer (installs to
  `%LOCALAPPDATA%\Programs\Velocity`, Start Menu and desktop shortcuts, an entry in *Apps & features*). It accepts
  `/S` for a silent install, `/desktop=false` to skip the desktop shortcut, and `/uninstall /S` to remove it.

## Build

```powershell
dotnet restore
dotnet build
dotnet test
```

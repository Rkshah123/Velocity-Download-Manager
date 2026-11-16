# Velocity

Velocity is a native Windows download manager built with WPF and .NET 10.

Velocity provides a native WPF download manager, real asynchronous HTTP downloads, progress reporting, pause/resume, retry, queue concurrency, persistent status, HTTP Range segmentation, browser integration, speed limits, scheduling, tray support, notifications, and optional category organization.

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

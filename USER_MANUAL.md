# Velocity User Manual

## Download a file

Run Velocity, select **Add Download**, enter a direct HTTP/HTTPS URL, choose a folder, and select **Start download**. The list shows real progress, size, and speed.

## Pause and resume

Use the row controls. Paused downloads preserve a `.part` file and resume when the server supports byte ranges.

## Settings

Settings controls the default folder, active-download count, connection count, retry limit, speed limit, notifications, and optional category organization.

## Schedule

Use **Scheduler** to start a download later. Velocity must remain running; minimizing to the tray is supported.

## Browser integration

Load the matching unpacked extension from `browser-extension`, publish the native host, and run `register-native-host.ps1` as described in `browser-extension/README.md`. Capture can be manual or automatic.

### Automatic detection

While the extension is enabled, a small Velocity button appears next to file links you point at, over a video or audio file the page plays directly, and in the page corner when the page fetches a file itself. Every offer is checked against the server's response first — name, type and size come from the response, not from the link text — and nothing is added to Velocity until you click it.

The switch is **Show a Velocity button for files on the page** on the extension's options page. Sites listed under **Never intercept these sites** stay completely untouched, and links that are ordinary pages are never offered.

Velocity does not bypass DRM, authentication controls, paywalls, or protected streaming systems, and never executes downloaded files automatically.

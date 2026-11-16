# Loading the Velocity extension into another Chrome profile

The extension is **unpacked** (not from the Web Store), so each Chrome profile loads it once by hand.
Chrome gives an unpacked extension an ID derived from **the folder it was loaded from** — load the *same folder*
and you always get the *same ID*, which is what the native-messaging host is already registered for.

Already-registered ID: `loopmagklpfneecglcankfihcephdbld`

## Steps (2 minutes)

1. Open the profile you want (Chrome → profile avatar → switch, or run `chrome.exe --profile-directory="Profile 1"`).
2. Go to `chrome://extensions`.
3. Turn on **Developer mode** (top-right).
4. Click **Load unpacked** and pick this folder:

   ```
   C:\Users\ShahrukhKhan\Desktop\Velocity\browser-extension\chromium
   ```

   (the installed copy, `%LOCALAPPDATA%\Programs\Velocity\browser-extension`, has the *same files* but a
   *different folder path*, so it would get a different ID — see below.)
5. The card must show **Velocity Download Manager 2.1.0** with ID `loopmagklpfneecglcankfihcephdbld`.
6. Pin it: puzzle-piece icon → pin "Velocity Download Manager".
7. Open its **Details → Extension options**, or click the toolbar icon, and set your download folder / detection
   switch once for that profile (settings are per profile).

That is all — no registry work, no restart. Make sure the Velocity app is running (or it will be started
automatically on your first download).

## If you load a different copy (different ID)

Chrome native messaging only talks to an extension whose full ID is listed in the host manifest. If step 5 shows a
different ID, add it to:

```
%LOCALAPPDATA%\Velocity\native-host\com.velocity.download_manager.json
```

```json
"allowed_origins": [
  "chrome-extension://loopmagklpfneecglcankfihcephdbld/",
  "chrome-extension://<the-new-id>/"
]
```

Save the file, then in `chrome://extensions` press ↻ (Reload) on the Velocity card. Nothing else changes.

## Reloading after an update

When Velocity is updated (or you change the code), each profile needs one click:
`chrome://extensions` → Velocity card → ↻ (Reload). Chrome does not hot-reload unpacked extensions.

## Removing it from a profile

`chrome://extensions` → Velocity card → **Remove**. Other profiles are unaffected, and the app keeps working.

# Loading the Velocity extension into another Chrome profile

The extension is **unpacked** (not from the Web Store), so each profile loads it once by hand.

Its ID is **fixed by the public key in `manifest.json`**, not by the folder it is loaded from:

```
jnfpmdnbiomjnelmlaohkpifaacandjg
```

That is the whole point of the `key` field. Before it was added, Chrome derived the ID from the load path, so moving
or renaming the folder gave the extension a new ID — Chrome dropped the old entry, and the native-messaging host,
which authorises one exact ID, stopped answering. Never remove `key` from the manifest.

## Load it from a folder that does not move

```
%LOCALAPPDATA%\Programs\Velocity\browser-extension
```

The installer keeps that copy up to date. Loading it means moving, renaming or deleting the project folder cannot
affect Chrome at all. (Loading `Velocity\browser-extension\chromium` from the project also works and gives the *same*
ID now; moving the project then needs one **Reload** click and nothing else.)

## Steps (2 minutes)

1. Open the profile you want (Chrome → profile avatar → switch, or run `chrome.exe --profile-directory="Profile 1"`).
2. Go to `chrome://extensions`.
3. Turn on **Developer mode** (top-right).
4. If an old Velocity card is still there, remove it (it points at a folder that may no longer exist).
5. Click **Load unpacked** and pick the installed folder above.
6. The card must show **Velocity Download Manager 2.1.0** with ID `jnfpmdnbiomjnelmlaohkpifaacandjg`.
7. Pin it: puzzle-piece icon → pin "Velocity Download Manager".
8. Open its **Details → Extension options**, or click the toolbar icon, and set your download folder / detection
   switch once for that profile (settings are per profile).

That is all — no registry work, no restart. Make sure the Velocity app is running (or it will be started
automatically on your first download).

## Why it stopped working after the folder moved

1. An unpacked extension's ID used to come from its folder path. New path → new ID.
2. At the same moment Chrome lost the folder it had been pointed at, so it removed the extension from the profile.
3. The native host manifest authorised only the old ID, so even a successfully reloaded copy got
   *"The Velocity native host is not installed"*.

All three are gone: the ID no longer depends on the path, the app rewrites the host manifest on every start and always
authorises `jnfpmdnbiomjnelmlaohkpifaacandjg`, and it registers the host for Chrome, Edge, Brave, Vivaldi and
Chromium. Reloading from any folder now keeps the same ID, the same settings and the same permission.

## Reloading after an update

When Velocity is updated (or you change the code), each profile needs one click:
`chrome://extensions` → Velocity card → ↻ (Reload). Chrome does not hot-reload unpacked extensions.

## Removing it from a profile

`chrome://extensions` → Velocity card → **Remove**. Other profiles are unaffected, and the app keeps working.

# Troubleshooting

## The widget says "nothing playing" while Spotify is playing

The track info (title, artist, art, play state) comes from the **Windows media
session** — the same system behind the Windows volume popup. If it's empty:

1. **Update Spotify.** Old versions of the desktop app didn't publish to the
   Windows media session at all. Install the latest from spotify.com or the
   Microsoft Store.
2. **Check the music is playing on this PC** — if you're casting to a phone or
   speaker via Spotify Connect, there's no local session to read.
3. **Check Windows itself:** play a song and press a volume key. If the Windows
   volume popup does *not* show the track name either, your Windows is missing
   media integration — common on Windows 11 **"N" editions** (sold in Europe).
   Fix: Settings → Apps → Optional features → *Add a feature* → install
   **Media Feature Pack**, then reboot.

## Antivirus / VirusTotal flags the installer

See [issue #4](https://github.com/mechanicwb2-hub/spotify-taskbar-widget/issues/4):
a couple of minor engines heuristically flag unsigned single-file .NET
installers. All major engines report clean, the source is public, and you can
install the Microsoft-signed build from the
[Microsoft Store](https://apps.microsoft.com/detail/9P12TLJZG2CJ) instead.

## Smart App Control blocks the installer

Windows 11's Smart App Control blocks unsigned apps with no override. Install
from the [Microsoft Store](https://apps.microsoft.com/detail/9P12TLJZG2CJ) —
that build is signed and certified by Microsoft.

## The widget overlaps something / sits in the wrong place

- Right-click → **Reset to automatic position** (also resets the monitor choice).
- To place it manually: right-click → **Move widget**, drag, untick to lock.
- Found a layout the auto-positioning handles badly? Please
  [open an issue](https://github.com/mechanicwb2-hub/spotify-taskbar-widget/issues)
  with a screenshot of your full taskbar — that's how the tricky setups get fixed.

## Favorites / Smart Shuffle / repeat state not showing

These come from the Spotify desktop app itself (the widget reads its interface).
Make sure the Spotify **desktop app** is installed and running — the web player
in a browser doesn't expose them. A Spotify update can occasionally break this
integration; if it happens, update the widget (fixes ship quickly) or open an
issue.

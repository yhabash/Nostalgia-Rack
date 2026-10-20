# Nostalgia Rack 0.6.2

*(Formerly "Amp Player". The app itself and its files still use the old name for now: the nameplate, `AmpPlayer.csproj` and `AmpPlayer.exe`.)*

The free hi-fi music player for Windows, now in its champagne skin.
This build covers **Milestones 4, 5 and 6** in one go.

## What's new in 0.6.2
- The whole player is **30% smaller** (drawn at 70% of the design size).
- Clearer text: bigger display, labels and buttons, and the label font is now real Jost Medium and SemiBold (the old file could only show its thin regular weight).
- **Search fix:** adding songs, or switching lists, clears a leftover search. If a search hides everything, the glass says so instead of looking empty.
- SQLite updated to the fixed engine (2.1.13).

## What's new in 0.6.0

**Sound (Milestone 4)**
- **Gapless playback.** Albums with songs that run into each other (live albums, DJ mixes) now play with no gap.
- **Crossfade**, 1 to 12 seconds (off by default). It never fades between songs from the same album, or on radio.
- **Choose your output device** (speakers, headphones, USB DAC), in **shared** or **exclusive** mode. Exclusive sends the music bit-perfect, with Windows' mixer out of the way.
- Unplug your headphones and the music pauses, then carries on through the Windows default device.
- **Advanced settings** (the ⚙ key on the right of the case, or Ctrl+,): device, mode, sample rate, buffer, resampler quality, crossfade, skip step, VU meter delay for Bluetooth, radio buffering, Podcast Index key, music folders, keyboard shortcuts.
- **Media keys** on your keyboard and the Windows media panel (with the song title and lock screen controls).
- **Taskbar buttons:** previous, play/pause and next appear when you hover over Nostalgia Rack on the taskbar, and the taskbar shows song progress.
- **Your own keyboard shortcuts**, set in Advanced settings.

**Radio and podcasts (Milestone 5)**
- **RADIO** key: your favourite stations, plus **TOP STATIONS** from the free Radio Browser directory. Type a name in Search and press **Enter** to find stations. Right-click a station to add it to favourites. The display shows the song the station is playing. If the stream drops, Nostalgia Rack reconnects by itself.
- **PODCASTS** key: your subscriptions. Click **ADD FEED…** and paste a feed address, or type a name in Search and press **Enter** (that needs the free Podcast Index key, below). Double-click a podcast to see its episodes. **CHAPTERS** and **TRANSCRIPT** links appear on the display when an episode has them.
- **Drag a web address in** (for example a browser tab): audio files and radio streams play, and podcast feeds are subscribed. Video sites aren't supported, by design.

**The skin (Milestone 6)**
- The full **Champagne & Black Glass** design: rosewood case, champagne decks, amber display, cream VU meters with long needles, backlit stencil buttons, champagne EQ caps, and the "Yazan" signature.
- **Fold** each deck with its little tab (Ctrl+1 for the EQ, Ctrl+2 for the playlist). Fold the main deck down to the **slim strip** (Ctrl+M). Folded decks come back with the pull tabs under the case.
- **EQ presets**, an **EQ ON** key, **BYPASS** (pure signal) and a **SOFT CLIP** key. The soft clipper only works while the EQ is boosting, so your volume is never lowered.
- Drag the player by any empty bit of metal or wood.

## Setting it up (please read, a few things changed)

1. **Unzip into a new, empty folder.** Don't unzip over the old one: a file was removed (Player.cs) and it would clash.
2. Download these four zips from **un4seen.com**, and from each one copy the DLL in its **x64** folder into the new project folder (next to `AmpPlayer.csproj`):

   | Zip from un4seen.com | File to copy (from the x64 folder) |
   |---|---|
   | bass24.zip | bass.dll |
   | bass_fx24.zip | bass_fx.dll |
   | bassmix24.zip | bassmix.dll **(new)** |
   | basswasapi24.zip | basswasapi.dll **(new)** |

   You can copy bass.dll and bass_fx.dll from your old folder.
3. Open `AmpPlayer.csproj` in Visual Studio, then choose **Build → Rebuild Solution**, then press **▶ Start**.
   Visual Studio downloads the new libraries by itself the first time (it needs the internet for that).

Your library, playlists, queue and settings are kept: they live in your Windows profile, not in the project folder.

### Podcast search (optional)
Searching podcasts by name uses the free **Podcast Index**. Sign up at <https://api.podcastindex.org/signup>, then paste the **API key** and **API secret** in Advanced settings → Podcasts.
They're stored encrypted, only on your PC, for your Windows account. They are never part of the project files.
You don't need them to subscribe with a feed address.

## Keyboard shortcuts (the originals)

| Keys | Does |
|---|---|
| Space | Play / pause |
| Ctrl+Space | Stop |
| Ctrl+Right / Ctrl+Left | Next / previous song |
| Shift+Right / Shift+Left | Skip forward / back (15 seconds) |
| Ctrl+Up / Ctrl+Down | Volume up / down |
| Ctrl+E | EQ on / off |
| Ctrl+1 / Ctrl+2 | Fold the EQ / the playlist |
| Ctrl+M | Slim strip |
| Ctrl+F | Search |
| Ctrl+, | Advanced settings |

You can change all of them in Advanced settings.

## If something goes wrong
- **Errors in Visual Studio's Error List**: copy them (right-click → Copy) and send them to Claude.
- **A "Something went wrong" box while running**: press Ctrl+C on it and send the text to Claude.
- **"bassmix.dll is missing or is the 32-bit version"** (or another DLL): copy that file from the **x64** folder of its zip, then Rebuild.

## Coming next
Milestone 7: ReplayGain (even loudness between songs) and the compressor knobs with the gain-reduction meter (they're shown on the EQ deck but not working yet).

---
The fonts (Krona One, Michroma, Jost (as "Amp Jost"), Great Vibes) are free under the SIL Open Font License (see `Skin/Fonts`).
BASS is free for non-commercial use; see un4seen.com before sharing it.

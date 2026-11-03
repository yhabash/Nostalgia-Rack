# Nostalgia Rack 0.8.2

The free hi-fi music player for Windows, in its champagne skin. (It used to be called "Amp Player".)

## Download
Get the latest version from the **Releases** page of this repository (on the right of the main GitHub page). Download `NostalgiaRack-x.y.z-win-x64.zip`, unzip it, and double-click `NostalgiaRack.exe`. It runs on Windows 10 and 11 (64-bit), and there's nothing to install. Windows may say "Windows protected your PC" because the download isn't signed yet: click **More info**, then **Run anyway**.

Releases are built automatically by GitHub (see `.github/workflows/release.yml`) whenever the version number changes. They include the BASS audio engine, which is free for non-commercial use (un4seen.com).

## What's new in 0.8.2
- **Tidier DYNAMICS deck, and shorter:** COMP and SOFT CLIP sit side by side under the name, the three knobs (GAIN, THRESH, RATIO) are bigger, and the gain-reduction meter moved to the right end.
- **Version number** in Advanced settings, bottom left (so testers can tell you which build they have).

## What's new in 0.8.1
- **New DYNAMICS deck.** The compressor moved out of the EQ deck onto its own foldable deck, below the EQ: the gain-reduction meter, the **COMP** and **SOFT CLIP** keys, and the knobs. The empty right side is saved for the limiter and ReplayGain. Fold it with its tab or **Ctrl+3**; folded, it leaves a slim panel with a COMP lamp.
- **Simpler compressor:** three knobs, in this order: **GAIN**, **THRESH**, **RATIO**. Attack and release are fixed at gentle settings (30 ms and 0.4 s).
- **The EQ deck breathes again:** wider spacing between the sliders, and EQ ON and BYPASS sit on their own on the right.
- **Clearer lights:** when a key or button is lit, its cap now turns smoked bronze and the pale yellow light shines through it, like real backlit hi-fi keys. The light is much easier to see than pale yellow on cream.
- Fold shortcut list: Ctrl+1 EQ, **Ctrl+3 dynamics**, Ctrl+2 playlist.

## What's new in 0.8.0
**The compressor works** (the first part of Milestone 7). It evens out quiet and loud passages, tuned for gentle levelling (late-night or background listening).
- **COMP** key under the meter switches it on and off (lit = on). BYPASS switches it off along with everything else.
- Five knobs: **THRESH** (where turning down starts, −40 to 0 dB), **RATIO** (1:1 to 10:1), **ATTACK** (1 to 200 ms), **RELEASE** (50 ms to 2 s) and **GAIN** (adds 0 to +18 dB back, so the music isn't quieter overall). Hover over a knob to see its exact value; double-click it to go back to the gentle starting position.
- The **gain-reduction meter** now moves: it shows how many dB the compressor is turning the music down.
- To make room for the fifth knob, the EQ sliders sit slightly closer together again.

**Also new**
- **Winamp keys:** X or C play/pause, V stop, B next, Z previous, ← / → skip. If you never changed those shortcuts, they switch over by themselves; any you changed stay as they are.
- **Window keys** (settings, minimise, close) are now the same shape and size as the fold tab, with engraved symbols and a gap before the fold tab.
- **Advanced settings:** no wooden frame (to match the player), a little more compact, and the mouse wheel scrolls at half the speed. Sample rate and buffer are hidden and always automatic. Crossfade is a slider from **Gapless** up to **20 seconds**. Shortcuts can now be single keys.
- Fixed: the VU meter labels were using a fallback font.

## What's new in 0.7.3
- **EQ banks instead of presets.** The preset list is gone. The EQ deck now has two memory keys, **BANK 1** and **BANK 2**, and a small amber window that says which one is lit.
  - Press a bank to light it. While it's lit, every slider move (preamp included) is saved into it automatically.
  - Only one bank is lit at a time. Press the lit bank again and both are off: the EQ goes **FLAT** and the sliders lock at 0 (they dim a little).
  - Both banks start flat. Your old EQ curve and presets are not carried over.
- **Share with friends:** in Visual Studio, right-click the project → **Publish…** → **FriendsTestBuild** → **Publish**. Zip the folder `bin\publish\NostalgiaRack` and send it. Your friends unzip it and double-click `NostalgiaRack.exe`; nothing to install. A `FOR TESTERS.txt` note inside explains the rest.

## What's new in 0.7.2
- **No more wood.** The rosewood case is gone: the player is just the champagne metal decks, with no frame or shadow around them. The dark seams between the decks stay.
- The **settings, minimise and close** keys moved onto the metal, in the top-right corner of the main deck next to the fold tab (and at the right end of the slim strip). The "Yazan" signature moved a little left to make room.

## What's new in 0.7.1
- **EQ fix:** the EQ sliders were upside down. Pushing a cap down actually boosted that band, and pushing the preamp up turned everything down, which caused the distortion and the sudden volume jumps. Up now means boost. *If your EQ sounds odd after updating, pick **Flat** in the preset list and start again: your saved curve may still hold boosts from before the fix.*
- **Playlist font:** the song list now uses **Inter**, a font designed for screens, for clearer titles at the same size.
- **Add button:** adding songs is now a plain **+**. The add-folder button is next to it.
- **Folded decks:** folding the EQ or the playlist now leaves a slim blank panel in the rack, with its unfold button. The folded EQ panel has a small lamp that glows while the EQ is on.
- **FM frequency:** when a station's name or tags mention its dial position (like "KEXP 90.3 FM"), the display shows it big, like a tuner ("FM 90.3"), with the listening time beside it, and in the top line ("RADIO · LIVE · FM 90.3"). Many internet-only stations don't have one, so nothing shows for them.
- **Radio content filter** (Advanced settings → Internet radio): *Off*, *Hide adult and explicit stations* (the default), or *Only kids' and family stations*. It applies to TOP STATIONS and station search. Stations label themselves, so it's a strong hint rather than a guarantee, and any station can still play a song with explicit lyrics. Your saved favourites always show.

## What's new in 0.7.0
- **New name: Nostalgia Rack**, everywhere: the nameplate, the window and taskbar title, the messages, the project file (`NostalgiaRack.csproj`) and the program (`NostalgiaRack.exe`).
- **13% wider.** The extra room goes to the display (longer titles fit), more space between the EQ sliders, a wider song list and a bigger search box.
- Your library, playlists, queue, radio favourites, podcasts and settings **move across by themselves** the first time Nostalgia Rack starts. They're copied from `%LocalAppData%\AmpPlayer` to `%LocalAppData%\NostalgiaRack`. The old folder is left as a safety copy; you can delete it once everything looks right.

## Updating from Amp Player (and keeping GitHub happy)
1. Close Visual Studio.
2. Open your project folder (the one GitHub Desktop tracks). Delete everything you can see **except the four DLLs** (bass.dll, bass_fx.dll, bassmix.dll, basswasapi.dll). If you have hidden files showing, also keep the **.git** folder: that's your GitHub history.
3. Copy everything from inside the zip's **NostalgiaRack** folder into your project folder.
4. Open **NostalgiaRack.csproj** in Visual Studio, choose **Build → Rebuild Solution**, then press **▶ Start**.
5. In GitHub Desktop you'll see the renamed files. Type `0.7.0: renamed to Nostalgia Rack, 13% wider`, click **Commit to main**, then **Push origin**.

Optional: rename the repository on github.com too (the repository's **Settings → Repository name → NostalgiaRack**). GitHub Desktop follows the new name by itself. If you also rename the folder on your PC, GitHub Desktop will say it can't find the repository; click **Locate…** and pick the renamed folder.

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
2. Download these four zips from **un4seen.com**, and from each one copy the DLL in its **x64** folder into the new project folder (next to `NostalgiaRack.csproj`):

   | Zip from un4seen.com | File to copy (from the x64 folder) |
   |---|---|
   | bass24.zip | bass.dll |
   | bass_fx24.zip | bass_fx.dll |
   | bassmix24.zip | bassmix.dll **(new)** |
   | basswasapi24.zip | basswasapi.dll **(new)** |

   You can copy bass.dll and bass_fx.dll from your old folder.
3. Open `NostalgiaRack.csproj` in Visual Studio, then choose **Build → Rebuild Solution**, then press **▶ Start**.
   Visual Studio downloads the new libraries by itself the first time (it needs the internet for that).

Your library, playlists, queue and settings are kept: they live in your Windows profile, not in the project folder.

### Podcast search (optional)
Searching podcasts by name uses the free **Podcast Index**. Sign up at <https://api.podcastindex.org/signup>, then paste the **API key** and **API secret** in Advanced settings → Podcasts.
They're stored encrypted, only on your PC, for your Windows account. They are never part of the project files.
You don't need them to subscribe with a feed address.

## Keyboard shortcuts (the originals)

The transport keys follow Winamp. Single letters are ignored while you type in the search box.

| Keys | Does |
|---|---|
| X or C | Play / pause (Winamp keys) |
| V | Stop |
| B / Z | Next / previous song |
| → / ← | Skip forward / back (15 seconds) |
| Ctrl+Up / Ctrl+Down | Volume up / down |
| Ctrl+E | EQ on / off |
| Ctrl+1 / Ctrl+3 / Ctrl+2 | Fold the EQ / the dynamics deck / the playlist |
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
The fonts (Krona One, Michroma, Jost (as "Rack Jost"), Inter (as "Rack Inter"), Great Vibes) are free under the SIL Open Font License (see `Skin/Fonts`).
BASS is free for non-commercial use; see un4seen.com before sharing it.

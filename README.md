# Yursic Studio

A multitrack music studio for **Xbox** (UWP), laid out like popular browser/mobile DAWs:
a transport bar on top, track headers on the left, a timeline with waveforms, and a
Mixer / Effects / Sounds panel along the bottom. Everything works with a controller.

It adds two things consoles usually lack:

- **Plugins from USB.** Copy `.yplug` files to a USB drive, scan, install. Effects and sound packs ([format](docs/PLUGIN_FORMAT.md)).
- **USB storage management.** See drive space, browse/delete files, set up a `Yursic` folder layout, back up projects, import audio, and copy mixdowns off the console.

## Features

| Area | What's there |
|---|---|
| Projects | Create, rename, delete; autosave every minute and on exit; export/import a whole project as one `.yproj` file via USB |
| Studio | Unlimited tracks with mute/solo/arm/volume/pan and meters; waveform clips; bar/beat ruler and grid; tempo 40–240 BPM; metronome; zoom; undo |
| Recording | Records from the default mic (headset or USB mic) onto the armed track |
| Clips | Select, move by beat or to another track, split / trim at playhead, duplicate, ±3 dB, delete |
| Effects | Up to 8 per track, reorderable, bypassable: Studio Reverb, Echo, Overdrive, Compressor, 3-Band EQ, Lo-Fi, Chorus, Tremolo/Auto-pan, Filter, plus installed plugins |
| Sounds | Built-in loops (drums, bass, keys, synth), plugin sound packs, and any audio on a USB drive (WAV/MP3/M4A/WMA/FLAC/AAC) |
| Export | Offline mixdown to 16-bit WAV, saved on the console and optionally copied to `USB:\Yursic\Exports` |

## Controller

| Button | Action |
|---|---|
| A | Select / press. On a selected clip, A again starts **move mode** (D-pad ←/→ by beat, ↑/↓ change track, A/B to finish) |
| B | Back |
| X | Play / pause |
| Y | Record / stop recording |
| LB / RB | Jump a bar back / forward |
| LT / RT | Zoom out / in |
| View | Switch bottom panel (Mixer → Effects → Sounds) |
| Menu | Clip or track menu (split, trim, duplicate, rename, color, delete…) |

Keyboard works too: Space, R, PageUp/PageDown, +/−, Home, Delete, Ctrl+S, Ctrl+Z.

## USB layout

```
USB:\Yursic\
   Plugins\    .yplug files or plugin folders
   Projects\   .yproj backups
   Samples\    your audio (searched 3 folders deep)
   Exports\    mixdowns
```

**USB Storage → choose a drive → Create Yursic folders** sets this up. Plugins and audio in the
drive root are found too. NTFS or exFAT drives work best.

## Building and running on Xbox

Requirements: Windows 10/11 with **Visual Studio 2022** and the *Universal Windows Platform development*
workload (Windows SDK 10.0.19041). An Xbox One / Series X|S in **Developer Mode**.

1. Open `Yursic.sln`. Set the platform to **x64**.
2. Switch the Xbox to Developer Mode (Dev Mode Activation app) and note its IP address in Dev Home.
3. In project properties → Debug, set **Target device: Remote Machine**, enter the Xbox IP, authentication **Universal (Unencrypted Protocol)**.
4. Press F5. To install without VS, use **Project → Publish → Create App Packages** (sideloading) and install the `.msix` through the Xbox Device Portal.

It also runs on a Windows PC (Local Machine) for quicker testing. The UI follows Xbox focus
navigation and keeps to the TV-safe area.

## Project structure

```
Yursic/
  App.xaml(.cs)            theme, services, back/B-button handling
  MainPage.xaml(.cs)       navigation shell (Projects, Plugins, USB Storage)
  Views/                   HomePage, StudioPage, PluginsPage, StoragePage
  Audio/                   AudioGraph engine, mixer, WAV I/O, import/transcode, built-in loops
  Dsp/DspNodes.cs          effect building blocks (biquads, delay, reverb, compressor…)
  Plugins/                 plugin.json parser, effect instances, built-ins, installer
  Models/                  project/track/clip model and on-disk store
  Storage/                 USB drive enumeration, browsing, copying
samples/plugins/           example effect and sound-pack plugins
tools/pack_plugin.py       validate + zip a plugin folder into .yplug
docs/PLUGIN_FORMAT.md      plugin reference
```

## How it works

- **Audio engine:** an `AudioGraph` with an `AudioFrameInputNode` fed by the app's own mixer, so every effect and plugin runs in app code at the device sample rate. The microphone is captured through an `AudioFrameOutputNode`.
- **Plugins are data, not code:** UWP on Xbox can't load native DLLs, so plugins chain the built-in DSP blocks. Archives are checked for unsafe paths before extracting.
- **USB access** uses the `removableStorage` capability. UWP only shows file types the app declares (`.wav .mp3 .m4a .wma .flac .aac .yplug .zip .json .yproj`), so other files on the drive are hidden.

## Known limits

- No MIDI / virtual instruments yet. Sound packs and recordings only.
- Recordings aren't latency-compensated. Nudge takes by a beat in move mode, or trim them.
- Tempo changes don't time-stretch audio.
- Sounds can't be previewed before adding (add, listen, undo).

# Yursic plugin format

A plugin is a folder with a `plugin.json`, optionally zipped into a `.yplug` file
(a plain zip, renamed). Yursic installs plugins from a USB drive and copies them
onto the console, so the drive can be unplugged afterwards.

Xbox doesn't let UWP apps load outside code (no VST/DLL plugins). A Yursic plugin
**describes** a sound instead of running code: an effect is a chain of built-in DSP
blocks wired to your own controls, and a sound pack is a list of audio files.

## Where to put plugins on the USB drive

```
USB:\
├─ my-plugin.yplug            ← found in the drive root
└─ Yursic\
   └─ Plugins\
      ├─ space-echo.yplug     ← or in Yursic\Plugins
      └─ vocal-chain\         ← unzipped folders work too
         └─ plugin.json
```

Then on the console go to **Plugins → Scan USB drives** and choose one, or open the
file from **USB Storage**. Installing a plugin with the same `id` again replaces it
(use this to update).

## plugin.json

```json
{
  "id": "me.space-echo",
  "name": "Space Echo",
  "version": "1.0.0",
  "author": "You",
  "description": "Tape echo into a big reverb.",
  "category": "Delay",
  "params": [
    { "id": "time", "name": "Time", "min": 50, "max": 1200, "default": 400, "unit": "ms", "curve": "log" },
    { "id": "mix",  "name": "Mix",  "min": 0,  "max": 1,    "default": 0.35, "unit": "%" }
  ],
  "chain": [
    { "node": "delay",  "time": "$time", "feedback": 0.55, "tone": 3500, "mix": "$mix" },
    { "node": "reverb", "size": 0.7, "mix": "$mix*0.5" }
  ]
}
```

| Field | Required | Notes |
|---|---|---|
| `id` | yes | 3–80 chars: letters, numbers, `.` `-` `_`. Projects remember effects by id. |
| `name` | yes | Shown in menus. |
| `version`, `author`, `description`, `category` | no | Shown on the Plugins page. |
| `params` | no | The sliders shown on the effect card (in order). |
| `chain` | for effects | Up to 16 nodes, processed top to bottom. |
| `sounds` | for sound packs | Audio files that appear in the studio's Sounds panel. |

A plugin needs a `chain`, `sounds`, or both.

### params

| Field | Default | Notes |
|---|---|---|
| `id` | — | Used as `$id` in the chain. |
| `name` | id | Slider label. |
| `min`, `max` | 0, 1 | `max` must be above `min`. |
| `default` | min | Clamped into range. |
| `unit` | "" | `%` shows 0–1 as 0–100 %. `Hz` shows kHz above 1000. Anything else is printed as is. |
| `curve` | `linear` | `log` makes the slider exponential (good for Hz and ms). Needs `min > 0`. |

### Values in the chain

Every node setting can be:

- a number: `"freq": 120`
- a param: `"mix": "$mix"`
- a scaled param: `"$amount*-24-6"` means `amount × -24 − 6`. Form: `$param`, optional `*number`, optional `+number` or `-number`.
- an object, same as above: `{ "param": "amount", "scale": -24, "offset": -6 }`

Booleans (`true`/`false`) count as 1/0.

## Nodes

All audio is stereo. Settings left out use the default.

| Node | Settings (default) |
|---|---|
| `gain` | `db` (0) |
| `lowpass`, `highpass`, `bandpass` | `freq` Hz (1000), `q` (0.707), `mix` 0–1 (1) |
| `peak`, `lowshelf`, `highshelf` | `freq` Hz (1000), `q` (0.707), `gain` dB (0), `mix` (1) |
| `delay` | `time` ms (350, max 2000), `feedback` 0–0.95 (0.35), `mix` (0.3), `tone` Hz lowpass on repeats (8000), `pingpong` 0/1 (0) |
| `distortion` | `drive` 1–50 (4), `mix` (1), `output` dB (0) |
| `bitcrush` | `bits` 2–16 (8), `downsample` 1–32 (4), `mix` (1) |
| `compressor` | `threshold` dB (-18), `ratio` (4), `attack` ms (10), `release` ms (120), `makeup` dB (0) |
| `reverb` | `size` 0–1 (0.6), `damp` 0–1 (0.4), `mix` (0.3), `width` 0–1 (1), `predelay` ms 0–200 (0) |
| `chorus` | `rate` Hz (0.8), `depth` ms 0.1–20 (6), `mix` (0.4) |
| `tremolo` | `rate` Hz (4), `depth` 0–1 (0.5), `stereo` 0/1 auto-pan (0) |
| `width` | `amount` 0 = mono, 1 = normal, 2 = extra wide (1) |

## Sound packs

```json
{
  "id": "me.lofi-kit",
  "name": "Lo-fi Kit",
  "sounds": [
    { "file": "loops/dusty-beat-85.wav", "name": "Dusty Beat", "category": "Drums", "bpm": 85 },
    { "file": "keys/rhodes-chords.wav", "name": "Rhodes Chords", "category": "Keys", "bpm": 85 }
  ]
}
```

- `file` is relative to the plugin folder (no `..`, no drive letters). WAV is fastest; MP3, M4A, WMA, FLAC and AAC are converted when added.
- `bpm` is optional. When a project is empty, adding a loop sets the project tempo to the loop's BPM so the grid lines up.

## Checking and packing on a PC

```
python tools/pack_plugin.py --check my-plugin/     # validate
python tools/pack_plugin.py my-plugin/ -o usb/     # writes usb/my-plugin.yplug
```

The script runs the same checks as the app. Examples are in `samples/plugins/`.

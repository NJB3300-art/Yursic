#!/usr/bin/env python3
"""Validate a Yursic plugin folder and pack it into a .yplug file.

Usage:
    python tools/pack_plugin.py samples/plugins/space-echo            -> space-echo.yplug
    python tools/pack_plugin.py samples/plugins/space-echo -o out/   -> out/space-echo.yplug
    python tools/pack_plugin.py --check samples/plugins/*             (validate only)

Copy the .yplug to the root of a USB drive (or USB:\\Yursic\\Plugins) and install it
from the Plugins page on the console. The checks mirror the app's own validation
(Yursic/Plugins/PluginDefinition.cs), so a plugin that passes here installs there.
"""
import argparse
import json
import os
import re
import sys
import zipfile

NODES = {
    "gain": {"db"},
    "lowpass": {"freq", "q", "gain", "mix"},
    "highpass": {"freq", "q", "gain", "mix"},
    "bandpass": {"freq", "q", "gain", "mix"},
    "peak": {"freq", "q", "gain", "mix"},
    "lowshelf": {"freq", "q", "gain", "mix"},
    "highshelf": {"freq", "q", "gain", "mix"},
    "delay": {"time", "feedback", "mix", "pingpong", "tone"},
    "distortion": {"drive", "mix", "output"},
    "bitcrush": {"bits", "downsample", "mix"},
    "compressor": {"threshold", "ratio", "attack", "release", "makeup"},
    "reverb": {"size", "damp", "mix", "width", "predelay"},
    "chorus": {"rate", "depth", "mix"},
    "tremolo": {"rate", "depth", "stereo"},
    "width": {"amount"},
}
EXPR = re.compile(r"^\s*\$([A-Za-z_][A-Za-z0-9_]*)\s*(?:\*\s*(-?[0-9]*\.?[0-9]+))?\s*(?:([+-])\s*([0-9]*\.?[0-9]+))?\s*$")
ID = re.compile(r"^[A-Za-z0-9._-]{3,80}$")
AUDIO = {".wav", ".mp3", ".m4a", ".wma", ".flac", ".aac"}


def validate(folder):
    errors = []
    path = os.path.join(folder, "plugin.json")
    if not os.path.isfile(path):
        return None, [f"{folder}: no plugin.json"]
    try:
        with open(path, encoding="utf-8-sig") as f:
            p = json.load(f)
    except ValueError as e:
        return None, [f"plugin.json is not valid JSON: {e}"]

    if not ID.match(str(p.get("id", ""))):
        errors.append('"id" must be 3-80 letters, numbers, dots, dashes or underscores')
    if not str(p.get("name", "")).strip():
        errors.append('"name" is required')

    params = {}
    for prm in p.get("params", []):
        pid = prm.get("id")
        lo, hi = prm.get("min", 0), prm.get("max", 1)
        if not pid:
            errors.append("a param is missing its id")
            continue
        if hi <= lo:
            errors.append(f'param "{pid}": max must be above min')
        if prm.get("curve", "linear") == "log" and lo <= 0:
            errors.append(f'param "{pid}": log curve needs min > 0')
        params[pid] = prm

    chain = p.get("chain", [])
    if len(chain) > 16:
        errors.append("a chain can have at most 16 nodes")
    for i, node in enumerate(chain):
        kind = str(node.get("node", "")).lower()
        if kind not in NODES:
            errors.append(f'chain[{i}]: unknown node "{kind}" (known: {", ".join(NODES)})')
            continue
        for key, val in node.items():
            if key == "node":
                continue
            if key not in NODES[kind]:
                errors.append(f'chain[{i}] ({kind}): unknown setting "{key}" (allowed: {", ".join(sorted(NODES[kind]))})')
            if isinstance(val, str):
                m = EXPR.match(val)
                if not m:
                    errors.append(f'chain[{i}] ({kind}).{key}: "{val}" must be a number, "$param" or "$param*2+100"')
                elif m.group(1) not in params:
                    errors.append(f'chain[{i}] ({kind}).{key}: "${m.group(1)}" is not in params')
            elif isinstance(val, dict):
                if val.get("param") not in params:
                    errors.append(f'chain[{i}] ({kind}).{key}: param "{val.get("param")}" is not in params')
            elif not isinstance(val, (int, float, bool)):
                errors.append(f"chain[{i}] ({kind}).{key}: unsupported value")

    sounds = p.get("sounds", [])
    for s in sounds:
        rel = s.get("file", "")
        if ".." in rel or rel.startswith(("/", "\\")) or ":" in rel:
            errors.append(f'sound "{rel}": path must be relative to the plugin folder')
        elif not os.path.isfile(os.path.join(folder, rel)):
            errors.append(f'sound "{rel}": file not found')
        elif os.path.splitext(rel)[1].lower() not in AUDIO:
            errors.append(f'sound "{rel}": unsupported audio type')

    if not chain and not sounds:
        errors.append('needs a "chain" (effect) or "sounds" (sound pack)')
    return p, errors


def pack(folder, out_dir):
    name = os.path.basename(os.path.normpath(folder))
    target = os.path.join(out_dir, name + ".yplug")
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(folder):
            for fn in files:
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, folder).replace(os.sep, "/")
                z.write(full, rel)
    return target


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folders", nargs="+")
    ap.add_argument("-o", "--out", default=".")
    ap.add_argument("--check", action="store_true", help="validate only")
    a = ap.parse_args()

    failed = False
    for folder in a.folders:
        p, errors = validate(folder)
        if errors:
            failed = True
            print(f"✗ {folder}")
            for e in errors:
                print("   -", e)
            continue
        kind = "effect" if p.get("chain") else "sound pack"
        if a.check:
            print(f"✓ {folder}: {p['name']} ({kind})")
        else:
            os.makedirs(a.out, exist_ok=True)
            print(f"✓ {folder} -> {pack(folder, a.out)}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()

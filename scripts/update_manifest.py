#!/usr/bin/env python3
"""Adds (or replaces) one version entry in the Jellyfin plugin repository manifest.

usage: update_manifest.py <manifest.json> <version> <targetAbi> <sourceUrl> <zip> <changelog>
"""
import hashlib
import json
import sys
from datetime import datetime, timezone

PLUGIN = {
    "guid": "1bad9bcc-8666-46aa-a223-3d700d7b1fa2",
    "name": "Disc Remux",
    "overview": "Turn DVD rips (VIDEO_TS, split VOB imports) into one client-friendly MKV",
    "description": (
        "Finds VIDEO_TS folders and single VTS_xx_x.VOB files imported by Radarr/Sonarr, joins the main title "
        "into one MKV without re-encoding the video, adds a stereo AAC default track and converts DVD bitmap "
        "subtitles to SRT with tesseract OCR."
    ),
    "owner": "HA-HUB-I",
    "category": "General",
    "imageUrl": "https://raw.githubusercontent.com/HA-HUB-I/jellyfin-plugin-disc-remux/main/assets/logo.png",
}


def main(manifest_path, version, target_abi, source_url, zip_path, changelog):
    with open(zip_path, "rb") as f:
        checksum = hashlib.md5(f.read()).hexdigest()  # Jellyfin verifies plugin zips with MD5

    try:
        with open(manifest_path, encoding="utf-8") as f:
            manifest = json.load(f)
    except FileNotFoundError:
        manifest = []

    plugin = next((p for p in manifest if p.get("guid") == PLUGIN["guid"]), None)
    if plugin is None:
        plugin = {**PLUGIN, "versions": []}
        manifest.append(plugin)
    plugin.update(PLUGIN)

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    plugin["versions"] = [entry] + [v for v in plugin.get("versions", []) if v.get("version") != version]

    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"manifest: {version} -> {source_url} (md5 {checksum})")


if __name__ == "__main__":
    if len(sys.argv) != 7:
        sys.exit(__doc__)
    main(*sys.argv[1:])

#!/usr/bin/env python3
"""Stage a Claude Desktop extension (.mcpb) for one platform from a self-contained `tt` publish.

Usage: package-mcpb.py <rid> <publish-dir> <staging-dir>
Then: npx @anthropic-ai/mcpb pack <staging-dir> todo-tracker-<rid>.mcpb

The bundle holds manifest.json, icon.png and server/tt[.exe]. Its manifest only lists the platform the binary runs on.
"""
import json
import os
import shutil
import stat
import sys
from pathlib import Path

PLATFORMS = {"win": "win32", "osx": "darwin", "linux": "linux"}


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 2
    rid, publish, staging = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3])
    platform = PLATFORMS.get(rid.split("-")[0])
    if platform is None:
        print(f"unknown runtime identifier {rid}")
        return 2

    source = Path(__file__).resolve().parent.parent / "integrations" / "claude-desktop"
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8"))
    manifest["compatibility"]["platforms"] = [platform]
    if platform != "win32":
        manifest["server"]["mcp_config"].pop("platform_overrides", None)

    exe = "tt.exe" if platform == "win32" else "tt"
    if not (publish / exe).is_file():
        print(f"{publish / exe} is missing (publish tt for {rid} first)")
        return 1

    if staging.exists():
        shutil.rmtree(staging)
    (staging / "server").mkdir(parents=True)
    for item in publish.iterdir():
        if item.is_file() and item.suffix != ".pdb":
            shutil.copy2(item, staging / "server" / item.name)
    binary = staging / "server" / exe
    binary.chmod(binary.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    shutil.copy2(source / "icon.png", staging / "icon.png")
    (staging / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"staged {rid} ({platform}) in {staging}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

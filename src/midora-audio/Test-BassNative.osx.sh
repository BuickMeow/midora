#!/usr/bin/env bash
# Verifies an operator-supplied macOS BASS/BASSMIDI release against the pinned baseline:
# schema, SHA-256 and the arm64 slice. Version codes are validated at runtime by
# Midora.Audio.Bass. Midora does not commit native binaries.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: Test-BassNative.osx.sh <native-directory>" >&2
  exit 2
fi

directory="$(cd "$1" && pwd)"
baseline="$(cd "$(dirname "$0")" && pwd)/bass-native-baseline.osx-arm64.json"

if [ ! -f "$baseline" ]; then
  echo "Pinned BASS macOS release baseline does not exist: $baseline" >&2
  exit 1
fi

python3 - "$directory" "$baseline" <<'PY'
import hashlib
import json
import pathlib
import subprocess
import sys

directory = pathlib.Path(sys.argv[1])
baseline_path = pathlib.Path(sys.argv[2])
manifest = json.loads(baseline_path.read_text(encoding="utf-8"))

if manifest.get("schemaVersion") != 1 or manifest.get("releaseBaseline") is not True:
    raise SystemExit("Pinned BASS macOS release baseline has an unsupported schema or is not a release baseline.")

failures = []
for entry in manifest["files"]:
    name = entry["name"]
    path = directory / name
    if not path.is_file():
        failures.append(f"missing {name}")
        continue
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != entry["sha256"]:
        failures.append(f"{name}: sha256 {digest} != pinned {entry['sha256']}")
        continue
    architectures = subprocess.run(
        ["lipo", "-archs", str(path)],
        check=True, capture_output=True, text=True).stdout.split()
    if "arm64" not in architectures:
        failures.append(f"{name}: arm64 slice missing (found {' '.join(architectures)})")
        continue
    print(f"OK {name} {entry['version']} {entry['versionCode']} arm64 present")

if failures:
    for failure in failures:
        print(f"FAIL {failure}", file=sys.stderr)
    raise SystemExit(1)
PY

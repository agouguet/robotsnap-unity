#!/usr/bin/env bash
# Maintainer entry point: build the Linux x86_64 player in Unity batch mode.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="${PROJECT:-$(cd -- "$SCRIPT_DIR/.." && pwd)}"
UNITY="${UNITY:-/home/adam/Unity/Hub/Editor/6000.4.4f1/Editor/Unity}"
OUT="${OUT:-$PROJECT/Builds/Linux/robotsnap-unity.x86_64}"
LOG="${LOG:-$PROJECT/Builds/build.log}"

NOGRAPHICS=1

usage() {
  cat <<'EOF'
Usage: build_player.sh [--no-nographics] [--help]

Build the Linux x86_64 player without opening the editor UI.
Exits non-zero when Unity fails, or when no player is produced.

Environment overrides:
  UNITY    Unity editor binary (default: 6000.4.4f1)
  PROJECT  Unity project directory (default: repository root)
  OUT      player output path
  LOG      Unity log file

Options:
  --no-nographics  drop -nographics from the Unity command line
  --help           show this help
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --no-nographics) NOGRAPHICS=0; shift ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'build_player.sh: unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ ! -x "$UNITY" ]; then
  printf 'build_player.sh: Unity editor not found or not executable: %s\n' "$UNITY" >&2
  exit 1
fi

mkdir -p -- "$(dirname -- "$OUT")" "$(dirname -- "$LOG")"

unity_args=(-batchmode -quit)
if [ "$NOGRAPHICS" = 1 ]; then
  unity_args+=(-nographics)
fi
unity_args+=(-projectPath "$PROJECT" -buildTarget Linux64 -executeMethod BuildLinuxPlayer.Build -logFile "$LOG")

export ROBOTSNAP_BUILD_OUTPUT="$OUT"

printf 'building player -> %s\n' "$OUT"
status=0
"$UNITY" "${unity_args[@]}" || status=$?
if [ "$status" -ne 0 ]; then
  printf 'build_player.sh: Unity exited with status %s; log: %s\n' "$status" "$LOG" >&2
  exit "$status"
fi

if [ ! -f "$OUT" ]; then
  printf 'build_player.sh: no player produced at %s; log: %s\n' "$OUT" "$LOG" >&2
  exit 1
fi

printf 'player: %s\n' "$OUT"
printf 'size:   %s\n' "$(du -h -- "$OUT" | cut -f1)"

data_dir="${OUT%.*}_Data"
[ -d "$data_dir" ] || data_dir="${OUT}_Data"
if [ -d "$data_dir" ]; then
  printf 'data:   %s\n' "$data_dir"
fi

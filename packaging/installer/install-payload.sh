#!/usr/bin/env bash
# End-user installer / uninstaller for the packaged Unity player.
#
# Run from a staging directory that contains:
#   payload/    full player tree (executable, *_Data, .so files)
#   app.png     256x256 icon (optional)
#
# make_installer.sh rewrites the APP_VERSION line below with the real version.
set -euo pipefail

APP_VERSION="${APP_VERSION:-0.0.0}"

APP_NAME="robotsnap-unity"
DESKTOP_NAME="robotsnap-unity.desktop"
ICON_NAME="robotsnap-unity"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
BIN_HOME="${XDG_BIN_HOME:-$HOME/.local/bin}"

PREFIX="$DATA_HOME/$APP_NAME/$APP_VERSION"
LAUNCHER="$BIN_HOME/$APP_NAME"
DESKTOP="$DATA_HOME/applications/$DESKTOP_NAME"
ICON="$DATA_HOME/icons/hicolor/256x256/apps/$ICON_NAME.png"

MODE="install"
QUIET=0
WANT_DESKTOP=1
STAGE_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"

say() {
  if [ "$QUIET" = 0 ]; then
    printf '%s\n' "$*"
  fi
}

warn() {
  if [ "$QUIET" = 0 ]; then
    printf '%s: %s\n' "$APP_NAME" "$*" >&2
  fi
}

die() {
  printf '%s: %s\n' "$APP_NAME" "$*" >&2
  exit 1
}

usage() {
  cat <<EOF
Usage: install-payload.sh [options]

Install or remove the RobotSNAP player for the current user.
Run it from a staging directory that holds payload/ (and app.png).

Options:
  --prefix DIR      install prefix (default: $DATA_HOME/$APP_NAME/$APP_VERSION)
  --no-desktop      skip the desktop entry and the icon
  --quiet           print errors only
  --uninstall       remove the installed prefix, launcher, desktop entry and icon
  --version         print the version
  --help            show this help
EOF
}

# Print the player executable to stdout: an executable regular file at the top
# level, ignoring UnityPlayer.so and the *_Data directory.
detect_binary() {
  local dir="$1"
  local candidate base
  local candidates=()

  while IFS= read -r -d '' candidate; do
    base="$(basename -- "$candidate")"
    case "$base" in
      *_Data|launcher-name) continue ;;
      *.so|*.so.*) continue ;;
    esac
    candidates+=("$base")
  done < <(find "$dir" -maxdepth 1 -type f -executable -print0)

  local count="${#candidates[@]}"
  if [ "$count" -eq 0 ]; then
    printf '%s: no executable player found in %s\n' "$APP_NAME" "$dir" >&2
    return 1
  fi

  local match=""
  for base in "${candidates[@]}"; do
    if [ -d "$dir/${base}_Data" ] || [ -d "$dir/${base%.*}_Data" ]; then
      match="$base"
      break
    fi
  done

  if [ -z "$match" ] && [ "$count" -eq 1 ]; then
    match="${candidates[0]}"
  fi

  if [ -z "$match" ]; then
    printf '%s: cannot tell which file is the player: %s\n' "$APP_NAME" "${candidates[*]}" >&2
    return 1
  fi

  printf '%s' "$match"
}

do_uninstall() {
  case "$PREFIX" in
    ""|"/") die "refusing to remove unsafe prefix: '$PREFIX'" ;;
  esac

  local removed=0
  local path
  for path in "$PREFIX" "$LAUNCHER" "$DESKTOP" "$ICON"; do
    if [ -e "$path" ] || [ -L "$path" ]; then
      rm -rf -- "$path"
      say "removed $path"
      removed=$((removed + 1))
    fi
  done

  if [ "$removed" -eq 0 ]; then
    say "nothing to remove"
  fi

  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DATA_HOME/applications" >/dev/null 2>&1 || true
  fi
}

do_install() {
  local payload="$STAGE_DIR/payload"
  [ -d "$payload" ] || die "payload directory not found: $payload"

  local main_bin
  main_bin="$(detect_binary "$payload")" || exit 1

  local stage_icon="$STAGE_DIR/app.png"

  if [ -e "$PREFIX" ]; then
    rm -rf -- "$PREFIX"
  fi
  mkdir -p -- "$PREFIX"
  cp -a -- "$payload/." "$PREFIX/"
  printf '%s\n' "$main_bin" > "$PREFIX/launcher-name"

  mkdir -p -- "$BIN_HOME"
  cat > "$LAUNCHER" <<EOF
#!/usr/bin/env bash
exec "$PREFIX/$main_bin" "\$@"
EOF
  chmod 0755 -- "$LAUNCHER"

  if [ "$WANT_DESKTOP" = 1 ]; then
    mkdir -p -- "$(dirname -- "$DESKTOP")"
    cat > "$DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=RobotSNAP
Comment=Robot social navigation assessment platform
Exec=$LAUNCHER
Icon=$ICON_NAME
Terminal=false
Categories=Science;Education;
EOF
    chmod 0644 -- "$DESKTOP"

    if [ -f "$stage_icon" ]; then
      mkdir -p -- "$(dirname -- "$ICON")"
      cp -f -- "$stage_icon" "$ICON"
    else
      warn "no app.png next to this script; skipping the icon"
    fi

    if command -v update-desktop-database >/dev/null 2>&1; then
      update-desktop-database "$DATA_HOME/applications" >/dev/null 2>&1 || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
      gtk-update-icon-cache -f "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true
    fi
  fi

  say "installed $APP_NAME $APP_VERSION in $PREFIX"
  say "launcher: $LAUNCHER"
  if [ "$WANT_DESKTOP" = 1 ]; then
    say "desktop entry: $DESKTOP"
  fi

  case ":$PATH:" in
    *":$BIN_HOME:"*) ;;
    *) warn "add $BIN_HOME to PATH to run '$APP_NAME' from any shell" ;;
  esac
}

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix)
      [ $# -ge 2 ] || die "--prefix needs a directory"
      PREFIX="$2"
      shift 2
      ;;
    --uninstall)
      MODE="uninstall"
      shift
      ;;
    --no-desktop)
      WANT_DESKTOP=0
      shift
      ;;
    --quiet)
      QUIET=1
      shift
      ;;
    --version)
      printf '%s\n' "$APP_VERSION"
      exit 0
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      die "unknown argument: $1"
      ;;
  esac
done

if [ "$MODE" = uninstall ]; then
  do_uninstall
else
  do_install
fi

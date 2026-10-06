#!/usr/bin/env bash
# Build distributable installers from an already built Linux player directory.
#
# Usage: make_installer.sh <player-dir> [--version V] [--out DIR]
#                          [--icon PNG] [--name NAME]
#
# Produces three artifacts in --out: a portable .tar.gz, a .deb, and a
# self-extracting .run. No network, and nothing written outside --out; the
# only thing read outside the player directory is the repository's own icon,
# version and git identity.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

PLAYER_DIR=""
VERSION=""
OUT="$REPO_ROOT/dist"
ICON="$REPO_ROOT/Assets/Resources/Icons/robotSNAP_icon-circle.png"
NAME="robotsnap-unity"

# The .deb control file wants a real contact. The repository's commit identity
# is the closest thing to one; fall back to a GitHub noreply address.
MAINTAINER="$(git -C "$REPO_ROOT" config user.email 2>/dev/null || true)"
[ -n "$MAINTAINER" ] || MAINTAINER="robotsnap@users.noreply.github.com"
MAINTAINER="RobotSNAP maintainers <$MAINTAINER>"

usage() {
  cat <<'EOF'
Usage: make_installer.sh <player-dir> [options]

Build a .tar.gz, a .deb and a self-extracting .run from a built player.

Arguments:
  <player-dir>     directory holding the player executable and its *_Data

Options:
  --version V      version string (default: bundleVersion in ProjectSettings)
  --out DIR        output directory (default: <repo>/dist)
  --icon PNG       source icon (default: Assets/Resources/Icons/robotSNAP_icon-circle.png)
  --name NAME      application name (default: robotsnap-unity)
  --help           show this help
EOF
}

die() {
  printf 'make_installer.sh: %s\n' "$*" >&2
  exit 1
}

# Copy the player without the editor's debug companions. A build drops a
# "<product>_BurstDebugInformation_DoNotShip" folder and, with symbols on,
# "*.pdb"/"*.debug" files next to the player; none of them belong in a
# distributed installer.
copy_player() {
  local dest="$1"
  mkdir -p -- "$dest"
  rsync -a \
    --exclude='*_BurstDebugInformation_DoNotShip*' \
    --exclude='*.pdb' \
    --exclude='*.debug' \
    "$PLAYER_DIR"/ "$dest"/
}

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
    printf 'make_installer.sh: no executable player found in %s\n' "$dir" >&2
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
    printf 'make_installer.sh: cannot tell which file is the player: %s\n' "${candidates[*]}" >&2
    return 1
  fi

  printf '%s' "$match"
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version)
      [ $# -ge 2 ] || die "--version needs a value"
      VERSION="$2"; shift 2 ;;
    --out)
      [ $# -ge 2 ] || die "--out needs a directory"
      OUT="$2"; shift 2 ;;
    --icon)
      [ $# -ge 2 ] || die "--icon needs a file"
      ICON="$2"; shift 2 ;;
    --name)
      [ $# -ge 2 ] || die "--name needs a value"
      NAME="$2"; shift 2 ;;
    --help|-h)
      usage; exit 0 ;;
    -*)
      die "unknown option: $1" ;;
    *)
      [ -z "$PLAYER_DIR" ] || die "unexpected argument: $1"
      PLAYER_DIR="$1"; shift ;;
  esac
done

[ -n "$PLAYER_DIR" ] || { usage >&2; die "missing <player-dir>"; }
[ -d "$PLAYER_DIR" ] || die "player directory not found: $PLAYER_DIR"
PLAYER_DIR="$(cd -- "$PLAYER_DIR" && pwd)"

MAIN_BIN="$(detect_binary "$PLAYER_DIR")" || exit 1
if [ ! -d "$PLAYER_DIR/${MAIN_BIN}_Data" ] && [ ! -d "$PLAYER_DIR/${MAIN_BIN%.*}_Data" ]; then
  die "no *_Data directory next to $MAIN_BIN in $PLAYER_DIR"
fi

if [ -z "$VERSION" ]; then
  settings="$REPO_ROOT/ProjectSettings/ProjectSettings.asset"
  if [ -f "$settings" ]; then
    VERSION="$(sed -n 's/^[[:space:]]*bundleVersion:[[:space:]]*//p' "$settings" | head -n 1)"
  fi
  [ -n "$VERSION" ] || VERSION="0.0.0"
fi

[ -f "$ICON" ] || die "icon not found: $ICON"
INSTALLER_SH="$SCRIPT_DIR/installer/install-payload.sh"
[ -f "$INSTALLER_SH" ] || die "installer script not found: $INSTALLER_SH"

mkdir -p -- "$OUT"
OUT="$(cd -- "$OUT" && pwd)"

WORK="$(mktemp -d)"
trap 'rm -rf -- "$WORK"' EXIT

# Icon forced to 256x256 PNG for both installed and .run/desktop use.
icon_size="$(identify -format '%wx%h' -- "$ICON" 2>/dev/null || true)"
if [ "$icon_size" = "256x256" ]; then
  cp -f -- "$ICON" "$WORK/app.png"
else
  convert -- "$ICON" -resize '256x256^' -gravity center -background none -extent 256x256 "$WORK/app.png"
fi
[ -f "$WORK/app.png" ] || die "failed to prepare the 256x256 icon from $ICON"

# -- portable .tar.gz -------------------------------------------------------
TAR_ROOT="$WORK/tarroot/$NAME-$VERSION"
copy_player "$TAR_ROOT"
cp -f -- "$WORK/app.png" "$TAR_ROOT/$NAME.png"

cat > "$TAR_ROOT/$NAME" <<EOF
#!/usr/bin/env bash
set -euo pipefail
here="\$(cd -- "\$(dirname -- "\$0")" && pwd)"
exec "\$here/$MAIN_BIN" "\$@"
EOF
chmod 0755 -- "$TAR_ROOT/$NAME"

cat > "$TAR_ROOT/README.txt" <<EOF
RobotSNAP $VERSION - portable Linux x86_64 player

Run the player directly:
    ./$MAIN_BIN

or use the launcher in this directory:
    ./$NAME

Keep $MAIN_BIN and its data directory together; do not move one without
the other. No Unity Hub or Unity editor is needed.
EOF

TARBALL="$OUT/$NAME-$VERSION-linux-x86_64.tar.gz"
tar -czf "$TARBALL" -C "$WORK/tarroot" "$NAME-$VERSION"

# -- .deb -------------------------------------------------------------------
DEB_ROOT="$WORK/debroot"
mkdir -p -- \
  "$DEB_ROOT/DEBIAN" \
  "$DEB_ROOT/opt/$NAME" \
  "$DEB_ROOT/usr/bin" \
  "$DEB_ROOT/usr/share/applications" \
  "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps"

copy_player "$DEB_ROOT/opt/$NAME"
cp -f -- "$WORK/app.png" "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps/$NAME.png"

cat > "$DEB_ROOT/usr/bin/$NAME" <<EOF
#!/usr/bin/env bash
set -euo pipefail
exec "/opt/$NAME/$MAIN_BIN" "\$@"
EOF
chmod 0755 -- "$DEB_ROOT/usr/bin/$NAME"

cat > "$DEB_ROOT/usr/share/applications/$NAME.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=RobotSNAP
Comment=Robot social navigation assessment platform
Exec=/usr/bin/$NAME
Icon=$NAME
Terminal=false
Categories=Science;Education;
EOF
chmod 0644 -- "$DEB_ROOT/usr/share/applications/$NAME.desktop"

cat > "$DEB_ROOT/DEBIAN/control" <<EOF
Package: $NAME
Version: $VERSION
Architecture: amd64
Maintainer: $MAINTAINER
Section: science
Priority: optional
Depends: libc6, libstdc++6, libgl1, libglu1-mesa | libglu1, libx11-6, libxrandr2, libxcursor1, libxi6, libxinerama1, libxkbcommon0
Description: Robot social navigation assessment platform
 Runs RobotSNAP scenarios and analyses from a packaged Unity player.
EOF
chmod 0644 -- "$DEB_ROOT/DEBIAN/control"

# Refresh the desktop and icon caches so the menu entry appears without a
# logout. Both tools are optional, so a missing one is not an error.
cat > "$DEB_ROOT/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f /usr/share/icons/hicolor >/dev/null 2>&1 || true
fi
exit 0
EOF
chmod 0755 -- "$DEB_ROOT/DEBIAN/postinst"

cat > "$DEB_ROOT/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
  fi
  if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f /usr/share/icons/hicolor >/dev/null 2>&1 || true
  fi
fi
exit 0
EOF
chmod 0755 -- "$DEB_ROOT/DEBIAN/postrm"

DEB="$OUT/${NAME}_${VERSION}_amd64.deb"
dpkg-deb --build --root-owner-group "$DEB_ROOT" "$DEB"

# -- self-extracting .run ---------------------------------------------------
copy_player "$WORK/stage/payload"
cp -f -- "$WORK/app.png" "$WORK/stage/app.png"

# Bake the version into the payload copy of install-payload.sh.
awk -v ver="$VERSION" '/^APP_VERSION="/ { print "APP_VERSION=\"" ver "\""; next } { print }' \
  "$INSTALLER_SH" > "$WORK/stage/install-payload.sh"
chmod 0755 -- "$WORK/stage/install-payload.sh"

PAYLOAD_TGZ="$WORK/payload.tar.gz"
tar -czf "$PAYLOAD_TGZ" -C "$WORK/stage" payload app.png install-payload.sh
PAYLOAD_SHA256="$(sha256sum "$PAYLOAD_TGZ" | awk '{print $1}')"

cat > "$WORK/run-header.template" <<'RUN_HEADER_TPL'
#!/usr/bin/env bash
# RobotSNAP self-extracting installer.
set -euo pipefail

APP_NAME="robotsnap-unity"
APP_VERSION="__APP_VERSION__"
PAYLOAD_SHA256="__PAYLOAD_SHA256__"

SELF="$0"

usage() {
  cat <<EOF
Usage: $(basename -- "$SELF") [options]

Self-extracting installer for RobotSNAP $APP_VERSION.

Options:
  --prefix DIR        install prefix (default: $HOME/.local/share/$APP_NAME/$APP_VERSION)
  --no-desktop        skip the desktop entry and the icon
  --quiet             print errors only
  --uninstall         remove a previous installation
  --extract-only DIR  unpack the payload into DIR and stop
  --checksum          verify the embedded payload checksum
  --version           print the installer version
  --help              show this help
EOF
}

payload_start() {
  LC_ALL=C awk '/^__ROBOTSNAP_PAYLOAD_BELOW__$/ { print NR + 1; exit 0 }' "$SELF"
}

# Copy the embedded payload to <dest>/payload.tar.gz and verify its checksum.
stage_payload() {
  local dest="$1"
  local start got
  start="$(payload_start)"
  if [ -z "$start" ]; then
    printf '%s: payload marker not found\n' "$APP_NAME" >&2
    return 1
  fi
  tail -n +"$start" "$SELF" > "$dest/payload.tar.gz"
  got="$(sha256sum "$dest/payload.tar.gz" | awk '{print $1}')"
  if [ "$got" != "$PAYLOAD_SHA256" ]; then
    printf '%s: payload checksum mismatch\n  expected %s\n  actual   %s\n' \
      "$APP_NAME" "$PAYLOAD_SHA256" "$got" >&2
    return 1
  fi
}

extract_dir=""
passthru=()

while [ $# -gt 0 ]; do
  case "$1" in
    --help|-h) usage; exit 0 ;;
    --version) printf '%s\n' "$APP_VERSION"; exit 0 ;;
    --checksum)
      tmp="$(mktemp -d)"
      trap 'rm -rf -- "$tmp"' EXIT
      stage_payload "$tmp"
      printf 'payload sha256: %s\n' "$PAYLOAD_SHA256"
      exit 0
      ;;
    --extract-only)
      if [ $# -lt 2 ]; then
        printf '%s: --extract-only needs a directory\n' "$APP_NAME" >&2
        exit 2
      fi
      extract_dir="$2"
      shift 2
      ;;
    *)
      passthru+=("$1")
      shift
      ;;
  esac
done

tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
stage_payload "$tmp"

if [ -n "$extract_dir" ]; then
  mkdir -p -- "$extract_dir"
  tar -xzf "$tmp/payload.tar.gz" -C "$extract_dir"
  printf 'extracted to %s\n' "$extract_dir"
  exit 0
fi

tar -xzf "$tmp/payload.tar.gz" -C "$tmp"
export APP_VERSION
status=0
if [ "${#passthru[@]}" -gt 0 ]; then
  "$tmp/install-payload.sh" "${passthru[@]}" || status=$?
else
  "$tmp/install-payload.sh" || status=$?
fi
# Stop here: the payload bytes below the marker are not shell code.
exit "$status"
RUN_HEADER_TPL

RUN="$OUT/$NAME-$VERSION-linux-x86_64.run"
while IFS= read -r line || [ -n "$line" ]; do
  line="${line//__APP_VERSION__/$VERSION}"
  line="${line//__PAYLOAD_SHA256__/$PAYLOAD_SHA256}"
  printf '%s\n' "$line"
done < "$WORK/run-header.template" > "$RUN"
printf '%s\n' '__ROBOTSNAP_PAYLOAD_BELOW__' >> "$RUN"
cat -- "$PAYLOAD_TGZ" >> "$RUN"
chmod 0755 -- "$RUN"

# -- report -----------------------------------------------------------------
printf 'artifacts written to %s\n' "$OUT"
for artifact in "$RUN" "$TARBALL" "$DEB"; do
printf '  %s (%s)\n' "$artifact" "$(du -h -- "$artifact" | cut -f1)"
done
printf 'test: bash %s --help\n' "$RUN"

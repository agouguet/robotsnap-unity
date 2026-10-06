# Packaging RobotSNAP

These scripts turn a built Linux player into installers a user can run with
neither Unity Hub nor the Unity editor. They only ship the compiled player.

## Build the player

Requirements: Unity 6000.4.4f1 with the Linux Build Support module, and an
activated licence on the build machine.

```bash
packaging/build_player.sh
```

The default output is `Builds/Linux/robotsnap-unity.x86_64`; the build log is
`Builds/build.log`. Override `UNITY`, `PROJECT`, `OUT` or `LOG` when needed, and
pass `--no-nographics` if Unity must run with a graphics context.

## Make the installers

Point the factory at the directory that holds the player executable and its
`*_Data` directory:

```bash
packaging/make_installer.sh Builds/Linux --version 0.1.0 --out dist
```

```
artifacts written to /home/adam/robotsnap-workspace/robotsnap-unity/dist
  .../dist/robotsnap-unity-0.1.0-linux-x86_64.run (225M)
  .../dist/robotsnap-unity-0.1.0-linux-x86_64.tar.gz (225M)
  .../dist/robotsnap-unity_0.1.0_amd64.deb (180M)
test: bash .../dist/robotsnap-unity-0.1.0-linux-x86_64.run --help
```

Without `--version` the version comes from `bundleVersion` in
`ProjectSettings/ProjectSettings.asset`. The icon defaults to
`Assets/Resources/Icons/robotSNAP_icon-circle.png` and is resized to 256x256.
The Burst debug folder and any debug symbols the build left next to the player
are left out of the three artefacts.

## What each artefact is for

| Artefact | Who uses it | Root required |
| --- | --- | --- |
| `.run` | One-file self-extracting installer for a normal user | no |
| `.tar.gz` | Portable directory to unpack and run in place | no |
| `.deb` | System-wide install on Debian/Ubuntu | yes (`sudo`) |

## Install on a clean machine

```bash
./robotsnap-unity-0.1.0-linux-x86_64.run
tar -xzf robotsnap-unity-0.1.0-linux-x86_64.tar.gz && ./robotsnap-unity-0.1.0/robotsnap-unity
sudo apt install ./robotsnap-unity_0.1.0_amd64.deb
```

The `.run` installs under `$HOME/.local/share/robotsnap-unity/<version>`,
creates `$HOME/.local/bin/robotsnap-unity` and a desktop entry. Pass
`--prefix DIR`, `--no-desktop`, `--quiet` or `--uninstall` as needed. The
`.tar.gz` runs directly from the unpacked folder.

## Checksums

```bash
sha256sum dist/*
```

The `.run` also carries the SHA256 of its embedded payload; check it with
`bash robotsnap-unity-0.1.0-linux-x86_64.run --checksum`.

These installers bundle only the compiled Unity player. End users do not need
Unity Hub, the Unity editor, or any Unity licence.

# RobotSNAP

**Robot Social Navigation Assessment Platform** - a simulator in which a
mobile robot moves among pedestrians, with a scenario editor, a live control
panel and an analysis dashboard.

RobotSNAP is three repositories that can be used separately:

| Repository | What it is |
| --- | --- |
| **robotsnap-unity** (this one) | The simulator, and the installers that ship it. |
| [robotsnap](https://github.com/agouguet/robotsnap) | The Python core: TCP bridge, Gymnasium environments, metrics, campaigns, viewer, CLI. |
| [robotsnap-ros2](https://github.com/agouguet/robotsnap-ros2) | The ROS2 gateway: drive or observe a session from a ROS2 graph. |

## Install the simulator

You do **not** need Unity Hub, the Unity editor, or a Unity licence to run
RobotSNAP. Download the latest release and pick one line:

| I want to... | download | command |
| --- | --- | --- |
| install for my user, no root | `robotsnap-unity-<version>-linux-x86_64.run` | `bash robotsnap-unity-<version>-linux-x86_64.run` |
| install system-wide (Debian/Ubuntu) | `robotsnap-unity_<version>_amd64.deb` | `sudo apt install ./robotsnap-unity_<version>_amd64.deb` |
| just run it, without installing | `robotsnap-unity-<version>-linux-x86_64.tar.gz` | `tar -xzf ...` then `./robotsnap-unity-<version>/robotsnap-unity` |

The `.run` installs under `$HOME/.local/share/robotsnap-unity/<version>`,
creates `$HOME/.local/bin/robotsnap-unity` and a desktop entry, and can be
removed with `--uninstall`. Run it with `--help` to see `--prefix`,
`--no-desktop` and `--quiet`.

Requirements: Linux x86_64, glibc 2.35 or newer (Ubuntu 22.04, Debian 12), and
a GPU with Vulkan or OpenGL support.

## Connect the simulator to Python

The application speaks to the Python package over TCP on port 10000. Start the
bridge, then start the application:

```bash
pip install robotsnap
python -m robotsnap bridge --port 10000
```

The application dials `127.0.0.1:10000` as soon as a scenario runs. From there
the Python side can create scenarios, read robot, lidar and pedestrian state,
step a Gymnasium environment, train a policy, and read back the metrics of
every episode. See the [robotsnap README](https://github.com/agouguet/robotsnap)
for those commands. For a ROS2 graph instead of a plain socket, start the
bridge with `--ros2` (that needs the
[robotsnap-ros2](https://github.com/agouguet/robotsnap-ros2) package).

## What the application does

- **Scenarios** - build a crowd by hand or from a range, save it, and replay it.
  The scenarios that ship with the application are the basic social-navigation
  situations: front approach, door passing, intersection, circle crowd,
  perpendicular traffic, corner.
- **Simulation** - run the scenario, drive a robot from the keyboard or from a
  policy, with the goal, the lidar and the pedestrian state overlaid.
- **Analysis** - every finished episode goes to a session archive with its
  trajectories and social metrics: select one episode for the trajectory replay
  and its details, or several for the session overview. Export single episodes
  or the whole session.
- **Settings** - the running configuration and the ROS2 streams, editable while
  the application runs.

## Build from source

Development needs Unity **6000.4.4f1** with the Linux Build Support module.

```bash
git clone https://github.com/agouguet/robotsnap-unity
cd robotsnap-unity
packaging/build_player.sh                 # writes Builds/Linux/
packaging/make_installer.sh Builds/Linux  # writes dist/
```

`build_player.sh` builds in batch mode, without opening the editor.
`make_installer.sh` turns the player directory into the `.run`, `.tar.gz` and
`.deb` above. Both take `--help`. Keep the player executable and its `_Data`
directory together: the executable finds its data by that name.

The packaging step needs no network, no root, and nothing outside `dist/`.

## Repository layout

| Path | What it holds |
| --- | --- |
| `Assets/Scripts/RobotSNAP/` | Simulation core: scenario model, agents, metrics, communication. |
| `Assets/Scripts/UI/` | The tabs, the settings and the overlays. |
| `Assets/Editor/BuildLinuxPlayer.cs` | The batch-mode build entry point `build_player.sh` calls. |
| `Assets/StreamingAssets/Scenarios/` | The scenarios the application ships with. |
| `packaging/` | Build and packaging scripts, and their own README. |
| `Assets/Tests/` | EditMode tests for the simulation core. |

## Licence

MIT. See [LICENSE](LICENSE).

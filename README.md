# **HLAE Observer Tools**

HLAE Observer Tools is a observing control system for Counter-Strike 2 observing. It enables an observer desk to control and monitor an HLAE-injected game instance via a graphical interface designed for observing. This project is unaffiliated with @advancedfx and HLAE, but uses a modified fork to function.


## Getting Started

1. Install .NET Desktop Runtime 10 https://dotnet.microsoft.com/en-us/download/dotnet/10.0
2. Download the latest **custom** HOT-HLAE (official HLAE will **NOT** work) and HOT build from  https://github.com/papesgit/hot/releases/latest (e.g. `HLAEvx.x.x-HOTvx.x.x.zip` and `HOTvx.x.x.zip`)
3. Launch CS2 with HOT-HLAE (File>Launch CS2, Example launch parameters: `-steam -insecure +sv_lan 1 -novid -console -afxDisableSteamStorage -allow_third_party_software -netconport 54545`)
4. Launch HlaeObsTools.exe
   
To use the RTP stream (requires NVENC capable gpu on game pc), enable it using `mirv_nvenc stream enable ip 5000` and start encoding using `mirv_nvenc start` (you can configure resolution `mirv_nvenc resolution <w> <h>` and bitrate `mirv_nvenc bitrate <bps>`)

Visit the [Wiki](https://github.com/papesgit/hot/wiki) for more information about the specific systems.

## Network setup (local / LAN / remote)

- **Local (same machine)**
  - In HOT: Settings > General > Network Endpoints leave all hosts as `127.0.0.1`, **Apply / Reconnect** if needed.
  - Video: Enable "Local Mode" to use locally shared texture.

- **LAN (different PC on same network)**
  - Launch HLAE with `-lanserver` (optionally `-lanserver <HLAE_PC_LAN_IP>` if it doesn't bind automatically) so WS/UDP bind to LAN
  and `-targetip <HOT_PC_LAN_IP>` (GSI posts to the HOT machine).
  - In HOT: set WS/UDP to the HLAE PC's LAN IP, then **Apply / Reconnect**.
  - RTP on HLAE: `mirv_nvenc stream enable <HOT_PC_LAN_IP> 5000`.

- **Remote / Internet WIP**
  - Forward ports on the routers: WS TCP 31338 and UDP input 31339 to the HLAE PC; GSI TCP 31337 and RTP UDP 5000 to the HOT PC (or your chosen ports).
  - Launch HLAE with `-lanserver` (optionally `-lanserver <HLAE_PC_LAN_IP>`) and `-targetip <HOT_public_or_forwarded_IP>` so the GSI config posts to the HOT machine.
  - In HOT: set WS/UDP host to the HLAE PC's public/WAN IP, then **Apply / Reconnect**.
  - RTP on HLAE: `mirv_nvenc stream enable <HOT_public_or_forwarded_IP> 5000`.

> _**Note:** For campaths to properly work in LAN/remote setup they have to be present on BOTH PC's in the SAME path_


## Third-Party Software

This application includes and depends on several third-party software components,
including open-source libraries and tools.

Notably:

- Binaries from a modified fork of Half-Life Advanced Effects (HLAE), licensed under the MIT License.
- Avalonia UI and related UI components, licensed under the MIT License.
- A modified fork of ValveResourceFormat, licensed under the MIT License.
- Various additional NuGet dependencies, licensed under their respective open-source licenses.

See `THIRD_PARTY_LICENSES.md` for a complete list of third-party components,
license texts, and attributions.


## License & Credits


Versions up to and including v0.2.2 are licensed under GPLv3.

Starting from v0.2.3, this project is licensed under a source-available license. See `LICENSE` for details.

Some included assets (e.g. Counter-Strike HUD icons) are © Valve Corporation
and are not covered by the included LICENSE. See `THIRD_PARTY_LICENSES.md` for details.

Thanks to the [HLAE](https://github.com/advancedfx/advancedfx) team, this project would not be possible without their decades long contributions to the Counter-Strike scene.

Thanks to [JT](https://github.com/JohnTimmermann) for allowing the use of his radar images, originally created for [JTs-Hud](https://github.com/JohnTimmermann/JTs-Hud) (check it out!).

Thanks to [drweissbrot](https://github.com/drweissbrot) , his [cs-hud](https://github.com/drweissbrot/cs-hud) repo helped greatly in designing the radar and hud.


## Donate

If you'd like to support the development of this project, consider donating on GitHub Sponsors:  

[![Sponsor](https://img.shields.io/badge/Sponsor-GitHub-pink?logo=github)](https://github.com/sponsors/papesgit)

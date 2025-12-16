# **HLAE Observer Tools**

HLAE Observer Tools is a (remote) observing control system for Counter-Strike 2 observing. It enables an observer desk to control and monitor an HLAE-injected game instance with low latency. This project is unaffiliated with @advancedfx and HLAE, but uses a modified fork to function.

---

## Getting Started

1. Install .NET Desktop Runtime 8.0 https://dotnet.microsoft.com/en-us/download/dotnet/8.0
2. Download the latest the custom HLAE and HOT build from  https://github.com/papesgit/hot/releases/latest
3. Launch CS2 with HLAE (File>Launch CS2, Example launch parameters: `-steam -insecure +sv_lan 1 -novid -console -afxDisableSteamStorage -allow_third_party_software -netconport 54545`)
4. Launch HlaeObsTools.exe (as admin if used over LAN/remote)
   
To use the RTP stream (requires NVENC capable gpu), enable it using `mirv_nvenc stream enable ip 5000`, start encoding using `mirv_nvenc start` (you can set resolution and fps cap, you can uncap using `mirv_nvenc fpscap 0`, enter `mirv_nvenc` for commands)

For decoding of the RTP stream you need to download a shared 8.0 ffmpeg build ( https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full-shared.7z ) and place the files of the /bin folder into folders /FFmpeg/win-x64 (create them) of the HOT root folder.

---
## Network setup (local / LAN / remote)

- **Local (same machine)**
  - Launch HLAE without `-lanserver` so it binds to `127.0.0.1`.
  - In HOT: Settings > General > Network Endpoints leave all hosts as `127.0.0.1`, **Apply / Reconnect** if needed.
  - Video: Enable "Local Mode" to use locally shared texture.

- **LAN (different PC on same network) WIP**
  - Launch HLAE with `-lanserver` (optionally `-lanserver <HLAE_PC_LAN_IP>`) so WS/UDP bind to the LAN IP
  and `-targetip <HOT_PC_LAN_IP>` (GSI posts to the HOT machine).
  - In HOT: set WS/UDP to the HLAE PC's LAN IP, then **Apply / Reconnect**.
  - RTP on HLAE: `mirv_nvenc stream enable <HOT_PC_LAN_IP> 5000`.
  - GSI listener must be allowed to bind to public inferfaces: run HOT as admin!

- **Remote / Internet WIP**
  - Forward ports on the routers: WS TCP 31338 and UDP input 31339 to the HLAE PC; GSI TCP 31337 and RTP UDP 5000 to the HOT PC (or your chosen ports).
  - Launch HLAE with `-lanserver` (optionally `-lanserver <HLAE_PC_LAN_IP>`) and `-targetip <HOT_public_or_forwarded_IP>` so the GSI config posts to the HOT machine.
  - In HOT: set WS/UDP host to the HLAE PC's public/WAN IP, then **Apply / Reconnect**.
  - RTP on HLAE: `mirv_nvenc stream enable <HOT_public_or_forwarded_IP> 5000`.
  - GSI listener must be allowed to bind to public inferfaces: run HOT as admin!

> _**Note:** For campaths to work in LAN/remote setup they have to be present on BOTH PC's in the SAME path_
---
---

## Usage


---
---
## Third-Party Software

This repository includes binaries from a modified fork of
Half-Life Advanced Effects (HLAE), licensed under the MIT License.

This application uses Avalonia UI, licensed under the MIT License.

See `THIRD_PARTY_LICENSES.md` for details.


## License & Credits


This project is licensed under the GNU General Public License v3.0.

Some included assets (e.g. Counter-Strike HUD icons) are © Valve Corporation
and are not covered by the GPLv3. See `THIRD_PARTY_LICENSES.md` for details.

Thanks to the HLAE team, this project would not be possible without their decades long contributions to the Counter-Strike scene.

Thanks to @drweissbrot , his cs-hud repo helped greatly in designing the radar and hud.

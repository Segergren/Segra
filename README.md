<img height="100" src="https://cdn.segra.tv/icon.png"/>

**Segra** is an open-source game recorder built on Open Broadcaster Software (OBS). It records your games in the background, lets you clip the good parts with a hotkey, and uploads them to [Segra.tv](https://segra.tv).

### Clip Editor

![image](https://github.com/user-attachments/assets/beed0524-35f1-48be-9dd8-c2455959d2f9)

### Highlights

![image](https://github.com/user-attachments/assets/481cc9fa-3efb-412d-b668-8be7d11b9851)


### Settings

![image](https://github.com/user-attachments/assets/de300431-1b63-4ed2-a022-110f8f828d1a)


---

## Features  
- Auto-starts recording when a game launches
- Replay buffer, save the last moments with a hotkey
- Up to 4K 144 FPS, HDR on Windows
- H.264, HEVC and AV1 (NVENC, AMD AMF, Intel QSV or x264)
- Multiple audio devices, separate audio tracks, mic noise suppression
- Clip editor with timeline and audio waveform
- Auto highlights from kill/death tracking in CS2, League, Dota 2, PUBG, Rocket League, Rust, Minecraft, RuneScape: Dragonwilds, War Thunder and GTA
- Upload to **[Segra.tv](https://segra.tv)**
- Per-game overrides for quality, recording mode, HDR and volume
- Storage limit auto-deletes old recordings

---

## Why "Segra"?  
**Segra** (pronounced *"say-grah"*) means **"to win"** in Swedish. We built Segra to help you preserve those moments: the chaotic fun with friends, the clutch plays, and the wins (*segra!*) that deserve their own highlight reel.  

---

## Installation

### Windows
1. **Download**: Get `Segra-win-Setup.exe` from the [latest release](https://github.com/Segergren/Segra/releases/latest).  
2. **Install**: Run the setup.  
3. **Configure**:  
   - Set recording directory and video quality.  
   - Assign hotkeys for clipping/uploading.  
   - Connect your Segra.tv account.  

### Linux (Alpha)
Linux support is in early alpha. It might not start at all, and features that work on Windows can be missing or broken. Please do not open GitHub issues for Linux problems yet.

1. Download `Segra.flatpak` from the [latest release](https://github.com/Segergren/Segra/releases/latest).
2. Install it with `flatpak install Segra.flatpak`.

## Uninstallation

### Windows
1. Open `Windows Settings`
2. Go to `Apps` -> `Installed apps`
3. Search for `Segra`
4. Click `Uninstall`

### Linux
Run `flatpak uninstall tv.segra.Segra`.

## Contributing  
See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, dependencies, and dev workflow.
Help improve Segra by:  
- Report bugs or suggest features  
- Submit pull requests

---

## License  
Segra is **GPLv2 licensed**.  

---

## Code Signing Policy
<table>
  <tr>
    <td><a href="https://signpath.org/" target="_blank"><img src="https://avatars.githubusercontent.com/u/34448643" height="30" alt="SignPath logo" /></a></td>
    <td>free code signing on Windows provided by <a href="https://signpath.io/" target="_blank">SignPath.io</a>, certificate by <a href="https://signpath.org/" target="_blank">SignPath Foundation</a></td>
  </tr>
</table>


**Team roles**

| Role      | Person |
|-----------|--------|
| Authors   | @Segergren |
| Reviewers | @Segergren |
| Approvers | @Segergren |

See our [Privacy Policy](https://segra.tv/privacy).

## Star History

<a href="https://www.star-history.com/#Segergren/Segra&Date">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=Segergren/Segra&type=Date&theme=dark" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=Segergren/Segra&type=Date" />
   <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=Segergren/Segra&type=Date" />
 </picture>
</a>

## Acknowledgments
- **[OBS Studio](https://obsproject.com)**: Segra records through OBS (libobs).
- **[ObsKit.NET](https://github.com/Segergren/ObsKit.NET)**: C# bindings for libobs, written for Segra.
- **[FFmpeg](https://github.com/FFmpeg/FFmpeg)**: for video and image encoding.  

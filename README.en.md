<p align="center">
  <a href="README.md">简体中文</a> · <strong>English</strong>
</p>

# Backdrop for Codex

Backdrop for Codex is an unofficial, open-source companion for **Windows 11 x64** that adds previewable and reusable workspace backdrops to the official Microsoft Store/MSIX Codex desktop app. It supports local images, muted looping videos, installed Wallpaper Engine Image/Video projects, and experimental Scene/Web projects, with an explicit Restore Official action.

**Does not modify the Codex package · Does not upload local media to a project-operated service · Does not read chats · Does not collect telemetry**

[![Latest release](https://img.shields.io/github/v/release/TogawaSakiko-desuwa/backdrop-for-codex?display_name=tag)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)
[![CI](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml/badge.svg)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

[Quick start](#download-and-quick-start) · [What's new](#whats-new-in-v150) · [Features](#features) · [Compatibility](#compatibility-and-limitations) · [Build](#build-from-source)

[**Download the Windows 11 x64 portable build →**](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)

`v1.5.0` · No installer · Runs as a standard user · Supports local media, Wallpaper Engine Image/Video, and experimental Scene/Web

> [!CAUTION]
> Backdrop for Codex is an independent community project. It is not affiliated with, sponsored, endorsed, or supported by OpenAI or Microsoft. It works through the Chrome DevTools Protocol (CDP) on a local loopback address. Never run the companion as administrator or expose the debugging port beyond loopback, and fully exit Codex when finished. See the [security policy](SECURITY.md) and [threat model](THREAT_MODEL.md).

## See it in action

<p align="center">
  <img src="docs/images/codex-backdrop-conversation.png" alt="A Codex conversation workspace with a local image backdrop" width="100%" />
</p>

<p align="center"><sub>A local backdrop behind an active conversation workspace</sub></p>

<table>
  <tr>
    <td width="33%"><img src="docs/images/codex-backdrop-warm.png" alt="A warm local image behind the Codex workspace" /></td>
    <td width="33%"><img src="docs/images/codex-backdrop-vivid.png" alt="A vivid local image behind the Codex workspace" /></td>
    <td width="34%"><img src="docs/images/codex-backdrop-camp.png" alt="A camp-themed local image behind the Codex workspace" /></td>
  </tr>
  <tr>
    <td align="center">Warm backdrop with translucent surfaces</td>
    <td align="center">Vivid backdrop with readability overlays</td>
    <td align="center">Full-window backdrop with content cards</td>
  </tr>
</table>

<p align="center"><sub>Example media is shown only to demonstrate local backdrop rendering and is not distributed with the project or its releases.</sub></p>

## What's new in v1.5.0

### Wallpaper Engine sources

> [!IMPORTANT]
> Scene/Web is experimental in v1.5.0 and is still being improved. Image/Video is the stable path.

- Browse installed Workshop items and Local Projects, filter them by type or origin, and choose projects from their thumbnails.
- Use Image/Video and experimental Scene/Web projects. Image/Video play directly; start Wallpaper Engine before using Scene/Web.

### Workbench and playback

- A redesigned workbench brings profiles, sources, recent media, preview, Apply, and Restore Official into one workflow.
- The source, settings, activation, and cleanup paths now use one profile model for local media and Wallpaper Engine projects. Existing profiles migrate automatically.
- Experimental Scene/Web steps down through quality profiles after recoverable failures and attempts to recover from brief capture, encoding, or page-connection interruptions.
- Updated compatibility for the current Codex conversation and Markdown table structure, with dark-theme contrast and dialog interaction fixes.

[Read the full changelog](CHANGELOG.md#150---2026-08-21)

## Download and quick start

| Release file | Purpose |
| --- | --- |
| `BackdropForCodex-v1.5.0-win-x64.zip` | The portable app most users should download |
| `BackdropForCodex-v1.5.0-SHA256SUMS.txt` | SHA-256 checksums for downloaded artifacts |
| `BackdropForCodex-v1.5.0-win-x64.spdx.json` | Machine-readable SPDX SBOM |

1. Install the official Microsoft Store/MSIX x64 Codex desktop app on Windows 11 x64.
2. Download `BackdropForCodex-v1.5.0-win-x64.zip` from [GitHub Releases](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest), then extract it into an empty directory writable by your standard user account.
3. Fully exit every Codex process, then start `BackdropForCodex.exe`.
4. Create or select a backdrop profile, add local media or an installed Wallpaper Engine project, adjust the preview, and select **Apply & launch Codex**.
5. Read the CDP risk notice that appears next. After you accept it, the companion launches Codex and applies the backdrop.
6. After the first successful media activation, the companion attempts to create `Codex（动态背景）.lnk` on your desktop for future enhanced launches.

> [!NOTE]
> The `v1.5.0` portable build is not Authenticode-signed. If Windows SmartScreen appears, first confirm that the file came from this repository's Release page and verify its SHA-256 or GitHub artifact attestation.

<details>
<summary><strong>Verify SHA-256 and GitHub build provenance</strong></summary>

Open PowerShell in the download directory:

```powershell
Get-FileHash .\BackdropForCodex-v1.5.0-win-x64.zip -Algorithm SHA256
Get-Content .\BackdropForCodex-v1.5.0-SHA256SUMS.txt
```

Confirm that the ZIP hash exactly matches the checksum manifest. If [GitHub CLI](https://cli.github.com/) is installed, you can also verify build provenance:

```powershell
gh attestation verify .\BackdropForCodex-v1.5.0-win-x64.zip `
  --repo TogawaSakiko-desuwa/backdrop-for-codex
```

</details>

## Features

### Media sources

- Local PNG, JPEG, and WebP images, plus muted looping MP4 and WebM videos.
- Discovery of installed Workshop items and Local Projects under `projects/myprojects` and `projects/backup` from a verified official Steam/Wallpaper Engine installation. The companion does not browse the online Workshop, subscribe, download, or modify Steam configuration.
- One valid installation is selected automatically. If several exist or discovery fails, the source library can select an installation location and return to automatic discovery at any time. A manual choice lasts only for the current run.
- Wallpaper Engine Image/Video projects play directly. Experimental Scene/Web requires Wallpaper Engine to be running. A project may play audio through Wallpaper Engine; Backdrop neither mutes nor forwards it.

### Workbench and profiles

- Multiple backdrop profiles with create, duplicate, rename, delete, and an Official background option with no custom media.
- Contain, cover, and stretch fit modes. Cover mode supports direct focus dragging and arrow-key adjustment.
- Independent dark/light theme overlays, panel opacity, and backdrop blur with a preview before Apply.
- File picker, single-file drag and drop, recent media, and pause/resume for video and experimental Scene/Web motion.

### Activation and recovery

- Latest-wins Apply behavior: an older request cannot overwrite a newer backdrop.
- Reopen the workbench, Restore Official, or fully exit the companion from the notification area.
- External companion architecture: the Codex MSIX package is never modified, replaced, or re-signed.

<details>
<summary><strong>View the backdrop profile workbench</strong></summary>

![Backdrop for Codex profile workbench](docs/images/backdrop-workbench.png)

</details>

## Compatibility and limitations

| Item | Current status |
| --- | --- |
| Windows 11 x64 | Supported and the only target platform |
| Official Microsoft Store/MSIX x64 Codex | Supported after package, process, session, loopback endpoint, and target-page verification |
| PNG, JPEG, WebP | Supported |
| MP4, WebM | Supported as muted looping video |
| Installed Wallpaper Engine Image/Video | Supported for Workshop and Local Projects through verified direct media; Wallpaper Engine does not need to be running |
| Installed Wallpaper Engine Scene/Web | Experimental support for Workshop and Local Projects when a verified Wallpaper Engine instance is already running and the system supports dynamic capture and playback. Project audio may play through Wallpaper Engine |
| Wallpaper Engine Application/Unknown | Always rejected and never executed |
| Online Workshop, automatic subscription/download, user properties, presets, playlists, or forwarding audio/input into Codex | Not supported |
| Local media limits | Ordinary files on local disks only; image up to 512 MiB, 32,768 px per side, and approximately 33.5 MP; video up to 8 GiB |
| Win32 portable Codex, Codex web/CLI, Windows 10, Windows on Arm, macOS, Linux | Not supported |
| Independent per-window or per-region backdrops and video audio | Not supported |

Experimental Scene/Web never uses Wallpaper Engine's global pause/play/mute commands and never captures the desktop. Start Wallpaper Engine before using a Scene/Web project; Backdrop does not start it automatically. Scene/Web may play audio, and Backdrop does not enumerate, mute, or change Wallpaper Engine audio sessions or relay audio into Codex. The first Web activation warns that the project may use the network and play audio. The repository and releases do not include Wallpaper Engine binaries, Workshop content, or user projects.

Compatibility is determined from the current Codex page and process state. A failed safety check stops activation. If only optional visual effects are unavailable, the companion keeps the usable backdrop and applies a high-readability fallback. Codex updates may temporarily affect compatibility, so use the latest Backdrop for Codex release.

## Frequently asked questions

### Why is the companion still running after I close the window?

Closing the window or pressing `Alt+F4` hides the workbench in the notification area. An active backdrop keeps running. Select Exit from the notification-area menu to end the companion.

### Codex cannot be found, or Apply fails. What should I do?

Confirm that you are using the official Microsoft Store/MSIX x64 Codex and that Backdrop for Codex is not running as administrator. Fully exit every Codex process, then reopen the workbench or use `Codex（动态背景）.lnk`. If the problem remains, export a diagnostic report from Settings and open a [GitHub issue](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/issues/new/choose).

### What if a Codex update breaks the backdrop?

Restore Official, fully exit Codex, and check the [latest release](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest) for a compatibility update. Do not bypass a failed safety check.

### How do I restore the official background?

Restore Official is available in both the workbench and the notification-area menu. It removes the current injection without deleting saved profiles. Select **Apply & launch Codex** to reactivate the selected profile.

### How do I completely reset or uninstall the companion?

1. Open Settings and select Reset app in the Danger zone. This restores the official background and deletes settings, recent media, risk acknowledgement, UI preferences, and the desktop shortcut owned by the app.
2. Exit Backdrop for Codex from the notification-area menu, then fully exit Codex.
3. Delete the directory where you extracted `win-x64.zip`.

App-owned settings are stored under `%LOCALAPPDATA%\CodexWallpaper`. If Reset reports a partial failure, check that directory and the desktop shortcut manually.

## Security and privacy

- The companion accepts only a verified official Store/MSIX Codex package and a strict IPv4 loopback CDP endpoint. Loopback is not a security boundary against another process running as the same Windows user.
- Verified local media is loaded through a controlled file input and a `blob:` URL; the companion starts no media HTTP server.
- Scene/Web sends only the image from a verified project window into Codex. It does not forward Wallpaper Engine scripts, keyboard/mouse input, audio, or Codex content. A third-party Web wallpaper may still make its own network requests and play audio through Wallpaper Engine.
- It does not alter or bypass Codex Content Security Policy (CSP), read chats, or proxy Codex/OpenAI traffic.
- It sends no telemetry, behavioral analytics, or project-operated crash report. A diagnostic report is created only after an explicit user export.
- Replacement, Restore Official, and Exit remove only resources owned by this companion. A CDP port owned by Codex closes only when Codex fully exits.

See the following documents for the complete data flow and security boundaries:

- [Security policy](SECURITY.md)
- [Threat model](THREAT_MODEL.md)
- [Privacy notice](PRIVACY.md)

## How it works

1. Local files and Wallpaper Engine Image/Video entries are validated on the device and loaded through a page-supported local media path.
2. Scene/Web captures pixels from a separate project window owned by the running Wallpaper Engine instance. It does not forward project scripts, input, or Codex content.
3. Before activation, the companion verifies Codex, the loopback debugging endpoint, and the target page. Replacement, Restore Official, and Exit remove only resources created by this companion.

## Build from source

Prerequisites: Windows 11 x64 and .NET SDK `10.0.301` or a later patch in the same feature band. SDK selection follows [`global.json`](global.json).

```powershell
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet test .\BackdropForCodex.slnx `
  --configuration Release `
  --filter "Category!=Integration&Category!=BrowserContract"
dotnet run --project .\src\BackdropForCodex.App\BackdropForCodex.App.csproj
```

See the [contributing guide](CONTRIBUTING.md) for publish parameters, formatting, DCO, and implementation constraints.

## Help and contributing

- [Report a bug or request a feature](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/issues/new/choose)
- [Changelog](CHANGELOG.md)
- [Contributing guide](CONTRIBUTING.md)
- [Acknowledgements](ACKNOWLEDGEMENTS.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

Bug fixes, documentation improvements, and discussed feature changes are welcome. Every commit must carry a DCO-compliant `Signed-off-by` line as described in [DCO.md](DCO.md). Report security or privacy issues privately through [SECURITY.md](SECURITY.md), not in a public issue.

## License and notices

The project is available under the [Apache License 2.0](LICENSE). Third-party components remain under their respective licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the SBOM included with a release.

“OpenAI,” “Codex,” “Microsoft,” “Windows,” “Steam,” “Wallpaper Engine,” and related names and marks may belong to their respective owners. They are used only to describe compatibility. No trademark license, affiliation, sponsorship, or endorsement is implied.

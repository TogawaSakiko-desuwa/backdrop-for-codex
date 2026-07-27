<p align="center">
  <a href="README.md">简体中文</a> · <strong>English</strong>
</p>

# Backdrop for Codex

An unofficial, open-source companion for **Windows 11 x64** that places a local image or a muted, looping video behind the workspace of the official Microsoft Store/MSIX Codex desktop app.

**Does not modify the Codex package · Does not upload local media to a project-operated service · Does not read chats · Does not collect telemetry**

[![Latest release](https://img.shields.io/github/v/release/TogawaSakiko-desuwa/backdrop-for-codex?display_name=tag)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)
[![CI](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml/badge.svg)](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

[**Download the Windows 11 x64 portable build →**](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest)

No installer · Runs as a standard user · Local PNG, JPEG, WebP, MP4, and WebM media

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

## Download and quick start

| Release file | Purpose |
| --- | --- |
| `BackdropForCodex-vX.Y.Z-win-x64.zip` | The portable app most users should download |
| `BackdropForCodex-vX.Y.Z-SHA256SUMS.txt` | SHA-256 checksums for downloaded artifacts |
| `BackdropForCodex-vX.Y.Z-win-x64.spdx.json` | Machine-readable SPDX SBOM |

1. Install the official Microsoft Store/MSIX x64 Codex desktop app on Windows 11 x64.
2. Download `win-x64.zip` from [GitHub Releases](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest), then extract it into an empty directory writable by your standard user account.
3. Fully exit every Codex process. Start `BackdropForCodex.exe`, read the CDP risk notice, and accept it if the boundary is appropriate for your machine.
4. Create or select a backdrop profile, choose or drop a local image/video, adjust the preview, and select Apply changes.
5. After the first successful media activation, the companion attempts to create `Codex（动态背景）.lnk` on your desktop for future enhanced launches.

> [!NOTE]
> A release may not have an Authenticode signature. If Windows SmartScreen appears, first confirm that the file came from this repository's Release page and verify its SHA-256 or GitHub artifact attestation.

<details>
<summary><strong>Verify SHA-256 and GitHub build provenance</strong></summary>

Open PowerShell in the download directory and replace `vX.Y.Z` with the actual version:

```powershell
Get-FileHash .\BackdropForCodex-vX.Y.Z-win-x64.zip -Algorithm SHA256
Get-Content .\BackdropForCodex-vX.Y.Z-SHA256SUMS.txt
```

Confirm that the ZIP hash exactly matches the checksum manifest. If [GitHub CLI](https://cli.github.com/) is installed, you can also verify build provenance:

```powershell
gh attestation verify .\BackdropForCodex-vX.Y.Z-win-x64.zip `
  --repo TogawaSakiko-desuwa/backdrop-for-codex
```

</details>

## Features

- Local PNG, JPEG, and WebP images, plus muted looping MP4 and WebM videos.
- Multiple backdrop profiles with create, duplicate, rename, delete, and a formal empty/Official state.
- Contain, cover, and stretch fit modes. Cover mode supports direct focus dragging and arrow-key adjustment.
- Independent dark/light theme overlays, panel opacity, and backdrop blur with a preview before Apply.
- File picker, single-file drag and drop, recent media, and video pause/resume.
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
| Local media limits | Ordinary files on local disks only; image up to 512 MiB, 32,768 px per side, and approximately 33.5 MP; video up to 8 GiB |
| Win32 portable, web, CLI, Windows 10, Windows on Arm, macOS, Linux | Not supported |
| Independent per-window or per-region backdrops, video audio, Wallpaper Engine | Not supported |

Compatibility is determined from the actual page structure, not the Codex version number alone. A Codex update can change page structure, process behavior, or debugging behavior and temporarily affect backdrop rendering. A failed safety check never falls through to injection.

## Frequently asked questions

### Why is the companion still running after I close the window?

Closing the window or pressing `Alt+F4` hides the workbench in the notification area. An active backdrop keeps running. Select Exit from the notification-area menu to end the companion.

### Codex cannot be found, or Apply fails. What should I do?

Confirm that you are using the official Microsoft Store/MSIX x64 Codex and that Backdrop for Codex is not running as administrator. Fully exit every Codex process, then reopen the workbench or use `Codex（动态背景）.lnk`. If the problem remains, export a diagnostic report from Settings and open a [GitHub issue](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/issues/new/choose).

### What if a Codex update breaks the backdrop?

Restore Official, fully exit Codex, and check the [latest release](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex/releases/latest) for a compatibility update. Do not bypass a failed safety check.

### How do I restore the official background?

Restore Official is available in both the workbench and the notification-area menu. It removes the current injection without deleting saved profiles. Select Apply changes to reactivate the selected profile.

### How do I completely reset or uninstall the companion?

1. Open Settings and select Reset app in the Danger zone. This restores the official background and deletes settings, recent media, risk acknowledgement, UI preferences, and the desktop shortcut owned by the app.
2. Exit Backdrop for Codex from the notification-area menu, then fully exit Codex.
3. Delete the directory where you extracted `win-x64.zip`.

App-owned settings are stored under `%LOCALAPPDATA%\CodexWallpaper`. If Reset reports a partial failure, check that directory and the desktop shortcut manually.

## Security and privacy

- The companion accepts only a verified official Store/MSIX Codex package and a strict IPv4 loopback CDP endpoint. Loopback is not a security boundary against another process running as the same Windows user.
- Verified local media is loaded through a controlled file input and a `blob:` URL; the companion starts no media HTTP server.
- It does not alter or bypass Codex Content Security Policy (CSP), read chats, or proxy Codex/OpenAI traffic.
- It sends no telemetry, behavioral analytics, or project-operated crash report. A diagnostic report is created only after an explicit user export.
- Replacement, Restore Official, and Exit remove only resources owned by this companion. A CDP port owned by Codex closes only when Codex fully exits.

For the complete data flow, fail-closed conditions, and residual risks, see:

- [Security policy](SECURITY.md)
- [Threat model](THREAT_MODEL.md)
- [Privacy notice](PRIVACY.md)

## How it works

1. One read-only file handle confirms that the media is an ordinary supported local file and validates its format, size, and image dimensions.
2. The companion verifies the official Codex package, process, Windows session, strict loopback listener, and one target work page.
3. After safety verification, local CDP binds the media to a controlled page file input and the page loads it through a `blob:` URL.
4. Replacement, Restore Official, and Exit remove only nodes, styles, URLs, and media resources owned by this companion.

## Build from source

Prerequisites: Windows 11 x64 and .NET SDK `10.0.301` or a later patch in the same feature band. SDK selection follows [`global.json`](global.json).

```powershell
dotnet restore .\BackdropForCodex.slnx --locked-mode
dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore
dotnet test .\BackdropForCodex.slnx `
  --configuration Release `
  --filter "Category!=Integration"
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

“OpenAI,” “Codex,” “Microsoft,” “Windows,” and related names and marks may belong to their respective owners. They are used only to describe compatibility. No trademark license, affiliation, sponsorship, or endorsement is implied.

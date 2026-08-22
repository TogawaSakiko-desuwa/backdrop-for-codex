# Product

## Platform

Windows 11 x64 desktop

## Users

- Windows 11 users of the official Microsoft Store / MSIX Codex desktop app who want to personalize their workspace with local media or installed Wallpaper Engine projects.
- Individuals configuring their own workstation rather than administrators managing shared or remote devices.

## Product Purpose

Backdrop for Codex is an independent Windows companion that lets a user preview, tune, save, and safely apply a visual background to Codex without modifying the Codex package or uploading local media to a project-owned service. Success means the user can confidently move from a chosen source to a readable Codex workspace, recover the official background, and understand any action that could not be completed safely.

## Positioning

The product combines a local-first wallpaper workbench with guarded Codex activation. It verifies Codex and the selected media before applying a backdrop, and it cleans up only the resources it creates.

## Operating Context

- Windows 11 x64 and the official x64 Microsoft Store / MSIX Codex app are the supported environment.
- Users create and switch named background profiles, choose media, adjust crop and readability effects in a live preview, and then apply the selected profile while launching Codex.
- The application can remain in the notification area after its workbench window closes and provides explicit restore and exit actions.
- Version 1.5.0 supports local PNG, JPEG, WebP, MP4, and WebM files, plus installed Wallpaper Engine Image and Video projects from Workshop and Local Projects. Scene and Web support is experimental and still being improved.
- Local video is muted and loops. Wallpaper Engine projects may play audio through Wallpaper Engine; Backdrop does not mute or forward audio into Codex.

## Capabilities and Constraints

- Local media remains on the device; Backdrop does not run a media HTTP service.
- Activation stops when Codex, the selected media, or the target page cannot be verified.
- Image and Video are the stable Wallpaper Engine content types. Scene and Web are experimental in version 1.5.0. Application and unknown project types are not launched.
- Image and Video use validated local media. Experimental Scene and Web support sends only captured pixels into Codex; project HTML, JavaScript, executables, input, and audio are not forwarded.
- Experimental Scene and Web support requires Wallpaper Engine to be running. Their audio is left to Wallpaper Engine and may be audible; Backdrop transports pixels only.
- Users can restore the official background at any time. Settings upgrades preserve older settings before migration.

## Brand Commitments

- The product name is **Backdrop for Codex**. It must remain clear that the project is independent and is not affiliated with, endorsed by, or supported by OpenAI or Microsoft.

## Product Principles

1. Keep the wallpaper visually expressive while keeping Codex content legible and primary.
2. Make source selection, preview, adjustment, and application one understandable workflow.
3. Fail closed at trust boundaries and explain the recovery path in plain language.
4. Preserve local privacy and provide a reliable route back to the official background.

## Accessibility & Inclusion

- The workbench must remain usable with keyboard navigation, visible focus, Windows high-contrast settings, and 200% display scaling.
- Interactive targets should be at least 32 device-independent pixels, with the primary action at least 36.
- Status changes and failures need a single, non-duplicated accessible announcement and localized action labels.

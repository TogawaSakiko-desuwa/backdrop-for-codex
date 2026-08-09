# Product

<!-- impeccable:product-schema 1 -->

## Platform

desktop

## Users

- **Confirmed from the product and current workflows:** Windows 11 users of the official Microsoft Store / MSIX Codex desktop app who want to personalize the Codex workspace with private local media.
- **Inferred from the current single-user desktop architecture:** the primary user is an individual configuring their own workstation, not an administrator managing shared or remote devices.

## Product Purpose

Backdrop for Codex is an independent Windows companion that lets a user preview, tune, save, and safely apply a visual background to Codex without modifying the Codex package or uploading local media to a project-owned service. Success means the user can confidently move from a chosen source to a readable Codex workspace, recover the official background, and understand any action that could not be completed safely.

## Positioning

The product combines a local-first wallpaper workbench with a fail-closed Codex activation pipeline. It verifies the Codex process, loopback debugging endpoint, target page, and media identity before it mutates the page, and it cleans up only resources that it owns.

## Operating Context

- Windows 11 x64 and the official x64 Microsoft Store / MSIX Codex app are the supported environment.
- Users create and switch named background profiles, choose media, adjust crop and readability effects in a live preview, and then apply the selected profile while launching Codex.
- The application can remain in the notification area after its workbench window closes and provides explicit restore and exit actions.
- The current release supports local PNG, JPEG, WebP, MP4, and WebM files. Video is silent and loops.
- Future source browsing is intended to connect to Wallpaper Engine and expose supported workshop or local projects from the same source library.

## Capabilities and Constraints

- Local media remains on the device and is delivered through a validated file handle, controlled file input, and page-owned `blob:` URL; no media HTTP server is introduced.
- Security-sensitive activation fails closed. An already-running Codex instance that was not launched by this application is not silently adopted.
- Source origin and content type are separate concepts. The planned content matrix is Image, Video, Scene, and Web; unsupported Application projects must never be launched.
- Image and Video can use the existing direct-media delivery path after provider validation. Wallpaper Engine Scene and Web projects require a separate, owned renderer and capture/delivery capability; their HTML, JavaScript, package contents, and executables are not injected into Codex.
- A Wallpaper Engine preview image is only a thumbnail and must not be treated as the project wallpaper itself.
- Until the renderer path exists, Scene and Web sources may be discovered and described but must report a typed unavailable state before CDP connection or page mutation.
- Settings format compatibility, recovery of the official background, and deterministic cleanup are durable contracts.
- **Open decision:** the eventual frame transport between a Wallpaper Engine child window and Codex remains intentionally unspecified pending a focused technical spike.

## Brand Commitments

- The product name is **Backdrop for Codex**. It must remain clear that the project is independent and is not affiliated with, endorsed by, or supported by OpenAI or Microsoft.
- **User-confirmed direction for the workbench redesign:** “精密创作台” (precision creation workbench). This is a binding product metaphor, not permission to imitate Wallpaper Engine or Codex branding.
- Product copy should be calm, direct, and actionable. Safety failures explain what happened and what the user can do next; raw exceptions are diagnostic detail, not primary interface copy.

## Evidence on Hand

- Current workbench reference: the user-provided session screenshot. The small yellow “关闭” control near the upper right is an external interference item and is not part of the product UI.
- Existing product screenshots are under `docs/images/`; current code and tests are the authority for supported behavior and safety contracts.
- There are no approved testimonials, customer logos, usage metrics, or performance claims; future interface work must not fabricate them.

## Product Principles

1. Keep the wallpaper visually expressive while keeping Codex content legible and primary.
2. Make source selection, preview, adjustment, and application one understandable workflow.
3. Fail closed at trust boundaries and explain the recovery path in plain language.
4. Model future source capabilities honestly; an extension point is real only when providers cannot bypass validation, ownership, and cleanup.
5. Preserve local privacy and provide a reliable route back to the official background.

## Accessibility & Inclusion

- The workbench must remain usable with keyboard navigation, visible focus, Windows high-contrast settings, and 200% display scaling.
- Interactive targets should be at least 32 device-independent pixels, with the primary action at least 36.
- Status changes and failures need a single, non-duplicated accessible announcement and localized action labels.

---
name: Backdrop for Codex
description: A precision optical proofing bench for choosing, judging, tuning, and safely applying local wallpaper.
colors:
  system-accent: "AccentColor"
  system-accent-text: "AccentColorText"
  stage-black: "#0B0D10"
  stage-header: "#11161B"
  stage-mat: "#151B22"
  stage-hairline: "#35404A"
  stage-text: "#F5F7F9"
  stage-muted: "#AAB4BE"
typography:
  display:
    fontFamily: "Segoe UI Variable Display, Segoe UI, sans-serif"
    fontSize: "20px"
    fontWeight: 600
  headline:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "16px"
    fontWeight: 600
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "14px"
    fontWeight: 400
  label:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "12px"
    fontWeight: 600
rounded:
  control: "6px"
  surface: "8px"
  frame: "12px"
  large: "16px"
spacing:
  half: "4px"
  base: "8px"
  compact: "12px"
  standard: "16px"
  section: "24px"
  major: "32px"
components:
  button-primary:
    backgroundColor: "{colors.system-accent}"
    textColor: "{colors.system-accent-text}"
    typography: "{typography.body}"
    rounded: "{rounded.control}"
    padding: "8px 16px"
    height: "44px"
  stage-header:
    backgroundColor: "{colors.stage-header}"
    textColor: "{colors.stage-text}"
    typography: "{typography.headline}"
    padding: "16px 20px"
    height: "52px"
  calibration-frame:
    backgroundColor: "{colors.stage-mat}"
    textColor: "{colors.stage-text}"
    rounded: "{rounded.surface}"
    padding: "8px"
---

# Design System: Backdrop for Codex

## Overview

**Creative North Star: "The Optical Proofing Bench"**

Backdrop for Codex is a stage-led precision workspace: source, proof, calibration, safety, and commit read as one continuous working flow. Its outer shell belongs to Windows through Mica, theme-aware Fluent neutrals, the user's system accent, crisp seams, and Segoe UI Variable; the center is a fixed dark optical chamber that isolates the wallpaper from the app theme so judgment remains stable.

The world is compact, calm, and workmanlike. Controls are grouped by whitespace instead of stacked settings cards, selection is neutral except for a narrow accent rail, and almost no decorative elevation competes with the proof. The wallpaper preview is the only saturated field; everything around it behaves like measuring equipment.

**Key Characteristics:**

- Theme-aware Windows Mica shell around a fixed dark proof stage.
- Dominant live preview with a nested safe frame, axes, crop marks, and focus reticle.
- Compact source rows and a progressive Basic/Effects inspector.
- Precise seams, low-radius surfaces, Fluent SymbolIcon glyphs, and almost no shadow.
- One fixed bottom status-and-commit path with one solid Apply action and an explicit restore route.

## Colors

The palette combines Windows-owned neutral roles with a theme-independent charcoal optical stage; the wallpaper alone may carry broad saturation.

### Primary

- **System Accent**: Use the current Windows accent for the single primary Apply action, 3-DIP selection rails, crop marks, active mobile underline, value emphasis, and focus reticle. It is semantic and must follow system theme and high-contrast behavior rather than being frozen to one blue.
- **System Accent Text**: Use the corresponding system-provided foreground on solid accent controls.

### Neutral

- **Stage Black**: The permanent outer field of the optical chamber.
- **Stage Header**: The proof-stage title strip, separated by one precise hairline.
- **Stage Mat**: The nested calibration frame behind the rendered wallpaper.
- **Stage Hairline**: Seams and the calibration-frame edge inside the dark stage.
- **Stage Text**: Primary labels within the fixed dark stage.
- **Stage Muted**: Secondary guidance within the fixed dark stage.
- **Windows Shell Neutrals**: Outside the stage, use WPF UI's dynamic application, layer, control, card, divider, text, and smoke brushes. These roles must continue to respond to light, dark, and high-contrast settings.

### Named Rules

**The Saturation Quarantine Rule.** The preview is the only large saturated field; the system accent appears as a compact signal, never as a decorative wash.

**The Two-Climate Rule.** The outer shell follows Windows theme resources while the optical stage remains dark, except when high contrast replaces it with system colors.

## Typography

**Display Font:** Segoe UI Variable Display (with Segoe UI and sans-serif fallback)

**Body Font:** Segoe UI Variable Text (with Segoe UI and sans-serif fallback)
**Label/Mono Font:** Segoe UI Variable Text; no separate mono face is used

**Character:** Native, highly legible, and deliberately quiet. Hierarchy comes from a compact 20/16/14/12 scale, semibold weight, and whitespace—not oversized display type, all-caps kickers, or ornamental tracking.

### Hierarchy

- **Display** (semibold, 20px): Window-level and profile-specific inspector titles.
- **Headline** (semibold, 16px): Stage and inspector section headings.
- **Body** (regular or semibold, 14px): Controls, filenames, source rows, status copy, and primary action labels.
- **Label** (semibold, 12px): Captions, metadata, values, safety summaries, secondary guidance, and library rail group captions. Rail captions use the secondary text role so the rail reads as grouped navigation rather than a stack of headlines competing with the inspector.

### Named Rules

**The Instrument Type Rule.** Use the four-step type ramp and weight changes for hierarchy; do not introduce hero-scale typography into the workbench.

## Layout

The default workbench is a three-pane grid under a 48-DIP title bar and above a fixed command bar: a 224-DIP collapsible library rail, a fluid proof stage, and a 360-DIP inspector titled for the active profile. The proof stage owns the available middle space and uses a 52-DIP header plus 20-DIP side insets, a 16-DIP top inset, and a 20-DIP bottom inset around the calibration surface.

Spacing follows an 8-DIP rhythm with 4-DIP half-steps and durable 12, 16, 24, and 32-DIP increments. Controls inside inspector sections are grouped by 24-DIP whitespace; 1-DIP seams define adjacent panes and fixed bars. At 960–1279 DIP wide, the library collapses to a 56-DIP icon rail while the 360-DIP inspector remains. Below 960 DIP, the panes stack: a 48-DIP toolbar exposes Library, Preview, and Adjust, only the selected Preview or Adjust pane is shown, and the library opens as an overlay drawer. The minimum designed window is 640 × 520 DIP.

**The Stage Priority Rule.** Preserve a useful proof area first; collapse or layer navigation and editing panes before shrinking the calibration surface into a dashboard card.

**The Single Commit Edge Rule.** Status, recovery, and Apply stay together in the fixed bottom bar at every width.

## Elevation & Depth

The system is flat by default and uses no decorative shadow vocabulary. Depth comes from Mica at the window boundary, tonal layer changes, one-DIP seams, the dark stage/mat relationship, and a smoke scrim behind the mobile library drawer. Cards are bordered surfaces, not floating tiles.

### Named Rules

**The Optical Flatness Rule.** A surface earns separation through tone or a precise border; do not add ambient card shadows or stacked elevations.

## Shapes

Corners are small and functional: controls use a 6-DIP radius, recurring cards and rows use 8 DIP, the inner proof mockup and calibration silhouettes use 12 DIP, and 16 DIP is reserved for larger contained forms. The visual signature is rectangular and seam-led, softened just enough to feel native to Windows. Selected rows keep their neutral fill and gain a 3-DIP vertical accent rail with 2-DIP rounding. The proof carries restrained 16-DIP crop marks, fine safe-frame rectangles, horizontal and vertical axes, and a circular focus reticle.

## Components

### Buttons

- **Shape:** Compact rounded controls (6-DIP radius) with a 32-DIP minimum target; the primary action is at least 44 DIP high.
- **Primary:** One solid system-accent Apply button, semibold, with 16-by-8-DIP padding and a 184-DIP minimum width in the wide command bar.
- **Hover / Focus:** Hover and pressed fills come from Fluent control resources. Keyboard focus uses a two-stroke system focus visual outside the control.
- **Secondary / Ghost:** Secondary controls use a subtle theme-aware fill plus a one-DIP divider stroke; quiet and icon buttons are transparent until hover. Disabled actions stay visibly subordinate.

### Chips

- **Style:** Inspector value chips are compact readouts, not filters: 64-DIP minimum width, 28-DIP minimum height, 8-by-4-DIP padding, subtle control fill, one-DIP stroke, and 6-DIP radius. The minimum is shared so every readout lands on one right-hand column, and chips size to content rather than a fixed width so a two-value readout such as crop focus is never clipped.
- **State:** Values use semibold label text with accent emphasis; selection state belongs to rows and tabs, not to the chip.

### Cards / Containers

- **Corner Style:** Functional 8-DIP surface corners; the calibration and internal proof silhouettes may use 12 DIP.
- **Background:** Theme-aware card fill in the shell; fixed Stage Mat inside the optical chamber.
- **Shadow Strategy:** None; use tonal contrast and a one-DIP border.
- **Border:** Dynamic card or divider strokes outside the stage, Stage Hairline inside it.
- **Internal Padding:** 12 DIP for media cards and 8 DIP for the calibration frame.

### Inputs / Fields

- **Style:** Native Fluent combo boxes, sliders, and expanders use a 36-DIP working height where specified, theme-aware fills and strokes, and the control radius.
- **Focus:** Preserve the system-visible focus treatment and keyboard operation; the crop-focus surface also supports directional keys with a larger Shift step.
- **Error / Disabled:** Missing media is identified with muted explanatory copy and a Fluent error symbol. Unsafe or unavailable actions are disabled and explained through the single status path.

### Navigation

Library navigation uses compact vertical rows with a 32-DIP media/source glyph, 14-DIP semibold name, 12-DIP metadata, neutral selected fill, and a 3-DIP accent rail. The rail is grouped, not flat: profiles, sources, and recent media are separated by one-DIP seams, each group carries a Label-scale caption, and group-level actions such as refresh and clear sit on that caption row instead of nesting inside a row control. Rail action buttons carry a subtle fill plus a one-DIP stroke so they never read as another selectable row. The 56-DIP compact rail drops the captions, keeps the seams, and preserves Fluent SymbolIcons and accessible names. On narrow windows, Preview and Adjust become two plain toolbar choices; the active choice gains only a subtle 2-DIP accent underline.

### Optical Proof Stage

The signature component is a fixed dark stage with a 52-DIP title strip, narrow accent marker, bordered 8-DIP calibration mat, nested safe frame, center axes, crop marks, draggable focus reticle, and a bottom-left media-name pill. The mat tracks the proof's 16:9 ratio inside its own padding rather than filling the stage, so crop marks sit on the real content corners and unused space stays outside the mat as stage, never as dead margin inside the frame. The wallpaper and translucent Codex wireframe dominate; surrounding calibration graphics stay fine, pale, and subordinate, resting near a third of full strength and rising only while the crop focus is adjustable and under the pointer.

### Inspector

The inspector pins the active profile name as a non-scrolling header on the panel fill, then exposes Wallpaper and Basics as the immediate path. Effects remain collapsed until requested and stay a seam-led section rather than a bordered card competing with the sections above it. Labels pair with Fluent SymbolIcons, values align in compact chips, and safety/privacy closes the panel as a quiet preflight rather than a competing card.

## Do's and Don'ts

### Do:

- **Do** keep the outer shell theme-aware and the optical stage fixed dark, with an explicit high-contrast override.
- **Do** preserve the 8-DIP rhythm, 6–8-DIP everyday radii, and 20/16/14/12 type ramp.
- **Do** use Fluent SymbolIcons and visible system focus, with 32-DIP minimum targets and a 44-DIP primary action.
- **Do** keep profile/source choice, live proof, calibration, safety preflight, status, recovery, and Apply in one legible flow.
- **Do** reserve the one solid primary treatment for Apply and keep Restore Official Background beside it as the clear recovery path.

### Don't:

- **Don't** turn the workbench into a horizontal wallpaper carousel paired with a stack of settings cards.
- **Don't** apply the current wallpaper's saturation to shell surfaces, navigation, or inspector chrome.
- **Don't** use decorative elevation, gradients, oversized typography, all-caps kickers, or invented glyphs.
- **Don't** replace the neutral-row-plus-accent-rail selection pattern with large accent-filled tiles.
- **Don't** split apply status, safety resolution, and recovery across multiple competing banners or action bars.

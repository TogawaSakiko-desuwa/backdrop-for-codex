---
name: Backdrop for Codex
description: A compact Windows workbench for choosing a wallpaper, checking it against a Codex mock, and applying it safely.
colors:
  system-accent: "AccentColor"
  system-accent-text: "AccentColorText"
  stage: "SolidBackgroundFillColorSecondary"
  stage-frame: "SurfaceStrokeColorDefault"
  pane: "LayerFillColorDefault"
  pane-alt: "LayerFillColorAlt"
  text: "TextFillColorPrimary"
  text-muted: "TextFillColorSecondary"
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
spacing:
  half: "4px"
  base: "8px"
  compact: "12px"
  standard: "16px"
  section: "24px"
components:
  button-primary:
    backgroundColor: "{colors.system-accent}"
    textColor: "{colors.system-accent-text}"
    typography: "{typography.body}"
    rounded: "{rounded.control}"
    padding: "8px 16px"
    height: "44px"
  profile-card:
    backgroundColor: "{colors.pane}"
    rounded: "{rounded.control}"
    thumbnail: "16:9, 104px tall in the 224px rail"
  preview-frame:
    borderColor: "{colors.stage-frame}"
    borderWidth: "1px"
    inset: "24px"
---

# Design: Backdrop for Codex

## What the window is

Backdrop for Codex is a small Windows 11 utility. The window has three panes under
the title bar and one command bar at the bottom:

- a 224 DIP library rail on the left with wallpaper profiles, sources, and recent media;
- a preview stage in the middle that shows the wallpaper behind a Codex-shaped mock;
- a 360 DIP inspector on the right with the handful of controls a wallpaper needs.

The shell is ordinary Fluent: Mica window, theme-aware fills, the user's system accent,
Segoe UI Variable, 1 DIP seams between panes. Nothing in the chrome is decorated;
the wallpaper is the only thing in the window that is allowed to be loud.

## Principles

1. **Show the wallpaper, not instruments.** The preview is the wallpaper behind a
   translucent Codex layout. There are no crop marks, axes, safe frames, or header
   strips around it. A rule-of-thirds guide appears only while the crop focus is
   adjustable and the pointer is over the preview.
2. **Thumbnails where the content is visual.** Profiles are 16:9 thumbnail cards.
   Recent media rows carry a 64 by 36 thumbnail. A media kind caption appears only
   when it says something the picture cannot (missing file, Scene, Web).
3. **Plain controls.** Inspector rows are a label on the left and a value on the
   right, in text. No per-row icons, no value chips, no collapsed sections for six
   controls.
4. **Copy states a fact, once.** Status text is one sentence. Operating hints live in
   tooltips and accessibility help text, not in visible captions. There is no
   onboarding banner on launch; the footer already says whether the official
   background or a wallpaper is active.
5. **An editor, dark by design.** The shell always runs the dark Fluent theme with the
   editor palette: a near-black stage (`#0F1012`), floating panes (`#1B1D21` at 95
   percent) with a `#2B2E33` hairline, and a `#161719` command bar. There is no theme
   picker. Windows high contrast still replaces every surface with system colors; the
   resolved state is published as the inherited attached property
   `WorkbenchAppearance.IsDark`, which the theme styles trigger on.

## Layout

- Title bar 48 DIP. Command bar at the bottom with status on the left and
  Pause, Restore official, and the single accent Apply button (44 DIP) on the right.
- Library rail 224 DIP; collapses to a 56 DIP icon rail between 960 and 1279 DIP;
  below 960 DIP the panes stack and the rail opens as a drawer.
- The stage carries an ambient field: the preview rendered again through a 96 DIP blur at
  42 percent, so the space above and below the 16:9 frame takes the wallpaper's color
  instead of staying black. It is hidden without media and in high contrast.
- The stage spans the whole workbench behind the panes. Library and inspector float on
  it as 12 DIP radius cards with a 12 DIP gutter (column widths are pane width plus the
  gutter). The 16:9 preview sits in the remaining space with the same 12 DIP gutter and a
  single 1 DIP hairline; there is no header, inset frame, or shadow.
- Inspector: profile name pinned at the top (20 DIP display), then three groups
  separated by 1 DIP dividers: Wallpaper, Composition, Readability.
- Spacing stays on an 8 DIP rhythm with 4 DIP half steps; 24 DIP between groups.

## Components

### Library rail

- Group captions are 12 DIP semibold in the secondary text color. Group actions
  (refresh, clear) sit on the caption row.
- Profile card: 16:9 thumbnail (image, or a video glyph on a neutral tile), name
  below in 14 DIP semibold, caption only for the official background or a missing
  file, an overflow button that appears on hover or selection.
- Recent row: 64 by 36 thumbnail, name, optional caption, hover-only remove button.
- Profile selection is a neutral fill plus a 2 DIP accent outline on the thumbnail;
  recent rows keep a 3 DIP accent rail on the left. No accent-filled tiles.
- Thumbnails come from the shared thumbnail service, so Wallpaper Engine projects show
  their project preview. Local videos show one still frame rendered through WPF's media
  pipeline. A glyph appears only while no image is available.
- The Wallpaper Engine entry is one persistent button with a status dot.

### Preview stage

- The mock mirrors the real Codex desktop layout: sidebar on the left, a thread
  header, one user bubble and assistant text, and a composer at the bottom. The
  translucent surfaces share one `PanelOpacity` and a 1 DIP stroke so they stay
  legible without media.
- Media name pill bottom left; static-preview pill bottom right when the preview is
  a thumbnail rather than live output.
- Crop focus reticle: 40 DIP, idles at 58 percent opacity, full while dragging. It is
  shown only while the pointer is over the preview or the focus surface has keyboard
  focus, so it never sits on the artwork at rest.
- Empty and unavailable states are two lines of text in theme colors. No icon tiles.

### Inspector

- Group captions 12 DIP semibold in the secondary color. Labels 13 DIP regular, values
  13 DIP secondary, right aligned. Sliders are native Fluent with 1 percent keyboard
  steps.
- Readability holds panel opacity, background blur, and the dark and light theme
  overlays. Composition holds media fit and, for Crop to fill, the crop focus with a
  Center button. The focus hint is a tooltip on that row.
- Safety and privacy copy lives in Settings, not in the inspector.

### Buttons

- One solid accent Apply button, 44 DIP, 184 DIP minimum width.
- Secondary: subtle fill plus 1 DIP divider stroke. Quiet and icon buttons are
  transparent until hover. Minimum target 32 DIP.
- Focus visual: two-stroke system focus ring outside the control.

## Don't

- Don't add a header strip, accent markers, crop marks, axes, or a safe frame to
  the preview.
- Don't put an icon in front of every control, or wrap values in chips.
- Don't explain a control in a visible caption when a tooltip will do.
- Don't show a status banner that only says the app is ready.
- Don't fix the stage to a dark color in light theme.
- Don't use gradients, oversized type, all-caps kickers, or decorative shadows; the
  stage, gutters, and hairlines carry the depth.

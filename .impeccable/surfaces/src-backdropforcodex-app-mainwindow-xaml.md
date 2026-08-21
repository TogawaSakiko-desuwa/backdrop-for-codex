---
version: 1
slug: "src-backdropforcodex-app-mainwindow-xaml"
primary_target: "src/BackdropForCodex.App/MainWindow.xaml"
related_targets: ["src/BackdropForCodex.App/MainWindow.xaml.cs","src/BackdropForCodex.App/Views/WallpaperPreviewView.xaml"]
---

# Workbench surface brief

- Scope/mode: native Windows desktop workbench; Operate.
- User/job: select a saved profile and a trusted wallpaper source, judge the live Codex proof, tune readability, and apply or recover without guessing at runtime safety.
- Primary action: apply the selected profile and launch Codex. Restore official background and retry are explicit recovery actions, not competing primary actions.
- Content: profiles, provider-backed source library, live proof, source/fit/focus/readability controls, concise runtime status, diagnostics on demand.
- Constraints: Windows 11 x64, WPF/WPF UI, minimum 640x520, 200% scaling, keyboard and high contrast, localized copy, no raw exception as primary error, and no unsupported Wallpaper Engine provider shown.
- Direction: stage-led “precision optical proofing bench”; seed `f91f3aae`. The approved composition metadata remains in `.impeccable/mocks/workbench-b-stage-led.json`; local visual evidence is intentionally Git-ignored because it may contain user media.
- Memorable moment: a selected source becomes the single saturated proof inside a precise calibration frame while the bottom preflight changes from a recoverable safety explanation to one confident commit action.
- Responsive: >=1280 uses 224 rail + fluid proof + 360 inspector; 960–1279 collapses the rail to 56 with a flyout; <960 uses a source drawer and Preview/Adjust single-task switch with a fixed bottom action bar.
- Unresolved: the future Scene/Web frame transport is outside this surface; until available, those sources may be described but cannot appear as activatable items.

## Fidelity inventory

| Comp ingredient | Implementation medium | Commitment |
| --- | --- | --- |
| App title and Windows caption controls | Existing WPF UI `TitleBar` | Preserve native window behavior; settings remains one 34–36 DIP icon action. |
| Profile/source rail | Semantic XAML lists and WPF UI symbols | 224 DIP expanded, 56 DIP compact; profile and source selection stay visible without a top carousel. |
| Dominant proof stage | Existing `WallpaperPreviewView` plus authored XAML calibration overlay | Own at least half the wide viewport; crop ticks are sparse functional geometry, never a decorative grid. |
| Wallpaper imagery | Existing safe local preview pipeline | Only saturated image-native region; no generated image ships as product evidence. |
| Inspector | Semantic XAML controls bound directly to section view models | 360 DIP, whitespace groups, Basic open and Effects collapsible; no nested cards. |
| Bottom preflight/commit bar | XAML status presenter and real commands | One live announcement, localized recovery copy, one primary 36+ DIP apply action. |
| Icons | Existing WPF UI SymbolIcon set | One consistent stroke family; no Unicode glyph stand-ins. |
| Type | Segoe UI Variable/system WPF typography | 24/16/14/12 ramp; no decorative display face in this high-frequency tool. |
| Theme/material | Dynamic WPF resources | Cool light/dark Mica neutrals and system accent; high contrast remains system-owned. |
| Motion | WPF visual states | One restrained rail/drawer transition; all content visible by default and reduced-motion safe. |

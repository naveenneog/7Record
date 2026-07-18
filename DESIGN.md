---
name: 7Record
description: A focused recording studio that turns software capture into a creator-ready first edit.
colors:
  primary: "oklch(0.535 0.144 352.6)"
  primary-hover: "oklch(0.559 0.146 351.4)"
  accent: "oklch(0.745 0.109 209.7)"
  background: "oklch(0.161 0.003 325.7)"
  surface: "oklch(0.203 0.005 325.8)"
  surface-raised: "oklch(0.252 0.007 325.8)"
  ink: "oklch(0.971 0.005 345.3)"
  muted: "oklch(0.746 0.012 334.2)"
  divider: "oklch(0.509 0.013 339.6)"
  success: "oklch(0.743 0.135 157.6)"
  warning: "oklch(0.800 0.134 81.4)"
  danger: "oklch(0.656 0.156 22.3)"
typography:
  headline:
    fontFamily: "Aptos, Segoe UI Variable, system-ui, sans-serif"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: 1.15
    letterSpacing: "-0.02em"
  title:
    fontFamily: "Aptos, system-ui, sans-serif"
    fontSize: "18px"
    fontWeight: 600
    lineHeight: 1.25
  body:
    fontFamily: "Aptos, system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Aptos, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 600
    lineHeight: 1.25
rounded:
  sm: "6px"
  md: "10px"
  lg: "14px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "24px"
  xl: "32px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "10px 16px"
  button-primary-hover:
    backgroundColor: "{colors.primary-hover}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "10px 16px"
  input:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.sm}"
    padding: "9px 12px"
---

# Design System: 7Record

## 1. Overview

**Creative North Star: "The Focused Edit Suite"**

7Record should feel like a dark, acoustically quiet editing room where the recorded content is the brightest object. The interface is dense enough for serious work but progressively disclosed so a first-time creator can record and export without learning a professional NLE.

The visual system is restrained: near-black neutral architecture, a smoky magenta primary used only for recording and primary actions, and a cool cyan accent for selection, automation, and timeline intelligence. It rejects generic dashboards, gratuitous glass, oversized rounding, decorative gradients, and card-within-card layouts.

**Key Characteristics:**

- Content-first dark workspace suitable for long editing sessions.
- One consistent control vocabulary across recorder, editor, and export.
- State-rich timeline with color supported by icons, labels, and patterns.
- Fast 150-250 ms state transitions with reduced-motion alternatives.
- Compact desktop typography and generous hit targets.

## 2. Colors

The palette places a single warm recording cue against a neutral black stage, balanced by a cool intelligence accent.

### Primary

- **Record Magenta** (`oklch(0.470 0.173 354.8)`): record controls, destructive emphasis that is explicitly labeled, and the single primary action in a view.
- **Record Magenta Hover** (`oklch(0.420 0.165 354.8)`): hover and pressed progression.

### Secondary

- **Automation Cyan** (`oklch(0.720 0.145 210)`): selected timeline events, smart-edit suggestions, focus indicators, and links.

### Neutral

- **Studio Black** (`oklch(0.075 0 0)`): application background.
- **Console Surface** (`oklch(0.115 0.008 355)`): sidebars, toolbars, and timeline lanes.
- **Raised Surface** (`oklch(0.155 0.010 355)`): menus, popovers, and selected panels.
- **Studio Ink** (`oklch(0.955 0.006 355)`): primary text and icons.
- **Muted Ink** (`oklch(0.690 0.012 355)`): secondary text that must remain readable.
- **Divider** (`oklch(0.260 0.012 355)`): structural separators.

**The One Red Light Rule.** Record Magenta is rare. It indicates capture, a primary commitment, or a clearly labeled destructive action; it is never decoration.

## 3. Typography

**Interface Font:** Aptos (with Segoe UI Variable and system fallbacks)

**Character:** Familiar Windows-native clarity with careful weight and spacing rather than decorative type pairing.

### Hierarchy

- **Headline** (650, 28px, 1.15): page titles and key empty-state guidance.
- **Title** (600, 18px, 1.25): panel and workflow headings.
- **Body** (400, 16px, 1.5): instructions and project metadata, capped near 70 characters for prose.
- **Label** (600, 13px, 1.25): controls, timeline labels, and compact metadata; sentence case by default.

**The Editing Density Rule.** Information hierarchy comes from weight, alignment, and spacing, not tiny text. Persistent controls should not fall below 13px.

## 4. Elevation

The product is flat by default and uses tonal layering plus dividers for architecture. Shadows appear only on floating menus, tooltips, drag previews, and dialogs, never as decoration around every panel.

**The State-Causes-Depth Rule.** A surface earns elevation only when it temporarily moves above the editing plane.

## 5. Components

### Buttons

- **Shape:** restrained 10px radius.
- **Primary:** Record Magenta with near-white text, 10px by 16px padding.
- **Hover / Focus:** darker fill on hover; Automation Cyan focus ring with sufficient separation.
- **Secondary / Ghost:** tonal surface or transparent, using the same geometry and state timing.

### Cards / Containers

- **Corner Style:** 10-14px only for standalone project tiles or preview frames.
- **Background:** Console Surface or Raised Surface.
- **Shadow Strategy:** none at rest.
- **Border:** dividers only where grouping is otherwise ambiguous.
- **Internal Padding:** 16-24px.

### Inputs / Fields

- **Style:** Console Surface, 6px radius, persistent label, clear text.
- **Focus:** Automation Cyan outline; never a color-only placeholder.
- **Error / Disabled:** icon plus message for errors; reduced contrast and explicit disabled semantics.

### Navigation

- Use familiar desktop side navigation, tabs, toolbars, context menus, and command palette patterns.
- Recorder setup prioritizes source selection and readiness.
- Editor prioritizes preview, properties, and timeline without nested navigation.

### Timeline

- Use lanes for screen, camera, microphone/system audio, captions, and automation events.
- Cursor zooms, silence cuts, loading speed-ups, and scene changes are explicit editable events.
- Every automatic edit must expose confidence, enable/disable, and reset-to-source behavior.

## 6. Do's and Don'ts

### Do

- Keep the recorded content visually dominant.
- Provide keyboard shortcuts and visible focus for every editing action.
- Use skeletons for media analysis and waveform generation.
- Show capture health, available storage, dropped frames, and source status before and during recording.
- Keep automated changes reversible and inspectable.

### Don't

- Do not imitate a generic analytics dashboard.
- Do not expose every encoder setting in the default recorder flow.
- Do not use glassmorphism, gradient text, decorative grid backgrounds, or excessive card shadows.
- Do not hide smart edits inside a single destructive "magic" action.
- Do not use color alone to distinguish timeline event types.

# LocalMorph Design System

## Visual Thesis

LocalMorph presents conversion as a legible route rather than a black-box upload. The product uses
wayfinding-label geometry so people can see input, engine, and output as one local journey.

## Color Roles

- `--lm-ink`: deep ink for primary type and structural lines.
- `--lm-blue`: active route, primary action, and conversion emphasis.
- `--lm-lime`: small, affirmative local-status mark; never a large text surface.
- `--lm-paper`: cool, quiet foundation for the persuasive landing surface.
- `--lm-line`: thin structural separators for cards, panels, and grouped information.

The converter workspace and reading surfaces use the same cool-paper field as the landing page,
with deep-ink grouping, cobalt route emphasis, squared controls, and restrained lime status
signals. Task density comes from structure rather than a darker theme.

## Typography

Use the condensed system-forward display stack for decisive headings and labels. Use the system UI
stack for explanatory copy. Display headings are dense, high-weight, and short; body copy is
plain-language, spacious, and bounded to a readable measure.

## Components

- **Route board:** The primary product demonstration: source, selected conversion engine, and output
  read as a connected route.
- **Conversion lane:** A row, not a feature card. It pairs one capability with an example transform.
- **Signal control:** Squared blue action control with a thin border, offset shadow, and visible
  keyboard focus.
- **Status mark:** A small lime or blue state indicator used only where it communicates progress or
  local availability.

## Layout and Motion

Landing pages use a wide, daylight-friendly reading field with a route demonstration in the first
viewport. Task surfaces prioritize compact scanability. Collapse paired columns before shrinking
their content, then stack rows at narrow widths.

The route signal is the single authored motion: it gently pulses to indicate a local conversion
path and is disabled when reduced motion is requested.

## Accessibility

Maintain semantic controls, visible `:focus-visible` outlines, high-contrast ink on paper, and
text alternatives for decorative route details. Never rely on color alone to state the active
conversion route.

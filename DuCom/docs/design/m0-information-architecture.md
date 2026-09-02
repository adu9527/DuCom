# M0 Information Architecture Brief

## Product Task Order

DuCom is organized around the user's recurring sequence:

1. Establish a connection and understand session state.
2. Read sustained device output without the interface competing for attention.
3. Prepare and send data with mode and newline behavior visible.

Diagnostics, settings, and advanced automation remain secondary to this sequence.

## Shell Regions

- Connection context: a compact left region for discovery, configuration entry, and active sessions.
- Log workspace: the dominant central region with the highest content capacity and contrast.
- Send workspace: a stable bottom region that keeps mode, input, newline policy, consolidated options, and the primary send action together without competing with log width.
- Diagnostic status: a low-height bottom region for architecture, load, and fault state.

The M0 shell presents these boundaries without implementing M1 behavior. Disabled controls communicate intended placement but do not simulate a working serial session.

## Responsive Rules

- Declared minimum window size is 960x640.
- The log workspace receives all flexible width.
- The connection region retains a fixed task-oriented width; the bottom send editor receives flexible horizontal space.
- At 1366x768 and 1920x1080, the log workspace remains the largest region.
- Future narrow-layout work may collapse secondary connection detail, but log reading and send access must remain available.

## Visual Language

- WPF UI supplies Fluent controls, system accent, Mica, and theme resources.
- DuCom tokens define spacing, panel hierarchy, radii, control sizes, and neutral log surfaces.
- Accent color is reserved for focus, selection, and the primary action.
- Status always includes text; no state relies on color alone.
- The shell does not reuse SuperCom layout, styling, names, screenshots, or information hierarchy.

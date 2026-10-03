# Mission control

Project purpose and intended outcome are defined in [PROJECT-VISION.md](PROJECT-VISION.md). This document describes the interface design in support of that vision.

AI Hub is a shared workspace for a person, Codex, and Claude Code. Its interface borrows the precise instrumentation of GMUNK's Oblivion interfaces and the quiet organization of Linear, with motion that explains real collaboration. The conversation remains the primary working surface.

## Design system

- Canvas: deep graphite `#0A1116`, with a restrained teal atmospheric gradient at the top.
- Navigation: `#0D191F`; raised controls: `#14242C`; separators: `#2A3B43`.
- Primary text: `#E6EDEE`; secondary text: `#A0B3BA`.
- Codex / primary action: sea glass `#82DFD1`; Claude / attention: copper `#EAB38B`.
- Danger: `#F0A0AC`, reserved for Stop and errors. Color always accompanies text.
- Sora Regular/SemiBold for titles; IBM Plex Sans Regular/Medium/SemiBold/Italic for reading and controls. Fonts are bundled under OFL. Code uses the Windows monospace fallback.
- Spacing: 4, 8, 12, 16, 24, 32, 48. Body: 15px/25px; controls: 13px; metadata: 12px. Window units are WPF device-independent pixels.
- Shape: 6px control corners, 12px composer, circular avatars. Conversation replies have open surfaces; the user's message has a subtle fill. Avoid outlining every region.
- Centralize brushes and typography in `Themes/DesignTokens.xaml`; control templates live in `Themes/Controls.xaml`.

## Layout and behavior

A quiet navigation rail anchors the project and conversations. The main workspace starts with a compact collaboration instrument: two named agents, their real states, a directional handoff signal, and the current round. It is present while reading and working. The conversation has a comfortable maximum reading width and retains the user's scroll position. The composer is the clearest interactive surface.

The activity timeline is a secondary inspection surface. It collapses automatically in compact windows until the user explicitly chooses its visibility. A labeled toggle remains available. The current agent action is summarized beside its state so work remains visible with the activity panel closed. Approvals reveal the timeline and explicitly show which agent needs input.

One short entrance sequence establishes the shell, collaboration display, and composer. Handoff motion runs only on a real peer dispatch; active status pulses only during work. Reduced motion and Windows animation preferences disable movement. Minimized windows suspend ongoing effects.

## Critique applied

1. Remove the idle spinning reactor: it implied work when no agents were running.
2. Remove the repeated bordered message cards: they competed with the composer and made every reply equally prominent.
3. Remove decorative micro-labels and tiny uppercase captions: readable sentence-case labels carry the same information with less clutter.

Preserve existing conversations, selected projects, drafts, recipients, keyboard shortcuts, native provider sessions, collaboration guards, approvals, Stop, and export. Validate the actual WPF window at compact sizes and with display scaling. No changes to provider protocols are required for this design.

## References

- https://gmunk.com/OBLIVION-GFX
- https://linear.app/features
- https://territorystudio.com/project/marvels-avengers-infinity-war-endgame/

These are design references, not bundled artwork. The collaboration instrument and background geometry are original WPF vectors.

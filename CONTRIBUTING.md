# Contributing to ircuitry

Thanks for helping build ircuitry. A few house rules keep the project consistent and cozy.

## Rule 1: NO EMOJI IN THE CODEBASE

Do not put emoji characters anywhere in source, data, UI labels, logs, commits, or docs.
Emoji render inconsistently across platforms and fonts, do not tint, and look out of place.

Use **Phosphor icons** instead. Every icon is referenced by its Phosphor name:

- **App (C#):** pass the name through the resolver, e.g. `Ircuitry.Core.Icons.Glyph("robot")`.
  A node's `Icon` field is the name string (e.g. `"dice-five"`); it is drawn via `NodeDef.IconGlyph`.
  The Phosphor font is a fallback on every typeface, so glyphs render crisp and tint to the text colour.
- **Community nodes / workflows (`.ircnode` / `.ircbot`):** the `icon` field is a Phosphor name.
- **Website (HTML/JS):** render `<i class="ph ph-<name>"></i>` (or a Phosphor glyph in SVG via the
  name to codepoint map).

Find a name at https://phosphoricons.com (use the "regular" weight). The full name to codepoint
map ships at `assets/fonts/phosphor-codepoints.json`.

The only acceptable exceptions are a **unicode escape** for a character the program must genuinely
emit at runtime (e.g. a node whose job is to output a specific glyph) or a test fixture that
deliberately exercises unicode handling - write it as `"\U0001F44F"`, never the literal character,
and add a short `// intentional unicode` comment.

CI / review will reject changes that add emoji.

## Other guidelines

- **Aesthetic:** keep visuals cozy and cute (Animal Crossing / Pokemon) - soft glows, rounded shapes,
  warm pastels. Never harsh, edgy, or neon.
- **No em dashes** in user-facing text or commit messages; use hyphens or commas.
- **Nodes are composites:** a community `.ircnode` composes built-in nodes; build a new built-in node
  to fill a gap rather than shipping a code blob.
- Run `dotnet run --project src/Ircuitry -- --selftest` before opening a PR.

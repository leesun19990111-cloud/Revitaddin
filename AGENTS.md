# Codex project instructions

This repository shares its project rules with Claude Code. Treat the existing
Claude instruction files as authoritative project instructions for Codex too.

## Required instructions

1. Before making any change, read the root `CLAUDE.md` completely and follow it.
2. Use the routing table in the root `CLAUDE.md` to identify every affected
   feature area.
3. Before inspecting or changing files in an affected area, read the matching
   `docs/<area>/CLAUDE.md` completely and follow its history, constraints, and
   warnings.
4. If a task spans multiple areas, read and apply every relevant area document.
5. When these instructions conflict, the more specific area document governs
   that area. System, developer, and explicit user instructions still have
   higher priority.

Do not duplicate or independently reinterpret the Claude rules here. Keeping
`CLAUDE.md` and the routed `docs/*/CLAUDE.md` files as the single source of truth
ensures Claude Code and Codex use the same maintained rules.

# Agent instructions for SnagItOpen

Before doing anything in this repository, read `docs/HANDOFF.md` in full. It holds the working rules, the verification gate, the decisions already agreed with the user, and what to do next.

Non-negotiables:
- Windows PowerShell 5.1 (no `&&`, no `rg`).
- Verify coherent implementation batches with build and both test suites; avoid repeated gates after minor edits (user instruction 2026-10-01). Run the full Release gate before packaging. Confirm counts match or exceed `docs/HANDOFF.md`.
- Never run a check in the same parallel batch as the edit it verifies.
- Commit locally in small steps; never push unless asked.
- The UI/UX PRD, including the style gallery, was approved and implemented on `ui-upgrade`. Plan and get approval for future large features outside that scope.
- Keep `docs/HANDOFF.md` up to date at the end of each work session (status, commit, test counts).

# Agent instructions for SnagItOpen

Before doing anything in this repository, read `docs/HANDOFF.md` in full. It holds the working rules, the verification gate, the decisions already agreed with the user, and what to do next.

Non-negotiables:
- Windows PowerShell 5.1 (no `&&`, no `rg`).
- After every change: build, run both test suites, and confirm the counts match or exceed `docs/HANDOFF.md`.
- Never run a check in the same parallel batch as the edit it verifies.
- Commit locally in small steps; never push unless asked.
- Plan and get approval before large new features (Phase B template gallery is next).
- Keep `docs/HANDOFF.md` up to date at the end of each work session (status, commit, test counts).

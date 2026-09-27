# Implementing with smaller LLMs

Use one model session per bounded task. The model should receive the task, its relevant specification sections, and the current contracts/files, not an instruction to build the entire application. The plan has 48 tasks because capture, document geometry, rendering, and interaction need separate validation.

## Starting prompt

Copy this into the model that will implement T01:

```text
We are building SnagItOpen, a local Windows 11 screenshot capture and image editor.
Read README.md, docs/CAPABILITIES.md, and task T01 in
docs/superpowers/plans/2026-09-27-snagitopen.md.
Read sections 1, 4, and 10 of
docs/superpowers/specs/2026-09-27-snagitopen-design.md.

Implement T01 only. The workspace is E:\Misc\SnagItOpen.
Use PowerShell. Check installed .NET SDKs before creating projects.
Create the prescribed project boundaries and compile a minimal WPF shell.
Do not implement capture, editor tools, or image processing yet.
Pin actual versions; do not invent package/API versions or replace the chosen stack.
Preserve these planning documents.

Run the task's verification. Report files changed, exact commands and outcomes,
any environmental blocker, and the next task. Do not claim a successful build
unless you ran it. If you cannot run Windows UI, say which checks remain manual.
```

## Prompt for each subsequent task

Fill the task ID and listed paths from the implementation plan before sending:

```text
Implement task [TASK ID] from
docs/superpowers/plans/2026-09-27-snagitopen.md only.

First read the task, its dependencies, the relevant specification sections,
and all existing contracts used by this task. Check current Git status.
Use the prescribed files and project boundaries. Do not revert unrelated edits.
Do not add a framework, package, schema change, or native backend unless the
task requires it. Reuse existing services instead of creating a second path.

Before editing, state the inputs, outputs, invariants, and behavioral test cases
for this task in a short paragraph. For meaningful algorithms/persistence,
write the task's tests first, observe the expected failure, then implement.
For visual-only wiring, perform the specified manual acceptance steps.
Work in small steps; compile after each contract or integration change.

If a required contract is missing or conflicts with the specification, show the
exact mismatch and propose the smallest correction. Do not invent an incompatible
parallel interface. If a native behavior needs proof, implement a bounded spike
and report the result before generalizing it.

Finish by running the targeted tests and full solution build. Report what passed,
what was not run, and which manual checks remain. Update the task record with
evidence. Stop after this task, leaving the application buildable.
```

The bracketed task field above is intentionally a prompt parameter. The implementation tasks themselves are concrete; do not hand an unfilled prompt to the model.

## Review prompt

Use a separate review pass for every release and the high-risk tasks:

```text
Review task [TASK ID] against its plan and specification.
Read the diff, tests, and relevant call sites. Do not rewrite code speculatively.

Check behavior and integration, not just style:
- Are physical screen pixels, document pixels, and WPF DIPs kept distinct?
- Do crop/rotation/zoom transforms agree between pointer input and export?
- Does a canceled operation leave no partial document/history update?
- Do native resources, streams, and WPF thread-affine objects have clear owners?
- Do save/load and undo preserve editability without copying full bitmaps?
- Are preview, clipboard, and export using the same renderer?
- Do limits prevent oversized decoding/allocation before work starts?
- Are failures visible without losing the existing composition or project file?
- Do tests include real behavioral oracles, not just mirrors of implementation?

Report only actionable findings with file/line, reproduction, impact, and smallest
fix. Separate verified findings from open questions. If nothing is found, say so
and list the checks actually performed; do not imply unrun UI checks passed.
```

## Context to give each task

| Tasks | Essential context |
|---|---|
| T01–T04 | Spec §4/6, current project files, asset/import interfaces |
| T05–T11 | Spec §3/5/6, layout records, renderer contract, export tests |
| T12–T20 | Spec §3–§6/8, reducer/history, image transforms, manifest |
| T21–T26 | Spec §5/7/9, capture state machine, DPI/native wrappers |
| T27–T33 | Spec §4/6/8–§10, annotations, storage revisions, lifecycle |
| T34–T38 | Spec §5/7/11, seam data, capture session adapters, fixtures |
| T39–T44 | Spec §4/5/6/11, transform pipeline, schema, annotation/effect dispatch |
| T45–T48 | Capability matrix, all release evidence, current compatibility notes |

Usually provide 3–8 relevant files plus the task, then let the model read immediate dependencies. Large generated files, build output directories, and entire unrelated libraries waste context. Never substitute a shortened prompt for the actual public contracts.

## Working agreements

1. Implement serially first. If parallel work is later chosen, use separate worktrees and explicit ownership; one owner integrates shared contracts. Do not parallel-edit DocumentState, renderer dispatch, or project schema.
2. Keep Core free of WPF and Win32. A unit test for layout must not need a desktop session.
3. Use one coordinate transform implementation. Do not repair misalignment with unexplained offsets or multipliers.
4. Use one asset and rendering pipeline. Capture output is imported through the same path as files.
5. Preserve the original asset. Edit metadata or generate a derived asset for operations like cut-out; undo must be possible.
6. Do not broaden scope when fixing failures. Identify root cause, add the smallest regression check, fix it, rerun affected checks.
7. Never skip a failing test by weakening the expected result without showing why the original requirement was incorrect.
8. Avoid background network dependencies. Restore/install may need internet; the finished app operates offline.
9. Commit one completed task or clearly bounded substep with a descriptive message. If Git is unavailable, retain a change log; Git initialization is not required to read or use this plan.
10. Preserve user changes. A fresh task starts by reading current state, not regenerating whole files from a stale plan.

## Task handoff record

Create `docs/evidence/task-Txx.md` when that task is implemented:

```markdown
# Task Txx: actual task title

Status: completed / blocked / partially verified
Build identity: commit ID or local revision identifier
Environment: Windows build, architecture, SDK version

Implemented:
- Concrete observable behavior

Files changed:
- Exact paths and responsibilities

Verification:
- Exact command, exit code, test count/result
- Manual scenario, input fixture, observed result

Remaining checks:
- Explicit checks not run and reason; write None if all required checks ran

Contract/schema changes:
- Exact change and migrated callers/fixtures; write None if unchanged

Next task and prerequisites:
- Task ID, required files/evidence
```

## When to split or escalate a task

Split if the model starts rewriting unrelated layers, repeatedly loses track of source vs destination coordinates, or cannot produce a buildable change within its context. For native capture, separate interop declarations/resource ownership, frame acquisition, overlay geometry, and editor routing. For rendering, separate effect math, scene integration, and UI controls.

Ask a more capable reviewer or experienced developer to inspect native handles/thread lifetime, schema migrations, DPI conversions, and scrolling confidence decisions. A smaller model can write the bounded code; a second review guards against locally plausible but incompatible assumptions.

## Suggested stop points

- After T11, use the combiner with real screenshots for a day and note friction.
- After T20, validate free placement, crop, and editable reopening before capture adds complexity.
- After T26, validate every actual monitor layout you use.
- After T33, keep a working baseline package while later features are developed.
- After T44, perform a complete tool inventory against the capability matrix.
- After T48, accept only the capabilities with recorded evidence. Optional OCR may remain deferred.

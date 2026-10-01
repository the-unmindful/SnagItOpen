# UI upgrade completion

Execute the approved 2026-09-30 PRD on the existing ui-upgrade branch, preserving the interrupted U03 changes. User authorized finishing the complete project on 2026-10-01 and requested coherent verification batches instead of checks after minor edits.

1. Foundation: finish U03; shared controls U06; token migration U05.
2. Canvas: U07-U09, U24 and zoom APIs. Independent ownership: CanvasView, ObjectsList and Core layout helpers.
3. Shell: U10-U14, contextual inspector U15-U17, feedback U18, start card U37. Root owns MainWindow, VM and composition.
4. Dialogs: U19-U21. Shared controls worker owns new controls and dialog helpers.
5. Gallery and settings: U22-U27, using the completed controls and state APIs.
6. Capture: U28-U32. Independent ownership: Core capture and App capture files; root routes results/toasts.
7. Secondary windows: U33-U36, then accessibility U38 and final cleanup U40. U39 is optional and outside required completion unless specifically selected.

Each domain adds the meaningful acceptance tests specified by the PRD. Root coordinates builds to avoid concurrent output writes, reviews requirement coverage, runs full Debug/Release gates and launch checks after integration, and records exact evidence and remaining interactive checks. Local commits only; never push or merge unless requested.

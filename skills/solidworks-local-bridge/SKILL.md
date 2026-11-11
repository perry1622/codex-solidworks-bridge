---
name: solidworks-local-bridge
description: Locate a user-provided SolidWorks toolkit, query official API and library locations, and use its local MCP COM bridge for model inspection and existing-part assembly.
---

# SolidWorks local bridge

Locate the complete toolkit from the user's supplied folder or project guidance. Verify package name `sw-codex-toolkit-01a1cb2e`, then read its concise `AGENTS.md` and `validation.json`. This skill alone does not install executable dependencies.

- Prefer existing native parts and configured standard parts. Query local API/Toolbox locations with `Discovery/sw-api-probe.exe`; verify saved configuration, measured size and orientation. Preview masters are not configured sizes; automatic Toolbox size generation is not implemented.
- Bind fresh session/document/revision and component/face references. Use `insert_component_with_mates` for an existing assembly and `assemble_demo` only for an empty one. Work on source copies with unique filenames within the user's authorization.
- Restore the user's assembly after temporary part inspection on success or failure, and verify the active document. Use `activate_document` for recovery.
- Read back component counts, original transforms/fixed states, mate errors and files. Request `sw_inspect` scope `snapshot` when PNG visual confirmation helps; inspect the image alongside numeric checks. Report clearance and unsupported geometry honestly.
- On timeout or partial failure, inspect the job and actual model before retrying. Keep other chats' files and global configuration intact.

For connection setup and complete request examples, read `docs/agent-usage.md` under the located toolkit root. Exact fields, units and geometry limits are in the toolkit's interface documentation and schema. One MCP stdio process calls official local COM; the default mode is read-only.

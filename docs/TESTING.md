# Testing and evidence

This repository ships only the Rhino 8 adapter, optional Grasshopper
components, and their matching local sidecar/MCP executable.

## Repository gate

Run from the repository root on macOS or Windows:

```bash
dotnet restore Tripo.Rhino.sln
dotnet build Tripo.Rhino.sln --configuration Release --no-restore
dotnet test Tripo.Rhino.sln --configuration Release --no-build
```

The build must produce:

```text
src/Tripo.Rhino/bin/Release/net7.0/Tripo.Rhino.rhp
src/Tripo.Rhino/bin/Release/net7.0/sidecar/Tripo.Rhino.Mcp.dll
src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/Tripo.Rhino.Grasshopper.gha
src/Tripo.Rhino.Mcp/bin/Release/net8.0/Tripo.Rhino.Mcp.dll
```

The automated suite covers:

- authenticated, bounded bridge and host-control transports;
- exact host PID and document-session binding;
- credential precedence and redaction, macOS/Windows native-store selection
  without file fallback, and an opt-in isolated Windows native
  write/read/delete canary;
- paid-operation write-ahead journal, replay, conflict, lock, and
  `outcome_unknown` refusal to resend;
- explicit successful-response `code` validation plus exact round-trip of
  current `task_...` and canonical lowercase UUID task identities;
- image transfer, signed download, ZIP extraction, content-addressed OBJ/GLB
  staging, GLB exact-manifest reuse, entry rehashing, symlink rejection,
  OBJ/MTL parsing, coordinate conversion, and parser limits;
- GLB v2 structure, aggregate accessor/image budgets, buffer/accessor bounds,
  acyclic nodes, fixed verified snapshots, host-import journal transitions,
  incomplete/corrupt-tail behavior, schema-2 PBR proof requirements, and
  queued-versus-started cancellation;
- fixed-snapshot cleanup naming, age, live/uncertain PID preservation,
  symlink/reparse rejection, bounded enumeration/mutation, quarantine restore,
  and stale tombstone deletion;
- Rhino proof construction across headless, active, completed-definition, and
  replay stages, including geometry, material-source/binding, recursive render
  content, PBR values, texture mappings, and referenced texture bytes;
- host UI state, recovery hints, explicit paid confirmations, and stable UUID
  retries;
- host-agnostic UI render coalescing, including leading busy and trailing final
  state, stale-session replacement, and disposal before queued callbacks;
- MCP schema plus real stdio initialization/tool-list handshakes;
- raw and packaged sidecar health/shutdown handshakes;
- stable Rhino plug-in identity;
- Rhino/Grasshopper compile-time API compatibility.

Generated `bin/` and `obj/` files are ignored. A clean test receipt also
requires `git diff --check` and a clean `git status`.

## Evidence boundaries

A green repository gate proves deterministic compilation and automated
contracts against pinned reference packages. Separate macOS development-host
testing has exercised the manual `.rhp` layout, Eto panel, Keychain-backed
credential save/use, text generation, and two-second progress refresh. Neither
evidence class proves that the new direct GLB/PBR path renders as intended,
that Undo/replay is correct in a real document, that the optional GHA loads, or
that a Windows production-user Credential Manager deployment works.

On the Windows CI lane,
`TRIPO_RUN_WINDOWS_CREDENTIAL_MANAGER_CANARY=1` exercises `CredWriteW`,
`CredReadW`, and `CredDeleteW` with a synthetic value under a unique
`TripoMCPs/Tests/.../<pid>/<guid>` target. Cleanup runs in `finally`; a hard
runner termination can still leave a clearly prefixed synthetic orphan. The
canary never reads `TRIPO_API_KEY` or the production
`TripoMCPs/TripoV3/<username>` target. It proves the native API works in that
ephemeral runner profile, not that Rhino UI, a production user profile, or
cross-host deployment has been accepted.

Remaining real Rhino 8 acceptance stays a separate gate. Repeat the previously
exercised macOS panel/credential/generation checks after each deployed revision:

1. Load the `.rhp` on every supported OS and open `TripoPanel`.
2. Verify the optional GHA loads and all three **Tripo → Generate** components
   appear.
3. Exercise session and persistent credentials without saving a key into
   `.3dm` or `.gh`.
4. Run text generation/status, then use the recommended direct GLB import and
   verify one PBR block is created without a conversion task or second charge.
   Separately run OBJ conversion and both `mesh` and `instance` compatibility
   imports with explicit cost confirmations.
5. Deliberately lose a paid response and recover with the displayed UUID
   without issuing a replacement POST.
6. In guided recovery, verify the checkbox starts clear, Cancel preserves every
   file, journal drift invalidates the dialog, an active operation remains
   blocked, and a repaired invalid file can be reloaded with **Refresh recovery
   status**.
7. Close the document while a recovery status query is delayed and verify no
   dialog, archival, or API-key mutation continues after panel teardown.
8. Let a durable generation task remain `queued`/`running`; verify its progress
   advances automatically about every two seconds without overlapping status
   calls. Confirm terminal status, disconnect, session replacement, and panel
   teardown stop polling. Then force one status error, verify polling stops
   without repeated dialogs, and confirm **Refresh generation** resumes it.
9. Open and cancel/save the API-key and recovery dialogs repeatedly; confirm
   they close before sidecar work begins and do not trigger Scrollable layout
   crashes.
10. Switch or close documents during delayed work and confirm fail-closed
   behavior.
11. For direct GLB, verify embedded textures/PBR channels, exact object count,
    one-step Undo, same-UUID `already_exists`, and that 401/403 recovery before
    mutation does not leave the import occupied.
12. Force/kill at `prepared`, during native import, after block wrapping, and
    after EndUndo-before-journal-commit. Reopening and retrying the same UUID
    must show manual review and must never call native import again.
13. Verify scale, handedness, OBJ material appearance, restart recovery, and no
    unexpected document mutation from Grasshopper recompute.
14. Verify package layout from a clean checkout rather than a developer `bin/`
   directory.

Static checks, unit/process tests, real Rhino interaction, visual acceptance,
signing/notarization, and public release are distinct evidence classes.

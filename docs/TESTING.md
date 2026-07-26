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
- credential precedence, native secret-store behavior, and redaction;
- paid-operation write-ahead journal, replay, conflict, lock, and
  `outcome_unknown` refusal to resend;
- image transfer, signed download, ZIP extraction, bundle hashing, OBJ/MTL
  parsing, coordinate conversion, and parser limits;
- host UI state, recovery hints, explicit paid confirmations, and stable UUID
  retries;
- MCP schema plus real stdio initialization/tool-list handshakes;
- raw and packaged sidecar health/shutdown handshakes;
- stable Rhino plug-in identity;
- Rhino/Grasshopper compile-time API compatibility.

Generated `bin/` and `obj/` files are ignored. A clean test receipt also
requires `git diff --check` and a clean `git status`.

## Evidence boundaries

A green repository gate proves deterministic compilation and automated
contracts against pinned reference packages. It does not prove that Rhino
loaded the `.rhp` or `.gha`, that macOS Keychain or Windows Credential Manager
behaved correctly in the real host, or that imported materials render as
intended.

Real Rhino 8 acceptance remains a separate gate:

1. Load the `.rhp` on every supported OS and open `TripoPanel`.
2. Verify the optional GHA loads and all three **Tripo → Generate** components
   appear.
3. Exercise session and persistent credentials without saving a key into
   `.3dm` or `.gh`.
4. Run text generation, status, conversion, and both `mesh` and `instance`
   imports with explicit cost confirmations.
5. Deliberately lose a paid response and recover with the displayed UUID
   without issuing a replacement POST.
6. Switch or close documents during delayed work and confirm fail-closed
   behavior.
7. Verify scale, handedness, material appearance, one-step Undo, restart
   recovery, and no unexpected document mutation from Grasshopper recompute.
8. Verify package layout from a clean checkout rather than a developer `bin/`
   directory.

Static checks, unit/process tests, real Rhino interaction, visual acceptance,
signing/notarization, and public release are distinct evidence classes.

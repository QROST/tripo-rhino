# Release candidates

`New-ReleaseCandidate.ps1` assembles a tested Windows-built candidate with
separate `rhino/`, `grasshopper/`, and optional standalone `mcp/` directories.
It refuses a version that differs from the root `VERSION` file and emits a ZIP
plus a SHA-256 sidecar under `artifacts/`.

```powershell
dotnet restore Tripo.Rhino.sln
dotnet build Tripo.Rhino.sln --configuration Release --no-restore
dotnet test Tripo.Rhino.sln --configuration Release --no-build
./packaging/New-ReleaseCandidate.ps1 -Version v0.1.0
```

The tag workflow uploads only a short-lived GitHub Actions artifact. It does
not create a GitHub Release, publish a package, sign, notarize, or establish
real Rhino acceptance. Candidate redistribution is governed by the Apache License, Version 2.0;
see the repository root [`LICENSE`](../LICENSE) and [`NOTICE`](../NOTICE).

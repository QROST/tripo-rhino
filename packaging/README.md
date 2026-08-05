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

## Food4Rhino / Yak assets

`manifest.yml` is the [Yak package manifest](https://developer.rhino3d.com/guides/yak/the-package-manifest/).
Keep its fields in sync with repository facts:

| field | source of truth |
| --- | --- |
| `version` | root [`VERSION`](../VERSION) |
| `name` | locked at first Yak upload; lowercase letters/digits/dashes only |
| `authors` | [`NOTICE`](../NOTICE) copyright line |
| `url` | `origin` remote, also in `TripoGrasshopperInfo.AuthorContact` |
| `icon` | `icon.png` (64×64); `icon-128.png` / `icon-256.png` ship alongside |
| `keywords` (GUIDs) | `626D164C-A15C-45DE-B8A1-0718C81305DE` (.rhp, `TripoRhinoPlugin.cs`) and `CC53B1D7-60D0-4F6A-A43C-BB1F4B68112D` (.gha, `TripoGrasshopperInfo.cs`) |

The `keywords` list includes both assembly GUIDs so Yak search and the
Food4Rhino listing can resolve an installed assembly back to this package,
matching the convention `yak spec` follows for Grasshopper assemblies.

### Icon

`icon.svg` is the vector source. `icon.png` (64×64), `icon-128.png`, and
`icon-256.png` are the rendered PNGs referenced by the manifest and the
Food4Rhino listing. The mark is an original geometric "T" lifting into an
isometric 3D plane; it avoids the Tripo wordmark and any McNeel/Rhino
trademarks. Re-render from the SVG after any design change.

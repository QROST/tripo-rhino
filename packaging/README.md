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

### Building a Yak package

The same manifest drives the Rhino Package Manager (Yak) distribution channel.
Yak is bundled with Rhino 8 on macOS at
`/Applications/Rhino 8.app/Contents/Resources/bin/yak`; on Windows it ships at
`C:\Program Files\Rhino 8\System\yak.exe`. A standalone build is also documented
in the [Yak CLI reference](https://developer.rhino3d.com/guides/yak/yak-cli-reference/).

One Yak package contains the full host deployment: the `.rhp`, its `sidecar/`
directory, the optional `.gha`, and the icon set. The standalone MCP server
(`src/Tripo.Rhino.Mcp/bin/Release/net8.0/`) is **not** part of the Yak package;
it is a sidecar process, not a Rhino plug-in assembly, and is distributed via
the Food4Rhino download or the `.rhp`-adjacent `sidecar/` directory.

To build the `.yak` file from a fresh Release output:

```bash
# 1. Assemble a flat package directory
PKG=/tmp/yak-pkg
rm -rf "$PKG" && mkdir -p "$PKG"
cp -R src/Tripo.Rhino/bin/Release/net7.0/. "$PKG/"
cp src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/Tripo.Rhino.Grasshopper.gha "$PKG/"
cp packaging/icon.png packaging/icon-128.png packaging/icon-256.png "$PKG/"
cp packaging/manifest.yml "$PKG/"

# 2. Build (platform=any declares Windows + macOS; see note below)
YAK="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
( cd "$PKG" && "$YAK" build --platform any )
# → produces tripo-rhino-<version>-rh8_32-any.yak
```

`yak build` emits two informational warnings that do not block packaging:

- *Content version doesn't match manifest* — the assembly carries a
  SourceLink-derived `0.1.0+<git-sha>` while the manifest declares `0.1.0`.
  This is expected under `Deterministic=true`; the manifest version wins for
  the Yak index.
- *Content name doesn't match manifest* — the assembly is `Tripo.Rhino` but
  the Yak package name is the lowercase-dashed `tripo-rhino` required by
  the Yak naming rule.

**Platform scope.** `platform: any` declares both Windows and macOS. The
`sidecar/runtimes/` directory ships `win/` (for `System.Diagnostics.EventLog`)
and relies on the .NET 8 framework fallback elsewhere. Only declare `any`
when both platforms have been exercised; otherwise use `--platform win` or
`--platform mac` to narrow the claim.

**Publishing.** `yak push <file.yak>` uploads to the central Yak repository
(`https://yak.rhino3d.com/`) after a one-time `yak login` with the same Rhino
Account used for the Food4Rhino listing. A pushed package can be linked to the
Food4Rhino page so users get an "Install via Package Manager" entry point
instead of a manual download.

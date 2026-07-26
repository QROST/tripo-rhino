# Materials + import-target design (bridge protocol v2)

> Repository scope: Rhino is the implementation shipped here. Revit material
> notes are retained as protocol compatibility constraints for the copied
> bridge/runtime snapshot.

Status: locked for the `harden/production-pass-1` implementation wave. Changes
require updating this document first.

## Route decision

Materials travel as **OBJ + MTL + baked textures** (`bake=true`), not GLB.

- One shared parser feeds both hosts; no new binary-format dependency.
- Revit's `TessellatedShapeBuilder` cannot consume arbitrary UV-mapped PBR on
  tessellated geometry — its realistic ceiling is per-face-group color and a
  baked diffuse appearance, which OBJ+MTL fully carries.
- Rhino applies the baked diffuse via `TextureCoordinates` + a render
  material; a native-GLB Rhino path stays a declined-for-now enhancement.
- Tripo facts (verified against live docs 2026-07-23): native generation
  output is GLB with full PBR; OBJ conversion with `bake=true` carries a
  single baked diffuse via `.mtl` + image files; STL/3MF are geometry-only.

## Tripo request changes (MCP side)

- `tripo_create_text_task` gains `withMaterials: bool`. When true the
  generation request sends `texture=true, pbr=true` (still `auto_size=true`);
  when false it stays geometry-only exactly as today.
- `tripo_create_obj_conversion` gains `withMaterials: bool`. When true the
  conversion request sends `bake=true`; when false `bake=false`.
- Both flags change the serialized payload, hence the paid-operation
  fingerprint — replaying an operationId with a flipped flag fails closed,
  which is correct.

## Staging: content-addressed bundles

`ArtifactStager.StageObjAsync` is replaced by `StageBundleAsync`:

- Non-zip download → single-entry bundle (the OBJ), same as today's flow.
- Zip download → extract ALL allowlisted entries, flattened decisions:
  - exactly one `*.obj` entry (else `TripoApiException`);
  - zero or one `*.mtl` entry;
  - zero or more texture entries with extensions `.png .jpg .jpeg`;
  - anything else in the archive is ignored (logged in the receipt count);
  - entry names are used as **relative paths inside the bundle directory**,
    normalized: reject absolute paths, `..` segments, empty names, names
    longer than 128 chars, and more than `MaximumBundleFiles = 32` kept
    entries; per-file cap stays `MaximumArtifactBytes`; aggregate cap
    `MaximumBundleBytes = 256 MiB` (both in `BridgeConstants`).
- Every kept entry is hashed (SHA-256, lowercase hex). The **bundle id** is
  the SHA-256 of the canonical manifest string: entries sorted ordinal by
  relative path, each line `"<relativePath>\n<sha256>\n<byteLength>\n"`,
  UTF-8. Final layout: `<staging>/<bundleId>/<relativePath>` with a
  `manifest.json` written last as the completion marker (atomic rename from a
  temp name). A bundle directory whose `manifest.json` exists is immutable;
  a pre-existing complete bundle is re-verified (every file hash) and reused;
  mismatch → collision error, fail closed.

`StagedArtifact` is superseded by:

```csharp
public sealed record StagedBundleEntry(
    string RelativePath,
    string Sha256,
    long ByteLength);

public sealed record StagedBundle(
    string BundleId,
    string ObjEntry,                       // relative path of the OBJ
    string? MtlEntry,                      // relative path or null
    IReadOnlyList<StagedBundleEntry> Entries,  // sorted ordinal by path
    string RootDirectory);                 // absolute staging dir
```

## Bridge contract v2 (`Tripo.Bridge`)

`BridgeConstants.ProtocolVersion` becomes `"2"`. No back-compat shims: the
product is unreleased; server and client ship together.

```csharp
public sealed record ImportMeshRequest(
    string DocumentSessionId,
    string BundleId,
    string ObjEntry,
    string? MtlEntry,
    IReadOnlyList<StagedBundleEntry> Entries,
    string SourceUnit,
    string UpAxis,
    string Handedness,
    string Name,
    string IdempotencyKey,
    string ImportMode,        // "mesh" | "instance" | "family"
    bool ApplyMaterials);
```

- `HostImportReceipt` gains `string ImportMode`, `int MaterialCount`,
  `int TextureCount`, `string? SavedFamilyPath` (Revit only, else null).
  Existing fields keep their meaning.
- New method constant `BridgeConstants.ImportFamilyMethod` is NOT added:
  `host.import_mesh` carries `ImportMode` instead — one method, one
  idempotency story. Hosts validate the mode they support and reject others
  with `BridgeCallException("import_mode_unsupported", ...)`.
- Mode support after all waves: Rhino `mesh | instance` (default wish is
  `instance`), Revit `mesh | family` (default wish is `family`). The MCP tool
  exposes `importMode` with `"native"` resolving to `instance` on Rhino and
  `family` on Revit (resolved MCP-side from the host context's host name).
- **During the M wave only** (before the R/V host waves land) both
  dispatchers keep compiling with today's behavior: they accept
  `ImportMode == "mesh"`, reject other modes with
  `import_mode_unsupported`, and ignore materials. The R and V waves then
  implement the real modes.

## Host-side loading (`StagedArtifactLoader`)

`LoadPreparedObjAsync(request, ct)` now:

1. Resolves `<staging>/<BundleId>` and verifies EVERY entry in
   `request.Entries` (existence, byte length, SHA-256) before parsing;
   `manifest.json` must exist. Any mismatch → `artifact_missing` /
   `artifact_hash_mismatch` typed errors.
2. Parses the OBJ (`request.ObjEntry`) with `ObjParser`, and — when
   `request.ApplyMaterials && request.MtlEntry is not null` — the MTL with
   `MtlParser`.
3. Runs `MeshPreparation.Prepare` and returns `PreparedMesh` (below), with
   texture paths resolved to absolute paths inside the bundle root (path
   containment re-checked).

## Mesh contracts v2

```csharp
public readonly record struct MeshPoint2(double U, double V);

public readonly record struct MeshTriangle(
    int A, int B, int C,
    int MaterialSlot);                     // -1 = none

public sealed record ObjMaterial(
    string Name,
    int? DiffuseArgb,                      // from Kd (+ d/Tr alpha), null if absent
    string? DiffuseTextureRelativePath);   // from map_Kd, bundle-relative

public sealed record ParsedObjMesh(
    IReadOnlyList<MeshPoint3> Positions,
    IReadOnlyList<MeshPoint2> Uvs,             // raw vt list (may be empty)
    IReadOnlyList<ObjFaceCorner> Corners,      // 3 per face, flattened
    IReadOnlyList<int> FaceMaterialSlots,      // 1 per face
    IReadOnlyList<string> MaterialNames);      // usemtl order of first use

public readonly record struct ObjFaceCorner(int Position, int Uv); // Uv -1 if absent

public sealed record PreparedMesh(
    IReadOnlyList<MeshPoint3> VerticesInMeters,
    IReadOnlyList<MeshPoint2> Uvs,         // empty OR parallel to vertices
    IReadOnlyList<MeshTriangle> Triangles,
    IReadOnlyList<PreparedMaterial> Materials,
    int RejectedTriangleCount);

public sealed record PreparedMaterial(
    string Name,
    int? DiffuseArgb,
    string? DiffuseTextureAbsolutePath);
```

### Parser semantics (ObjParser)

- `v`, `f` as today (tri + quad fan, negative indices, bounds checks).
- `vt u v [w]` parsed; out-of-range/absent `vt` index on a corner → corner
  Uv = -1.
- `usemtl <name>`: subsequent faces get the slot of `<name>` (slot = index of
  first use; unknown names still create slots — MTL may not define them).
  Faces before any `usemtl` → slot -1. `mtllib` lines are parsed for the
  FIRST library name only and surfaced to the caller (`MtlLibraryName`
  property on the parse result is NOT added — the loader already knows the
  MTL entry from the bundle; `mtllib` content is ignored deliberately).
- Normals (`vn`, corner normal indices) are skipped entirely: both hosts
  recompute normals; carrying them adds surface with no fidelity gain.

### MTL parser (new `MtlParser`)

Subset: `newmtl`, `Kd r g b` (0..1 floats → 24-bit RGB; alpha from `d`
(opacity) or `Tr` (transparency, 1-d) → ARGB int), `map_Kd <options...>
<file>` (LAST whitespace token is the filename; options ignored). Filename
must resolve, after normalization, to a bundle entry (ordinal match against
`request.Entries` relative paths; also try basename match if the MTL uses a
bare filename). Unknown keywords ignored. Bounded: max 64 materials, max
1024 lines, existing line-length cap reused.

### MeshPreparation v2

- Same unit/axis/handedness transform for positions.
- **Corner-split welding**: host meshes need per-vertex UVs, so unique
  `(position, uv)` pairs become vertices. When the OBJ has no UVs at all,
  vertices pass through as today (no split).
- Winding: the existing left-handed corner swap (A,C,B) must swap the SAME
  corners' UVs (swap whole corners, not just position indices).
- UVs pass through untransformed (baked textures are authored in OBJ UV
  space). V is NOT flipped; if a host renders textures visibly inverted,
  that host flips at application time (host waves own that decision and must
  document it).
- Degenerate-triangle rejection unchanged (operates on positions only).
- Material slots pass through; slots referenced by zero surviving triangles
  are still kept (hosts may create fewer materials if they choose).

## Idempotency

- The journal-side (paid-operation) fingerprint serializes the full request
  including the document session, as before.
- The HOST-side import fingerprint serializes the v2 request with
  `DocumentSessionId` replaced by the empty string: import identity is the
  content (bundle, units, name, mode, materials), not the session. Session
  correctness is enforced separately by the active-session check, and this
  makes a legitimate cross-restart replay (same idempotency key, new
  session) recover instead of failing as a conflict. A retry with a
  different mode or flag still fails closed as `idempotency_conflict`.
- Rhino stores the idempotency key + fingerprint user strings on the
  geometry INSIDE the block definition as well as on the instance, so the
  definition-without-instance reconcile path verifies the fingerprint
  before reusing a leftover definition.
- Revit's Extensible Storage entity also persists MaterialCount and
  TextureCount so `already_exists` receipts report what the document holds,
  not the incoming request's intent. Before LoadFamily, an already-loaded
  family with the target name is reused (recovers the
  instance-deleted-but-family-loaded retry). The template probe also scans
  one bounded level of FamilyTemplatePath subdirectories.
- Rhino instance mode (R wave): definition name `Tripo_<IdempotencyKey>`;
  user strings move to the InstanceObject; lookup switches to
  `ObjectType.InstanceReference`; a definition existing without its instance
  is reconciled by adding the instance to the existing definition.
- Revit family mode (V wave): idempotency via Extensible Storage schema on
  the created FamilyInstance (fields: idempotency key, fingerprint); lookup
  via FilteredElementCollector over FamilyInstance + schema filter. The
  saved `.rfa` lands at `<data root>/families/<IdempotencyKey>.rfa` and its
  path is returned in the receipt.

## Error codes added

`import_mode_unsupported`, `artifact_missing`, `artifact_hash_mismatch`,
`mtl_invalid`, `bundle_invalid`. All fail closed; no silent degradation:
`ApplyMaterials=true` with an unusable MTL/texture is an error, not a
silent geometry-only import.

## Docs

Done: ARCHITECTURE.md and all EN/zh-CN README pairs describe the protocol v2
end-state (updated after the R and V host waves landed).

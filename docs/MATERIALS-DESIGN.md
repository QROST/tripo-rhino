# Materials + import-target design (bridge protocol v2 + Rhino GLB capability)

> Repository scope: Rhino is the implementation shipped here. Revit material
> notes are retained as protocol compatibility constraints for the copied
> bridge/runtime snapshot.

Status: locked for the Rhino direct-GLB implementation wave. Changes require
updating this document first.

## Route decision

Rhino's recommended path imports the successful generation task's **binary
glTF (`.glb`)** directly. **OBJ + MTL + baked textures** (`bake=true`) remains
an explicit compatibility fallback and remains the shared Rhino/Revit route.

- Direct GLB is a Rhino-only, capability-gated branch. It does not change
  Revit's bridge behavior, the shared OBJ parser, or Grasshopper's scalar
  `Mesh` output.
- The direct branch skips the separate billable conversion task. Generation
  and import still use different UUIDs; import is local and idempotent.
- The expiring signed `model_url` never crosses the bridge or enters recovery
  state. The sidecar downloads it into private content-addressed staging and
  sends only a relative entry, exact length, and SHA-256 to Rhino.
- Rhino uses its native glTF importer so PBR render materials and embedded
  textures are not reduced to OBJ's baked diffuse ceiling. The result is
  wrapped in one outer block instance for a single durable import identity.
- One shared OBJ parser continues to feed both hosts; no binary parser is
  introduced on the Revit path.
- Revit's `TessellatedShapeBuilder` cannot consume arbitrary UV-mapped PBR on
  tessellated geometry — its realistic ceiling is per-face-group color and a
  baked diffuse appearance, which OBJ+MTL fully carries.
- Rhino's OBJ fallback continues to apply the baked diffuse via
  `TextureCoordinates` + a render material.
- Tripo facts (verified against live docs 2026-07-23): native generation
  output is GLB with full PBR; OBJ conversion with `bake=true` carries a
  single baked diffuse via `.mtl` + image files; STL/3MF are geometry-only.

Direct GLB is additive. Existing `ImportMeshRequest`, OBJ paid fingerprints,
conversion recovery, and `host.import_mesh` semantics do not change.

## Rhino direct-GLB route

The route is:

1. Query the existing generation task and require exact `success` plus a
   supported generation type.
2. Require a valid absolute `model_url`, then download it through the existing
   HTTPS/public-address/redirect/deadline/size controls.
3. Validate the GLB container before content-addressed staging:
   - magic `glTF`, version 2, exact declared file length;
   - exactly one first JSON chunk and at most one following BIN chunk;
   - no overflow, overlap, duplicate chunk, or trailing bytes;
   - at most 64 MiB total GLB and 4 MiB JSON, with `asset.version` in the 2.x
     family;
   - bounded aggregate accessors, primitive vertex/triangle estimates,
     single-parent acyclic nodes, buffer/view/accessor ranges, and embedded
     decoded-image pixels before Rhino's native parser runs: decoded accessor
     storage is at most 64 MiB; embedded images are at most 4096 pixels on
     either side, 16 Mi pixels each, and 32 Mi pixels in aggregate;
   - no `uri` member on `buffers[]` or `images[]` in the first release.
     Embedded `bufferView` content is accepted; remote, file, absolute,
     relative, and `data:` references are all rejected.
4. Write `<staging>/<artifactId>/model.glb` and `manifest.json` last. The
   artifact ID is derived from a canonical descriptor containing the file
   hash and byte length. A pre-existing completed artifact is re-hashed and
   reused only on an exact match.
5. Re-check the active document session after staging.
6. Send `ImportGlbRequest` through the additive `host.import_glb` capability.
   The bridge accepts no URL or absolute path and re-verifies containment,
   manifest, length, hash, and GLB structure.
7. On Rhino's UI thread, import once into a disposable headless document as a
   preflight. Require at least one valid mesh, bounded vertices/triangles, and
   finite geometry. This protects the user document from ordinary parse
   failures, but it is not process isolation.
8. In one dedicated undo record, create deterministic prepared definition
   `Tripo_<idempotencyKey>`, flush a host-import `prepared` journal record, then
   run the native import in the active document. Collect only newly created
   top-level objects, preserve their native attributes/material references,
   replace the marker geometry with the imported result, remove the temporary
   roots, and create one root instance carrying import identity metadata.
9. Verify unique top-level addition, mesh counts, PBR/material binding, direct
   membership, and cycle-bounded recursive geometry and PBR-content digests.
   End the Undo record, then flush
   `committed(createdId, counts, member digest, PBR-content digest)`.

`RhinoDoc.Import` and headless documents are supported APIs, but they are not
transaction APIs. Failure handling therefore compares pre/post object,
definition, material, render-material, texture, embedded-file, and layer state
and performs best-effort Undo. Any failure after native import begins remains
`mutation_state_uncertain` even if those tracked sets are restored; the same
import is never automatically re-dispatched.

The outer definition members and root instance carry a fingerprint over the
artifact identity, name, fixed GLB import mode, and materials policy, with the
document session omitted exactly as on the OBJ host fingerprint. A flushed
append-only host journal is authoritative: `prepared`, `outcome_unknown`,
corrupt/incomplete state, or a definition without a committed root always
requires manual review and never triggers definition-only reconciliation.
`committed` replay only returns `already_exists` after exact root, counts,
recursive geometry and PBR-content digests, and member identity verification.
An existing direct-import root without a durable committed journal fails
closed. Journal schema 3 records both digests plus PBR proof version 5;
schema-2, older-proof-version, or incomplete records explicitly require manual
review and do not authorize replay. This is intentional: a weaker/older digest
cannot be safely migrated without recomputing its exact proof algorithm. A
different format or content under the same UUID is `idempotency_conflict`.

The PBR-content proof has two domains. A portable semantic proof must match
across headless preflight, active import, and completed definition. It hashes
exact mesh topology, vertices, normals, colors, explicit UVs, transforms,
persistent texture-mapping definitions, the selected object/layer material
source, effective front/back/plugin/subobject bindings, legacy-material fallback
values, and recursive render content. Render content is restricted to Rhino's
built-in PBR/basic material and bitmap/simple-bitmap texture types. Its
portable identity hashes type, canonical persistent RDK fields, normalized
child-slot/on/amount state, and the SHA-256 bytes of every readable referenced
texture file. Projection, wrapping, mapping, linear-workflow, and normal-map
meaning are represented by those persistent fields and slot semantics;
contextual/derived `RenderTexture` getters are not a second source of truth.
The portable domain also excludes the document-owned RDK render hash,
`CachedTextureCoordinates`, and exact editor/preview-only fields, all of which
can change without changing sampled material content. The completed
definition's document-domain proof retains the RDK render hash and becomes
authoritative for journal commit and exact read-only replay. Roots, definition
members, and render children are normalized rather than trusting Rhino table
or linked-list order. Parent-inherited materials, custom/procedural render
content, non-object plugin material sources, unreadable/unsafe texture files,
cycles, duplicate/empty child slots, or unsupported object types fail closed.

Fixed snapshots normally disappear when their read lease ends. Creation also
performs a best-effort cleanup pass over at most 256 strictly named entries,
mutating at most 16 snapshots older than 24 hours whose recorded owner PID is
definitely dead. Symlink/reparse content is rejected. Cleanup renames candidates
through current-process quarantine/tombstone names and never recursively
deletes an uninspected directory.

The host-control method is `workflow.import_generation_glb`; the bridge method
is `host.import_glb`. Both are advertised only for Rhino. Protocol versions
remain unchanged because this is an additive, capability-gated method. Older
or partially deployed host/sidecar pairs fail closed on the missing
capability; the UI keeps OBJ available as fallback.

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
- The Rhino OBJ instance path stores the idempotency key + fingerprint user
  strings on geometry inside the block definition as well as on the instance,
  so its legacy definition-without-instance reconcile path verifies the
  fingerprint before reusing a leftover definition. The native GLB path never
  uses that reconcile rule: only an exact, durable committed journal may be
  replayed.
- Revit's Extensible Storage entity also persists MaterialCount and
  TextureCount so `already_exists` receipts report what the document holds,
  not the incoming request's intent. Before LoadFamily, an already-loaded
  family with the target name is reused (recovers the
  instance-deleted-but-family-loaded retry). The template probe also scans
  one bounded level of FamilyTemplatePath subdirectories.
- Rhino OBJ instance mode (R wave): definition name
  `Tripo_<IdempotencyKey>`;
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

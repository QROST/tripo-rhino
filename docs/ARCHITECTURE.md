# Architecture

> Repository scope: this repository ships the Rhino adapter only. Revit
> references below document bridge protocol-v2, journal, credential, and
> local-data compatibility inherited from the `f6300ce` migration snapshot;
> they do not imply that Revit source is present here. Host-control has since
> advanced from the snapshot's v2 to protocol-v3.

Accepted product-direction lock for **dual front doors** (host UI + MCP) and
**sidecar-only API credentials**:
[`ADR-0001`](./adr/0001-dual-front-doors-and-sidecar-credentials.md).
The optional Rhino Grasshopper surface and its explicit, recoverable component
contract are locked by
[`ADR-0002`](./adr/0002-recoverable-grasshopper-components.md).

Implementation status: the shared workflow seam, authenticated host-control
sidecar mode, sidecar-owned credentials, and Rhino Eto panel implement the
**text-to-3D** portion of Phases 0–2. Source also implements local PNG/JPEG
staging, multi-stage image-to-3D creation for MCP/Grasshopper, and the optional
Rhino GHA's stage-only mesh value path. The Eto panel remains text-only.
Portable tests and package compilation are separate evidence classes; real
Rhino/Grasshopper loading and interactive acceptance remain separate gates.

## Trust boundaries

```text
Front door A: Rhino Eto panel / optional Grasshopper GHA / Revit WPF window
  │ authenticated current-user host-control pipe
  ▼
Host-specific sidecar ───── stdio ───── Front door B: MCP client
  ├── sole Tripo v3 API-key owner
  ├── Tripo v3 HTTPS API
  ├── bounded image-transfer and content-addressed artifact staging
  ├── durable paid-operation journal
  │
  │ authenticated current-user host bridge
  ▼
Rhino plug-in or Revit add-in
  │ Rhino UI thread / Revit ExternalEvent
  ▼
Explicitly identified active document
```

The sidecar is the only component that reads, stores, or uses the Tripo API key.
Host plug-ins transiently collect a pasted key only inside the modal credential
dialog, send it to the sidecar, clear the field, and never put it in `.rvt`,
`.rfa`, `.3dm`, host settings, workflow state, logs, or receipts. Resolution
precedence is process environment (`TRIPO_API_KEY`), then a sidecar
session-only key, then the OS current-user secret store. The private local file
store is used only on unsupported operating systems where no native store is
implemented.

The local bridge uses a random process-lifetime pipe name and session token. A request must also carry:

- protocol version;
- request ID;
- exact document-session ID;
- operation idempotency key;
- content hash and byte length;
- source unit, up axis, and handedness.

The bridge accepts a small method allowlist. It never accepts source code, scripts, remote URLs, or arbitrary local paths.

The host-control channel is a separate protocol-v3 named pipe with a distinct
random token and descriptor. It is bound to one explicit host PID, allows only
health, graceful shutdown, credential status/set/clear, and workflow calls, and
has independent request-size, deadline, and concurrency bounds. A bridge token
cannot authenticate to host-control. The plug-in starts only its
install-relative sidecar (or an explicitly configured absolute
`TRIPO_SIDECAR_PATH`), passes its own host PID and local data root, and asks for
graceful shutdown instead of force-killing an unrelated process.

## Recoverable generation, conversion, import, and mesh-value slice

Both MCP tools and the host UI state machine call the same `ITripoWorkflow`
implementation. The workflow exposes paid Tripo work as recoverable stages.
Materials and the final import-or-value target are explicit choices:

1. Read and retain the exact host document-session ID.
2. Choose one generation branch:
   - **text:** generate a UUID, persist `Prepared`, then persist `Dispatching`
     before one Tripo v3 text-to-model POST. `withMaterials=true` sends
     `texture=true, pbr=true` (`auto_size=true`, `quad=false`);
   - **local image:** validate and privately snapshot one 1–20,000,000-byte
     PNG/JPEG under `image-transfers/`, then generate a UUID. Persist
     `Prepared`, `ImageUploadDispatching`, a valid `file_token`, and
     `ImageGenerationDispatching` as distinct checkpoints around the upload and
     image-to-model POSTs. The upload uses a generic filename; neither source
     path nor filename crosses the protocol.
3. Persist the valid generation task ID before returning it; recover it with the
   same UUID or `tripo_operation_status` if the response is lost.
4. Poll that task through the separate read-only task-status tool.
5. Generate a second UUID, re-check the document, validate the successful generation
   task, then checkpoint and submit one OBJ conversion POST. `withMaterials=true`
   sends `bake=true` so the OBJ ships with an MTL and baked-diffuse image textures;
   `withMaterials=false` sends `bake=false`. `quad` and `with_animation` are always
   `false`.
6. Persist and return the conversion task ID, then poll it through the task-status tool.
7. Download the short-lived `model_url` without an API-key header.
8. Validate and stage a content-addressed bundle instead of one bare OBJ file: a
   non-zip download is a single-entry bundle; a zip download is extracted to exactly
   one `.obj` entry, zero or one `.mtl` entry, and zero or more `.png`/`.jpg`/`.jpeg`
   texture entries, with every other archive entry ignored. Entry names are
   normalized (reject absolute paths, `..` segments, empty or over-128-char names)
   and bounded (`MaximumBundleFiles = 32` kept entries, `MaximumArtifactBytes` per
   file, `MaximumBundleBytes = 256 MiB` aggregate). The bundle ID is the SHA-256 of a
   canonical manifest listing each kept entry's relative path, SHA-256, and byte
   length; a bundle directory is complete only once its `manifest.json` marker
   exists, and a pre-existing complete bundle is re-verified byte-for-byte before
   reuse rather than trusted blindly.
9. Choose one final path:
   - **document import:** re-check the document and import with a third
     caller-generated UUID that is reused across host retries, carrying
     `importMode` (`native` resolves MCP-side from the host context to `instance`
     on Rhino or `family` on Revit) and `applyMaterials`;
   - **Grasshopper value:** use the stage-only host-control method, re-check the
     exact canvas/Rhino document binding, parse and validate the bundle inside
     the GHA, transform meters/Y-up/right-handed coordinates into Rhino document
     units/Z-up/right-handed coordinates, and publish one GH `Mesh`.

The import request may also explicitly select `mesh`, `instance`, or `family`;
a host rejects an unsupported mode before mutation. `applyMaterials` fails
closed when the bundle has no MTL rather than silently importing geometry-only.
The Grasshopper value path performs no Rhino document mutation and creates no
Undo record.

The document-import path returns a typed host mutation receipt, including
`ImportMode`, `MaterialCount`, `TextureCount`, and—Revit family mode only—
`SavedFamilyPath`.

The generation and conversion cloud requests may consume credits. Each
task-creation entry point therefore requires an explicit cost confirmation.
Task-creating POST requests are never automatically retried because the API
does not document an idempotency key.

The paid POSTs are deliberately not hidden inside one end-to-end tool. A
root-global sidecar execution lock admits only one credential mutation or paid
create/convert workflow at a time, while each paid operation also has its own
cross-process lock and an append-only, revisioned, checksummed JSONL journal.
The request fingerprint is an HMAC keyed by the API credential over the API
base, endpoint, exact serialized request bytes, and document session; the key
and prompt are not stored in the journal. Same UUID plus the same fingerprint
replays a durable ID. Any mismatch fails closed before a POST, including a
`TRIPO_MODEL` override or a flipped `withMaterials` changed between attempts
with the same operation UUID.

Image creation extends that journal with
`ImageUploadDispatching → ImageFileTokenPersisted →
ImageGenerationDispatching → TaskIdPersisted`. Upload and generation have
separate `outcome_unknown` failure stages. Once the file token and exact image
request identity are durable, the same UUID can resume generation without
reopening or uploading the image. An ambiguous upload or generation is not
automatically resent. The sidecar verifies the staged descriptor and uploads an
immutable in-memory snapshot, closing the check-versus-upload race on the
owner-writable transfer file.

The shared host UI generates and displays each operation UUID before dispatch
and requires a separate Tripo-credit confirmation for generation and
conversion. If a paid response is lost, refresh queries the local operation
journal and recovers a durable task ID or same-UUID retry only when locally
proven safe. An unresolved or ambiguous dispatch disables reset and new-key
changes so the UI cannot disguise a paid result. A definitive
`request_rejected` receipt instead clears the rejected stage and requires a
corrected credential plus a new UUID; generation rejection clears downstream
work, while conversion rejection preserves successful generation. Conversion
and import remain bound to the document session captured by generation;
switching documents fails closed.

A recovered Grasshopper definition may request same-UUID recovery only when
the original local journal exists. `RequireExistingOperation` makes that
admission atomic: a copied/tampered definition or deleted journal cannot turn a
saved UUID into a fresh paid operation.

`Dispatching` is durable before network send. A timeout, cancellation, or malformed response within that same process is checkpointed to `OutcomeUnknown` immediately, before the API client returns. A definite credential failure before POST, or provider HTTP 401/403 response, is checkpointed to `RequestRejected` instead and cannot authorize reuse of that UUID. A `Dispatching` record abandoned by a killed process is different: `tripo_operation_status` reads still report state `dispatching` untouched, and the durable rewrite to `OutcomeUnknown` happens only on the next `AcquireAsync` for that same operation ID — the next creation attempt or explicit retry with the same UUID. Neither ambiguous path automatically resends the paid request. A task ID and a definitive rejection are checkpointed with caller cancellation ignored before the API client returns. `tripo_operation_status` reads local recovery truth only; it does not query the provider.

This is a local process-crash recovery and at-most-one automatic-dispatch boundary, not a remote exactly-once guarantee. The provider POST and local storage cannot form one atomic transaction. Power loss, storage failure, a non-cooperating same-user process, a network filesystem, or deletion/change of `TRIPO_LOCAL_DATA_DIR` remains an explicit manual-recovery boundary.

The API and signed-download paths use independent linked deadlines that cover response headers and the complete response body. A POST transport failure reports that remote creation may already have happened and remains non-retryable.

The host-side import fingerprint is a separate identity from the journal fingerprint
above: it serializes the same v2 import request with `DocumentSessionId` replaced by
the empty string, so import identity is the bundle, units, name, mode, and materials
flag — not the session. A legitimate cross-restart replay (same idempotency key, a
new session) recovers instead of failing as a conflict; the still-separate
active-session check keeps session correctness honest on its own. A retry with a
different mode or materials flag fails closed as `idempotency_conflict`.

Materials travel as baked diffuse only: OBJ+MTL carries `Kd`/`d`/`Tr` color and alpha
plus one `map_Kd` diffuse texture per material slot; normals are never carried
(both hosts recompute them, and UV V is never flipped on this path). Revit binds
that diffuse texture into a duplicated appearance asset only when the target
document (family or host) already ships an `AppearanceAssetElement` to duplicate —
a template lacking one still yields a correctly colored but untextured material,
which is an environmental limit, not an error.

## Panel crash-recovery hints

`Tripo.HostUi` writes a private, non-authoritative recovery hint before a
generation, conversion, or import dispatch can leave the panel process. The
file is
`<data root>/ui-recovery/<host>/<recovery-id>.json`, written through a
same-directory temporary file and atomic replace. It contains canonical
operation IDs, durable task IDs when known, journal state, and only the bounded
name/mode/material fields required to identify an import retry. It excludes the
prompt, credential, Authorization header, URL, fingerprint, artifact path, and
host document path. Each panel session owns a distinct recovery ID, so
multiple panels for one document cannot overwrite each other. The hint remains
through a successful import and is removed only by an explicit live-workflow
reset or archived after recovered reconciliation.

The live session owns its hint in process. Once that session or process is gone,
a later panel treats the hint as stale and blocks new workflows and credential
mutation. A different host process cannot observe panel-session disposal inside
the owner PID, so it conservatively treats any foreign hint as blocking instead
of guessing liveness; another process cannot archive it until the exact owner
process is verified as exited. Failure to query owner-process metadata is
blocking, not proof of exit. Credential mutation scans unresolved paid hints,
unverifiable foreign-owner records, and invalid recovery storage across both
Rhino and Revit. A root-global current-user UI intent lease is held from that
credential-recovery scan through completion or cancellation of the key-mutation
or paid host-control call. A second current-user execution lease is owned by
the sidecar for the actual key mutation and for every paid UI or standalone MCP
workflow, beginning before the credential-derived request fingerprint and
ending only after a durable task ID, definitive `request_rejected`, or
ambiguous-outcome journal checkpoint. A
disconnected UI client therefore cannot release the
last protection around sidecar work, closing the cross-panel and MCP
check-then-act gaps. Paid IDs can only be queried through the read-only
operation-status path; imports require manual same-UUID reconciliation. Valid
hints are archived only after the guided dialog performs that local inspection,
shows the evidence and risks, and the user explicitly checks the confirmation.
The dialog submits a snapshot token covering both the recovery files and every
displayed full journal receipt or unavailable result. The session reloads the
recovery set and, while holding the shared credential-workflow execution gate,
queries the journal twice before archival. Any drift fails closed, and a receipt
marked in progress cannot be archived. Existing panel
workflow state is first reloaded into the same recovery review so dispatched
identities are not discarded. Invalid UTF-8/JSON/schema/UUID/task IDs,
oversized files, non-private Unix modes, symlinks, and unreadable hints fail
closed. The hint is an index: the paid-operation journal remains the authority
for paid POSTs, and host idempotency metadata remains the authority for imports.

The GHA's component-owned private payload persists bounded local recovery
state in the `.gh` definition: operation UUIDs/fingerprints, durable task IDs,
last status/progress/credits, and for image work the opaque transfer
UUID/hash/length/media type. That private payload does not duplicate the text
prompt and does not serialize source paths, filenames, image bytes, file
tokens, credentials, URLs, or component UI error text. The enclosing
Grasshopper definition may still serialize ordinary input data—including a
prompt, persistent default, or upstream data—under normal Grasshopper
semantics, so users must treat `.gh` as a potentially sensitive model file.
Deserialization restores local display/recovery state only and never dispatches
a paid call.

## Host rules

### Rhino

- The plug-in registers a per-document Eto panel opened from the **Tripo**
  panel entry or the `TripoPanel` command. The text UI exposes prompt, face
  limit, materials, import mode, key management, progress, and the three durable
  stage IDs as selectable fields. It also exposes read-only crash recovery and
  explicit snapshot-bound review controls.
- All document reads and writes run on the Rhino UI thread.
- A delayed request is rejected if the active document no longer matches the requested document session.
- Geometry is prepared before mutation; each import (a mesh, or an instance plus its
  definition) is created in one undo record.
- Two import modes: `mesh` (one Rhino mesh object; refuses `applyMaterials` when the
  bundle carries more than one material slot rather than silently collapsing
  colors) and `instance` (one block definition holding one sub-mesh per material
  slot, plus one `InstanceObject`) — `importMode=native` resolves to `instance` on
  Rhino.
- Idempotency metadata binds a key to the complete canonical import request: user
  strings on the mesh object in `mesh` mode; on the `InstanceObject` **and** on each
  geometry member inside the block definition in `instance` mode, so a definition
  that exists without its instance (a crash between the two creates) is reconciled
  by verifying the stored fingerprint on the definition's members before adding the
  missing instance.
- Materials apply the baked diffuse OBJ/MTL via `TextureCoordinates` plus a render
  `Material` with an optional bitmap texture; native GLB/PBR import stays a
  declined-for-now enhancement.
- Shutdown synchronously drains panel sessions, performs the bounded sidecar
  graceful-shutdown attempt, and drains the bridge before disposing dispatcher
  state.

#### Optional Grasshopper surface

- `Tripo Text Task`, `Tripo Image Task`, and `Tripo Task to Mesh` appear under
  **Tripo → Generate** and reuse the startup-loaded Rhino plug-in, sidecar,
  journal, recovery store, and exact document-session registry.
- Paid creation/conversion starts only from an explicit component context-menu
  action plus a modal cost confirmation. `SolveInstance`, recompute, definition
  load, and deserialization are non-billable.
- Inputs are scalar-only. Paid actions refuse Grasshopper Player, headless,
  compiled-command, and any context where the exact GH document, associated
  Rhino document, or original captured binding cannot be proven.
- Task-to-Mesh uses the stage-only method and returns a GH mesh value. It does
  not call the host import bridge, mutate the Rhino document, bake, or create an
  Undo record.
- Removing a component discards future UI/mesh publication; an already admitted
  sidecar wait continues to its durable task-ID or ambiguity safety checkpoint.
  This is not remote cancellation.

### Revit

- The add-in registers **Add-Ins → Tripo → Tripo 3D** and opens one modeless WPF
  window. The window exposes the same text workflow fields and durable IDs as
  Rhino; it does not retain `Document` or `UIApplication` objects. Closing a
  workflow-bearing window hides it for the current Revit process so reopening
  preserves the in-memory operation identities.
- All document reads and writes run inside an `ExternalEvent`.
- Requests use a bounded FIFO queue instead of a global mutable slot.
- The active document is checked again inside the API context.
- A bounded FIFO and pending-event retry close the `ExternalEvent.Raise` lost-wakeup race.
- Two import modes: `mesh` (one transaction creates one non-solid Generic Models
  `DirectShape`, identity kept in `ApplicationId`/`ApplicationDataId`) and `family`
  (`importMode=native` resolves to this on Revit): a family template is probed
  (`TRIPO_REVIT_FAMILY_TEMPLATE` env var first, then `Metric Generic Model.rft` /
  `Generic Model.rft` / localized candidates under `FamilyTemplatePath`, including
  one bounded level of subdirectories), a new family document builds the same
  non-solid tessellated geometry and materials in its own transaction, is saved to
  `<data root>/families/<IdempotencyKey>.rfa`, then loaded into the host document
  (or reused by its derived name if an earlier attempt already loaded it) and placed
  as one `FamilyInstance` in a second transaction.
- Family-mode idempotency uses an Extensible Storage schema entity on the created
  `FamilyInstance` (idempotency key, request fingerprint, `MaterialCount`,
  `TextureCount`), so an `already_exists` receipt reports what the document actually
  holds; before `LoadFamily`, a family already loaded under the derived name is
  reused, recovering an instance-deleted-but-family-loaded retry. `mesh`-mode
  `DirectShape`s persist no counts, so their `already_exists` receipt reports zero
  materials/textures rather than guessing.
- Shutdown synchronously closes the panel session, performs the bounded sidecar
  graceful-shutdown attempt, cancels bridge intake, fails queued work, drains
  clients, then disposes the external event.

## Evidence boundaries

Protocol and parser tests, package compilation, packaged-sidecar process
handshakes, native secret-store canaries, real host runtime, installer behavior,
and visual/material fidelity are separate evidence classes. A passing portable
test suite does not claim that Rhino, Grasshopper, or Revit loaded the binaries.
The current source tree implements local PNG/JPEG upload/image creation for MCP
and Grasshopper plus a text-only Eto/Revit panel slice. It does not implement
panel image controls, WebP/public-URL input, a Yak package, or a signed
installer.

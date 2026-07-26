# Tripo-Rhino

English | [简体中文](./README.zh-CN.md)

Tripo-Rhino is an independent community adapter for Rhino 8. AEC users can run
the text-to-model workflow from a per-document Eto panel, or use the optional
Grasshopper GHA for explicit text/local-image generation and a Grasshopper mesh
value; agentic clients can use the same sidecar through MCP. Validated OBJ
output can be imported into the exact active Rhino document as a mesh/block, or
staged without document mutation for Grasshopper.

It is not an official Tripo or McNeel product.

```text
Rhino Eto / optional Grasshopper             MCP client
                   ↕ host-control               ↕ stdio
       Tripo.Rhino.Mcp sidecar / server ── Tripo v3 HTTPS API
                    ↕ authenticated protocol-v2 host bridge
                 Tripo.Rhino.rhp
                    ↕ Rhino UI thread + one undo record
                 exact active Rhino document
```

The sidecar is the only process that resolves, stores, or uses the Tripo API
key. The plug-in's password dialog only forwards a transient value over the
authenticated local control channel and clears the field; it does not write
the key to Rhino settings or the `.3dm` document.

> **Current status:** the `.rhp` and optional `.gha` target Rhino 8 and compile
> against pinned RhinoCommon/Grasshopper packages. The Eto text workflow,
> Grasshopper text/local-PNG-or-JPEG workflow, credential dialog, sidecar
> launcher, and bundled sidecar layout exist in source, with portable
> control/workflow/MCP/process tests. Real Rhino panel/GHA loading, component
> visibility and menu interaction, macOS Keychain and Windows Credential
> Manager interaction, Undo, scale/orientation, performance, and visual
> acceptance remain open gates.
> There is no Yak package, installer, signing, notarization, or automatic update
> mechanism.

For GHA-specific build, installation, component, privacy, and recovery details,
see the [Grasshopper guide](./src/Tripo.Rhino.Grasshopper/README.md).

## Prerequisites

- Rhino 8.
- .NET 8 SDK to restore and build the projects. The repository selects
  `8.0.100` with `latestFeature` roll-forward inside .NET 8. Restore requires
  NuGet access.
- A .NET 8 runtime to run the framework-dependent MCP server. The SDK includes
  this runtime.
- An MCP client that supports stdio servers, only for the optional MCP path.
- A Tripo v3 API key for remote generation and conversion.
- Rhino, the panel sidecar, and any MCP server must run as the same
  operating-system user.

The host plug-in targets `net7.0` and compiles against RhinoCommon
`8.32.26160.13001`. The repository does not yet establish a minimum Rhino 8
service release or a fully tested Windows/macOS runtime matrix.

## Build

Run from the repository root:

```bash
dotnet restore src/Tripo.Rhino/Tripo.Rhino.csproj
dotnet restore src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj
dotnet restore src/Tripo.Rhino.Mcp/Tripo.Rhino.Mcp.csproj

dotnet build src/Tripo.Rhino/Tripo.Rhino.csproj \
  --configuration Release \
  --no-restore

dotnet build src/Tripo.Rhino.Mcp/Tripo.Rhino.Mcp.csproj \
  --configuration Release \
  --no-restore

dotnet build src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj \
  --configuration Release \
  --no-restore
```

Outputs:

```text
src/Tripo.Rhino/bin/Release/net7.0/
src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/
src/Tripo.Rhino.Mcp/bin/Release/net8.0/
```

Keep each output directory together:

- deploy `Tripo.Rhino.rhp`, `Tripo.Bridge.dll`, `Tripo.HostUi.dll`, the complete
  generated `sidecar/` directory, and the other host output files from the same
  build;
- keep the MCP assembly, `.deps.json`, `.runtimeconfig.json`, and dependency
  files together; do not deploy only `Tripo.Rhino.Mcp.dll`;
- install `Tripo.Rhino.Grasshopper.gha` only after the matching complete Rhino
  host output is installed and loads at startup; the GHA output is not a
  complete sidecar deployment;
- Rhino supplies `RhinoCommon`; it is intentionally not copied locally;
- `.pdb` files are optional debugging symbols.

Copy deployments to a stable directory. Do not register a plug-in directly
from `bin/` if that directory may later be removed by `dotnet clean`.
The host build's `sidecar/` directory is the panel runtime. The separate
`src/Tripo.Rhino.Mcp/bin/Release/net8.0/` output is needed only when configuring an MCP
client. Bridge protocol v2 and host-control protocol v3 have no
backward-compatibility shim, so deploy all components from the same repository
revision.

## Install the Rhino plug-in

This repository currently provides only a manual development installation.

### Windows

1. Close Rhino.
2. Copy the complete `src/Tripo.Rhino/bin/Release/net7.0/` output to a stable local
   plug-in directory.
3. Start Rhino 8 and run `PlugInManager`.
4. Choose the install/load action and select `Tripo.Rhino.rhp` in that
   directory.
5. Restart Rhino so the plug-in's startup load behavior is exercised.
6. Open or create a Rhino document.
7. Confirm that Rhino's command history contains:

   ```text
   [Tripo] Rhino bridge and Eto panel ready for PID <process-id>.
   ```

8. Run the Rhino command `TripoPanel` to open the per-document **Tripo** panel.

Rhino's official Windows guidance confirms that a `.rhp` can be loaded through
`PlugInManager`: [Registering Plugins (Windows)](https://developer.rhino3d.com/guides/rhinocommon/registering-plugins-windows/).
Exact menu labels and downloaded-file security prompts can vary by Rhino build.

### macOS

Rhino for Mac does not use the Windows Plug-in Manager workflow. For a manual,
version-specific development install:

1. Quit Rhino.
2. Copy the complete `src/Tripo.Rhino/bin/Release/net7.0/` output to a stable
   directory, then rename that containing directory to `Tripo.Rhino.rhp`. Do
   not rename the `Tripo.Rhino.rhp` assembly inside it.
3. Place the resulting package directory at:

   ```text
   ~/Library/Application Support/McNeel/Rhinoceros/8.0/MacPlugIns/Tripo.Rhino.rhp/
   ```

   The package directory must contain the `Tripo.Rhino.rhp` assembly,
   `Tripo.Bridge.dll`, `Tripo.HostUi.dll`, and the complete `sidecar/`
   directory from the same build.
4. Restart Rhino, open a document, look for the ready message above, and run
   `TripoPanel`.

McNeel documents the `.rhp` package-folder convention and the Rhino 8
version-specific `MacPlugIns` location:
[Plugin Installers (Mac)](https://developer.rhino3d.com/guides/rhinocommon/plugin-installers-mac/).
McNeel now describes `.macrhi` as no longer under active development and points
authors to Package Manager. This repository provides neither a Yak package nor
a `.macrhi`, and the manual layout has not been accepted in a real-host canary.

## Install the optional Grasshopper components

Install and verify the complete `.rhp`/`sidecar/` first. Then open Grasshopper's
**File → Special Folders → Components Folder**, close Rhino, and copy the
same-revision
`src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/Tripo.Rhino.Grasshopper.gha` into that
assembly directory. Restart Rhino/Grasshopper and verify **Tripo → Generate**
contains **Tripo Text Task**, **Tripo Image Task**, and
**Tripo Task to Mesh**.

The GHA output is not a complete deployment. It borrows the sidecar manager,
credential owner, document-session registry, journal, and recovery store from
the startup-loaded `.rhp`. Copying only the `.gha` is unsupported. See the
[complete Grasshopper deployment and use guide](./src/Tripo.Rhino.Grasshopper/README.md).

## Configure the optional MCP server

The most portable invocation uses the `dotnet` host and the MCP assembly:

```text
dotnet /absolute/path/to/tripo-rhino/src/Tripo.Rhino.Mcp/bin/Release/net8.0/Tripo.Rhino.Mcp.dll
```

Use absolute paths. If a GUI MCP client has a restricted `PATH`, set `command`
to the absolute path of the `dotnet` executable.

The following is a common configuration shape for clients that use
`mcpServers`, `command`, `args`, and `env`. Adapt it to your client's schema and
secret mechanism:

```json
{
  "mcpServers": {
    "tripo-rhino": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/tripo-rhino/src/Tripo.Rhino.Mcp/bin/Release/net8.0/Tripo.Rhino.Mcp.dll"
      ],
      "env": {
        "TRIPO_API_KEY": "REPLACE_USING_YOUR_CLIENT_SECRET_MECHANISM"
      }
    }
  }
}
```

On Windows, JSON backslashes must be escaped:

```json
{
  "mcpServers": {
    "tripo-rhino": {
      "command": "dotnet",
      "args": [
        "C:\\absolute\\path\\to\\tripo-rhino\\src\\Tripo.Rhino.Mcp\\bin\\Release\\net8.0\\Tripo.Rhino.Mcp.dll"
      ],
      "env": {
        "TRIPO_API_KEY": "REPLACE_USING_YOUR_CLIENT_SECRET_MECHANISM"
      }
    }
  }
}
```

The direct `Tripo.Rhino.Mcp.exe` on Windows or `Tripo.Rhino.Mcp` on macOS can
also be used when that apphost was built for the same OS and architecture. The
`dotnet` plus `.dll` form avoids that portability assumption.

### Environment variables

| Variable | Where to set it | Requirement |
| --- | --- | --- |
| `TRIPO_API_KEY` | Sidecar / MCP server | Optional environment-supplied key. It overrides session and stored keys. The panel can set a key without this variable. |
| `TRIPO_MODEL` | Sidecar / MCP server | Optional text-generation model identifier. The default is `v3.1-20260211`; an override must match `[A-Za-z0-9._-]{1,64}`, is returned by the text-task receipt, and is part of the text-task paid request identity. Set it before Rhino starts for the panel-launched sidecar. |
| `TRIPO_HOST_PID` | MCP server only | Required when more than one live Rhino bridge exists. Must be a positive integer. |
| `TRIPO_LOCAL_DATA_DIR` | Rhino and sidecar / MCP server | Optional absolute, private, stable local path. Every participating process must resolve exactly the same value. |
| `TRIPO_SIDECAR_PATH` | Rhino process only | Optional absolute development override to the matching `Tripo.Rhino.Mcp.dll` or native apphost. Normal deployment uses the copied install-relative `sidecar/`; set an override before Rhino starts. |

For the panel path, use its **API key…** dialog: leave **Save in this user's OS
credential store** checked for macOS Keychain or Windows Credential Manager, or
uncheck it for sidecar-process memory only. The UI reports only
`environment`, `session`, `store`, or `none`, never the key. On unsupported
platforms only, persistence uses a reported private-file fallback. For the MCP
path, prefer the client's credential store or inherited process environment.
An `env` object may store the key as plaintext. `${NAME}` interpolation is
client-specific and must not be assumed. This repository does not load `.env`
files. Replacing the effective key changes paid-operation identity and can make
same-UUID recovery fail closed for unfinished panel or MCP operations. Reconcile
every unfinished paid UUID before rotating a key.

The safest local-data configuration is to leave `TRIPO_LOCAL_DATA_DIR` unset in
both processes. They then share the current user's default local
application-data directory under `TripoMCP`.

If you customize the directory:

- set the identical value before starting Rhino and before the MCP client
  launches the server;
- use an absolute, private path on a stable local filesystem;
- do not use NFS/SMB; and
- do not move or delete its `bridges`, `controls`, `staging`,
  `image-transfers`, `operations`, `secrets`, or `ui-recovery` content during
  recovery.

Setting this variable only in the MCP client makes the server and Rhino use
different discovery/staging roots and prevents a correct bridge connection.

`image-transfers` may contain a private copy of a Grasshopper- or MCP-selected
PNG/JPEG until a durable file-token or upload-ambiguity checkpoint exists. It
is not an import allowlist and must be preserved with the journal during
recovery.

## Use the Rhino panel

1. Start Rhino, open the target document, and run `TripoPanel`.
2. The per-document panel automatically connects to the exact active document.
   **Connect / Refresh** is available for an explicit refresh. If a workflow
   already owns state, a different document session is rejected rather than
   silently adopting the old task.
3. If no key is usable, paste one into **API key…** and choose persistent or
   session-only storage. Create keys at
   [Tripo Platform](https://platform.tripo3d.ai/api-keys).
4. Enter the prompt, face limit, and material preference. Click **Generate**.
   The panel displays a selectable durable operation UUID before showing a
   credit confirmation. Declining sends no paid request.
5. Click **Refresh generation** until the task reports `success`.
6. Click **Convert to OBJ**. This creates a different UUID and requires a
   separate credit confirmation. Refresh conversion until `success`.
7. Choose object name, `native`/`mesh`/`instance`, and whether to apply baked
   diffuse materials; then click **Import into Rhino**.

The panel does not auto-poll or claim to cancel remote tasks. After a lost
response, the stage first requires **Refresh**. A retry becomes available only
when the paid-operation journal says creation can resume, and the button is
explicitly labelled **Retry same UUID**. Once a durable task or import
receipt is known, the stage action is disabled instead of looking like a new
request. **New workflow** is disabled while any dispatch is unresolved, and
API-key mutation is disabled while a paid dispatch is unresolved. Hiding or
closing a tab does not cancel the workflow while Rhino retains that panel
instance. A durable `request_rejected` receipt is not unresolved: generation
rejection clears generation and downstream stages, while conversion rejection
clears conversion/import and preserves successful generation. Correct the
credential and prepare a new UUID for the rejected stage.

Before dispatch, the shared state layer atomically writes a private recovery
hint under
`<TRIPO_LOCAL_DATA_DIR>/ui-recovery/rhino/<recovery-id>.json` (or the
default local-data root). The hint contains UUIDs, durable task IDs when known,
and the minimum import retry parameters. It does not contain the prompt, API
key, Authorization header, URL, or arbitrary path.

The independently identified hint remains through a successful import until
the live workflow is explicitly reset. Closing the document, disposing an
inspector, exiting Rhino, or crashing does not cancel the remote task. The next
panel shows stale recovery IDs and blocks
new workflows. A hint owned by another Rhino process is conservatively
blocking because panel-session liveness is not guessed across processes.
Recovery must happen in that owner process, or after its exit can be verified.
API-key changes also refuse any unresolved recorded UI paid hint, unverifiable
foreign-owner record, or invalid recovery storage from Rhino or Revit. A
root-global UI intent lease serializes cross-panel credential-recovery scans,
key-mutation requests, and paid dispatch calls. A separate private sidecar
execution lease holds the actual key mutation and each paid UI or standalone
MCP workflow from credential-derived fingerprinting through its durable task,
definitive `request_rejected`, or ambiguous-outcome journal checkpoint, even if
the UI pipe disconnects. Only one key mutation or paid
create/convert is admitted at a time; retry a contending request with the same
UUID after the active operation checkpoints. **Review recovery…** automatically
queries only local `operation_status`; it does not resend a paid call or import.
The dialog distinguishes durable tasks, same-UUID recovery, ambiguous outcomes,
and missing local evidence, then asks for an explicit checkbox confirmation
before archiving the local notice. Reconcile imports in the original document
and check Tripo task and billing history whenever local evidence is missing or
ambiguous. The dialog binds both the recovery files and the full local journal
receipts or explicit unavailable results into the displayed snapshot. Before
archival, the plug-in holds the same cross-UI/MCP execution lease used by paid
work and key mutation, then queries and compares those receipts twice again. A
changed set or status is refused, and an operation still in progress remains
blocked. If the panel also owns current workflow state, **Reload and review all
work…** preserves
dispatched IDs as recovery evidence, clears only unsent setup, and reviews the
combined set. After manually repairing an invalid file, use **Refresh recovery
status** directly.
Invalid, oversized, unknown-schema, non-private Unix, or symlinked hints remain
blocked for manual inspection. The paid-operation journal—not the hint—is
authoritative. The Eto panel remains text-only; local-image controls are
currently available through the optional Grasshopper components and MCP tools.

## Use the Grasshopper components

1. Open the target Rhino document and an associated interactive Grasshopper
   definition. Paid actions refuse headless, Player, and compiled-command
   contexts.
2. Configure the sidecar key through `TripoPanel` or a component's
   **Open Tripo panel / API key…** menu item.
3. Place **Tripo Text Task** or **Tripo Image Task**. Right-click its explicit
   create action, review the durable UUID and cost warning, and confirm only if
   intended. Image mode accepts one local PNG/JPEG of 1–20,000,000 bytes.
4. Manually refresh the generation status to `success`.
5. Connect its task ID to **Tripo Task to Mesh**. Right-click
   **Create OBJ conversion…**, confirm the separate possible charge, and
   manually refresh/load after success.
6. Use the resulting Grasshopper `Mesh` in the definition or bake it through
   normal Grasshopper UI if desired.

Canvas recompute and loading `.gh` never dispatch paid work. The mesh is scaled
from meters into the associated Rhino document units and does not create a
Rhino object or Undo record. `With Materials=true` retains validated UVs and
material names where present, but does not automatically bind Rhino/PBR
materials. See the [full GHA guide](./src/Tripo.Rhino.Grasshopper/README.md).

## Start and verify MCP

For the optional MCP path:

1. Install the plug-in.
2. Start Rhino and open the target document.
3. Wait for the bridge-ready message and note its PID.
4. Start or restart the MCP client so it launches `Tripo.Rhino.Mcp`.
5. Confirm that the client lists the eight tools below.
6. Call `tripo_host_context`.

A successful context receipt proves that the MCP server reached Rhino. It
returns the host version, process ID, document title, document units,
capabilities, and an ephemeral `documentSessionId`.

There is no HTTP endpoint or standalone `--health` command. When run directly,
the MCP server waits for a stdio handshake.

If exactly one Rhino bridge is live, it is selected automatically. Multiple
live Rhino instances fail closed with `host_ambiguous`; set `TRIPO_HOST_PID` to
the PID printed by the intended Rhino process and restart the MCP server.

## MCP tools

The MCP front door exposes the same shared workflow as these eight tools:

| Tool | Main arguments | Effect |
| --- | --- | --- |
| `tripo_host_context` | none | Reads the connected Rhino process and exact active-document session. No Tripo API call. |
| `tripo_task_status` | `taskId` | Queries one existing Tripo task. |
| `tripo_operation_status` | `operationId` | Reads a durable local paid-operation record. No Tripo or Rhino call. |
| `tripo_create_text_task` | `prompt`, `faceLimit`, `withMaterials`, `documentSessionId`, `operationId`, `confirmExternalCost` | Creates one text-to-model task. `withMaterials=true` requests textured PBR generation (`texture`/`pbr`); `false` stays geometry-only. May consume credits. |
| `tripo_stage_local_image` | `localImagePath` | Validates and privately snapshots one local PNG/JPEG and returns an opaque descriptor. No Tripo call. |
| `tripo_create_image_task` | `transferId`, `sha256`, `byteLength`, `mediaType`, `faceLimit`, `withMaterials`, `documentSessionId`, `operationId`, `confirmExternalCost` | Uploads one staged image and creates an image-to-model task with durable upload/generation checkpoints. Copy the four descriptor fields exactly from `tripo_stage_local_image`. May consume credits. |
| `tripo_create_obj_conversion` | `sourceTaskId`, `faceLimit`, `withMaterials`, `documentSessionId`, `operationId`, `confirmExternalCost` | Creates one OBJ conversion. `withMaterials=true` requests an OBJ bundle with a baked-diffuse MTL and image textures (`bake=true`); `false` converts geometry only. May consume credits. |
| `tripo_import_obj_task` | `conversionTaskId`, `name`, `documentSessionId`, `operationId`, `importMode` (default `native`), `applyMaterials` (default `false`) | Downloads, validates, and imports a successful OBJ conversion as one Rhino mesh or block instance. |

Input boundaries:

- `prompt`: 1–1024 characters;
- `faceLimit`: 500–200000;
- imported object `name`: 1–128 characters;
- task IDs begin with `task_`;
- `documentSessionId` must be the exact UUID from `tripo_host_context`;
- each `operationId` is a caller-generated UUID;
- `importMode` is `native`, `mesh`, or `instance`; this build rejects `family` with
  `import_mode_unsupported`. `native` resolves to `instance`.
- `applyMaterials=true` fails closed if the converted bundle has no MTL, and mesh
  mode additionally refuses it when the OBJ uses more than one `usemtl`
  material slot.

`confirmExternalCost=true` is valid only after the user explicitly accepts the
possible external charge.

## Typical workflow

1. Call `tripo_host_context` and retain its exact `documentSessionId`.
2. Choose one generation branch:
   - text: generate UUID A and, after explicit cost confirmation, call
     `tripo_create_text_task`;
   - local image: call `tripo_stage_local_image`, then generate UUID A and,
     after explicit cost confirmation, call `tripo_create_image_task`, copying
     the returned descriptor's `transferId`, `sha256`, `byteLength`, and
     `mediaType` into the four same-named arguments.
3. Poll its returned task ID with `tripo_task_status` until it reports
   `success` or a terminal failure. Stop on `failed`, `cancelled`, `banned`, or
   `expired`.
4. Generate UUID B and, after a second explicit cost confirmation, call
   `tripo_create_obj_conversion`.
5. Poll the returned conversion task until it reports `success` or a terminal
   failure.
6. Generate UUID C and call `tripo_import_obj_task`, choosing `importMode` and
   `applyMaterials` as needed.
7. Inspect the returned receipt and the created Rhino mesh or block instance.
   One Rhino Undo operation should revert a committed import.

For a material-bearing import, set `withMaterials=true` on both paid creation
stages and `applyMaterials=true` on import. Keep all three false for a
geometry-only workflow.

The generation, OBJ conversion, and host import must use three different
caller-owned UUIDs. Do not switch or close the active document during the
workflow; the document session is rechecked before paid operations, before the
download/import, and inside the Rhino UI-thread mutation.

If a paid-stage response is lost, first use `tripo_operation_status` to inspect
its local record. Retry with the original UUID, identical explicit arguments,
API key, and document session only when the journal says creation can resume; a
text-task retry must also keep the same effective model.

If an operation is `outcome_unknown`, do not automatically resend it or create
a replacement UUID. Preserve the journal and inspect Tripo task or billing
history manually.

If an operation is `request_rejected`, the provider definitively rejected the
request before creating a task. Correct the credential and prepare a new UUID;
do not retry the rejected UUID.

Image creation separately checkpoints upload and generation. A durable
`file_token` resumes generation without another upload. An ambiguous upload or
generation records its stage and refuses automatic resend; preserve
`image-transfers/` and the journal until manual reconciliation.

Import recovery is deliberately different. Reuse the import UUID, conversion
task and artifact content, name, resolved mode, and materials flag. If Rhino
restarted, reopen the same target document, call `tripo_host_context`, and pass
the new `documentSessionId`; the host fingerprint excludes that ephemeral
session ID while the active-session check still fails closed. For
`already_exists` to survive the application restart, save the `.3dm` after the
original import; if unsaved changes were lost, the retry can commit the import
again because no persisted object remains.

## Rhino import behavior

- The converted OBJ (and, when present, its MTL plus PNG/JPEG textures) is staged
  as a content-addressed bundle, every entry is SHA-256 and byte-length checked
  against its manifest, then parsed and geometrically validated before mutation.
  A bundle keeps at most 32 entries, each at most 128 MiB, with a 256 MiB
  aggregate limit.
- The first release treats `auto_size=true` output as meters.
- Y-up, right-handed input is transformed to Rhino Z-up and scaled into the
  active document's unit system.
- Two import modes: `mesh` creates one Rhino mesh object; `instance` creates one
  block definition (one sub-mesh per material slot) plus one `InstanceObject`.
  `importMode=native` resolves to `instance`. `AddMesh`/`AddInstanceObject`,
  object attributes, the undo record, and redraw all run on the Rhino UI thread.
- `applyMaterials=true` applies the baked diffuse color and, when present, the
  diffuse texture via `TextureCoordinates` plus a Rhino render `Material`; mesh
  mode refuses more than one OBJ `usemtl` slot rather than collapsing colors
  onto a single mesh, so a multi-slot bundle needs `instance` mode. Texture
  validation fails closed with the typed errors described below.
- The import UUID and canonical import-identity fingerprint are stored in
  object attributes. The fingerprint intentionally excludes the ephemeral
  `documentSessionId`. The UUID and fingerprint are stored on the mesh object
  in `mesh` mode; on the `InstanceObject` and on every geometry member inside
  the block definition in `instance` mode. A block definition left over from a
  crashed import (created but never referenced) is reconciled by verifying its
  members' fingerprint before adding the missing instance.
- Retrying after a committed identical import returns the existing object
  rather than creating a second one. Crash reconciliation may add a missing
  instance, but does not duplicate its verified block definition.
- Reusing an import UUID with different arguments (including a different
  resolved `importMode` or `applyMaterials`) fails with an idempotency conflict.
- Rhino must be idle enough to create a dedicated undo record.

## Import receipt

The host receipt reports `createdId` (the Rhino mesh or `InstanceObject` GUID),
`transactionStatus` (`committed` or `already_exists`), the resolved
`importMode`, geometry counts, and prepared `materialCount`/`textureCount`.
`savedFamilyPath` is always `null` for Rhino. This is evidence of the
mutation/idempotency path, not visual-rendering acceptance.

## Troubleshooting

### `host_unavailable`

Check that Rhino is running, the plug-in loaded, the bridge-ready message
appeared, both processes use the same OS account, any `TRIPO_HOST_PID` is
correct, and `TRIPO_LOCAL_DATA_DIR` is either unset on both sides or identical
on both sides. Also replace the complete plug-in and MCP outputs from the same
revision and restart both processes: a mixed host-control deployment is
normally ignored during discovery and appears as `host_unavailable`.

### `host_ambiguous`

More than one Rhino bridge is live. Set `TRIPO_HOST_PID` to the intended Rhino
PID and restart the MCP server.

### API-key errors

Set the real key in the MCP server environment. Supply only the key characters:
do not add `Bearer`, whitespace, control characters, or literal quote
characters. JSON configuration still requires quotes around the string; those
delimiters are not part of the key. `tripo_host_context` and local
operation-status reads can work without a key; Tripo API tools cannot.

### `document_unavailable` or document-session errors

Open the intended Rhino document and call `tripo_host_context` again. If the
document was switched, closed, or reopened, paid-operation identity does not
move to the new session. For a paid stage already sent or missing a response,
first call `tripo_operation_status`. Preserve its original UUID and identity
unless the journal reports definitive `request_rejected`; in that state,
correct the credential and prepare a new UUID. An import retry may use the new
session only after reopening the same target document and keeping the original
import UUID, conversion task and content, name, resolved mode, and materials
flag.

### `host_busy` or `undo_unavailable`

Wait for the current Rhino command or undo activity to finish, then retry the
same import UUID with identical arguments.

### The MCP process does not start

Confirm that a .NET 8 runtime is installed, the command and assembly paths are
absolute, the complete MCP output directory is present, and the client can
resolve the configured `dotnet` executable.

### `outcome_unknown`

The remote paid request may already have succeeded. Query
`tripo_operation_status`, preserve the journal, and inspect Tripo task/billing
history. Do not send another paid request automatically. A killed process can
leave a readable `dispatching` record; acquiring that same operation converts
it to `outcome_unknown` without resending.

### Material or bundle errors

Use `withMaterials=true` during OBJ conversion before importing with
`applyMaterials=true`. A texture entry referenced by the MTL but absent from
the bundle fails as `mtl_invalid`; a missing staged file fails as
`artifact_missing`; a byte length or SHA-256 mismatch fails as
`artifact_hash_mismatch`; and a Rhino bitmap-binding failure reports
`mtl_invalid`. In `mesh` mode, more than one OBJ `usemtl` slot requires
switching to `instance`.

## Current limitations

- One Rhino mesh (`mesh` mode) or one block instance (`instance` mode) per
  import; no placement controls, and mesh mode carries at most one material.
- The Eto panel currently supports text-to-3D only. Local PNG/JPEG image
  selection/upload/create is available through the optional GHA and MCP;
  panel image mode, WebP, and public URL input remain open.
- Default text-generation model `v3.1-20260211`; `TRIPO_MODEL` can select
  another syntactically valid identifier, and changing it changes text-task
  paid-operation identity.
- Materials are baked diffuse only (OBJ `Kd`/`d`/`Tr` color/alpha plus one
  `map_Kd` texture per slot): no true PBR channels and no native GLB import.
  Text generation disables quad output; OBJ conversion disables quad output
  and animation.
- The GHA is scalar-only and interactive-only: no Grasshopper Player, headless
  execution, automatic polling, automatic material binding, or one-call paid
  workflow.
- No Yak package, installer, signing, notarization, or automatic update.
- Production HTTP connections intentionally do not use system proxies.
- No completed real-host acceptance on Windows or macOS.

See [Architecture](./docs/ARCHITECTURE.md),
[Materials design](./docs/MATERIALS-DESIGN.md),
[Security](./docs/SECURITY.md), and
[Testing and evidence](./docs/TESTING.md) for the detailed trust and
acceptance boundaries.

Repository provenance and reference decisions are recorded in
[Migration](./docs/MIGRATION.md) and
[Blender reference](./docs/BLENDER-REFERENCE.md). Candidate packaging is
documented under [`packaging/`](./packaging/README.md).

## License

No distribution license has been selected. Until one is added, do not treat
this source as licensed for redistribution. The Blender reference informed
repository and product structure only; no upstream source code was copied.

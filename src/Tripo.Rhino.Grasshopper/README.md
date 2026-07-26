# Tripo Grasshopper components

English | [简体中文](./README.zh-CN.md) | [Rhino adapter](../../README.md)

`Tripo.Rhino.Grasshopper.gha` is the optional Rhino 8 Grasshopper surface for
tripo-rhino. It lets an interactive Grasshopper canvas explicitly create a Tripo
text-to-model or local-image-to-model task, explicitly create its separate OBJ
conversion, and output the validated result as a Grasshopper `Mesh`.

It is not an official Tripo, McNeel, or Grasshopper product.

The GHA does not contain another Tripo client or credential store. It borrows
the sidecar, API-key owner, paid-operation journal, recovery records, and exact
Rhino document session from the matching installed `Tripo.Rhino.rhp`.

> **Evidence boundary:** this source builds against the pinned Rhino 8
> RhinoCommon and Grasshopper packages. Portable tests cover shared image
> staging, upload/generation recovery, OBJ staging, and journal behavior.
> Compilation does not prove that a real Rhino/Grasshopper installation loaded
> the GHA, resolved all assemblies, displayed the components, or rendered the
> output correctly. Windows and macOS interactive acceptance is still required.

## Components

All three appear under **Tripo → Generate**:

| Component | Inputs | Outputs | Explicit menu actions |
| --- | --- | --- | --- |
| **Tripo Text Task** | `Prompt`, `Face Limit`, `With Materials` | task/status/progress/credits/operation/message | **Create text task…**, **Refresh task status** |
| **Tripo Image Task** | `Face Limit`, `With Materials` | task/status/progress/credits/operation/image SHA/message | **Choose image and create task…**, **Refresh task status** |
| **Tripo Task to Mesh** | `Source Task ID`, `Face Limit`, `With Materials` | `Mesh`, conversion task/status/progress/credits/operation/material names/message | **Create OBJ conversion…**, **Refresh conversion / load mesh** |

Every input is scalar. Lists and data-tree batching are refused because one
component owns one recoverable paid-operation identity.

Canvas recompute, `SolveInstance`, opening a saved `.gh`, and deserialization
never create or convert a model. Paid work starts only from a component
context-menu action followed by an explicit cost confirmation that displays
the durable operation UUID.

## Requirements

- Rhino 8 with Grasshopper.
- The complete matching `Tripo.Rhino.rhp` output installed and loaded at Rhino
  startup. Its `sidecar/` directory must remain beside the host plug-in.
- .NET 8 runtime for the sidecar; the .NET 8 SDK includes it.
- A Tripo v3 API key, configured through `TripoPanel` or inherited by the
  sidecar as `TRIPO_API_KEY`.
- Rhino, the GHA, and sidecar running as the same operating-system user.
- The GHA, Rhino plug-in, and sidecar built from the same repository revision.

Grasshopper Player, headless execution, and compiled-command execution are
deliberately unsupported for paid actions.

## Build

From the repository root:

```bash
dotnet restore src/Tripo.Rhino/Tripo.Rhino.csproj
dotnet restore src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj

dotnet build src/Tripo.Rhino/Tripo.Rhino.csproj \
  --configuration Release \
  --no-restore

dotnet build src/Tripo.Rhino.Grasshopper/Tripo.Rhino.Grasshopper.csproj \
  --configuration Release \
  --no-restore
```

Important outputs:

```text
src/Tripo.Rhino/bin/Release/net7.0/
src/Tripo.Rhino.Grasshopper/bin/Release/net7.0/Tripo.Rhino.Grasshopper.gha
```

The GHA output directory also contains project-reference artifacts. It is not a
standalone plug-in package and does not replace the complete Rhino host output.

## Install

1. Close Rhino.
2. Install the complete Release Rhino host output, including
   `Tripo.Rhino.rhp`, `Tripo.Bridge.dll`, `Tripo.HostUi.dll`, and its complete
   `sidecar/`, by following the
   [Rhino adapter installation guide](../../README.md#install-the-rhino-plug-in).
3. Start Rhino once and verify that the command history reports:

   ```text
   [Tripo] Rhino bridge and Eto panel ready for PID <process-id>.
   ```

4. Open Grasshopper. Use Grasshopper's **File → Special Folders → Components
   Folder** command (wording can vary by Rhino service release) to open the
   active GHA assembly directory.
5. Close Rhino again and copy the same-revision
   `Tripo.Rhino.Grasshopper.gha` into that directory.
6. Restart Rhino and Grasshopper. Do not register the GHA from a `bin/`
   directory that may be deleted by `dotnet clean`.
7. Confirm that **Tripo → Generate** contains all three components.

This is a manual development installation. There is no Yak package, installer,
signing, notarization, or automatic update mechanism. McNeel documents
Grasshopper package assembly folders in the
[Grasshopper Folders API](https://developer.rhino3d.com/api/grasshopper/html/T_Grasshopper_Folders.htm)
and future `.gha` packaging in its
[Yak guide](https://developer.rhino3d.com/en/guides/yak/creating-a-grasshopper-plugin-package/).

Copying only the `.gha` without first installing and loading the matching
Rhino host plug-in is unsupported and should fail closed.

## Configure the API key

1. Start Rhino and open a target `.3dm`.
2. Run `TripoPanel`, or right-click any Tripo component and choose
   **Open Tripo panel / API key…**.
3. Use **API key…** to set a session-only key or store it in macOS Keychain /
   Windows Credential Manager.

The GHA never receives or serializes the key. `TRIPO_API_KEY` in the sidecar
environment takes precedence over session and stored keys. Reconcile unfinished
paid UUIDs before rotating the effective key.

## Text-to-mesh workflow

1. Open the target Rhino document and a Grasshopper definition associated with
   that document.
2. Place **Tripo Text Task** and provide exactly one prompt, face limit
   (500–200000), and material choice.
3. Right-click it and choose **Create text task…**.
4. Review the displayed UUID and cost warning. Choosing **No** sends no paid
   request.
5. Use **Refresh task status** manually until the task reports `success` or a
   terminal state. There is no automatic polling.
6. Connect `Task ID` to **Tripo Task to Mesh**. Use the same conversion face
   limit/material intent you want in the OBJ.
7. Right-click the mesh component, choose **Create OBJ conversion…**, and
   accept the independent conversion cost only if intended.
8. Use **Refresh conversion / load mesh** manually. When conversion succeeds,
   the component stages, validates, projects, and publishes the GH mesh.

Generation and conversion use separate UUIDs. Preserve both if a response is
lost.

## Image-to-mesh workflow

1. Place **Tripo Image Task** and set one face limit/material choice.
2. Right-click it and choose **Choose image and create task…**.
3. Select one local PNG or JPEG from 1 through 20,000,000 bytes.
4. Review the UUID and cost warning before confirming.
5. Refresh manually to `success`.
6. Connect its `Task ID` to **Tripo Task to Mesh**, then follow steps 7–8 of the
   text workflow.

The source file path and filename do not cross the sidecar protocol and are not
saved in `.gh`. After selection, a private snapshot is stored under:

```text
<TRIPO_LOCAL_DATA_DIR>/image-transfers/
```

The component-owned private `.gh` state may save its opaque transfer UUID,
SHA-256, byte length, media type, operation UUID/fingerprint, durable task IDs,
and bounded status/progress/credits. It does not save the source path, image
bytes, Tripo `file_token`, API key, or transient UI error text.

Normal Grasshopper inputs and upstream data follow Grasshopper's own
serialization rules. A Text component's prompt or persistent default may
therefore be present in the enclosing `.gh`; treat the definition as a
potentially sensitive model file.

Do not delete `image-transfers/`, `operations/`, or `ui-recovery/` while an
image operation is unresolved. A durable upload token lets the same UUID resume
generation without re-uploading. An ambiguous upload or generation is recorded
as `outcome_unknown` and is never automatically resent.

## Mesh behavior

- The output is one Grasshopper `Mesh` value in the associated Rhino
  document's units.
- Tripo's meter-based Y-up/right-handed geometry is converted to Rhino
  Z-up/right-handed coordinates and scaled to document units.
- No Rhino object, layer, block, material, or Undo record is created. Baking is
  a separate ordinary Grasshopper user action.
- `With Materials=true` carries validated UVs and returns material names when
  present. It does not automatically bind a Rhino document material, texture,
  or PBR graph to the GH mesh.
- Changing source task, face limit, material flag, prompt, or image identity
  after an operation was prepared marks the saved result stale. The mesh is
  withheld from mismatched inputs; the old UUID is not reused.
- Removing a component stops UI/mesh publication. An already admitted local
  sidecar wait continues to its durable task-ID or ambiguity safety checkpoint;
  any remote task may also continue. Removal is not local or remote
  cancellation.

## Recovery

- Use **Retry same … operation…** only with the original unchanged inputs and
  UUID. The original local journal must still exist; it replays a durable task
  ID or fails closed instead of creating a replacement operation.
- Use the output `Operation ID` and `tripo_operation_status`/Tripo history to
  reconcile a lost response.
- Do not create a replacement UUID for `outcome_unknown`.
- A conflicting recovery hint blocks a new canvas paid action. Open
  `TripoPanel` and choose **Review recovery…**. The panel runs read-only local
  checks, shows the evidence and risks, and asks for an explicit checkbox
  confirmation only after manual reconciliation.
- Loading a saved definition only restores local IDs/status text. Use an
  explicit refresh; loading never calls Tripo.

## Current limitations

- One scalar item per input; no list/data-tree fan-out.
- Local PNG/JPEG only; no WebP, public URL, clipboard, or multiview input.
- No one-call generation/conversion workflow and no automatic polling.
- No Grasshopper Player, headless, compiled-command, or unattended paid mode.
- No automatic GH/Rhino material binding.
- No Yak package or automated installation.
- Real Rhino/Grasshopper Windows/macOS loading, UI, scale, and visual
  acceptance remain open gates.

See [ADR-0002](../../docs/adr/0002-recoverable-grasshopper-components.md),
[Architecture](../../docs/ARCHITECTURE.md),
[Security](../../docs/SECURITY.md), and
[Testing](../../docs/TESTING.md) for the normative safety and evidence
boundaries.

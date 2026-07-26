# ADR-0002: Explicit, recoverable Grasshopper components over the Rhino sidecar

- **Status:** Accepted
- **Date:** 2026-07-25
- **Deciders:** Project maintainers
- **Supersedes/clarifies:** ADR-0001 §3.3 for the detailed safety, recovery,
  image-transfer, mesh-value, and packaging contract of the optional Rhino
  Grasshopper surface
- **Affects:** `src/Tripo.Rhino.Grasshopper`, the Rhino host plug-in,
  host-control contracts, image-transfer staging, paid-operation recovery,
  Rhino documentation, testing, and packaging
- **Implementation:** Implemented in source. Portable bridge, host-control,
  workflow, MCP, and package-compilation evidence is maintained separately from
  real Rhino/Grasshopper loading and interactive acceptance. There is no Yak
  package yet.

---

## 0. 中文摘要（非规范正文）

Rhino 适配器增加一套可选 Grasshopper GHA。它不是第三套 cloud、credential 或
journal 路径，而是 ADR-0001 中 **host-side front door** 的另一个 UI surface：

- `Tripo Text Task`：显式创建并检查一个 text-to-model task；
- `Tripo Image Task`：显式选择一张本地 PNG/JPEG，上传并创建
  image-to-model task；
- `Tripo Task to Mesh`：显式创建独立付费的 OBJ conversion，随后把经校验的结果
  作为 Grasshopper `Mesh` value 输出。

普通 `SolveInstance`、canvas recompute、打开 `.gh` definition 或恢复序列化状态
绝不能发起付费请求。每个付费阶段只能由本次 Rhino 会话中的 component context-menu
动作触发，必须显示并确认独立 UUID。Grasshopper Player、headless 与 compiled
command 模式一律 fail closed。

GHA 复用已加载 Rhino `.rhp` 的 sidecar、credential owner、paid journal、recovery
store 与精确 document-session 绑定。源图路径、图像 bytes、Tripo `file_token` 和
API key 不写入 GHA 自有 recovery fields；这些 fields 只保存有限的 opaque transfer
identity、恢复 ID 与 bounded status/progress/credits。普通 GH input（包括 prompt）
仍可能由 Grasshopper 写入 `.gh`，因此 definition 应视为可能敏感。从 `.gh` 恢复出的
operation identity 只能要求原 journal 继续恢复，不能授权新的付费请求；该
fail-closed 字段最初由端到端 host-control protocol v2 引入，当前 runtime 因新增
credential-rejection recovery 语义而要求 protocol v3。Task-to-Mesh 不 bake、不
创建 Rhino object，也不建立 Undo record。

规范条款以英文正文为准。

---

## 1. Context

ADR-0001 established two peer front doors over one sidecar-owned execution
core: host-side interaction for AEC users and stdio MCP for agentic clients.
The first Rhino host-side slice was an Eto panel. Grasshopper users, however,
need generation and conversion stages that can be wired into a definition and
produce a native Grasshopper mesh value without using an external MCP client or
mutating the Rhino document.

Grasshopper recomputes definitions for many reasons. A conventional boolean
`Run` input, a saved `true` value, or work performed from `SolveInstance` could
therefore repeat a credit-consuming request without a contemporaneous user
gesture. Definitions can also run in Grasshopper Player, headless automation,
or compiled-command contexts where a cost dialog and exact canvas ownership
cannot be trusted.

Image creation adds a second ambiguity boundary. Tripo documents a local file
upload followed by image-to-model generation, but does not document a remote
idempotency key for either POST. A lost upload response must not cause an
automatic re-upload, and a durable `file_token` must allow generation to resume
without reading or sending the local image again.

## 2. Decision

### 2.1 Grasshopper remains part of the host-side front door

The optional GHA is a Rhino host-side UI adapter. It shall:

- borrow the sidecar owned by the loaded `Tripo.Rhino.rhp`;
- use the same host-control protocol, credential resolution, paid-operation
  journal, recovery records, staging roots, and Tripo client as the Eto panel
  and MCP surface;
- never read, store, or send the Tripo API key itself;
- never introduce a third cloud client, journal, recovery authority, or
  arbitrary-path bridge method; and
- require the GHA and `.rhp`/sidecar to come from the same repository revision.

The GHA is optional. It does not impose Grasshopper on Revit or MCP users and
does not replace the Rhino Eto panel.

### 2.2 Three staged components

The GHA exposes these components in **Tripo → Generate**:

| Component | Responsibility | Paid action |
| --- | --- | --- |
| `Tripo Text Task` | Hold one scalar prompt/options set; explicitly create and manually inspect one text-to-model task | Text generation |
| `Tripo Image Task` | Explicitly choose one local PNG/JPEG; stage a private snapshot; explicitly upload and create one image-to-model task | Image upload/generation operation |
| `Tripo Task to Mesh` | Accept one successful text/image task ID; explicitly create and inspect an OBJ conversion; stage and project the result to a Grasshopper mesh | OBJ conversion |

Generation and conversion use different caller-owned UUIDs and separate cost
confirmations. There is no one-click paid workflow, hidden auto-poll, or
automatic resend.

### 2.3 Paid work requires a live explicit menu action

`SolveInstance`, ordinary recompute, definition load, deserialization, status
display, and mesh output publication shall not dispatch a paid request.

A paid request may start only from a component context-menu action in the
current interactive session. Before dispatch the component shall:

1. verify scalar item inputs and reject list/tree batching;
2. capture the exact Grasshopper document identity and associated Rhino
   document serial/unit system;
3. connect to the `.rhp` sidecar and match its exact Rhino
   `documentSessionId`;
4. display the durable operation UUID and an explicit external-cost
   confirmation; and
5. write the recovery intent before the host-control call.

Retries reuse the same UUID and unchanged identity. Changed inputs make the
prior result stale and must never repurpose its UUID. A different paid request
uses a new component or a deliberately reset future workflow.

Any operation identity deserialized from a `.gh` definition is recovery-only.
It must set `RequireExistingOperation` and atomically prove that the original
local journal exists with the same identity. A serialized dispatch flag is not
authority for a fresh request. Because an older sidecar would ignore that new
request field, host-control protocol v1 was rejected when this contract first
required protocol v2 end to end. The current runtime requires protocol v3 so an
older sidecar cannot omit credential-rejection recovery semantics.

### 2.4 Unsupported execution contexts fail closed

Paid component actions are refused when:

- Grasshopper is running headless;
- the definition is running as Grasshopper Player or a compiled command;
- no exact Grasshopper document or associated Rhino document can be proven;
- the captured document/canvas binding changes before a checkpoint or mesh
  publication; or
- another credential/recovery conflict is active.

Loading a saved definition in any context remains non-billable. Manual refresh
may query an already known task, but does not create a replacement operation.

### 2.5 Image transfer and journal rules

The first image slice accepts only a local file whose bytes prove PNG or JPEG,
from 1 through 20,000,000 bytes. File extension alone is not trusted; WebP and
public URL input are outside this slice.

After a live menu action selects a file, the Rhino process copies a private
snapshot beneath:

```text
<TRIPO_LOCAL_DATA_DIR>/image-transfers/
```

The host-control request carries only an opaque transfer UUID, SHA-256, byte
length, and detected media type. The sidecar reopens only the derived transfer
path, rejects symlinks/reparse points and identity mismatches, copies the
validated bytes into an immutable bounded in-memory snapshot, and uploads that
snapshot with a generic `input.png` or `input.jpg` filename. The source path and
source filename do not cross the protocol.

One image-operation UUID covers two journaled dispatch boundaries:

```text
prepared
  → image_upload_dispatching
  → image_file_token_persisted
  → image_generation_dispatching
  → task_id_persisted
```

An interrupted or ambiguous upload becomes
`outcome_unknown(failureStage=upload)` and is never automatically uploaded
again. An interrupted or ambiguous generation becomes
`outcome_unknown(failureStage=generation)` and is never automatically
generated again. Once the file token and exact generation fingerprint are
durable, a same-UUID retry may resume generation without re-uploading.

The GHA's component-owned private recovery payload may serialize the transfer
UUID, image SHA-256, byte length, media type, operation UUID, request
fingerprint, durable task IDs, and bounded status/progress/credits needed for
local recovery. That payload shall not serialize:

- source path or source filename;
- image bytes;
- Tripo `file_token`;
- API key, Authorization header, a duplicate copy of the prompt, or signed URL.

This does not claim that the enclosing `.gh` file is prompt-free. Grasshopper
may serialize normal component inputs, persistent defaults, and upstream data,
including a text prompt, under its own definition format. Users shall treat the
whole `.gh` file as potentially sensitive.

The staged image may remain until the file token or an upload
`outcome_unknown` checkpoint is durable. Users must preserve
`image-transfers/`, `operations/`, and `ui-recovery/` while recovering.

### 2.6 Task-to-Mesh is a value path, not an import path

`Tripo Task to Mesh` reuses task status, paid OBJ conversion, download,
content-addressed bundle staging, parsing, hash/length validation, and geometry
validation. It uses a stage-only sidecar method and constructs a
`Rhino.Geometry.Mesh` in the GHA process.

The component:

- transforms Tripo Y-up, right-handed meter output to Rhino Z-up and the
  associated document units;
- rejects non-finite values, invalid indices, invalid UV counts, degenerate or
  invalid projected meshes;
- publishes one Grasshopper mesh value only after the original
  canvas/document/input binding is revalidated;
- creates no Rhino document object, block, layer, material, or Undo record; and
- may expose validated UVs and material names as metadata, but does not
  automatically bind PBR or Rhino document materials.

The existing `tripo_import_obj_task` path remains the explicit document
mutation path.

### 2.7 Recovery and component lifetime

The GHA uses the same root-global UI intent lease, sidecar credential/workflow
execution lease, paid-operation journal, and panel recovery store as other
host-side actions. A conflicting unresolved record blocks a new paid UUID.

Removing a component or closing a definition does not cancel an already
admitted sidecar wait. That work continues to its durable task-ID or ambiguity
safety checkpoint, and no remote cancellation is claimed. A durable or
ambiguous journal checkpoint cannot be erased by component lifetime; UI and
mesh publication are discarded if the component or binding no longer exists.

Saved IDs are recovery aids, not proof that remote work is complete. After
load, the component shows saved local state and waits for an explicit refresh.

## 3. Consequences

### Positive

- Grasshopper users can compose text/image generation with downstream GH logic
  while retaining the same paid-operation and credential boundary.
- Canvas recompute and saved definitions are non-billable by construction.
- Image upload ambiguity is separated from image generation ambiguity.
- Mesh output avoids unintended Rhino document mutation and can be baked later
  through normal Grasshopper user action.

### Costs and limitations

- A complete result requires two explicit paid stages and manual refreshes.
- Only one scalar request is supported per component; no data-tree fan-out.
- Image input is local PNG/JPEG only.
- No Grasshopper Player, headless, compiled-command, automatic polling,
  automatic material binding, or one-call workflow is supported.
- The GHA depends on a matching installed Rhino plug-in and sidecar.
- Manual `.gha` installation is currently required; no Yak package, signing,
  installer, or update mechanism exists.

## 4. Verification boundary

Portable tests and compilation must cover at least:

- image signature/size/symlink/hash/length boundaries;
- upload-before-generation ordering and generic multipart metadata;
- durable token resume without re-upload;
- stage-specific `outcome_unknown` no-resend behavior;
- old journal checksum compatibility and UUID/identity conflicts;
- eight-tool MCP discovery/schema;
- stage-only OBJ loading and mesh projection invariants where extractable; and
- GHA compilation against the pinned Rhino/Grasshopper SDK packages.

Those gates do not prove that Rhino loaded the `.rhp`/`.gha`, that components
appear on a real canvas, that modal confirmations work on Windows/macOS, or
that Rhino renders the resulting mesh/materials correctly. Those are explicit
real-host and visual acceptance gates in `docs/TESTING.md`.

## 5. Packaging rule

A manual deployment installs the complete Rhino host output, including its
matching `sidecar/`, before installing the same-revision `.gha` into a
Grasshopper assembly folder. Copying only the `.gha` is not a complete
deployment.

A future Yak package may bundle the GHA and its required managed dependencies,
but it must preserve the `.rhp`/sidecar credential and recovery architecture.
It must not copy an API key or create a second data root.

## 6. References

- ADR-0001: `0001-dual-front-doors-and-sidecar-credentials.md`
- Tripo file upload: <https://developers.tripo3d.ai/en/docs/files>
- Tripo image-to-model:
  <https://developers.tripo3d.ai/en/docs/generation-image-to-model/standard>
- McNeel task-capable component guidance:
  <https://developer.rhino3d.com/en/guides/grasshopper/programming-task-capable-component/>
- McNeel Grasshopper packaging:
  <https://developer.rhino3d.com/en/guides/yak/creating-a-grasshopper-plugin-package/>

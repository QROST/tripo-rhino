# ADR-0001: Dual front doors (host UI + MCP) and sidecar-only API credentials

> Migration note: this ADR originated in the combined repository. This fresh
> repository ships Rhino only, while preserving the shared protocol, journal,
> credential, recovery, and local-data identities described here.

- **Status:** Accepted; §3.3 Grasshopper scope partially superseded by ADR-0002
- **Date:** 2026-07-24
- **Deciders:** Project maintainers (open-source direction lock in working session)
- **Supersedes:** Nothing (extends the MCP-only vertical slice; does not revoke it)
- **Partially superseded by:** ADR-0002 for the optional Rhino Grasshopper
  host-side surface
- **Affects:** `src/Tripo.Mcp`, `src/Tripo.Bridge`, `src/Tripo.Rhino`,
  `tripo-revit/src/Tripo.Revit`, MCP server executables, `docs/ARCHITECTURE.md`,
  `docs/SECURITY.md`, root and adapter READMEs
- **Implementation:** In progress. Phases 0–2 are implemented in source. Local
  PNG/JPEG staging, multi-stage image journaling, MCP image creation, and the
  optional Rhino GHA are implemented in source; panel image controls, public
  URL/WebP input, signed packaging, and real Rhino/Revit/Grasshopper interactive
  acceptance remain open. Each evidence class must be reported separately.

---

## 0. 中文摘要（非规范正文）

开源目标下同时保留两套入口：

1. **AEC 宿主 UI**（Rhino Eto 面板 + Revit 对应简单 UI）：在宿主内填 prompt / 选图 /
   调参数、确认费用、看进度、导入当前文档。
2. **MCP 入口**：Agent 团队用 Cursor / Claude 等 stdio MCP client 逐步调用现有
   （及新增）tools。

**API key 只存在于本地 sidecar（及 OS 秘密存储 / 进程环境）**，永不写入 `.rvt` /
`.3dm`、宿主文档设置、journal、日志或 MCP/UI 回执。缺 key 时由宿主弹窗收集，并给出
获取说明；写入动作必须经由 sidecar。

**图生 3D 在范围内**：本地图先 upload 得 `file_token`，再走
`generation/image-to-model`；其后的 conversion / staging / import 与 text 路径共用。

规范条款以英文正文为准；本节仅便于阅读。

---

## 1. Context

### 1.1 Baseline when this ADR was accepted

The repository already ships a recoverable paid Tripo v3 vertical slice:

```text
MCP client
  ↕ stdio
host-specific MCP server          ← sole reader of TRIPO_API_KEY today
  ├─ durable paid-operation journal
  ├─ Tripo v3 HTTPS client
  └─ content-addressed OBJ(+MTL+texture) staging
  ↕ authenticated protocol-v2 current-user named pipe
Rhino 8 plug-in / Revit 2026 add-in
  ↕ exact document session
mesh / block instance (Rhino), mesh / family (Revit)
```

At acceptance time, evidence was portable tests and package compilation. There
was no installer, host panel, or image-to-model creation path. Downstream conversion already
accepts successful tasks whose type is `text_to_model`, `image_to_model`, or
`multiview_to_model`, but nothing in-tree creates the latter two.

### 1.2 Problem

Two distinct open-source audiences share one product surface:

| Audience | Need |
| --- | --- |
| AEC practitioners | Install, open Rhino/Revit, type a prompt or pick an image, get geometry in the active document — without learning MCP, Cursor, or stdio server config |
| Agentic AI teams | Keep a first-class MCP tool surface so agents can orchestrate stages, confirm cost, recover via journal UUIDs, and target an exact document session |

A single “thicken the plug-in until it calls Tripo” design would:

- place the API key inside the host process (and tempt storage in document-adjacent settings);
- duplicate paid-operation and staging logic per host;
- either abandon MCP or maintain two incompatible clouds of truth;
- couple long HTTPS / poll / download work to host UI-thread lifetime.

A single “MCP only” design leaves AEC users behind: current docs require building
DLLs, configuring an external MCP client, and chatting tools that have no host
toolbar or panel.

### 1.3 Non-goals for this ADR

- Commercial packaging, pricing, telemetry, or SaaS accounts beyond Tripo’s own API.
- Replacing Tripo with another generator.
- Native GLB/PBR import on Rhino (still declined; see `MATERIALS-DESIGN.md`).
- Multiview-to-model as a Day-1 creation mode (may be added later under the same
  credential and journal rules).
- Shipping an installer, Yak package, or code signing in the same change as this
  decision (packaging is a later phase; the ADR only constrains how packaging must
  place the sidecar binary and never the key).
- Exactly-once remote billing (local journal remains process-crash /
  at-most-one-automatic-dispatch among cooperating same-user processes).

### 1.4 Forces

1. **Usability for AEC** vs **agent composability** — both are first-class.
2. **Credential isolation** — key must not enter project files or host document
   serialization.
3. **Paid-operation honesty** — Tripo creation POSTs lack a documented idempotency
   key; local journal + caller UUID + `confirmExternalCost` must survive a second
   front door.
4. **Host threading rules** — Rhino UI thread / Revit `ExternalEvent` must stay
   the only document mutators.
5. **Shared recovery** — Panel and MCP users on one machine must not double-charge
   because they keep separate journals or separate staging roots.
6. **Open-source maintainability** — prefer one Core, two thin adapters, over two
   products.
7. **Image inputs** — local files need upload → `file_token`; public URL input is
   secondary and insufficient for typical AEC desktop files.

---

## 2. Decision

### 2.1 Dual front doors (locked)

`tripo-rhino` shall expose **two peer front doors** over one shared execution core:

| Front door | Primary user | UX surface | Transport into Core |
| --- | --- | --- | --- |
| **Host UI** | AEC | Rhino **Eto** panel; Revit **simple WPF/WinForms** panel (host-idiomatic, minimal) | Local host-control channel to the sidecar |
| **MCP** | Agentic teams | Existing stdio MCP tools (+ new image tool) | Unchanged stdio MCP protocol |

Neither door is a shim that merely shells the other. Both call the same workflow,
journal, staging, and import pipeline. Feature parity for generation modes
(text and image) is required at the Core layer; UI may expose a subset of
advanced MCP-only recovery tools, but must not invent a second billing path.

### 2.2 Sidecar owns credentials and cloud I/O (locked)

Rename the conceptual role of today’s host-specific MCP server process to
**sidecar** (binary may keep current assembly names initially):

- The sidecar is the **only** component that may read, store, or use the Tripo
  API key for HTTPS calls.
- Host plug-ins **must not** persist the key in:
  - `.3dm` / `.rvt` / `.rfa` or any document-embedded storage;
  - Rhino/Revit user settings that round-trip into shared project files;
  - bridge receipts, MCP tool results, UI status strings, screenshots, or logs.
- When the sidecar reports that no usable key is configured, the **host UI**
  shows a modal dialog that:
  1. explains that a Tripo v3 API key is required;
  2. links or quotes instructions to obtain one from Tripo’s developer console;
  3. accepts a one-time paste;
  4. sends the value to the sidecar over the host-control channel for storage;
  5. clears the dialog field from UI memory after a successful `SetApiKey`.
- MCP users may continue to supply `TRIPO_API_KEY` in the MCP client’s server
  `env` block. Environment supply and secret-store supply are both valid; see
  §4.2 for precedence.

### 2.3 Extract a shared Core (locked direction; timing deferred)

Logical split (names illustrative):

```text
src/
├── Tripo.Bridge/     # IPC contracts, staging validation consumers, host import types
├── Tripo.Core/       # v3 client, file upload, journal, workflow, artifact staging
└── Tripo.Mcp/        # MCP tool schemas + stdio host over Tripo.Core
```

Host panels and MCP tools are adapters. Implementation may stage the split
(refactor-in-place inside `Tripo.Mcp` first) but **must not** grow a second copy
of journal or Tripo HTTP logic inside either plug-in.

### 2.4 Image-to-3D is in scope (locked)

Core shall support creating image-to-model tasks:

1. Accept a local image (PNG / JPEG / WebP per Tripo limits) or, optionally, a
   public HTTPS image URL.
2. For local files: upload via Tripo file API (`POST /v3/files` and/or presign
   flow as documented by Tripo) to obtain `file_token`. Upload uses the sidecar
   credential. The host may pass **bytes or a sidecar-readable path under an
   allowlisted transfer mechanism** — never an arbitrary unchecked path into the
   bridge import allowlist.
3. Create `POST /v3/generation/image-to-model` with `input` = `file_token` or URL,
   journaled like text creation (caller UUID, `Prepared` → `Dispatching` → task
   id / `outcome_unknown`).
4. Reuse existing poll → OBJ conversion → stage → import stages.

MCP gains a creation tool (name illustrative: `tripo_create_image_task`) with the
same cost-confirmation and document-session requirements as text creation.
Multiview creation remains out of scope until a follow-on ADR or an additive
revision of this one.

### 2.5 Preserve existing safety semantics (locked)

The following remain mandatory for **both** front doors:

- Separate paid stages for generation and OBJ conversion; no silent combined
  auto-resend across `outcome_unknown`.
- Explicit user acceptance of possible external cost before each paid POST
  (`confirmExternalCost` for MCP; an equivalent modal / checkbox gate for UI).
- Caller-owned operation UUIDs (UI generates and displays them; retries reuse).
- Exact `documentSessionId` checks before conversion and before mutation.
- Content-addressed staging; host import accepts bundle identity + hash, not
  remote URLs or arbitrary filesystem paths.
- Same stable `TRIPO_LOCAL_DATA_DIR` (or the same default LocalApplicationData
  root) for host and sidecar so bridges, staging, operations, and secrets root
  do not fork.

---

## 3. Target architecture

### 3.1 Process topology

```text
┌─────────────────────────────────────────────────────────────┐
│ Front door A — Host UI                                      │
│  Rhino Eto panel  /  Revit simple panel                     │
│  · prompt / image / params                                  │
│  · cost confirmations                                       │
│  · missing-key dialog (collect only; do not persist)        │
│  · EnsureSidecar / connect                                  │
└─────────────┬───────────────────────────────────────────────┘
              │ host-control (local, current-user, authenticated)
              ▼
┌─────────────────────────────────────────────────────────────┐
│ Sidecar = Tripo.<Host> worker (evolved MCP server process)  │
│  Modes (may be separate argv / entrypoints of one binary):  │
│    · stdio MCP          (Front door B)                      │
│    · host-control serve (Front door A)                      │
│  Tripo.Core:                                                │
│    · API key resolve (env / OS secret store / SetApiKey)    │
│    · PaidOperationJournal                                   │
│    · Tripo v3 client + file upload                          │
│    · Workflow stages                                        │
│    · Artifact staging                                       │
└─────────────┬───────────────────────────────────────────────┘
              │ existing protocol-v2 named pipe (import / context)
              ▼
┌─────────────────────────────────────────────────────────────┐
│ Host plug-in                                                │
│  · document session + import mutation (UI thread / ExtEvent)│
│  · panel hosting                                            │
│  · NO Tripo HTTPS, NO key persistence                       │
└─────────────────────────────────────────────────────────────┘

Front door B — MCP client (Cursor / Claude / …)
  ↕ stdio
  same Sidecar binary in MCP mode
```

### 3.2 Two channels, one trust story

| Channel | Direction | Purpose | Must remain |
| --- | --- | --- | --- |
| **Host bridge (protocol v2)** | Sidecar → Plug-in | `host_context`, idempotent import, capabilities | Small method allowlist; no scripts; no remote URLs; no arbitrary paths; session token |
| **Host-control** | Plug-in panel → Sidecar | Key status/set, start/status/inspect workflow stages, progress | Current-user only; authenticated; never returns the raw key after set; never accepts document mutation commands that bypass the bridge |

Document mutation continues to flow **sidecar → bridge → plug-in**, even when the
user clicked a button in the panel. The panel is not a second importer.

### 3.3 UI responsibilities (minimal)

**Rhino (Eto):**

- Panel: mode Text | Image; prompt; image picker; `faceLimit`; `withMaterials`;
  `importMode`; `applyMaterials`; generate / convert / import actions or a guided
  wizard that still checkpoints each paid stage; progress; last `operationId`s;
  link/button for “API key…”.
- **Partially superseded by ADR-0002 for Rhino:** Grasshopper is now an optional
  host-side interaction surface. It reuses the existing Rhino plug-in,
  host-control sidecar, credential owner, journal, and recovery rules; it does
  not create a third architectural front door or impose Grasshopper on Revit or
  MCP users.

**Revit (simple UI):**

- Equivalent fields and wizard; respect `ExternalEvent` for any document touch.
- Family-template discovery remains environment / probe based
  (`TRIPO_REVIT_FAMILY_TEMPLATE` etc.); the panel may surface the resolved path
  as read-only status, not embed secrets.

**Shared UX rules:**

- Missing key → blocking dialog with instructions before any paid stage.
- Cost confirmation copy must state that Tripo credits may be consumed.
- Show durable local operation ids so users can reconcile with
  `tripo_operation_status` or Tripo’s billing/task history after a crash.
- Do not switch/close the target document mid-flight; if session mismatches,
  fail closed with a clear message (same as MCP).

### 3.4 MCP surface (additive)

Retain existing tools and semantics. Add image creation. Illustrative set:

| Tool | Role |
| --- | --- |
| `tripo_host_context` | Unchanged |
| `tripo_create_text_task` | Unchanged |
| `tripo_create_image_task` | **New** — upload/URL + journaled image-to-model create |
| `tripo_task_status` | Unchanged |
| `tripo_create_obj_conversion` | Unchanged (already accepts `image_to_model` sources) |
| `tripo_operation_status` | Unchanged |
| `tripo_import_obj_task` | Unchanged |

Optional later (not required by this ADR): MCP tools that only read key
presence (`tripo_credential_status`) without revealing secrets.

Agents must still pass `confirmExternalCost=true` only after explicit user
acceptance. Host UI must not expose an MCP tool that bypasses that flag.

---

## 4. Credential design

### 4.1 Storage locations (allowed)

In preference order when resolving a key inside the sidecar:

1. **Process environment** `TRIPO_API_KEY` — for MCP clients, CI, and power users.
2. **Session-only sidecar memory** — populated by `SetApiKey` when the user
   declines persistence; it lasts only for that sidecar process.
3. **OS secret store** — macOS Keychain / Windows Credential Manager, service name
   under a stable TripoMCP identifier, current-user scope.
4. **Fallback file** under the TripoMCP local data root (e.g. `secrets/`), with
   Unix mode `0600` and directory `0700` — only on an unsupported operating
   system where this implementation has no native secret-store backend. A
   native-store runtime failure fails closed rather than silently writing this
   weaker fallback.

Disallowed:

- Host document user strings, extensible storage, hidden elements, sheet
  parameters, or workset data.
- Plug-in XML/`addin` files committed next to projects.
- Journal JSONL, bridge session descriptors, staging manifests, MCP responses.

### 4.2 Precedence and identity

- If `TRIPO_API_KEY` is set in the sidecar process environment, it wins for that
  process lifetime (explicit operator override).
- Else use the session-only value, then the OS secret store (or the explicitly
  reported unsupported-platform fallback) written by `SetApiKey`.
- Paid-operation fingerprints today HMAC with the API credential. Changing the
  effective key changes identity: replays with the same UUID but a different key
  must fail closed (already true). UI and docs must warn that rotating a key
  invalidates in-flight fingerprint matches for unfinished UUIDs.

### 4.3 SetApiKey / ClearApiKey / HasApiKey

Host-control methods (names illustrative):

| Method | Behavior |
| --- | --- |
| `HasApiKey` | Returns boolean and `source`: environment \| session \| store \| none. Never returns the secret. |
| `SetApiKey` | Validates a non-empty opaque string; stores persistently or in sidecar memory as requested; returns ok without echoing the key. |
| `ClearApiKey` | Removes session and store/fallback material. Does not clear a parent-process env var the sidecar cannot unset permanently. |

The missing-key dialog calls `HasApiKey` → if false, collect → `SetApiKey` →
re-check. Instructions in the dialog must include: where to create a Tripo v3
key, that the key stays on this machine in the sidecar, and that it will not be
saved into the Revit/Rhino document.

### 4.4 Logging

- Never log key material, Authorization headers, or pasted dialog buffers.
- Redact signed download URLs in verbose logs (already required by SECURITY).

---

## 5. Sidecar lifecycle and concurrency

### 5.1 Default process model (accepted default)

- **MCP front door:** the MCP client spawns the sidecar with stdio as today.
- **Host UI front door:** the plug-in ensures a sidecar is reachable on the
  host-control endpoint; if not, it starts the sidecar executable from a known
  install-relative path with `--host-control` (or equivalent), same user, same
  `TRIPO_LOCAL_DATA_DIR`.
- One machine may therefore run **more than one sidecar process** (e.g. Cursor
  stdio instance + panel-launched instance). They **must** share the same local
  data root and rely on both the sidecar credential/workflow execution gate and
  the per-operation journal locks for paid POST safety.

Rationale: forcing a single long-lived daemon increases install and wakeup
complexity for OSS AEC users; stdio MCP already implies client-managed lifetime.

### 5.2 Multi-host-instance selection

Existing rule stands: when multiple live host bridges exist, `TRIPO_HOST_PID`
is required and selection never guesses. The host UI should set this
automatically to **its own PID** when launching or configuring its sidecar.

### 5.3 Concurrent orchestration

Accepted default:

- Per-operation journal locks remain the source of truth for paid create/convert.
- The host UI should disable “Generate” while it has a local in-flight wizard for
  that panel instance.
- Before dispatch, the shared UI layer writes a private recovery hint containing
  the displayed UUIDs/task IDs and minimum import retry metadata, but no prompt,
  credential, URL, fingerprint, or arbitrary path. A stale hint blocks new
  UUIDs and key changes until read-only paid checks/manual import reconciliation
  and an explicit typed acknowledgement. The paid journal remains authoritative.
- Credential mutation scans unresolved paid hints across both Rhino and Revit,
  including live panel sessions. A different host process cannot prove that a
  panel session inside the owner PID is still alive, so its hint is
  conservatively blocking rather than guessed away and cannot be archived
  outside the owner process until that exact process is verified as exited.
  Failure to query its process metadata is also blocking. A root-global local
  UI intent lease serializes credential-recovery scanning, key-mutation
  requests, and paid dispatch calls across panels until each UI call completes
  or is cancelled. A second private execution lease is owned by the sidecar for
  the actual key mutation and every paid UI or standalone MCP workflow, from
  before credential-derived fingerprinting through a durable task-ID or
  ambiguous-outcome journal checkpoint. It remains held if the UI pipe client
  disconnects, removing both the cross-panel and MCP check-then-act gaps.
- Revit hides a workflow-bearing window on ordinary close so reopening it in the
  same host process retains the exact in-memory operation identities.
- Completed stage buttons are disabled. An ambiguous paid stage requires a
  status refresh and exposes a same-UUID retry only when the journal reports
  `CanResumeCreation`.
- If MCP and UI contend on the **same operation UUID**, existing fail-closed /
  replay rules apply.
- If they contend on **different UUIDs** against the same document session,
  the credential/workflow execution gate admits only one paid create/convert or
  key mutation at a time. A contending caller receives a typed refusal and must
  retry the unchanged UUID after the active operation checkpoints. This is not
  a document-session import mutex: two imports into one document are still
  allowed if the user (or agent) asked for both, and Undo remains host-native.
  Across host processes, a foreign panel recovery hint is conservatively
  blocking because panel-session liveness is not shared.

### 5.4 Shutdown

- Panel-launched sidecar: plug-in teardown synchronously drains panel sessions,
  performs the bounded host-control graceful-shutdown attempt, and drains the
  import bridge before returning. It allows in-flight journal checkpoints to
  finish or mark `outcome_unknown`; it never deletes journals or force-kills
  the sidecar. The parent-PID monitor remains the fallback after host exit.
- Bridge shutdown draining rules in `ARCHITECTURE.md` remain in force.

---

## 6. Image pipeline details

### 6.1 Creation stage

The implemented local-image kind is `image_task_creation`. Its operation
identity covers the API base/endpoints, exact image identity (SHA-256, length,
detected media type), generation options, effective credential, and document
session. After upload, the exact generation request including `file_token` has
its own durable fingerprint. Neither raw image bytes nor the source path enter
the operations JSONL.

### 6.2 Upload

- The current implementation uses Tripo's documented `POST /v3/files` for one
  verified local PNG/JPEG of 1–20,000,000 bytes.
- WebP, public URL, presign, and large-file paths remain deferred.
- Upload is its own ambiguity boundary:
  `prepared → image_upload_dispatching → image_file_token_persisted`.
  A lost/ambiguous upload response becomes `outcome_unknown` with
  `failureStage=upload` and is never automatically resent.
- Generation then transitions
  `image_file_token_persisted → image_generation_dispatching →
  task_id_persisted`. A lost/ambiguous generation response becomes
  `outcome_unknown` with `failureStage=generation` and is never resent.
- Once a file token and generation fingerprint are durable, a same-UUID retry
  resumes generation without reopening or re-uploading the source image.

### 6.3 Host transfer of local images

The implemented GHA reads a user-picked local file only after an explicit
component-menu action, writes a private snapshot under
`<TRIPO_LOCAL_DATA_DIR>/image-transfers/`, and sends only an opaque transfer
UUID, SHA-256, byte length, and detected media type over host-control. The
sidecar derives the path, rejects symlinks/reparse points and identity
mismatches, copies the validated bytes into an immutable bounded in-memory
snapshot, and uploads that snapshot with a generic filename.

The source path, source filename, image bytes, and `file_token` are not saved in
the `.gh` definition. The import bridge allowlist remains bundle-ID based and
does not accept arbitrary user image paths.

### 6.4 Materials flags

Align with `MATERIALS-DESIGN.md`: image creation should expose `withMaterials`
(mapping to Tripo `texture` / `pbr` as documented for image-to-model). Conversion
and `applyMaterials` remain separate choices.

---

## 7. Alternatives considered

### 7.1 Single thick plug-in (rejected)

UI + HTTPS + journal + staging + import in-process.

- Rejected: key in host process; threading and crash coupling; duplicated Core;
  MCP becomes orphaned or inconsistently reimplemented.

### 7.2 MCP-only forever (rejected for product scope)

Keep today’s architecture as the only front door.

- Rejected: fails AEC ease-of-use goal for this open-source project.

### 7.3 Host UI that shells MCP tools via a hidden client (rejected as primary)

Panel spawns `mcp` CLI or speaks JSON-RPC stdio as if it were Cursor.

- Rejected as the **primary** control path: awkward lifetime, harder key dialogs,
  poor progress UX. A thin internal reuse of tool handlers inside Core is fine;
  pretending to be an MCP client is not required.

### 7.4 UI-only, drop MCP (rejected)

- Rejected: agentic teams are an explicit audience; MCP investment stays.

### 7.5 Key in Revit/Rhino passwords or document (rejected)

- Rejected: leaks via file share, BIM 360, email, support zips, worksharing.

### 7.6 Cloud proxy holding the key (out of scope / rejected for now)

Would ease AEC onboarding but contradicts “local sidecar credential” and expands
operational scope beyond this repository’s OSS charter.

---

## 8. Consequences

### 8.1 Positive

- AEC users get an in-host path; agents keep MCP.
- Single journal and staging root preserves recovery and reduces double-charge
  classes caused by divergent implementations.
- Credential story stays auditable: one process role reads Tripo.
- Image-to-3D extends an already-tolerant conversion/import path.

### 8.2 Negative / costs

- New host-control protocol and sidecar argv modes to design, test, and version.
- Two UI toolkits (Eto vs Revit) with only logical parity.
- Multiple sidecar processes sharing one data root need clear docs and tests for
  lock behavior.
- Secret-store portability across OS accounts and headless CI still needs env
  override.
- Larger documentation surface (two install stories).

### 8.3 Security consequences

- Attack surface moves: host-control becomes a sensitive channel (can set key,
  trigger paid calls). It must be current-user, authenticated, versioned, and
  size-bounded like the import bridge.
- Same-user malware can still abuse the sidecar (already true for MCP env keys);
  the ADR protects **document exfiltration of secrets** and **cross-user** access,
  not same-user hostile code.
- UI must not write secrets into autosave crash dumps if avoidable; clear paste
  buffers after `SetApiKey`.

### 8.4 Documentation consequences

When implementation lands, update:

- `docs/ARCHITECTURE.md` — topology diagram and dual front doors.
- `docs/SECURITY.md` — secret store + `SetApiKey` rules; keep “plug-in never
  stores key”.
- Root and adapter READMEs — **AEC panel path** as the default getting-started;
  MCP path as the Agent / advanced appendix (ordering preference once UI ships).
- Testing evidence classes — add host-control protocol tests; keep real-host UI
  canary separate.

---

## 9. Implementation progress and remaining phases

The user authorized implementation after accepting this ADR. The order remains
trust-sensitive; phase status below describes the current source tree without
claiming real-host acceptance or release.

| Phase | Intent | Current state / exit evidence |
| --- | --- | --- |
| 0 | Extract or isolate `Tripo.Core` behind stable workflow APIs; MCP remains green | **Implemented in place:** `ITripoWorkflow` and shared execution registration are used by MCP and host-control; portable regression gate required before merge |
| 1 | Secret resolve/store + `Has/Set/ClearApiKey` + missing-key dialog wiring | **Implemented in source:** env/session/native store resolution, authenticated methods, and both dialogs; native store and real-host canaries remain |
| 2 | Rhino Eto + Revit simple panel for **text** end-to-end via sidecar | **Implemented in source:** both host package layouts include their sidecar and prior package handshakes passed; the latest recovery, credential-concurrency, and shutdown-hardening delta still needs the complete portable/host compilation rerun, and real AEC host import/interaction and visual recovery canaries remain |
| 3 | Image upload + `tripo_create_image_task` + host-side image UX | **Partial implementation:** local PNG/JPEG transfer, multi-stage journal, MCP tools, and Rhino GHA image creation exist in source; Eto/Revit panel image controls, public URL/WebP input, full current gates, and real-host acceptance remain open |
| 4 | Packaging scripts / zip layout; README dual-door rewrite | **Partial:** build output includes a complete sidecar and READMEs describe both doors; installer, signing, and release artifacts remain |

Packaging must place plug-in + sidecar binaries; it must never bake a shared API
key into the package.

---

## 10. Compliance checklist

A text-only implementation claims partial conformance only if:

- [x] Both front doors still share one journal/staging root semantics.
- [x] Plug-in code paths cannot persist `TRIPO_API_KEY` into documents or project
      settings.
- [x] Missing-key UX exists on the host UI path and only persists via sidecar.
- [x] Paid stages remain separately confirmable; no automatic resend of
      `outcome_unknown`.
- [x] Panel dispatches persist secret-free recovery IDs before connector entry;
      stale or invalid hints block replacement UUIDs until reconciliation.
- [x] Implemented local PNG/JPEG image creation is multi-stage journaled, uses
      upload/`file_token`, and does not weaken import bridge allowlists.
- [x] MCP text tools remain usable for agents without requiring the panel.
- [x] Docs updated if trust boundaries move.

Panel image mode, WebP/public URL input, packaging, and real-host acceptance
remain incomplete, so this ADR is not a release-completion claim.

---

## 11. Open items explicitly deferred

These are **not** blockers for accepting this ADR. The first three received
implementation defaults in the text slice; the remaining items still need
follow-up work:

1. **Resolved for v1:** authenticated, current-user second named pipe with a
   distinct protocol/channel/token and exact host PID.
2. **Resolved for v1:** the owning plug-in sends graceful shutdown during its
   own teardown; there is no idle daemon or blind force-kill.
3. **Resolved for v1:** credential dialogs offer session-only and persistent
   storage choices; persistent storage stays inside the sidecar.
4. Session-level mutex forbidding parallel UI+MCP imports into one
   `documentSessionId`.
5. Multiview-to-model creation.
6. Installer / Yak / signing.

Recommended defaults if implementers must choose without a new ADR:

- Host-control: authenticated current-user named pipe sibling to the import
  bridge, distinct pipe name published in a descriptor under the data root.
- Parent-host PID monitoring plus owning plug-in graceful shutdown; v1 has no
  idle daemon or idle-timeout exit policy.
- Offer “save on this Mac/PC” (default) vs “this session only”.
- No session-level mutex in v1 (operation locks only).
- Multiview later.
- Scripts/zip before full installer.

---

## 12. References

- In-repo: `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/MATERIALS-DESIGN.md`,
  `docs/TESTING.md`, root `README.md` / `README.zh-CN.md`
- Tripo docs (external; verify at implementation time):
  - Text-to-model: https://developers.tripo3d.ai/en/docs/generation-text-to-model/standard
  - Image-to-model: https://developers.tripo3d.ai/en/docs/generation-image-to-model/standard
  - File upload: https://developers.tripo3d.ai/en/docs/files
  - Task query / format conversion (as linked from root README)

---

## 13. Decision record

| Question | Decision |
| --- | --- |
| Host UI + MCP dual entry? | **Yes** — peer front doors |
| Rhino UI toolkit? | **Eto** panel |
| Revit UI? | **Simple** host UI with feature parity to the Rhino panel’s core fields |
| Image-to-3D? | **Yes** — in scope |
| Where does API key live? | **Sidecar only** (+ OS secret store / env); never `.rvt`/`.3dm` |
| Missing key? | **Host dialog** with instructions; persist via sidecar |
| Commercial goals? | **Out of scope** — optimize for OSS installability and dual audiences |
| Implementation authorized? | **Yes** — retain one shared seam and extend it in separately verifiable slices |

**Status: Accepted; implementation in progress.**

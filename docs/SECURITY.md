# Security

> Repository scope: this repository ships the Rhino adapter only. Cross-host
> rules remain documented because independently installed adapters still share
> credential, journal, recovery, and execution-lock identities.

## Credential ownership

- The sidecar is the only component allowed to read, store, or use the Tripo API
  key. The MCP stdio process and the panel-launched host-control process are two
  modes of that same sidecar role.
- Resolution order is `TRIPO_API_KEY`, a sidecar session-only key, then the
  current-user OS secret store. macOS uses Keychain and Windows uses Credential
  Manager. A private local file is used only on unsupported operating systems
  where no native store is implemented.
- The macOS item uses service `ai.qrost.TripoMCPs.TripoV3` and the current OS
  username as its account. The Windows Generic Credential target is
  `TripoMCPs/TripoV3/<username>`. Supported desktop platforms never persist the
  key in a temp file; a caller that does not want native persistence must use
  sidecar-session memory or its MCP client's secret/environment mechanism.
- `TRIPO_API_KEY` remains the recommended non-persistent path for MCP clients,
  CI, and power users. An environment key cannot be cleared by the sidecar.
- Treat the key as an opaque Bearer credential; do not infer or rewrite a provider-specific prefix.
- The host credential dialog may hold a paste only long enough to call the
  authenticated sidecar and must clear its field after success. Never place
  keys in host settings, model parameters, source files, screenshots, logs,
  workflow state, protocol `ToString()` output, or MCP/UI results.
- The optional Grasshopper GHA reuses the loaded Rhino plug-in's sidecar and
  never receives or serializes the key. Saved `.gh` definitions contain no API
  key or Authorization material.
- Credential status reports only presence/source and whether a stored key can
  be cleared. Set and clear responses never echo key material.
- Native secret-store calls fail closed without falling back after an error.
  Windows write buffers are cleared in managed and unmanaged memory before
  release. The unsupported-platform fallback rejects symlinks, bounds file
  size and UTF-8 content, and enforces `0700` directory / `0600` file modes on
  Unix.
- Signed download URLs are treated as short-lived secrets and are never persisted in receipts.
- Paid-operation fingerprints use the API key only as an HMAC key over the exact request identity. The API key, prompt, signed URL, and artifact path are not written to the operation journal.
- Replacing the effective key changes that fingerprint identity. The host
  dialogs and deployment guides warn users to reconcile every unfinished UI or
  MCP paid UUID before key rotation; otherwise a same-UUID recovery fails
  closed on the credential mismatch.

## Panel preferences

- Rhino stores only the last valid face limit, generated-material preference,
  and object name under
  `<TRIPO_LOCAL_DATA_DIR>/ui-settings/rhino-panel.json`. The file never contains
  the prompt, API key, document path, task/operation UUID, signed URL, import
  source, or OBJ conversion choice.
- The file is schema-versioned and limited to 16 KiB. Missing, corrupt,
  oversized, symlink/reparse, or unknown-schema input falls back to defaults
  without a read-time delete or rewrite. A later valid user preference change
  may atomically replace that stale file.
- Writes use a private same-directory temporary file and atomic replace.
  Directories/files use `0700`/`0600` on Unix; Windows relies on current-user
  LocalApplicationData ACLs. Preference writes reject a custom root or settings
  directory containing a symlink/reparse point.
- Every newly constructed panel selects direct GLB. OBJ compatibility remains a
  deliberate per-session choice and cannot silently become the next panel's
  default paid conversion route.
- The visible ready-state credits disclosure and click on the enabled
  **Create in Rhino** button authorize one fresh direct-GLB generation. There is
  no second modal on that happy path. Advanced manual generation/conversion and
  every paid retry retain explicit confirmations; sidecar cost confirmation,
  UUID, capability, active-document, recovery, and single-flight checks remain
  mandatory.
- Panels share one complete preference snapshot. Atomic replacement prevents
  torn JSON; when multiple panels save valid preferences, the last complete
  snapshot wins rather than merging individual fields.
- This preference file is not workflow or recovery evidence. Deleting it only
  restores defaults and does not authorize a resend, retry, import, or remote
  cancellation.

## Paid-operation journal

- Each text creation, image creation, and conversion uses its own canonical
  caller-generated UUID.
- `Prepared` is durable before preflight; `Dispatching` is durable before the POST; a valid task ID is durable before it is returned.
- Image creation uses separate durable upload and generation checkpoints. A
  valid file token resumes generation without a second upload; ambiguous upload
  and generation states carry distinct failure stages and refuse automatic
  resend.
- Same UUID and identical credential/request/document identity replays the same task ID. Reusing a UUID with another kind, key, payload, source task, or document fails closed.
- A recovered Grasshopper retry sets `RequireExistingOperation`; journal
  existence and identity are checked atomically. A copied/tampered `.gh` or
  deleted journal cannot silently create a fresh paid operation under its saved
  UUID.
- Per-operation lock files are retained and held through POST completion and the
  final durable task, definitive rejection, or ambiguous-outcome checkpoint.
- A separate private root-global execution lock admits only one credential
  mutation or paid create/convert workflow across cooperating sidecars at a
  time. Contention fails with `credential_workflow_unavailable`; callers reuse
  the same UUID only when the reconciled journal explicitly authorizes resume.
  A definitive `request_rejected` state requires a corrected credential and new
  UUID.
- On Unix, the operations directory is mode `0700` and journal/lock files are `0600`. On Windows, the default path relies on the current user's inherited LocalApplicationData ACL. A custom `TRIPO_LOCAL_DATA_DIR` must itself be private.
- `tripo_operation_status` is local and read-only. A live lock sets
  `OperationInProgress=true`; a pre-send record may surface as
  `operation_in_progress`, while a post-checkpoint record can remain
  `dispatching`. Neither form authorizes a replacement POST.
- Use a stable local filesystem path shared by all MCP processes for the same user/account. Do not place the journal on NFS/SMB, delete incomplete operations, or change the root during recovery.

The lock and journal protect cooperating processes under one login. They do not isolate a malicious same-user process. `Flush(true)` supports a process-crash recovery boundary but is not a remote transaction or a complete power-loss proof.

## Local image transfers

- The image-transfer layer accepts one byte-proven PNG, JPEG, or WebP from 1
  through 20,000,000 bytes. Extensions are checked against detected content;
  public URLs are not accepted.
- The choosing process creates a private snapshot under
  `<TRIPO_LOCAL_DATA_DIR>/image-transfers/`. The protocol carries only a
  canonical transfer UUID, lowercase SHA-256, byte length, and detected media
  type. Source paths and filenames do not cross host-control.
- The image-transfer root and UUID-derived file are checked for
  symlink/reparse-point substitution before use or deletion. Unix directories
  and files use `0700`/`0600`; a custom data root must be private.
- Before multipart upload, the sidecar bounds, reads, hashes, and signature
  checks the file into an immutable in-memory snapshot. The upload never
  continues from the mutable owner-writable path after validation and uses only
  a generic `input.png`, `input.jpg`, or `input.webp` filename.
- A newly staged image that has not been handed to the sidecar is deleted on a
  pre-dispatch failure where safe. Once admitted, it may remain until a durable
  file-token or upload-ambiguity checkpoint exists. Preserve
  `image-transfers/`, `operations/`, and `ui-recovery/` during reconciliation.
- The GHA's component-owned private recovery fields may store the opaque
  descriptor, paid-operation identity, durable task IDs, and bounded
  status/progress/credits, but never source paths, filenames, bytes, Tripo
  `file_token`, credentials, URLs, transient UI error text, or a duplicate copy
  of the text prompt. Normal Grasshopper input parameters and upstream data may
  still be serialized by Grasshopper itself; treat the whole `.gh` definition
  as potentially sensitive.

## Panel recovery hints

- The shared UI layer writes a recovery hint synchronously and atomically before
  an attempted generation, conversion, or import dispatch reaches the
  connector.
- Hints contain schema/host/process timestamps, the exact document-session and
  operation UUIDs, durable task IDs when known, bounded journal state, and the
  minimum bounded import retry fields. They never contain the API key, prompt,
  Authorization header, URL, request fingerprint, signed URL, artifact path, or
  document path.
- Hints live under
  `<TRIPO_LOCAL_DATA_DIR>/ui-recovery/<host>/<recovery-id>.json`. Recovery IDs
  are independent per panel session, preventing same-document panels from
  sharing a last-writer-wins file.
  Directories/files use `0700`/`0600` on Unix; Windows relies on current-user
  LocalApplicationData ACLs. Recovery/host/archive directory symlinks and
  destination or lock-file symlinks are rejected. Writes use a private
  same-directory temporary file, one exclusive host-recovery cooperative lock,
  and atomic replace. A separate root-global private UI intent lock serializes
  cross-panel credential-recovery scanning, key-mutation requests, and paid
  dispatch calls across Rhino and Revit.
- Recovery files are length-bounded, strict UTF-8, strict-property JSON with a
  fixed schema, and validate canonical lowercase UUIDs, task IDs, timestamps,
  host, import mode, and all bounded text. Unknown, duplicate, corrupt,
  oversized, non-private, unreadable, or symlinked hints fail closed.
- A stale hint blocks new workflow UUIDs in that host. A hint owned by a
  different host process is conservatively blocking even while that process is
  alive, because panel-session liveness is not guessed across processes. It
  cannot be archived elsewhere until the exact owner process is verified as
  exited; inability to query process metadata remains blocking. Reconcile it in
  the owner process instead.
  Credential mutation additionally scans both Rhino and Revit recovery roots,
  including active hints, and refuses while any generation/conversion workflow
  remains recorded before explicit reset, an import receipt is unconfirmed, a
  foreign owner cannot be verified as exited, or recovery storage is invalid.
  The only excluded hint must match the current store's host, recovery ID,
  process ID, process start time, and owned path. A definitive
  `request_rejected` stage is cleared instead of remaining a mutation block.
  The current owner may restore a recovery key only in sidecar-session memory;
  persistent replacement and clearing remain blocked until reset. The UI holds
  its intent lease from credential-recovery
  scan through completion or cancellation of the key-mutation or paid
  host-control call. Independently, the sidecar holds a private execution lease
  for the actual credential mutation and for each paid workflow from before
  credential-derived fingerprinting until the task ID, definitive
  `request_rejected`, or ambiguous outcome is durably journaled. The execution
  lease remains held if the UI pipe client
  disconnects. A standalone MCP process does not take the UI intent lease, but
  it does take this sidecar execution lease; its journal credential fingerprint
  also fails closed, so the key-rotation warning remains mandatory. Paid checks
  call only local
  `operation_status`; no recovery scan automatically resends a paid POST or
  import. Imports require manual same-UUID reconciliation.
- A current live Rhino hint is auto-archived only after the state passes the
  full verified-terminal-import check (host/document/operation/source/format/
  mode/created-object identity, terminal task evidence, and committed or
  already-existing host transaction). The receipt-known hint is atomically
  persisted first, then the same private file is moved to the private archive.
  Later saves of that completed operation do not recreate an active hint. Any
  archive setup/move failure leaves import success intact; when the move did not
  complete, the active hint remains fail-closed. No running,
  successful-but-unimported, uncertain, mismatched, invalid, or foreign evidence
  is auto-archived.
- The guided review automatically performs those read-only checks, displays
  missing or ambiguous local evidence as a provider-history check, and requires
  an unchecked-by-default confirmation before archival. The hint is not an
  authority: paid journals and host idempotency metadata remain the sources of
  truth. The review snapshot includes the exact recovery set plus each complete
  local journal receipt or a bounded unavailable result. Before archival, the
  session holds the shared UI/MCP credential-workflow execution gate while it
  queries and compares the evidence twice again and archives. Any recovery or
  journal drift fails closed, and a receipt marked `OperationInProgress` cannot
  be archived. A panel
  that already owns workflow state reloads first, preserving dispatched IDs as
  recovery evidence while clearing only unsent setup.

## Local bridge

- Named pipes are created with current-user-only access.
- Session descriptors live under the current user's local application-data directory.
- Every host process gets a random pipe name and a 256-bit session token.
- Requests and responses have byte limits, protocol versions, request IDs, deadlines, and typed errors.
- Multiple running instances require an explicit `TRIPO_HOST_PID`; selection never guesses.

This boundary protects against cross-user access. It does not treat another process already running as the same operating-system user as untrusted isolation.

## Host-control sidecar channel

- Host-control is a separate current-user-only named pipe, descriptor, random
  256-bit token, protocol version, and channel identifier. The host-bridge token
  is not accepted.
- Discovery requires the exact host PID. Missing/null descriptor fields,
  unexpected channel/version/PID, stale processes, and ambiguous hosts fail
  closed.
- The method allowlist is limited to health, graceful shutdown, credential
  status/set/clear, and shared workflow calls, including opaque image creation,
  Rhino-only generation-GLB import, and stage-only OBJ receipts. It does not
  accept scripts, arbitrary shell commands, remote import URLs, or arbitrary
  local import paths. Only explicit host import methods can mutate a document;
  stage-only mesh retrieval cannot.
- Requests are length-bounded, concurrency-bounded, authenticated before
  dispatch, and covered by client/server deadlines. Request and descriptor
  string rendering redacts tokens and payloads.
- The plug-in starts only an install-relative sidecar or an explicitly configured
  absolute `TRIPO_SIDECAR_PATH`; it passes the exact host PID and shared data
  root. Shutdown is a protocol request and never a blind process kill.

Like the host bridge, this boundary protects against cross-user access and
accidental cross-process confusion, not malicious code already running as the
same user.

## Artifact handling

- Only HTTPS downloads are accepted.
- API-key headers are not sent to signed artifact URLs.
- Production API and download connections disable proxy and cookie state, resolve DNS once, reject any non-public address, and connect to the vetted endpoint directly.
- Redirect count, compressed bytes, expanded bytes, OBJ vertices, faces, and
  coordinate magnitude are bounded.
- Independent deadlines cover response headers and the complete response body.
- ZIP entry paths are never used as filesystem paths.
- The host accepts only a content-addressed artifact ID under the staging root and verifies byte length and SHA-256 before parsing.
- NaN, infinity, invalid indices, oversized polygons, and degenerate geometry fail closed.
- GLB staging rejects symlink/reparse components, uses no-overwrite placement,
  publishes `manifest.json` last, and exact-revalidates both manifest and
  payload before reuse. The signed provider URL, API key, absolute staging
  path, and fixed-snapshot path never cross the host bridge or recovery state.
- Before Rhino's native parser runs, GLB v2 structure, JSON size, aggregate
  accessor elements, mesh vertex/triangle estimates, scene-graph acyclicity,
  buffer/view/accessor bounds, aggregate decoded image pixels, and embedded
  PNG/JPEG dimensions are bounded. External/data/file buffer and image URIs are
  rejected. The exact top-level limits are 64 MiB per GLB, 4 MiB JSON, 64 MiB
  aggregate decoded accessor storage, 4096 pixels on either image side,
  16 Mi pixels per image, and 32 Mi pixels across all images.
- The bridge loads verified GLB bytes, not an authority-bearing staging path.
  Rhino writes those bytes with `CreateNew`, `WriteThrough`, `Flush(true)`, and
  private permissions into a random non-reparse snapshot directory, keeps a
  read lease through import, and verifies the same length/hash before and after
  each native call.
- Snapshot deletion is attempted when its lease ends. Each new snapshot also
  attempts a bounded stale cleanup for leftovers: inspect at most 256 strictly
  named entries, mutate at most 16, require both directory/file age over
  24 hours and a definitely exited owner PID, reject
  symlink/reparse/device content, and use current-cleaner-owned
  quarantine/tombstone names. It never recursively deletes an uninspected
  directory; uncertain liveness or filesystem metadata preserves the
  candidate.
- `RhinoDoc.Import` still executes inside the Rhino process. A headless
  preflight isolates ordinary parse mutations from the user's target document,
  but is not process isolation against a native parser crash. On macOS, another
  malicious process already running as the same user remains outside the
  filesystem isolation claim.

## Rhino direct-GLB mutation journal

- The host owns an append-only, checksummed, write-through JSONL journal under
  `host-imports/rhino/<operation-id>.jsonl`; it stores identity, hashes, bounded
  counts, and state, never credentials, URLs, or paths.
- A deterministic prepared block marker and durable `prepared` record exist
  immediately before native import. Any failure after native import begins is
  `outcome_unknown` even when best-effort Undo restores tracked tables.
- `prepared`, `outcome_unknown`, corrupt, incomplete, or document/journal
  mismatch state disables UI dispatch and never authorizes a second native
  import. A normal exit before `prepared` deletes its unused empty journal;
  an empty journal left by a crash fails closed.
- `committed` replay is read-only. It verifies the exact root GUID, unique
  idempotency identity, stored counts, direct membership, recursive
  geometry digest, PBR-content digest, and recalculated mesh counts. The
  portable PBR proof must match from headless preflight through active import
  and completed definition. It covers selected material source and effective
  front/back/plugin/subobject bindings, explicit mesh UVs, persistent mappings,
  legacy-material fallback values, allowlisted built-in render-content types,
  canonical persistent RDK fields, normalized child-slot/on/amount state, and
  SHA-256 of readable referenced texture bytes. Projection/wrap/mapping and
  linear/normal-map semantics are proved through those persistent fields and
  slots. The cross-stage proof excludes the document-owned RDK render hash,
  derived cached texture coordinates/runtime getters, and exact
  editor/preview-only fields; the completed definition's document proof
  retains the render hash. The stored document proof must match replay exactly.
  Unsupported inheritance, custom/procedural content, duplicate/empty child
  slots, or unsafe/unreadable texture references fail closed. Replay never
  recreates a missing root or definition.
- Journal schema 3 requires both geometry-membership and PBR-content proof and
  records PBR proof version 5. A schema-2, older-proof-version, or incomplete
  record reports an explicit unsupported-proof/manual-review error. It cannot
  return `already_exists`, and neither can an existing direct-import root
  without a durable committed journal.
- Cancellation may win while UI work is queued. Once the Rhino UI mutation
  starts, the caller waits for its real completion before the fixed snapshot,
  journal lease, and single-mutation gate are released.

## Remote-task semantics

- Creating or converting a model may consume credits.
- The panel's visible one-click direct-GLB action is an explicit fresh paid
  authorization. Manual generation/conversion, every retry, Grasshopper, and
  MCP keep their independent confirmation gates.
- Text creation, image upload/generation, and OBJ conversion are separate,
  journaled stages.
- Task-creating POST requests have no automatic retry.
- A POST timeout, caller cancellation, connection failure, malformed success
  response, or incomplete response is persisted as an outcome-unknown operation
  that may already have created a remote task. An abruptly killed process leaves
  `Dispatching` durable; the next acquire with the same UUID changes it to
  outcome-unknown without resending.
- Outcome-unknown operations refuse automatic resend. Preserve the original UUID and inspect local status plus provider task/billing history.
- If a valid task ID was checkpointed but the MCP response was lost, the same UUID replays that ID without another POST.
- Task status is queried separately with bounded read retries and a hard local deadline; no server-side long-poll loop hides a paid task ID.
- Unknown task states fail closed.
- Task-query responses must carry the exact requested task ID.
- Cancelling a local status query does not claim that a remote task was cancelled.
- Host import canonicalizes a caller-owned UUID before the bridge and persistent host lookup; case-only UUID variants cannot create a second host object.
- Direct GLB import is local and does not create another Tripo task. An HTTP
  401/403 while reading the existing generation task is reported as
  `credential_invalid` before host mutation; the same import UUID can continue
  after a session-only replacement key. Once native mutation is uncertain,
  credential replacement never turns that state into an import retry.

## Reporting

Please avoid attaching API keys, signed download URLs, private model files, or user-profile session descriptors to a public issue.

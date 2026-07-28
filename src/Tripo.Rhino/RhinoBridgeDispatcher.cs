using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Tripo.Rhino;

internal sealed class RhinoBridgeDispatcher : Tripo.Bridge.IHostBridgeDispatcher, IDisposable
{
    private const string IdempotencyUserString = "TripoMCP.IdempotencyKey";
    private const string RequestFingerprintUserString = "TripoMCP.RequestFingerprint";
    private const string DocumentSessionUserString = "TripoMCP.DocumentSession";
    private const string VertexCountUserString = "TripoMCP.VertexCount";
    private const string TriangleCountUserString = "TripoMCP.TriangleCount";
    private const string RejectedCountUserString = "TripoMCP.RejectedTriangleCount";
    private const string MaterialCountUserString = "TripoMCP.MaterialCount";
    private const string TextureCountUserString = "TripoMCP.TextureCount";
    private const string GlbMarkerStateUserString =
        "TripoMCP.GlbImportState";
    private const string DefinitionMemberCountUserString =
        "TripoMCP.DefinitionMemberCount";
    private const string DefinitionMemberDigestUserString =
        "TripoMCP.DefinitionMemberDigest";
    private const string PbrContentDigestUserString =
        "TripoMCP.PbrContentDigest";
    private const string PreparedGlbMarkerState = "prepared";
    private const int MaximumNativeGlbObjects = 4_096;
    private const int MaximumNativeGlbMaterials = 256;
    private const int MaximumNativeGlbTextures = 512;

    // Instance mode block definitions are named by idempotency key so a crashed
    // half-import (definition created, instance not) is reconcilable by name.
    private const string DefinitionNamePrefix = "Tripo_";

    private static readonly IReadOnlyList<string> Capabilities =
    [
        Tripo.Bridge.BridgeConstants.ContextMethod,
        Tripo.Bridge.BridgeConstants.ImportMeshMethod,
        Tripo.Bridge.BridgeConstants.ImportGlbMethod,
    ];

    private readonly RhinoDocumentSessions _documentSessions;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    public RhinoBridgeDispatcher(RhinoDocumentSessions documentSessions)
    {
        _documentSessions =
            documentSessions ?? throw new ArgumentNullException(nameof(documentSessions));
    }

    public Task<object> DispatchAsync(
        string method,
        JsonElement payload,
        CancellationToken cancellationToken) =>
        method switch
        {
            Tripo.Bridge.BridgeConstants.ContextMethod =>
                GetContextObjectAsync(cancellationToken),
            Tripo.Bridge.BridgeConstants.ImportMeshMethod =>
                ImportObjectAsync(payload, cancellationToken),
            Tripo.Bridge.BridgeConstants.ImportGlbMethod =>
                ImportGlbObjectAsync(payload, cancellationToken),
            _ => throw new Tripo.Bridge.BridgeCallException(
                "method_not_allowed",
                "The requested Rhino method is not allowed."),
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutationGate.Dispose();
    }

    private async Task<object> GetContextObjectAsync(
        CancellationToken cancellationToken) =>
        await RhinoUiThread.InvokeAsync<object>(
                () => CreateContextReceipt(GetActiveDocument()),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<object> ImportObjectAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        Tripo.Bridge.ImportMeshRequest request;
        try
        {
            request = payload.Deserialize<Tripo.Bridge.ImportMeshRequest>(
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new JsonException("The import payload was null.");
        }
        catch (JsonException exception)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "invalid_request",
                "The Rhino import payload was invalid.",
                exception);
        }

        // Reject unsupported modes (e.g. "family") before any mutation or staging read.
        bool supportedMode =
            string.Equals(request.ImportMode, "mesh", StringComparison.Ordinal) ||
            string.Equals(request.ImportMode, "instance", StringComparison.Ordinal);
        if (!supportedMode)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "import_mode_unsupported",
                "This Rhino build supports the mesh and instance import modes only.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureActiveSessionAsync(
                    request.DocumentSessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            Tripo.Bridge.PreparedMesh prepared =
                await Tripo.Bridge.StagedArtifactLoader.LoadPreparedObjAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            return await RhinoUiThread.InvokeAsync<object>(
                    () => ImportOnUiThread(request, prepared),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<object> ImportGlbObjectAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        Tripo.Bridge.ImportGlbRequest request;
        try
        {
            request = payload.Deserialize<Tripo.Bridge.ImportGlbRequest>(
                    Tripo.Bridge.BridgeJson.Options)
                ?? throw new JsonException("The direct GLB import payload was null.");
        }
        catch (JsonException exception)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "invalid_request",
                "The Rhino direct GLB import payload was invalid.",
                exception);
        }

        if (!request.ApplyMaterials)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "invalid_request",
                "Direct GLB import always preserves Rhino-native PBR materials.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureActiveSessionAsync(
                    request.DocumentSessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            Tripo.Bridge.PreparedGlbArtifact prepared =
                await Tripo.Bridge.StagedArtifactLoader.LoadPreparedGlbAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            using Tripo.Bridge.VerifiedGlbSnapshot snapshot =
                await Tripo.Bridge.VerifiedGlbSnapshot.CreateAsync(
                        prepared,
                        cancellationToken)
                    .ConfigureAwait(false);
            return await RhinoUiThread.InvokeAsync<object>(
                    () => ImportGlbOnUiThread(request, snapshot),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private Task<bool> EnsureActiveSessionAsync(
        string requestedSessionId,
        CancellationToken cancellationToken) =>
        RhinoUiThread.InvokeAsync(
            () =>
            {
                global::Rhino.RhinoDoc document = GetActiveDocument();
                EnsureMatchingSession(document, requestedSessionId);
                return true;
            },
            cancellationToken);

    private Tripo.Bridge.HostImportReceipt ImportOnUiThread(
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared)
    {
        global::Rhino.RhinoDoc document = GetActiveDocument();
        EnsureMatchingSession(document, request.DocumentSessionId);

        return string.Equals(request.ImportMode, "instance", StringComparison.Ordinal)
            ? ImportInstanceOnUiThread(document, request, prepared)
            : ImportMeshOnUiThread(document, request, prepared);
    }

    // ---- native GLB mode ---------------------------------------------------

    private Tripo.Bridge.HostImportReceipt ImportGlbOnUiThread(
        Tripo.Bridge.ImportGlbRequest request,
        Tripo.Bridge.VerifiedGlbSnapshot snapshot)
    {
        using Tripo.Bridge.HostImportJournal journal =
            Tripo.Bridge.HostImportJournal.Open(
                new Tripo.Bridge.HostImportJournalIdentity(
                    "rhino",
                    request.DocumentSessionId,
                    request.IdempotencyKey,
                    CreateGlbRequestFingerprint(request),
                    request.ArtifactId,
                    request.Entry.Sha256,
                    request.Entry.ByteLength));
        global::Rhino.RhinoDoc initialDocument = GetActiveDocument();
        EnsureMatchingSession(initialDocument, request.DocumentSessionId);
        Tripo.Bridge.HostImportJournalStatus? journalStatus =
            journal.Current;
        if (journalStatus?.State is
            Tripo.Bridge.HostImportJournal.PreparedState or
            Tripo.Bridge.HostImportJournal.OutcomeUnknownState)
        {
            throw UncertainGlbImport(
                "A previous native GLB import did not reach a durable commit.");
        }

        RhinoObject[] existing = FindExistingForMode(
            initialDocument,
            request.IdempotencyKey,
            ObjectType.InstanceReference);
        if (existing.Length > 1)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "Multiple Rhino instances already carry this idempotency key.");
        }

        string definitionName = DefinitionNamePrefix + request.IdempotencyKey;
        InstanceDefinition? existingDefinition =
            initialDocument.InstanceDefinitions.Find(definitionName);
        if (string.Equals(
                journalStatus?.State,
                Tripo.Bridge.HostImportJournal.CommittedState,
                StringComparison.Ordinal))
        {
            return CreateCommittedGlbReceipt(
                initialDocument,
                request,
                journalStatus?.Commit ??
                throw UncertainGlbImport(
                    "The committed GLB journal is missing its receipt."));
        }

        if (journalStatus is not null &&
            (existing.Length > 0 || existingDefinition is not null))
        {
            throw UncertainGlbImport(
                "An aborted GLB journal has unexpected Rhino document state.");
        }

        if (existing.Length == 1)
        {
            throw UncertainGlbImport(
                "A native GLB root exists without an authoritative committed journal.");
        }

        if (existingDefinition is not null)
        {
            throw UncertainGlbImport(
                "A native GLB block definition exists without a committed journal.");
        }

        GlbPreflightReceipt preflight = PreflightGlb(
            initialDocument,
            snapshot);

        // Native import can take long enough for the user to activate another
        // document. Resolve the active document again after preflight and perform
        // the final session check immediately before starting the undo record.
        global::Rhino.RhinoDoc document = GetActiveDocument();
        EnsureMatchingSession(document, request.DocumentSessionId);
        if (document.RuntimeSerialNumber != initialDocument.RuntimeSerialNumber)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "document_changed",
                "The active Rhino document changed during GLB preflight.");
        }

        existing = FindExistingForMode(
            document,
            request.IdempotencyKey,
            ObjectType.InstanceReference);
        if (existing.Length > 1)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "Multiple Rhino instances already carry this idempotency key.");
        }

        if (existing.Length == 1)
        {
            throw UncertainGlbImport(
                "A native GLB root appeared without an authoritative committed journal.");
        }

        existingDefinition = document.InstanceDefinitions.Find(definitionName);
        if (existingDefinition is not null)
        {
            throw UncertainGlbImport(
                "A native GLB block definition appeared during preflight.");
        }

        EnsureCanRecordUndo(document);
        return CreateGlbInstance(
            document,
            request,
            snapshot,
            journal,
            definitionName,
            preflight);
    }

    private static GlbPreflightReceipt PreflightGlb(
        global::Rhino.RhinoDoc targetDocument,
        Tripo.Bridge.VerifiedGlbSnapshot snapshot)
    {
        global::Rhino.RhinoDoc? headless = null;
        try
        {
            headless = global::Rhino.RhinoDoc.CreateHeadless(null);
            if (headless is null)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino could not create an isolated document for GLB preflight.");
            }

            headless.ModelUnitSystem = targetDocument.ModelUnitSystem;
            snapshot.Verify();
            bool imported;
            try
            {
                imported = headless.Import(
                    snapshot.GlbPath,
                    new global::Rhino.Collections.ArchivableDictionary());
            }
            finally
            {
                snapshot.Verify();
            }
            if (!imported)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "Rhino could not import the staged GLB in an isolated document.");
            }

            return InspectImportedGlb(headless);
        }
        catch (Tripo.Bridge.BridgeCallException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "Rhino rejected the staged GLB during isolated preflight.",
                exception);
        }
        finally
        {
            headless?.Dispose();
        }
    }

    private static GlbPreflightReceipt InspectImportedGlb(
        global::Rhino.RhinoDoc document,
        IReadOnlyList<RhinoObject>? auditedRoots = null)
    {
        RhinoObject[] roots = auditedRoots?.ToArray() ??
                              GetAuditedTopLevelObjects(document);
        if (roots.Length == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The staged GLB imported without any Rhino objects.");
        }

        GlbGeometryCounts counts = new();
        HashSet<Guid> visitedDefinitions = new();
        InspectGlbObjects(roots, visitedDefinitions, depth: 0, counts);
        if (counts.MeshCount == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The staged GLB contains no importable mesh geometry.");
        }

        GlbContentProof proof = CreateGlbContentProof(document, roots);
        return new GlbPreflightReceipt(
            checked((int)counts.VertexCount),
            checked((int)counts.TriangleCount),
            proof.MaterialCount,
            proof.TextureCount,
            proof.ContentDigest);
    }

    private static void InspectGlbObjects(
        IEnumerable<RhinoObject> objects,
        HashSet<Guid> visitedDefinitions,
        int depth,
        GlbGeometryCounts counts)
    {
        if (depth > 32)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_too_complex",
                "The staged GLB instance hierarchy exceeds the supported depth.");
        }

        foreach (RhinoObject item in objects)
        {
            counts.ObjectCount++;
            if (counts.ObjectCount > MaximumNativeGlbObjects)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_too_complex",
                    $"The staged GLB exceeds the {MaximumNativeGlbObjects} object limit.");
            }

            if (item.Geometry is Mesh mesh)
            {
                if (!mesh.IsValid)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "glb_invalid",
                        "The staged GLB contains a Rhino mesh that is not valid.");
                }

                BoundingBox bounds = mesh.GetBoundingBox(accurate: true);
                if (!bounds.IsValid)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "glb_invalid",
                        "The staged GLB contains non-finite mesh bounds.");
                }

                counts.MeshCount++;
                counts.VertexCount += mesh.Vertices.Count;
                for (int faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
                {
                    counts.TriangleCount += mesh.Faces[faceIndex].IsQuad ? 2 : 1;
                }

                if (counts.VertexCount > Tripo.Bridge.BridgeConstants.MaximumVertices)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "glb_too_complex",
                        "The staged GLB exceeds the supported vertex limit.");
                }

                if (counts.TriangleCount > Tripo.Bridge.BridgeConstants.MaximumTriangles)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "glb_too_complex",
                        "The staged GLB exceeds the supported triangle limit.");
                }

                continue;
            }

            if (item is InstanceObject instance)
            {
                InstanceDefinition definition = instance.InstanceDefinition;
                if (visitedDefinitions.Add(definition.Id))
                {
                    InspectGlbObjects(
                        definition.GetObjects(),
                        visitedDefinitions,
                        depth + 1,
                        counts);
                }

                continue;
            }

            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The staged GLB contains an unsupported non-mesh object.");
        }
    }

    private static Tripo.Bridge.HostImportReceipt CreateGlbInstance(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportGlbRequest request,
        Tripo.Bridge.VerifiedGlbSnapshot snapshot,
        Tripo.Bridge.HostImportJournal journal,
        string definitionName,
        GlbPreflightReceipt preflight)
    {
        RhinoDocumentState before = CaptureDocumentState(document);
        uint undoRecord = document.BeginUndoRecord("Import Tripo GLB");
        if (undoRecord == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "undo_unavailable",
                "Rhino could not start an undo record for the GLB import.");
        }

        Guid createdId = Guid.Empty;
        int definitionIndex = -1;
        List<GeometryBase> definitionGeometry = new();
        GlbDefinitionIdentity? definitionIdentity = null;
        bool nativeImportStarted = false;
        Exception? failure = null;
        try
        {
            definitionIndex = CreatePreparedGlbDefinition(
                document,
                request,
                definitionName);
            snapshot.Verify();
            journal.RecordPrepared();
            nativeImportStarted = true;
            bool imported;
            try
            {
                imported = document.Import(
                    snapshot.GlbPath,
                    new global::Rhino.Collections.ArchivableDictionary());
            }
            finally
            {
                snapshot.Verify();
            }

            if (!imported)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino rejected the staged GLB during document import.");
            }

            RhinoObject[] importedObjects = FindNewLiveObjects(document, before.ObjectIds);
            if (importedObjects.Length == 0 ||
                importedObjects.Length > MaximumNativeGlbObjects)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino did not create a bounded set of objects from the staged GLB.");
            }

            GlbPreflightReceipt activeReceipt =
                InspectImportedGlb(document, importedObjects);
            if (activeReceipt != preflight)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    Tripo.Bridge.BridgeConstants
                        .MutationStateUncertainError,
                    "The active GLB import did not match its isolated preflight.");
            }

            List<ObjectAttributes> definitionAttributes = new(importedObjects.Length);
            foreach (RhinoObject importedObject in importedObjects)
            {
                GeometryBase geometry = importedObject.Geometry.Duplicate();
                ObjectAttributes? attributes = importedObject.Attributes.Duplicate();
                if (geometry is null || attributes is null)
                {
                    geometry?.Dispose();
                    throw new Tripo.Bridge.BridgeCallException(
                        "host_import_failed",
                        "Rhino could not duplicate imported GLB geometry for its block.");
                }

                ApplyGlbDefinitionMemberIdentity(
                    attributes,
                    request,
                    preflight);
                definitionGeometry.Add(geometry);
                definitionAttributes.Add(attributes);
            }

            if (!document.InstanceDefinitions.ModifyGeometry(
                    definitionIndex,
                    definitionGeometry,
                    definitionAttributes))
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino could not replace the prepared marker with GLB geometry.");
            }

            InstanceDefinition definition =
                document.InstanceDefinitions.Find(definitionName) ??
                throw new Tripo.Bridge.BridgeCallException(
                    Tripo.Bridge.BridgeConstants
                        .MutationStateUncertainError,
                    "Rhino lost the prepared native GLB block definition.");
            VerifyGlbDefinitionFingerprint(definition, request);
            GlbPreflightReceipt definitionReceipt =
                InspectImportedGlb(document, definition.GetObjects());
            if (definitionReceipt != preflight)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    Tripo.Bridge.BridgeConstants
                        .MutationStateUncertainError,
                    "Rhino did not preserve the imported GLB geometry and PBR content in its block.");
            }

            Guid[] importedIds = importedObjects.Select(item => item.Id).ToArray();
            int deleted = document.Objects.Delete(importedIds, quiet: true);
            if (deleted != importedIds.Length)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino could not replace all imported GLB objects with a block instance.");
            }

            if (FindNewLiveObjects(document, before.ObjectIds).Length != 0)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    Tripo.Bridge.BridgeConstants
                        .MutationStateUncertainError,
                    "Rhino retained unexpected top-level objects after GLB wrapping.");
            }

            definitionIdentity =
                CreateGlbDefinitionIdentity(document, definition);
            ObjectAttributes instanceAttributes = new()
            {
                Name = request.Name,
            };
            ApplyGlbIdentityUserStrings(
                instanceAttributes,
                request,
                preflight,
                definitionIdentity);
            createdId = document.Objects.AddInstanceObject(
                definitionIndex,
                Transform.Identity,
                instanceAttributes);
            if (createdId == Guid.Empty)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino rejected the native GLB block instance.");
            }

            Guid[] liveAdditions = CaptureDocumentState(document)
                .ObjectIds
                .Except(before.ObjectIds)
                .ToArray();
            if (liveAdditions.Length != 1 || liveAdditions[0] != createdId)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    Tripo.Bridge.BridgeConstants
                        .MutationStateUncertainError,
                    "Rhino created unexpected top-level objects during GLB import.");
            }

            VerifyCommittedGlbState(
                document,
                request,
                createdId,
                preflight,
                definitionIdentity);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            foreach (GeometryBase geometry in definitionGeometry)
            {
                geometry.Dispose();
            }
        }

        if (failure is not null)
        {
            RollBackFailedGlbImport(
                document,
                undoRecord,
                before,
                journal,
                nativeImportStarted,
                failure);
        }

        bool undoEnded = document.EndUndoRecord(undoRecord);
        if (!undoEnded)
        {
            RecordOutcomeUnknownBestEffort(journal);
            throw new Tripo.Bridge.BridgeCallException(
                Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
                "Rhino created the GLB instance but could not finish its undo record.");
        }

        GlbDefinitionIdentity committedIdentity =
            definitionIdentity ??
            throw UncertainGlbImport(
                "Rhino finished the GLB undo record without definition identity.");
        try
        {
            journal.RecordCommitted(
                new Tripo.Bridge.HostImportCommitReceipt(
                    createdId.ToString("D"),
                    preflight.VertexCount,
                    preflight.TriangleCount,
                    preflight.MaterialCount,
                    preflight.TextureCount,
                    committedIdentity.MemberCount,
                    committedIdentity.MemberDigest,
                    preflight.PbrContentDigest));
        }
        catch (Exception exception)
        {
            throw new Tripo.Bridge.BridgeCallException(
                Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
                "Rhino committed the GLB but its durable journal commit failed.",
                exception);
        }

        document.Views.Redraw();
        return CreateGlbReceipt(
            request,
            createdId.ToString("D"),
            preflight,
            "committed");
    }

    private static void RollBackFailedGlbImport(
        global::Rhino.RhinoDoc document,
        uint undoRecord,
        RhinoDocumentState before,
        Tripo.Bridge.HostImportJournal journal,
        bool nativeImportStarted,
        Exception failure)
    {
        RhinoDocumentState mutated = CaptureDocumentState(document);
        bool undoEnded = document.EndUndoRecord(undoRecord);
        bool restored = DocumentStatesEqual(before, mutated);
        if (!restored &&
            undoEnded &&
            document.Undo())
        {
            document.ClearRedoRecords();
            restored = DocumentStatesEqual(
                before,
                CaptureDocumentState(document));
        }

        if (nativeImportStarted)
        {
            RecordOutcomeUnknownBestEffort(journal);
            throw new Tripo.Bridge.BridgeCallException(
                Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
                restored
                    ? "Rhino rolled back a failed native GLB import, but the " +
                      "native importer outcome remains unsafe to retry."
                    : "Rhino could not prove that a failed native GLB import " +
                      "was fully rolled back.",
                failure);
        }

        if (!undoEnded || !restored)
        {
            RecordOutcomeUnknownBestEffort(journal);
            throw new Tripo.Bridge.BridgeCallException(
                Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
                "Rhino could not prove that a pre-import mutation was rolled back.",
                failure);
        }

        if (string.Equals(
                journal.Current?.State,
                Tripo.Bridge.HostImportJournal.PreparedState,
                StringComparison.Ordinal))
        {
            journal.RecordAbortedBeforeImport();
        }

        ThrowGlbFailure(failure);
    }

    private static void RecordOutcomeUnknownBestEffort(
        Tripo.Bridge.HostImportJournal journal)
    {
        if (!string.Equals(
                journal.Current?.State,
                Tripo.Bridge.HostImportJournal.PreparedState,
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            journal.RecordOutcomeUnknown();
        }
        catch (Tripo.Bridge.BridgeCallException)
        {
            // A prepared or corrupt tail is already fail-closed on replay.
        }
    }

    private static void ThrowGlbFailure(Exception failure)
    {
        if (failure is Tripo.Bridge.BridgeCallException bridge)
        {
            throw bridge;
        }

        throw new Tripo.Bridge.BridgeCallException(
            "host_import_failed",
            "Rhino failed while importing the staged GLB.",
            failure);
    }

    private static RhinoDocumentState CaptureDocumentState(
        global::Rhino.RhinoDoc document)
    {
        HashSet<Guid> objectIds = GetAuditedTopLevelObjects(document)
            .Select(item => item.Id)
            .ToHashSet();
        HashSet<Guid> definitionIds = document.InstanceDefinitions
            .GetList(ignoreDeleted: true)
            .Select(item => item.Id)
            .ToHashSet();
        HashSet<Guid> materialIds = new();
        for (int index = 0; index < document.Materials.Count; index++)
        {
            Material material = document.Materials[index];
            if (!material.IsDeleted)
            {
                materialIds.Add(material.Id);
            }
        }

        HashSet<Guid> renderMaterialIds = document.RenderMaterials
            .Select(item => item.Id)
            .ToHashSet();
        HashSet<Guid> renderTextureIds = document.RenderTextures
            .Select(item => item.Id)
            .ToHashSet();
        HashSet<string> embeddedFiles = document
            .GetEmbeddedFilesList(false)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<Guid> layerIds = new();
        for (int index = 0; index < document.Layers.Count; index++)
        {
            Layer layer = document.Layers[index];
            if (!layer.IsDeleted)
            {
                layerIds.Add(layer.Id);
            }
        }

        return new RhinoDocumentState(
            objectIds,
            definitionIds,
            materialIds,
            renderMaterialIds,
            renderTextureIds,
            embeddedFiles,
            layerIds,
            document.ModelUnitSystem,
            document.Layers.CurrentLayerIndex);
    }

    private static bool DocumentStatesEqual(
        RhinoDocumentState left,
        RhinoDocumentState right) =>
        left.ModelUnitSystem == right.ModelUnitSystem &&
        left.CurrentLayerIndex == right.CurrentLayerIndex &&
        left.ObjectIds.SetEquals(right.ObjectIds) &&
        left.DefinitionIds.SetEquals(right.DefinitionIds) &&
        left.MaterialIds.SetEquals(right.MaterialIds) &&
        left.RenderMaterialIds.SetEquals(right.RenderMaterialIds) &&
        left.RenderTextureIds.SetEquals(right.RenderTextureIds) &&
        left.EmbeddedFiles.SetEquals(right.EmbeddedFiles) &&
        left.LayerIds.SetEquals(right.LayerIds);

    private static RhinoObject[] FindNewLiveObjects(
        global::Rhino.RhinoDoc document,
        HashSet<Guid> priorIds) =>
        GetAuditedTopLevelObjects(document)
            .Where(item => !priorIds.Contains(item.Id))
            .ToArray();

    private static RhinoObject[] GetAuditedTopLevelObjects(
        global::Rhino.RhinoDoc document)
    {
        ObjectEnumeratorSettings settings = new()
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            IdefObjects = false,
            DeletedObjects = false,
            ActiveObjects = true,
            ReferenceObjects = false,
            IncludeLights = true,
            IncludeGrips = true,
            IncludePhantoms = true,
        };
        return document.Objects.GetObjectList(settings).ToArray();
    }

    private static int CreatePreparedGlbDefinition(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportGlbRequest request,
        string definitionName)
    {
        global::Rhino.Geometry.Point marker = new(Point3d.Origin);
        ObjectAttributes attributes = new();
        if (!attributes.SetUserString(
                IdempotencyUserString,
                request.IdempotencyKey) ||
            !attributes.SetUserString(
                RequestFingerprintUserString,
                CreateGlbRequestFingerprint(request)) ||
            !attributes.SetUserString(
                GlbMarkerStateUserString,
                PreparedGlbMarkerState))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "host_metadata_failed",
                "Rhino could not attach the prepared GLB marker identity.");
        }

        int definitionIndex;
        try
        {
            definitionIndex = document.InstanceDefinitions.Add(
                definitionName,
                "Prepared Tripo GLB import marker",
                Point3d.Origin,
                marker,
                attributes);
        }
        finally
        {
            marker.Dispose();
        }

        if (definitionIndex < 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "host_import_failed",
                "Rhino could not create the prepared GLB marker definition.");
        }

        InstanceDefinition definition =
            document.InstanceDefinitions.Find(definitionName) ??
            throw UncertainGlbImport(
                "Rhino did not retain the prepared GLB marker definition.");
        RhinoObject[] members = definition.GetObjects();
        if (members.Length != 1 ||
            members[0].Geometry is not global::Rhino.Geometry.Point ||
            !string.Equals(
                members[0].Attributes.GetUserString(
                    GlbMarkerStateUserString),
                PreparedGlbMarkerState,
                StringComparison.Ordinal) ||
            !string.Equals(
                members[0].Attributes.GetUserString(
                    RequestFingerprintUserString),
                CreateGlbRequestFingerprint(request),
                StringComparison.Ordinal))
        {
            throw UncertainGlbImport(
                "Rhino could not verify the prepared GLB marker definition.");
        }

        return definitionIndex;
    }

    private static Tripo.Bridge.HostImportReceipt CreateCommittedGlbReceipt(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportGlbRequest request,
        Tripo.Bridge.HostImportCommitReceipt commit)
    {
        if (!Guid.TryParseExact(
                commit.CreatedId,
                "D",
                out Guid createdId))
        {
            throw UncertainGlbImport(
                "The committed GLB journal contains an invalid Rhino object ID.");
        }

        GlbPreflightReceipt stored = new(
            commit.VertexCount,
            commit.TriangleCount,
            commit.MaterialCount,
            commit.TextureCount,
            commit.PbrContentDigest);
        GlbDefinitionIdentity identity = new(
            commit.DefinitionMemberCount,
            commit.DefinitionMemberDigest);
        VerifyCommittedGlbState(
            document,
            request,
            createdId,
            stored,
            identity);
        return CreateGlbReceipt(
            request,
            commit.CreatedId,
            stored,
            "already_exists");
    }

    private static void VerifyCommittedGlbState(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportGlbRequest request,
        Guid createdId,
        GlbPreflightReceipt expectedCounts,
        GlbDefinitionIdentity expectedDefinition)
    {
        RhinoObject? existing = document.Objects.FindId(createdId);
        if (existing is not InstanceObject instance)
        {
            throw UncertainGlbImport(
                "The committed GLB root instance is missing.");
        }

        RhinoObject[] matches = FindExistingForMode(
            document,
            request.IdempotencyKey,
            ObjectType.InstanceReference);
        if (matches.Length != 1 || matches[0].Id != createdId)
        {
            throw UncertainGlbImport(
                "The committed GLB does not have exactly one identified root.");
        }

        string expectedFingerprint = CreateGlbRequestFingerprint(request);
        if (!string.Equals(
                existing.Attributes.GetUserString(
                    RequestFingerprintUserString),
                expectedFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                instance.InstanceDefinition.Name,
                DefinitionNamePrefix + request.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw UncertainGlbImport(
                "The committed GLB root identity no longer matches.");
        }

        VerifyGlbDefinitionFingerprint(instance.InstanceDefinition, request);
        GlbDefinitionIdentity actualDefinition =
            CreateGlbDefinitionIdentity(document, instance.InstanceDefinition);
        GlbPreflightReceipt actualReceipt = InspectImportedGlb(
            document,
            instance.InstanceDefinition.GetObjects());
        if (actualDefinition != expectedDefinition ||
            actualReceipt != expectedCounts ||
            ReadStoredCount(existing, VertexCountUserString, -1) !=
                expectedCounts.VertexCount ||
            ReadStoredCount(existing, TriangleCountUserString, -1) !=
                expectedCounts.TriangleCount ||
            ReadStoredCount(existing, MaterialCountUserString, -1) !=
                expectedCounts.MaterialCount ||
            ReadStoredCount(existing, TextureCountUserString, -1) !=
                expectedCounts.TextureCount ||
            ReadStoredCount(existing, DefinitionMemberCountUserString, -1) !=
                expectedDefinition.MemberCount ||
            !string.Equals(
                ReadStoredHash(existing, PbrContentDigestUserString),
                expectedCounts.PbrContentDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                existing.Attributes.GetUserString(
                    DefinitionMemberDigestUserString),
                expectedDefinition.MemberDigest,
                StringComparison.Ordinal))
        {
            throw UncertainGlbImport(
                "The committed GLB block membership or receipt changed.");
        }
    }

    private static GlbDefinitionIdentity CreateGlbDefinitionIdentity(
        global::Rhino.RhinoDoc document,
        InstanceDefinition definition)
    {
        int memberCount = definition.GetObjects().Length;
        if (memberCount == 0 ||
            memberCount > MaximumNativeGlbObjects)
        {
            throw UncertainGlbImport(
                "The native GLB definition has an invalid member count.");
        }

        GlbContentProof proof =
            CreateGlbContentProof(document, definition.GetObjects());
        return new GlbDefinitionIdentity(memberCount, proof.ContentDigest);
    }

    private static GlbContentProof CreateGlbContentProof(
        global::Rhino.RhinoDoc document,
        IReadOnlyList<RhinoObject> roots)
    {
        GlbProofContext context = new();
        string[] rootDigests = roots
            .Select(root =>
                CreateObjectContentDigest(
                    document,
                    root,
                    context,
                    depth: 0))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (rootDigests.Length == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The native GLB content proof has no reachable objects.");
        }

        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "tripo-rhino-glb-content-proof-v1");
        AppendDigestInt32(hash, rootDigests.Length);
        foreach (string digest in rootDigests)
        {
            AppendDigestField(hash, digest);
        }

        return new GlbContentProof(
            context.MaterialDigests.Count,
            context.TextureDigests.Count,
            FinishDigest(hash));
    }

    private static string CreateObjectContentDigest(
        global::Rhino.RhinoDoc document,
        RhinoObject item,
        GlbProofContext context,
        int depth)
    {
        if (depth > 32 ||
            ++context.VisitedObjectCount > MaximumNativeGlbObjects)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_too_complex",
                "The native GLB proof exceeds the supported object hierarchy.");
        }

        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "object");
        AppendDigestInt32(hash, (int)item.ObjectType);
        AppendDigestInt32(hash, (int)item.Attributes.MaterialSource);
        AppendDigestField(
            hash,
            CreateObjectMaterialBindingsDigest(
                document,
                item,
                context));
        AppendDigestField(hash, CreateTextureMappingsDigest(item));
        switch (item)
        {
            case MeshObject meshObject:
                AppendDigestField(
                    hash,
                    CreateMeshGeometryFingerprint(meshObject.MeshGeometry));
                break;
            case InstanceObject instance:
                AppendDigestTransform(hash, instance.InstanceXform);
                AppendDigestField(
                    hash,
                    CreateDefinitionContentDigest(
                        document,
                        instance.InstanceDefinition,
                        context,
                        depth + 1));
                break;
            default:
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "The native GLB proof encountered unsupported geometry.");
        }

        return FinishDigest(hash);
    }

    private static string CreateDefinitionContentDigest(
        global::Rhino.RhinoDoc document,
        InstanceDefinition definition,
        GlbProofContext context,
        int depth)
    {
        if (depth > 32 ||
            !context.ActiveDefinitionIds.Add(definition.Id))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The native GLB definition hierarchy is cyclic or too deep.");
        }

        try
        {
            RhinoObject[] members = definition.GetObjects();
            if (members.Length == 0 ||
                members.Length > MaximumNativeGlbObjects)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "The native GLB definition hierarchy has invalid membership.");
            }

            string[] memberDigests = members
                .Select(member =>
                    CreateObjectContentDigest(
                        document,
                        member,
                        context,
                        depth))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendDigestField(hash, "definition");
            AppendDigestInt32(hash, memberDigests.Length);
            foreach (string digest in memberDigests)
            {
                AppendDigestField(hash, digest);
            }

            return FinishDigest(hash);
        }
        finally
        {
            context.ActiveDefinitionIds.Remove(definition.Id);
        }
    }

    private static string CreateObjectMaterialBindingsDigest(
        global::Rhino.RhinoDoc document,
        RhinoObject item,
        GlbProofContext context)
    {
        List<string> bindings = new();
        AddMaterialBinding(
            bindings,
            "effective-front",
            item.GetRenderMaterial(frontMaterial: true),
            item.GetMaterial(frontMaterial: true),
            context);
        AddMaterialBinding(
            bindings,
            "effective-back",
            item.GetRenderMaterial(frontMaterial: false),
            item.GetMaterial(frontMaterial: false),
            context);
        switch (item.Attributes.MaterialSource)
        {
            case ObjectMaterialSource.MaterialFromObject:
                AddMaterialBinding(
                    bindings,
                    "object-direct",
                    item.RenderMaterial,
                    ResolveMaterial(
                        document,
                        item.Attributes.MaterialIndex),
                    context);
                break;
            case ObjectMaterialSource.MaterialFromLayer:
                Layer? layer =
                    document.Layers.FindIndex(item.Attributes.LayerIndex);
                if (layer is null)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "glb_invalid",
                        "The native GLB has an unresolved material layer.");
                }

                AddMaterialBinding(
                    bindings,
                    "layer",
                    layer.RenderMaterial,
                    ResolveMaterial(
                        document,
                        layer.RenderMaterialIndex),
                    context);
                break;
            case ObjectMaterialSource.MaterialFromParent:
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "Direct GLB import does not accept parent-inherited materials.");
            default:
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "The native GLB has an unsupported material source.");
        }

        foreach (KeyValuePair<Guid, MaterialRef> pair in
                 item.Attributes.MaterialRefs)
        {
            MaterialRef materialRef = pair.Value;
            if (materialRef.MaterialSource !=
                ObjectMaterialSource.MaterialFromObject)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "Direct GLB import only accepts object-bound plugin materials.");
            }

            using IncrementalHash referenceHash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendDigestField(referenceHash, "material-ref");
            AppendDigestField(referenceHash, pair.Key.ToString("D"));
            AppendDigestInt32(
                referenceHash,
                (int)materialRef.MaterialSource);
            AppendDigestField(
                referenceHash,
                CreateResolvedMaterialDigest(
                    item.GetRenderMaterial(
                        ComponentIndex.Unset,
                        pair.Key),
                    item.GetMaterial(
                        ComponentIndex.Unset,
                        pair.Key),
                    context));
            AppendDigestField(
                referenceHash,
                CreateResolvedMaterialDigest(
                    ResolveRenderMaterial(
                        document,
                        materialRef.FrontFaceMaterialId),
                    ResolveMaterial(
                        document,
                        materialRef.FrontFaceMaterialIndex),
                    context));
            AppendDigestField(
                referenceHash,
                CreateResolvedMaterialDigest(
                    ResolveRenderMaterial(
                        document,
                        materialRef.BackFaceMaterialId),
                    ResolveMaterial(
                        document,
                        materialRef.BackFaceMaterialIndex),
                    context));
            bindings.Add(FinishDigest(referenceHash));
        }

        foreach (ComponentIndex component in
                 item.SubobjectMaterialComponents
                     .OrderBy(value => value.ComponentIndexType)
                     .ThenBy(value => value.Index))
        {
            List<string> componentMaterials =
            [
                CreateResolvedMaterialDigest(
                    item.GetRenderMaterial(component),
                    item.GetMaterial(component),
                    context),
            ];
            foreach (Guid plugInId in
                     item.Attributes.MaterialRefs.Keys.OrderBy(
                         value => value))
            {
                using IncrementalHash plugInHash =
                    IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendDigestField(plugInHash, plugInId.ToString("D"));
                AppendDigestField(
                    plugInHash,
                    CreateResolvedMaterialDigest(
                        item.GetRenderMaterial(component, plugInId),
                        item.GetMaterial(component, plugInId),
                        context));
                componentMaterials.Add(FinishDigest(plugInHash));
            }

            componentMaterials.Sort(StringComparer.Ordinal);
            using IncrementalHash componentHash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendDigestField(componentHash, "subobject-material");
            AppendDigestInt32(
                componentHash,
                (int)component.ComponentIndexType);
            AppendDigestInt32(componentHash, component.Index);
            AppendDigestInt32(
                componentHash,
                componentMaterials.Count);
            foreach (string materialDigest in componentMaterials)
            {
                AppendDigestField(componentHash, materialDigest);
            }

            bindings.Add(FinishDigest(componentHash));
        }

        bindings.Sort(StringComparer.Ordinal);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "material-bindings");
        AppendDigestInt32(hash, bindings.Count);
        foreach (string binding in bindings)
        {
            AppendDigestField(hash, binding);
        }

        return FinishDigest(hash);
    }

    private static void AddMaterialBinding(
        List<string> bindings,
        string role,
        global::Rhino.Render.RenderMaterial? renderMaterial,
        Material? material,
        GlbProofContext context)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, role);
        AppendDigestField(
            hash,
            CreateResolvedMaterialDigest(
                renderMaterial,
                material,
                context));
        bindings.Add(FinishDigest(hash));
    }

    private static string CreateResolvedMaterialDigest(
        global::Rhino.Render.RenderMaterial? renderMaterial,
        Material? material,
        GlbProofContext context)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "resolved-material");
        if (renderMaterial is not null)
        {
            AppendDigestField(hash, "render");
            AppendDigestField(
                hash,
                CreateRenderContentDigest(
                    renderMaterial,
                    context,
                    new HashSet<Guid>(),
                    depth: 0));
            Material simulated = renderMaterial.ToMaterial(
                global::Rhino.Render.RenderTexture.TextureGeneration.Disallow);
            try
            {
                AppendDigestField(
                    hash,
                    CreateMaterialValueDigest(
                        simulated,
                        context,
                        countTextures: false));
            }
            finally
            {
                simulated.Dispose();
            }
        }
        else if (material is not null)
        {
            AppendDigestField(hash, "legacy");
            AppendDigestField(
                hash,
                CreateMaterialValueDigest(
                    material,
                    context,
                    countTextures: true));
        }
        else
        {
            AppendDigestField(hash, "none");
        }

        string digest = FinishDigest(hash);
        if ((renderMaterial is not null || material is not null) &&
            context.MaterialDigests.Add(digest) &&
            context.MaterialDigests.Count > MaximumNativeGlbMaterials)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_too_complex",
                "The native GLB proof exceeds the material limit.");
        }

        return digest;
    }

    private static string CreateRenderContentDigest(
        global::Rhino.Render.RenderContent content,
        GlbProofContext context,
        HashSet<Guid> activeContentIds,
        int depth)
    {
        if (depth > 32 || !activeContentIds.Add(content.Id))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The native GLB render-content hierarchy is cyclic or too deep.");
        }

        try
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendDigestField(hash, "render-content");
            AppendDigestField(hash, content.TypeId.ToString("D"));
            global::Rhino.Render.CrcRenderHashFlags flags =
                global::Rhino.Render.CrcRenderHashFlags
                    .ExcludeLinearWorkflow |
                global::Rhino.Render.CrcRenderHashFlags.ExcludeUnits |
                global::Rhino.Render.CrcRenderHashFlags
                    .ExcludeDocumentEffects;
            AppendDigestUInt32(
                hash,
                content.RenderHashExclude(flags, "filename"));
            AppendDigestField(hash, content.ChildSlotName ?? string.Empty);
            AppendDigestFileReferences(hash, content, context);

            if (content is global::Rhino.Render.RenderTexture texture)
            {
                AppendDigestField(hash, "texture");
                AppendDigestTransform(hash, texture.LocalMappingTransform);
                (int Width, int Height, int Depth)? size = texture.PixelSize2;
                AppendDigestBool(hash, size.HasValue);
                if (size.HasValue)
                {
                    AppendDigestInt32(hash, size.Value.Width);
                    AppendDigestInt32(hash, size.Value.Height);
                    AppendDigestInt32(hash, size.Value.Depth);
                }
            }

            int childIndex = 0;
            for (global::Rhino.Render.RenderContent? child =
                     content.FirstChild;
                 child is not null;
                 child = child.NextSibling)
            {
                if (++childIndex > MaximumNativeGlbTextures)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "glb_too_complex",
                        "The native GLB render-content hierarchy is too large.");
                }

                string slot = child.ChildSlotName ?? string.Empty;
                AppendDigestInt32(hash, childIndex);
                AppendDigestField(hash, slot);
                AppendDigestBool(
                    hash,
                    slot.Length > 0 && content.ChildSlotOn(slot));
                AppendDigestDouble(
                    hash,
                    slot.Length > 0
                        ? content.ChildSlotAmount(slot)
                        : 0d);
                AppendDigestField(
                    hash,
                    CreateRenderContentDigest(
                        child,
                        context,
                        activeContentIds,
                        depth + 1));
            }

            AppendDigestInt32(hash, childIndex);
            string digest = FinishDigest(hash);
            if (content is global::Rhino.Render.RenderTexture)
            {
                context.TextureDigests.Add(digest);
            }

            if (context.TextureDigests.Count > MaximumNativeGlbTextures)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_too_complex",
                    "The native GLB proof exceeds the texture limit.");
            }

            return digest;
        }
        finally
        {
            activeContentIds.Remove(content.Id);
        }
    }

    private static void AppendDigestFileReferences(
        IncrementalHash hash,
        global::Rhino.Render.RenderContent content,
        GlbProofContext context)
    {
        List<string> paths = new();
        if (!string.IsNullOrWhiteSpace(content.Filename))
        {
            paths.Add(content.Filename);
        }

        paths.AddRange(
            (content.FilesToEmbed ?? []).Where(
                path => !string.IsNullOrWhiteSpace(path)));
        paths.AddRange(
            (content.GetEmbeddedFilesList() ?? []).Where(
                path => !string.IsNullOrWhiteSpace(path)));
        string[] fileDigests = paths
            .Distinct(StringComparer.Ordinal)
            .Select(path => CreateTextureFileDigest(path, context))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        AppendDigestInt32(hash, fileDigests.Length);
        foreach (string digest in fileDigests)
        {
            AppendDigestField(hash, digest);
        }
    }

    private static string CreateMaterialValueDigest(
        Material material,
        GlbProofContext context,
        bool countTextures)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "material-values");
        AppendDigestBool(hash, material.IsPhysicallyBased);
        AppendDigestColor(hash, material.DiffuseColor);
        AppendDigestColor(hash, material.AmbientColor);
        AppendDigestColor(hash, material.EmissionColor);
        AppendDigestColor(hash, material.SpecularColor);
        AppendDigestColor(hash, material.ReflectionColor);
        AppendDigestColor(hash, material.TransparentColor);
        AppendDigestDouble(hash, material.Shine);
        AppendDigestDouble(hash, material.Transparency);
        AppendDigestDouble(hash, material.IndexOfRefraction);
        AppendDigestDouble(hash, material.FresnelIndexOfRefraction);
        AppendDigestDouble(hash, material.RefractionGlossiness);
        AppendDigestDouble(hash, material.ReflectionGlossiness);
        AppendDigestBool(hash, material.FresnelReflections);
        AppendDigestBool(hash, material.DisableLighting);
        AppendDigestBool(hash, material.AlphaTransparency);
        AppendDigestDouble(hash, material.Reflectivity);

        if (material.IsPhysicallyBased)
        {
            global::Rhino.DocObjects.PhysicallyBasedMaterial pbr =
                material.PhysicallyBased;
            AppendDigestColor4f(hash, pbr.BaseColor);
            AppendDigestInt32(hash, (int)pbr.BRDF);
            AppendDigestColor4f(
                hash,
                pbr.SubsurfaceScatteringColor);
            AppendDigestDouble(hash, pbr.Subsurface);
            AppendDigestDouble(
                hash,
                pbr.SubsurfaceScatteringRadius);
            AppendDigestDouble(hash, pbr.Metallic);
            AppendDigestDouble(hash, pbr.Specular);
            AppendDigestDouble(hash, pbr.ReflectiveIOR);
            AppendDigestDouble(hash, pbr.SpecularTint);
            AppendDigestDouble(hash, pbr.Roughness);
            AppendDigestDouble(hash, pbr.Anisotropic);
            AppendDigestDouble(hash, pbr.AnisotropicRotation);
            AppendDigestDouble(hash, pbr.Sheen);
            AppendDigestDouble(hash, pbr.SheenTint);
            AppendDigestDouble(hash, pbr.Clearcoat);
            AppendDigestDouble(hash, pbr.ClearcoatRoughness);
            AppendDigestDouble(hash, pbr.OpacityIOR);
            AppendDigestDouble(hash, pbr.Opacity);
            AppendDigestDouble(hash, pbr.OpacityRoughness);
            AppendDigestDouble(hash, pbr.Alpha);
            AppendDigestBool(
                hash,
                pbr.UseBaseColorTextureAlphaForObjectAlphaTransparencyTexture);
            AppendDigestColor4f(hash, pbr.Emission);
        }

        string[] textureDigests = material.GetTextures()
            .Select(texture =>
                CreateLegacyTextureDigest(
                    texture,
                    context,
                    countTextures))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        AppendDigestInt32(hash, textureDigests.Length);
        foreach (string textureDigest in textureDigests)
        {
            AppendDigestField(hash, textureDigest);
        }

        return FinishDigest(hash);
    }

    private static string CreateLegacyTextureDigest(
        Texture texture,
        GlbProofContext context,
        bool countTexture)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "legacy-texture");
        AppendDigestBool(hash, texture.Enabled);
        AppendDigestInt32(hash, (int)texture.TextureType);
        AppendDigestInt32(hash, (int)texture.MinFilter);
        AppendDigestInt32(hash, (int)texture.MagFilter);
        AppendDigestInt32(hash, texture.MappingChannelId);
        AppendDigestInt32(hash, (int)texture.ProjectionMode);
        AppendDigestBool(hash, texture.WcsProjected);
        AppendDigestBool(hash, texture.WcsBoxProjected);
        AppendDigestBool(hash, texture.TreatAsLinear);
        AppendDigestInt32(hash, (int)texture.TextureCombineMode);
        AppendDigestInt32(hash, (int)texture.WrapU);
        AppendDigestInt32(hash, (int)texture.WrapV);
        AppendDigestInt32(hash, (int)texture.WrapW);
        AppendDigestBool(hash, texture.ApplyUvwTransform);
        AppendDigestTransform(hash, texture.UvwTransform);
        AppendDigestDouble(hash, texture.Repeat.X);
        AppendDigestDouble(hash, texture.Repeat.Y);
        AppendDigestDouble(hash, texture.Offset.X);
        AppendDigestDouble(hash, texture.Offset.Y);
        AppendDigestDouble(hash, texture.Rotation);
        if (string.IsNullOrWhiteSpace(texture.FileName))
        {
            AppendDigestField(hash, string.Empty);
        }
        else
        {
            AppendDigestField(
                hash,
                CreateTextureFileDigest(texture.FileName, context));
        }

        string digest = FinishDigest(hash);
        if (countTexture)
        {
            context.TextureDigests.Add(digest);
            if (context.TextureDigests.Count > MaximumNativeGlbTextures)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_too_complex",
                    "The native GLB proof exceeds the texture limit.");
            }
        }

        return digest;
    }

    private static string CreateTextureMappingsDigest(RhinoObject item)
    {
        int[] channels = item.GetTextureChannels()
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "texture-mappings");
        AppendDigestInt32(hash, channels.Length);
        foreach (int channel in channels)
        {
            Transform objectTransform;
            global::Rhino.Render.TextureMapping mapping =
                item.GetTextureMapping(channel, out objectTransform);
            if (mapping is null)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "The native GLB has an unresolved texture mapping.");
            }

            AppendDigestInt32(hash, channel);
            AppendDigestInt32(hash, (int)mapping.MappingType);
            AppendDigestBool(hash, mapping.Capped);
            AppendDigestInt32(hash, (int)mapping.TextureSpace);
            AppendDigestTransform(hash, mapping.UvwTransform);
            AppendDigestTransform(hash, mapping.PrimitiveTransform);
            AppendDigestTransform(hash, mapping.NormalTransform);
            AppendDigestTransform(hash, objectTransform);
            global::Rhino.Render.CachedTextureCoordinates? cached =
                item.Geometry is Mesh mesh
                    ? mesh.GetCachedTextureCoordinates(mapping.Id)
                    : null;
            AppendDigestBool(hash, cached is not null);
            if (cached is not null)
            {
                AppendDigestInt32(hash, cached.Dim);
                AppendDigestInt32(hash, cached.Count);
                foreach (Point3d coordinate in cached)
                {
                    AppendDigestDouble(hash, coordinate.X);
                    AppendDigestDouble(hash, coordinate.Y);
                    AppendDigestDouble(hash, coordinate.Z);
                }
            }
        }

        return FinishDigest(hash);
    }

    private static string CreateMeshGeometryFingerprint(Mesh mesh)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestField(hash, "mesh");
        AppendDigestInt32(hash, mesh.Vertices.Count);
        AppendDigestInt32(hash, mesh.Faces.Count);
        AppendDigestInt32(hash, mesh.Normals.Count);
        AppendDigestInt32(hash, mesh.FaceNormals.Count);
        AppendDigestInt32(hash, mesh.VertexColors.Count);
        AppendDigestInt32(hash, mesh.TextureCoordinates.Count);
        foreach (Point3f vertex in mesh.Vertices)
        {
            AppendDigestPoint3f(hash, vertex);
        }

        foreach (MeshFace face in mesh.Faces)
        {
            AppendDigestInt32(hash, face.A);
            AppendDigestInt32(hash, face.B);
            AppendDigestInt32(hash, face.C);
            AppendDigestInt32(hash, face.D);
        }

        foreach (Vector3f normal in mesh.Normals)
        {
            AppendDigestVector3f(hash, normal);
        }

        foreach (Vector3f normal in mesh.FaceNormals)
        {
            AppendDigestVector3f(hash, normal);
        }

        foreach (System.Drawing.Color color in mesh.VertexColors)
        {
            AppendDigestColor(hash, color);
        }

        foreach (Point2f coordinate in mesh.TextureCoordinates)
        {
            AppendDigestSingle(hash, coordinate.X);
            AppendDigestSingle(hash, coordinate.Y);
        }

        return FinishDigest(hash);
    }

    private static Material? ResolveMaterial(
        global::Rhino.RhinoDoc document,
        int materialIndex) =>
        materialIndex >= 0 && materialIndex < document.Materials.Count
            ? document.Materials[materialIndex]
            : null;

    private static global::Rhino.Render.RenderMaterial? ResolveRenderMaterial(
        global::Rhino.RhinoDoc document,
        Guid renderMaterialId) =>
        renderMaterialId == Guid.Empty
            ? null
            : global::Rhino.Render.RenderContent.FromId(
                document,
                renderMaterialId) as global::Rhino.Render.RenderMaterial;

    private static string CreateTextureFileDigest(
        string path,
        GlbProofContext context)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                  NotSupportedException or
                  PathTooLongException)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The native GLB texture path was invalid.",
                exception);
        }

        if (context.FileDigests.TryGetValue(
                fullPath,
                out string? existing))
        {
            return existing;
        }

        FileInfo before = new(fullPath);
        before.Refresh();
        const long maximumTextureFileBytes = 64L * 1024 * 1024;
        if (!before.Exists ||
            before.LinkTarget is not null ||
            (before.Attributes &
             (FileAttributes.Directory |
              FileAttributes.Device |
              FileAttributes.ReparsePoint)) != 0 ||
            before.Length <= 0 ||
            before.Length > maximumTextureFileBytes ||
            context.HashedFileBytes + before.Length >
                Tripo.Bridge.BridgeConstants.MaximumArtifactBytes)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The native GLB texture file was missing or unsafe.");
        }

        long expectedLength = before.Length;
        DateTime expectedWriteTime = before.LastWriteTimeUtc;
        string digest;
        using (FileStream stream = new(
                   fullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            if (stream.Length != expectedLength)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "glb_invalid",
                    "The native GLB texture changed while it was inspected.");
            }

            digest = Convert.ToHexString(SHA256.HashData(stream))
                .ToLowerInvariant();
        }

        FileInfo after = new(fullPath);
        after.Refresh();
        if (!after.Exists ||
            after.LinkTarget is not null ||
            (after.Attributes &
             (FileAttributes.Directory |
              FileAttributes.Device |
              FileAttributes.ReparsePoint)) != 0 ||
            after.Length != expectedLength ||
            after.LastWriteTimeUtc != expectedWriteTime)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "glb_invalid",
                "The native GLB texture changed while it was inspected.");
        }

        context.HashedFileBytes += expectedLength;
        context.FileDigests.Add(fullPath, digest);
        return digest;
    }

    private static void AppendDigestField(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendDigestInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendDigestBool(
        IncrementalHash hash,
        bool value) =>
        hash.AppendData(value ? [1] : [0]);

    private static void AppendDigestInt32(
        IncrementalHash hash,
        int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendDigestUInt32(
        IncrementalHash hash,
        uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendDigestSingle(
        IncrementalHash hash,
        float value) =>
        AppendDigestInt32(hash, BitConverter.SingleToInt32Bits(value));

    private static void AppendDigestDouble(
        IncrementalHash hash,
        double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes,
            BitConverter.DoubleToInt64Bits(value));
        hash.AppendData(bytes);
    }

    private static void AppendDigestPoint3f(
        IncrementalHash hash,
        Point3f value)
    {
        AppendDigestSingle(hash, value.X);
        AppendDigestSingle(hash, value.Y);
        AppendDigestSingle(hash, value.Z);
    }

    private static void AppendDigestVector3f(
        IncrementalHash hash,
        Vector3f value)
    {
        AppendDigestSingle(hash, value.X);
        AppendDigestSingle(hash, value.Y);
        AppendDigestSingle(hash, value.Z);
    }

    private static void AppendDigestColor(
        IncrementalHash hash,
        System.Drawing.Color value) =>
        AppendDigestInt32(hash, value.ToArgb());

    private static void AppendDigestColor4f(
        IncrementalHash hash,
        global::Rhino.Display.Color4f value)
    {
        AppendDigestSingle(hash, value.R);
        AppendDigestSingle(hash, value.G);
        AppendDigestSingle(hash, value.B);
        AppendDigestSingle(hash, value.A);
    }

    private static void AppendDigestTransform(
        IncrementalHash hash,
        Transform transform)
    {
        double[] coefficients =
        [
            transform.M00, transform.M01, transform.M02, transform.M03,
            transform.M10, transform.M11, transform.M12, transform.M13,
            transform.M20, transform.M21, transform.M22, transform.M23,
            transform.M30, transform.M31, transform.M32, transform.M33,
        ];
        foreach (double coefficient in coefficients)
        {
            AppendDigestDouble(hash, coefficient);
        }
    }

    private static string FinishDigest(IncrementalHash hash) =>
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    private static Tripo.Bridge.BridgeCallException UncertainGlbImport(
        string message) =>
        new(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            message + " Do not retry this operation; inspect the Rhino document manually.");

    private static void ApplyGlbIdentityUserStrings(
        ObjectAttributes attributes,
        Tripo.Bridge.ImportGlbRequest request,
        GlbPreflightReceipt counts,
        GlbDefinitionIdentity? definitionIdentity = null)
    {
        if (!attributes.SetUserString(
                IdempotencyUserString,
                request.IdempotencyKey) ||
            !attributes.SetUserString(
                RequestFingerprintUserString,
                CreateGlbRequestFingerprint(request)) ||
            !attributes.SetUserString(
                DocumentSessionUserString,
                request.DocumentSessionId) ||
            !attributes.SetUserString(
                VertexCountUserString,
                counts.VertexCount.ToString(CultureInfo.InvariantCulture)) ||
            !attributes.SetUserString(
                TriangleCountUserString,
                counts.TriangleCount.ToString(CultureInfo.InvariantCulture)) ||
            !attributes.SetUserString(
                RejectedCountUserString,
                "0") ||
            !attributes.SetUserString(
                MaterialCountUserString,
                counts.MaterialCount.ToString(CultureInfo.InvariantCulture)) ||
            !attributes.SetUserString(
                TextureCountUserString,
                counts.TextureCount.ToString(CultureInfo.InvariantCulture)) ||
            !attributes.SetUserString(
                PbrContentDigestUserString,
                counts.PbrContentDigest))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "host_metadata_failed",
                "Rhino could not attach the native GLB import identity metadata.");
        }

        if (definitionIdentity is not null &&
            (!attributes.SetUserString(
                 DefinitionMemberCountUserString,
                 definitionIdentity.MemberCount.ToString(
                     CultureInfo.InvariantCulture)) ||
             !attributes.SetUserString(
                 DefinitionMemberDigestUserString,
                 definitionIdentity.MemberDigest)))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "host_metadata_failed",
                "Rhino could not attach the native GLB block membership metadata.");
        }
    }

    private static void ApplyGlbDefinitionMemberIdentity(
        ObjectAttributes attributes,
        Tripo.Bridge.ImportGlbRequest request,
        GlbPreflightReceipt counts)
    {
        ApplyGlbIdentityUserStrings(attributes, request, counts);
        attributes.DeleteUserString(DocumentSessionUserString);
    }

    private static void VerifyGlbDefinitionFingerprint(
        InstanceDefinition definition,
        Tripo.Bridge.ImportGlbRequest request)
    {
        string expectedFingerprint = CreateGlbRequestFingerprint(request);
        RhinoObject[] members = definition.GetObjects();
        if (members.Length == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "The existing native GLB block holds no fingerprinted geometry.");
        }

        foreach (RhinoObject member in members)
        {
            if (member.Attributes.GetUserString(
                    GlbMarkerStateUserString) is not null)
            {
                throw UncertainGlbImport(
                    "The native GLB definition is still a prepared marker.");
            }

            string? fingerprint =
                member.Attributes.GetUserString(RequestFingerprintUserString);
            if (!string.Equals(
                    expectedFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "idempotency_conflict",
                    "The existing Rhino block definition belongs to a different import request.");
            }
        }
    }

    private static Tripo.Bridge.HostImportReceipt CreateGlbReceipt(
        Tripo.Bridge.ImportGlbRequest request,
        string createdId,
        GlbPreflightReceipt counts,
        string transactionStatus) =>
        new(
            "rhino",
            request.DocumentSessionId,
            request.IdempotencyKey,
            createdId,
            counts.VertexCount,
            counts.TriangleCount,
            RejectedTriangleCount: 0,
            transactionStatus,
            ImportMode: "glb_instance",
            counts.MaterialCount,
            counts.TextureCount,
            SavedFamilyPath: null);

    private static string CreateGlbRequestFingerprint(
        Tripo.Bridge.ImportGlbRequest request)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            request with { DocumentSessionId = string.Empty },
            Tripo.Bridge.BridgeJson.Options);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private sealed class GlbGeometryCounts
    {
        public int ObjectCount { get; set; }

        public int MeshCount { get; set; }

        public long VertexCount { get; set; }

        public long TriangleCount { get; set; }
    }

    private sealed record GlbPreflightReceipt(
        int VertexCount,
        int TriangleCount,
        int MaterialCount,
        int TextureCount,
        string PbrContentDigest);

    private sealed record GlbContentProof(
        int MaterialCount,
        int TextureCount,
        string ContentDigest);

    private sealed class GlbProofContext
    {
        public HashSet<Guid> ActiveDefinitionIds { get; } = [];

        public HashSet<string> MaterialDigests { get; } =
            new(StringComparer.Ordinal);

        public HashSet<string> TextureDigests { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, string> FileDigests { get; } =
            new(StringComparer.Ordinal);

        public int VisitedObjectCount { get; set; }

        public long HashedFileBytes { get; set; }
    }

    private sealed record GlbDefinitionIdentity(
        int MemberCount,
        string MemberDigest);

    private sealed record RhinoDocumentState(
        HashSet<Guid> ObjectIds,
        HashSet<Guid> DefinitionIds,
        HashSet<Guid> MaterialIds,
        HashSet<Guid> RenderMaterialIds,
        HashSet<Guid> RenderTextureIds,
        HashSet<string> EmbeddedFiles,
        HashSet<Guid> LayerIds,
        global::Rhino.UnitSystem ModelUnitSystem,
        int CurrentLayerIndex);

    // ---- mesh mode --------------------------------------------------------

    private static Tripo.Bridge.HostImportReceipt ImportMeshOnUiThread(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared)
    {
        // A single mesh object can only carry one material. Multiple slots plus
        // materials require the per-slot fidelity of instance mode; refuse rather
        // than silently collapsing colors. A zero/single-slot request applies that
        // one material. This guard runs before any mutation.
        if (request.ApplyMaterials && prepared.Materials.Count > 1)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "import_mode_unsupported",
                "Mesh mode cannot apply more than one material; use instance mode for this model.");
        }

        RhinoObject[] existing = FindExistingForMode(
            document,
            request.IdempotencyKey,
            ObjectType.Mesh);
        if (existing.Length > 1)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "Multiple Rhino objects already carry this idempotency key.");
        }

        if (existing.Length == 1)
        {
            return CreateExistingReceipt(request, existing[0], prepared);
        }

        EnsureCanRecordUndo(document);

        using Mesh rhinoMesh = BuildMesh(document, prepared, applyUvs: request.ApplyMaterials);
        ObjectAttributes attributes = new()
        {
            Name = request.Name,
        };
        ApplyIdentityUserStrings(
            attributes,
            request,
            rhinoMesh.Vertices.Count,
            rhinoMesh.Faces.Count,
            prepared.RejectedTriangleCount);

        uint undoRecord = document.BeginUndoRecord("Import Tripo mesh");
        if (undoRecord == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "undo_unavailable",
                "Rhino could not start an undo record for the import.");
        }

        Guid createdId = Guid.Empty;
        int materialCount = 0;
        int textureCount = 0;
        bool undoEnded;
        try
        {
            if (request.ApplyMaterials && prepared.Materials.Count > 0)
            {
                int[] slotMaterialIndex = CreateMaterials(
                    document,
                    prepared,
                    out materialCount,
                    out textureCount);
                int meshMaterialIndex = slotMaterialIndex.Length > 0
                    ? slotMaterialIndex[0]
                    : -1;
                if (meshMaterialIndex >= 0)
                {
                    attributes.MaterialIndex = meshMaterialIndex;
                    attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                }
            }

            createdId = document.Objects.AddMesh(rhinoMesh, attributes);
            if (createdId == Guid.Empty)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino rejected the prepared mesh.");
            }
        }
        finally
        {
            undoEnded = document.EndUndoRecord(undoRecord);
        }

        VerifyUndoEnded(document, undoEnded, createdId);

        document.Views.Redraw();
        return CreateReceipt(
            request,
            createdId.ToString("D"),
            rhinoMesh.Vertices.Count,
            rhinoMesh.Faces.Count,
            prepared.RejectedTriangleCount,
            materialCount,
            textureCount,
            "committed");
    }

    // ---- instance mode ----------------------------------------------------

    private static Tripo.Bridge.HostImportReceipt ImportInstanceOnUiThread(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared)
    {
        RhinoObject[] existing = FindExistingForMode(
            document,
            request.IdempotencyKey,
            ObjectType.InstanceReference);
        if (existing.Length > 1)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "Multiple Rhino instances already carry this idempotency key.");
        }

        // (a) Our instance already exists: fingerprint check then already_exists.
        if (existing.Length == 1)
        {
            return CreateExistingReceipt(request, existing[0], prepared);
        }

        string definitionName = DefinitionNamePrefix + request.IdempotencyKey;
        // Find is case-insensitive and never returns deleted definitions (which are
        // now purged permanently), so a hit is a live block named exactly ours.
        InstanceDefinition? existingDefinition =
            document.InstanceDefinitions.Find(definitionName);

        EnsureCanRecordUndo(document);

        if (existingDefinition is not null)
        {
            // The definition "Tripo_<key>" exists but no instance carries our
            // user strings. Either (b) a prior import crashed between definition
            // and instance creation, or (c) the name belongs to something else.
            InstanceObject[] references = existingDefinition.GetReferences(1);
            if (references.Length > 0)
            {
                // Referenced instances exist yet none carry our idempotency key
                // (else the lookup above would have found them): not ours.
                throw new Tripo.Bridge.BridgeCallException(
                    "idempotency_conflict",
                    "A block definition with this name already carries instances that are not ours.");
            }

            // (b) Name match plus a fingerprint match on the definition's own
            // geometry is our ownership proof; complete the import by adding only the
            // instance to the existing definition inside the undo record.
            return ReconcileInstance(document, request, prepared, existingDefinition);
        }

        // Fresh create: definition + instance under one undo record.
        return CreateInstance(document, request, prepared, definitionName);
    }

    private static Tripo.Bridge.HostImportReceipt CreateInstance(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared,
        string definitionName)
    {
        uint undoRecord = document.BeginUndoRecord("Import Tripo mesh");
        if (undoRecord == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "undo_unavailable",
                "Rhino could not start an undo record for the import.");
        }

        List<Mesh> subMeshes = new();
        Guid createdId = Guid.Empty;
        int definitionIndex = -1;
        int materialCount = 0;
        int textureCount = 0;
        int vertexTotal = 0;
        int triangleTotal = 0;
        bool undoEnded;
        try
        {
            int[] slotMaterialIndex;
            if (request.ApplyMaterials && prepared.Materials.Count > 0)
            {
                slotMaterialIndex = CreateMaterials(
                    document,
                    prepared,
                    out materialCount,
                    out textureCount);
            }
            else
            {
                slotMaterialIndex = [];
            }

            double scale = MetersToDocumentScale(document);
            List<GeometryBase> geometry = new();
            List<ObjectAttributes> geometryAttributes = new();
            BuildSubMeshes(
                request,
                prepared,
                slotMaterialIndex,
                scale,
                subMeshes,
                geometry,
                geometryAttributes,
                out vertexTotal,
                out triangleTotal);

            ObjectAttributes instanceAttributes = new()
            {
                Name = request.Name,
            };
            // Set identity metadata before creating the definition so a metadata
            // failure leaves no orphan definition to reconcile against.
            ApplyIdentityUserStrings(
                instanceAttributes,
                request,
                vertexTotal,
                triangleTotal,
                prepared.RejectedTriangleCount);

            definitionIndex = document.InstanceDefinitions.Add(
                definitionName,
                request.Name,
                Point3d.Origin,
                geometry,
                geometryAttributes);
            if (definitionIndex < 0)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino could not create the block definition for the import.");
            }

            createdId = document.Objects.AddInstanceObject(
                definitionIndex,
                Transform.Identity,
                instanceAttributes);
            if (createdId == Guid.Empty)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino rejected the block instance for the import.");
            }
        }
        finally
        {
            undoEnded = document.EndUndoRecord(undoRecord);
            foreach (Mesh subMesh in subMeshes)
            {
                subMesh.Dispose();
            }
        }

        VerifyInstanceUndoEnded(document, undoEnded, definitionIndex);

        document.Views.Redraw();
        return CreateReceipt(
            request,
            createdId.ToString("D"),
            vertexTotal,
            triangleTotal,
            prepared.RejectedTriangleCount,
            materialCount,
            textureCount,
            "committed");
    }

    private static Tripo.Bridge.HostImportReceipt ReconcileInstance(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared,
        InstanceDefinition definition)
    {
        // Reject a leftover definition that is not ours before any mutation: the name
        // matched, but reuse also requires the stored fingerprint to match exactly.
        VerifyDefinitionFingerprint(definition, request);

        // The definition already holds the (compacted) sub-meshes, so read the
        // authoritative geometry counts from it. Material/texture counts come from
        // the freshly prepared request: an existing definition proves the prior
        // material creation (and any bitmap binding) already succeeded.
        SumDefinitionGeometry(definition, out int vertexTotal, out int triangleTotal);
        (int materialCount, int textureCount) =
            DryCountMaterials(prepared, request.ApplyMaterials);

        uint undoRecord = document.BeginUndoRecord("Import Tripo mesh");
        if (undoRecord == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "undo_unavailable",
                "Rhino could not start an undo record for the import.");
        }

        Guid createdId = Guid.Empty;
        bool undoEnded;
        try
        {
            ObjectAttributes instanceAttributes = new()
            {
                Name = request.Name,
            };
            ApplyIdentityUserStrings(
                instanceAttributes,
                request,
                vertexTotal,
                triangleTotal,
                prepared.RejectedTriangleCount);

            createdId = document.Objects.AddInstanceObject(
                definition.Index,
                Transform.Identity,
                instanceAttributes);
            if (createdId == Guid.Empty)
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "host_import_failed",
                    "Rhino rejected the reconciled block instance.");
            }
        }
        finally
        {
            undoEnded = document.EndUndoRecord(undoRecord);
        }

        VerifyUndoEnded(document, undoEnded, createdId);

        document.Views.Redraw();
        return CreateReceipt(
            request,
            createdId.ToString("D"),
            vertexTotal,
            triangleTotal,
            prepared.RejectedTriangleCount,
            materialCount,
            textureCount,
            "committed");
    }

    private static void BuildSubMeshes(
        Tripo.Bridge.ImportMeshRequest request,
        Tripo.Bridge.PreparedMesh prepared,
        int[] slotMaterialIndex,
        double scale,
        List<Mesh> subMeshes,
        List<GeometryBase> geometry,
        List<ObjectAttributes> geometryAttributes,
        out int vertexTotal,
        out int triangleTotal)
    {
        // Group triangles by material slot (-1 = unassigned), preserving first-seen
        // order for deterministic definition geometry.
        Dictionary<int, List<int>> trianglesBySlot = new();
        List<int> slotOrder = new();
        for (int i = 0; i < prepared.Triangles.Count; i++)
        {
            int slot = prepared.Triangles[i].MaterialSlot;
            if (!trianglesBySlot.TryGetValue(slot, out List<int>? bucket))
            {
                bucket = new List<int>();
                trianglesBySlot[slot] = bucket;
                slotOrder.Add(slot);
            }

            bucket.Add(i);
        }

        vertexTotal = 0;
        triangleTotal = 0;
        bool hasUvs = prepared.Uvs.Count > 0;
        foreach (int slot in slotOrder)
        {
            Mesh mesh = new();
            Dictionary<int, int> compactMap = new();
            foreach (int triangleIndex in trianglesBySlot[slot])
            {
                Tripo.Bridge.MeshTriangle triangle = prepared.Triangles[triangleIndex];
                int a = MapVertex(triangle.A, prepared, scale, hasUvs, mesh, compactMap);
                int b = MapVertex(triangle.B, prepared, scale, hasUvs, mesh, compactMap);
                int c = MapVertex(triangle.C, prepared, scale, hasUvs, mesh, compactMap);
                mesh.Faces.AddFace(a, b, c);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();
            if (!mesh.IsValid)
            {
                mesh.Dispose();
                throw new Tripo.Bridge.BridgeCallException(
                    "mesh_invalid",
                    "A prepared material sub-mesh was not valid for Rhino.");
            }

            int materialIndex = slot >= 0 && slot < slotMaterialIndex.Length
                ? slotMaterialIndex[slot]
                : -1;
            ObjectAttributes attributes = new();
            if (materialIndex >= 0)
            {
                attributes.MaterialIndex = materialIndex;
                attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
            }

            // The definition's own geometry carries the idempotency key and
            // fingerprint so the definition-without-instance reconcile path can
            // verify ownership before reusing a leftover definition
            // (per MATERIALS-DESIGN.md §Idempotency).
            ApplyDefinitionMemberIdentity(attributes, request);

            subMeshes.Add(mesh);
            geometry.Add(mesh);
            geometryAttributes.Add(attributes);
            vertexTotal += mesh.Vertices.Count;
            triangleTotal += mesh.Faces.Count;
        }
    }

    private static int MapVertex(
        int vertex,
        Tripo.Bridge.PreparedMesh prepared,
        double scale,
        bool hasUvs,
        Mesh mesh,
        Dictionary<int, int> compactMap)
    {
        if (compactMap.TryGetValue(vertex, out int existing))
        {
            return existing;
        }

        Tripo.Bridge.MeshPoint3 point = prepared.VerticesInMeters[vertex];
        int index = mesh.Vertices.Add(
            point.X * scale,
            point.Y * scale,
            point.Z * scale);
        if (hasUvs)
        {
            // OBJ and Rhino share a bottom-left UV origin, so V is never flipped
            // anywhere on this import path (per MATERIALS-DESIGN.md).
            Tripo.Bridge.MeshPoint2 uv = prepared.Uvs[vertex];
            mesh.TextureCoordinates.Add(uv.U, uv.V);
        }

        compactMap[vertex] = index;
        return index;
    }

    private static void SumDefinitionGeometry(
        InstanceDefinition definition,
        out int vertexTotal,
        out int triangleTotal)
    {
        vertexTotal = 0;
        triangleTotal = 0;
        foreach (RhinoObject member in definition.GetObjects())
        {
            if (member.Geometry is Mesh mesh)
            {
                vertexTotal += mesh.Vertices.Count;
                triangleTotal += mesh.Faces.Count;
            }
        }
    }

    // ---- materials --------------------------------------------------------

    private static int[] CreateMaterials(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.PreparedMesh prepared,
        out int materialCount,
        out int textureCount)
    {
        int[] slotMaterialIndex = new int[prepared.Materials.Count];
        List<int> addedMaterialIndices = new();
        int addedMaterials = 0;
        int addedTextures = 0;
        try
        {
            for (int i = 0; i < prepared.Materials.Count; i++)
            {
                Tripo.Bridge.PreparedMaterial source = prepared.Materials[i];
                bool hasColor = source.DiffuseArgb.HasValue;
                bool hasTexture = source.DiffuseTextureAbsolutePath is not null;
                if (!hasColor && !hasTexture)
                {
                    // Neither color nor texture: honest default material (index -1).
                    slotMaterialIndex[i] = -1;
                    continue;
                }

                using Material material = new();
                if (hasColor)
                {
                    material.DiffuseColor =
                        System.Drawing.Color.FromArgb(source.DiffuseArgb!.Value);
                }

                if (hasTexture)
                {
                    if (!material.SetBitmapTexture(source.DiffuseTextureAbsolutePath!))
                    {
                        throw new Tripo.Bridge.BridgeCallException(
                            "mtl_invalid",
                            "Rhino could not bind the diffuse texture bitmap for a material.");
                    }

                    addedTextures++;
                }

                int index = document.Materials.Add(material);
                if (index < 0)
                {
                    throw new Tripo.Bridge.BridgeCallException(
                        "host_material_failed",
                        "Rhino could not add a render material to the document.");
                }

                addedMaterialIndices.Add(index);
                slotMaterialIndex[i] = index;
                addedMaterials++;
            }
        }
        catch (Exception exception)
        {
            // Partial-mutation cleanup: a mid-loop failure (mtl_invalid /
            // host_material_failed) leaves the materials already added orphaned in
            // the document with no receipt. Roll those back best-effort before
            // rethrowing; if any cannot be removed, report it honestly rather than
            // leak silently (per MATERIALS-DESIGN.md, fail closed, no silent state).
            int undeleted = CleanupAddedMaterials(document, addedMaterialIndices);
            if (undeleted == 0)
            {
                throw;
            }

            string note =
                $" Additionally, {undeleted} partially created material(s) could not be" +
                " removed and remain in the document.";
            throw exception is Tripo.Bridge.BridgeCallException bridge
                ? new Tripo.Bridge.BridgeCallException(
                    bridge.Code,
                    bridge.Message + note,
                    bridge)
                : new Tripo.Bridge.BridgeCallException(
                    "host_material_failed",
                    exception.Message + note,
                    exception);
        }

        materialCount = addedMaterials;
        textureCount = addedTextures;
        return slotMaterialIndex;
    }

    private static int CleanupAddedMaterials(
        global::Rhino.RhinoDoc document,
        List<int> addedMaterialIndices)
    {
        // Delete newest-first. MaterialTable.DeleteAt marks a material deleted without
        // renumbering the table, so earlier recorded indices stay valid throughout.
        // A false return (or a throw) counts as undeleted so the caller can disclose
        // exactly what leaked instead of swallowing the failure.
        int undeleted = 0;
        for (int i = addedMaterialIndices.Count - 1; i >= 0; i--)
        {
            bool deleted;
            try
            {
                deleted = document.Materials.DeleteAt(addedMaterialIndices[i]);
            }
            catch (Exception)
            {
                deleted = false;
            }

            if (!deleted)
            {
                undeleted++;
            }
        }

        return undeleted;
    }

    private static (int MaterialCount, int TextureCount) DryCountMaterials(
        Tripo.Bridge.PreparedMesh prepared,
        bool applyMaterials)
    {
        if (!applyMaterials)
        {
            return (0, 0);
        }

        int materialCount = 0;
        int textureCount = 0;
        foreach (Tripo.Bridge.PreparedMaterial source in prepared.Materials)
        {
            bool hasColor = source.DiffuseArgb.HasValue;
            bool hasTexture = source.DiffuseTextureAbsolutePath is not null;
            if (hasColor || hasTexture)
            {
                materialCount++;
            }

            if (hasTexture)
            {
                textureCount++;
            }
        }

        return (materialCount, textureCount);
    }

    // ---- shared helpers ---------------------------------------------------

    private static Mesh BuildMesh(
        global::Rhino.RhinoDoc document,
        Tripo.Bridge.PreparedMesh prepared,
        bool applyUvs)
    {
        double scale = MetersToDocumentScale(document);
        Mesh mesh = new();
        mesh.Vertices.Capacity = prepared.VerticesInMeters.Count;
        bool hasUvs = applyUvs && prepared.Uvs.Count > 0;
        for (int i = 0; i < prepared.VerticesInMeters.Count; i++)
        {
            Tripo.Bridge.MeshPoint3 point = prepared.VerticesInMeters[i];
            mesh.Vertices.Add(
                point.X * scale,
                point.Y * scale,
                point.Z * scale);
            if (hasUvs)
            {
                // OBJ and Rhino share a bottom-left UV origin, so V is never
                // flipped anywhere on this import path (per MATERIALS-DESIGN.md).
                Tripo.Bridge.MeshPoint2 uv = prepared.Uvs[i];
                mesh.TextureCoordinates.Add(uv.U, uv.V);
            }
        }

        mesh.Faces.Capacity = prepared.Triangles.Count;
        foreach (Tripo.Bridge.MeshTriangle triangle in prepared.Triangles)
        {
            mesh.Faces.AddFace(triangle.A, triangle.B, triangle.C);
        }

        mesh.Normals.ComputeNormals();
        mesh.Compact();
        if (!mesh.IsValid)
        {
            mesh.Dispose();
            throw new Tripo.Bridge.BridgeCallException(
                "mesh_invalid",
                "The prepared mesh was not valid for Rhino.");
        }

        return mesh;
    }

    private static double MetersToDocumentScale(global::Rhino.RhinoDoc document)
    {
        double scale = global::Rhino.RhinoMath.UnitScale(
            global::Rhino.UnitSystem.Meters,
            document.ModelUnitSystem);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "document_units_invalid",
                "Rhino could not convert meters to the document unit system.");
        }

        return scale;
    }

    private static RhinoObject[] FindExistingForMode(
        global::Rhino.RhinoDoc document,
        string idempotencyKey,
        ObjectType ownType)
    {
        // Query BOTH import object types for the key, not only this mode's: a replay
        // of the same key in the other mode would otherwise miss the existing object
        // and silently create a duplicate. A hit whose type belongs to the other mode
        // is a cross-mode conflict (fail closed, naming that mode); own-mode hits keep
        // the existing fingerprint/replay behavior (per MATERIALS-DESIGN.md §Idempotency).
        RhinoObject[] matches = GetAuditedTopLevelObjects(document)
            .Where(item =>
                (item.ObjectType == ObjectType.Mesh ||
                 item.ObjectType == ObjectType.InstanceReference) &&
                string.Equals(
                    item.Attributes.GetUserString(IdempotencyUserString),
                    idempotencyKey,
                    StringComparison.Ordinal))
            .ToArray();

        List<RhinoObject> ownMode = new();
        foreach (RhinoObject match in matches)
        {
            if (match.ObjectType == ownType)
            {
                ownMode.Add(match);
            }
            else
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "idempotency_conflict",
                    ownType == ObjectType.Mesh
                        ? "The Rhino idempotency key is already bound to an instance-mode import."
                        : "The Rhino idempotency key is already bound to a mesh-mode import.");
            }
        }

        return ownMode.ToArray();
    }

    private static Tripo.Bridge.HostImportReceipt CreateExistingReceipt(
        Tripo.Bridge.ImportMeshRequest request,
        RhinoObject existing,
        Tripo.Bridge.PreparedMesh prepared)
    {
        string expectedFingerprint = CreateRequestFingerprint(request);
        string? existingFingerprint =
            existing.Attributes.GetUserString(RequestFingerprintUserString);
        if (!string.Equals(
                expectedFingerprint,
                existingFingerprint,
                StringComparison.Ordinal))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "The Rhino idempotency key is already bound to a different import request.");
        }

        (int materialCount, int textureCount) =
            DryCountMaterials(prepared, request.ApplyMaterials);
        return CreateReceipt(
            request,
            existing.Id.ToString("D"),
            ReadStoredCount(
                existing,
                VertexCountUserString,
                prepared.VerticesInMeters.Count),
            ReadStoredCount(
                existing,
                TriangleCountUserString,
                prepared.Triangles.Count),
            ReadStoredCount(
                existing,
                RejectedCountUserString,
                prepared.RejectedTriangleCount),
            materialCount,
            textureCount,
            "already_exists");
    }

    private static void ApplyIdentityUserStrings(
        ObjectAttributes attributes,
        Tripo.Bridge.ImportMeshRequest request,
        int vertexCount,
        int triangleCount,
        int rejectedTriangleCount)
    {
        if (!attributes.SetUserString(
                IdempotencyUserString,
                request.IdempotencyKey) ||
            !attributes.SetUserString(
                RequestFingerprintUserString,
                CreateRequestFingerprint(request)) ||
            !attributes.SetUserString(
                DocumentSessionUserString,
                request.DocumentSessionId) ||
            !attributes.SetUserString(
                VertexCountUserString,
                vertexCount.ToString(CultureInfo.InvariantCulture)) ||
            !attributes.SetUserString(
                TriangleCountUserString,
                triangleCount.ToString(CultureInfo.InvariantCulture)) ||
            !attributes.SetUserString(
                RejectedCountUserString,
                rejectedTriangleCount.ToString(CultureInfo.InvariantCulture)))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "host_metadata_failed",
                "Rhino could not attach the import identity metadata.");
        }
    }

    private static void ApplyDefinitionMemberIdentity(
        ObjectAttributes attributes,
        Tripo.Bridge.ImportMeshRequest request)
    {
        if (!attributes.SetUserString(
                IdempotencyUserString,
                request.IdempotencyKey) ||
            !attributes.SetUserString(
                RequestFingerprintUserString,
                CreateRequestFingerprint(request)))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "host_metadata_failed",
                "Rhino could not attach the block definition identity metadata.");
        }
    }

    private static void VerifyDefinitionFingerprint(
        InstanceDefinition definition,
        Tripo.Bridge.ImportMeshRequest request)
    {
        // No unverified reuse: every geometry member of a leftover definition must
        // carry exactly our fingerprint before we add an instance to it. A missing
        // or differing fingerprint means the definition is not ours, even though the
        // name matched (per MATERIALS-DESIGN.md §Idempotency).
        string expectedFingerprint = CreateRequestFingerprint(request);
        RhinoObject[] members = definition.GetObjects();
        if (members.Length == 0)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "idempotency_conflict",
                "The existing Rhino block definition holds no fingerprinted geometry to verify.");
        }

        foreach (RhinoObject member in members)
        {
            string? memberFingerprint =
                member.Attributes.GetUserString(RequestFingerprintUserString);
            if (!string.Equals(
                    expectedFingerprint,
                    memberFingerprint,
                    StringComparison.Ordinal))
            {
                throw new Tripo.Bridge.BridgeCallException(
                    "idempotency_conflict",
                    "The existing Rhino block definition was created by a different import request.");
            }
        }
    }

    private static void EnsureCanRecordUndo(global::Rhino.RhinoDoc document)
    {
        if (document.InCommand(false) > 0 ||
            !document.UndoRecordingEnabled ||
            document.UndoRecordingIsActive)
        {
            throw new Tripo.Bridge.BridgeCallException(
                "host_busy",
                "Rhino is busy or cannot start a dedicated undo record.");
        }
    }

    private static void VerifyUndoEnded(
        global::Rhino.RhinoDoc document,
        bool undoEnded,
        Guid createdId)
    {
        if (undoEnded)
        {
            return;
        }

        bool removed = createdId != Guid.Empty &&
                       document.Objects.Delete(createdId, quiet: true);
        throw new Tripo.Bridge.BridgeCallException(
            removed ? "undo_record_failed" : "mutation_state_uncertain",
            removed
                ? "Rhino could not finish the undo record; the imported object was removed."
                : "Rhino created the object but could not verify its undo record or remove it.");
    }

    private static void VerifyInstanceUndoEnded(
        global::Rhino.RhinoDoc document,
        bool undoEnded,
        int definitionIndex)
    {
        if (undoEnded)
        {
            return;
        }

        // Instance mode created BOTH a definition and an instance. Deleting the
        // definition with reference cleanup removes the instance too, so a single
        // delete unwinds the whole import; report exactly what was removed. If the
        // definition cannot be deleted, decline honestly as mutation_state_uncertain
        // rather than claim a clean rollback (per MATERIALS-DESIGN.md §Idempotency).
        bool removed = definitionIndex >= 0 &&
                       document.InstanceDefinitions.Delete(
                           definitionIndex,
                           deleteReferences: true,
                           quiet: true);
        throw new Tripo.Bridge.BridgeCallException(
            removed ? "undo_record_failed" : "mutation_state_uncertain",
            removed
                ? "Rhino could not finish the undo record; the imported block instance and its definition were removed."
                : "Rhino created the block instance and definition but could not verify the undo record or remove the definition.");
    }

    private Tripo.Bridge.HostContextReceipt CreateContextReceipt(
        global::Rhino.RhinoDoc document) =>
        new(
            "rhino",
            global::Rhino.RhinoApp.Version.ToString(),
            System.Environment.ProcessId,
            _documentSessions.GetOrCreate(document),
            string.IsNullOrWhiteSpace(document.Name) ? "Untitled" : document.Name,
            document.ModelUnitSystem.ToString(),
            Capabilities);

    private void EnsureMatchingSession(
        global::Rhino.RhinoDoc document,
        string requestedSessionId)
    {
        string activeSession = _documentSessions.GetOrCreate(document);
        if (!string.Equals(
                activeSession,
                requestedSessionId,
                StringComparison.Ordinal))
        {
            throw new Tripo.Bridge.BridgeCallException(
                "document_changed",
                "The active Rhino document no longer matches the requested session.");
        }
    }

    private static global::Rhino.RhinoDoc GetActiveDocument() =>
        global::Rhino.RhinoDoc.ActiveDoc
        ?? throw new Tripo.Bridge.BridgeCallException(
            "document_unavailable",
            "Rhino has no active document.");

    private static int ReadStoredCount(
        RhinoObject stored,
        string userStringKey,
        int fallback)
    {
        string? raw = stored.Attributes.GetUserString(userStringKey);
        return int.TryParse(
            raw,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : fallback;
    }

    private static string ReadStoredHash(
        RhinoObject stored,
        string userStringKey)
    {
        string? raw = stored.Attributes.GetUserString(userStringKey);
        return raw is { Length: 64 } &&
               raw.All(character =>
                   character is >= '0' and <= '9' or
                       >= 'a' and <= 'f')
            ? raw
            : string.Empty;
    }

    private static Tripo.Bridge.HostImportReceipt CreateReceipt(
        Tripo.Bridge.ImportMeshRequest request,
        string createdId,
        int vertexCount,
        int triangleCount,
        int rejectedTriangleCount,
        int materialCount,
        int textureCount,
        string transactionStatus) =>
        new(
            "rhino",
            request.DocumentSessionId,
            request.IdempotencyKey,
            createdId,
            vertexCount,
            triangleCount,
            rejectedTriangleCount,
            transactionStatus,
            request.ImportMode,
            materialCount,
            textureCount,
            SavedFamilyPath: null);

    private static string CreateRequestFingerprint(
        Tripo.Bridge.ImportMeshRequest request)
    {
        // Import identity is the content (bundle, units, name, mode, materials), not
        // the session: a legitimate cross-restart replay carries the same idempotency
        // key under a new session and must recover instead of failing closed. Session
        // correctness is enforced separately by the active-session check
        // (per MATERIALS-DESIGN.md §Idempotency).
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            request with { DocumentSessionId = string.Empty },
            Tripo.Bridge.BridgeJson.Options);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}

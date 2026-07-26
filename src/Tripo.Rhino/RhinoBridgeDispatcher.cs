using System.Globalization;
using System.Security.Cryptography;
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

    // Instance mode block definitions are named by idempotency key so a crashed
    // half-import (definition created, instance not) is reconcilable by name.
    private const string DefinitionNamePrefix = "Tripo_";

    private static readonly IReadOnlyList<string> Capabilities =
    [
        Tripo.Bridge.BridgeConstants.ContextMethod,
        Tripo.Bridge.BridgeConstants.ImportMeshMethod,
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
        RhinoObject[] matches = document.Objects.FindByUserString(
            IdempotencyUserString,
            idempotencyKey,
            caseSensitive: true,
            searchGeometry: false,
            searchAttributes: true,
            ObjectType.Mesh | ObjectType.InstanceReference)
            ?? [];

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

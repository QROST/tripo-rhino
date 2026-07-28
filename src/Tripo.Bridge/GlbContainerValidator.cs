using System.Buffers.Binary;
using System.Text.Json;

namespace Tripo.Bridge;

public sealed record GlbContainerInfo(
    int JsonChunkLength,
    int? BinaryChunkLength);

public static class GlbContainerValidator
{
    internal const int MaximumJsonChunkBytes = 4 * 1024 * 1024;
    internal const int MaximumNodes = 4_096;
    internal const int MaximumAccessorElements = 1_500_000;
    internal const int MaximumTotalAccessorElements = 4_000_000;
    internal const long MaximumTotalAccessorDecodedBytes = 64L * 1024 * 1024;
    internal const int MaximumImageDimension = 4_096;
    internal const long MaximumImagePixels = 16L * 1024 * 1024;
    internal const long MaximumTotalImagePixels = 32L * 1024 * 1024;

    private const uint GlbMagic = 0x46546C67;
    private const uint GlbVersion = 2;
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinaryChunkType = 0x004E4942;
    private const int HeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const int MaximumScenes = 128;
    private const int MaximumMeshes = 4_096;
    private const int MaximumPrimitives = 8_192;
    private const int MaximumAccessors = 16_384;
    private const int MaximumBufferViews = 16_384;
    private const int MaximumMaterials = 256;
    private const int MaximumImages = 512;
    private const int MaximumTextures = 512;
    private const int MaximumSamplers = 512;
    private const int MaximumSkins = 512;
    private const int MaximumAnimations = 512;
    private const int MaximumCameras = 512;
    private const int MaximumAnimationEntries = 4_096;
    private const int MaximumImageHeaderScanBytes = 1024 * 1024;

    public static GlbContainerInfo Validate(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Validate(data.AsSpan());
    }

    public static GlbContainerInfo Validate(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength + ChunkHeaderLength)
        {
            throw Invalid("The GLB container is too short.");
        }

        if (data.Length > BridgeConstants.MaximumGlbArtifactBytes)
        {
            throw Invalid("The GLB container exceeds the direct-import size limit.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != GlbMagic)
        {
            throw Invalid("The staged artifact does not have the GLB magic.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version != GlbVersion)
        {
            throw Invalid("Only GLB container version 2 is supported.");
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (declaredLength != data.Length)
        {
            throw Invalid(
                "The GLB declared length does not match the staged artifact length.");
        }

        int offset = HeaderLength;
        int chunkIndex = 0;
        ReadOnlySpan<byte> jsonChunk = default;
        ReadOnlySpan<byte> binaryChunk = default;
        int? binaryChunkLength = null;
        while (offset < data.Length)
        {
            if (data.Length - offset < ChunkHeaderLength)
            {
                throw Invalid("The GLB container has trailing bytes.");
            }

            uint chunkLengthValue =
                BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            uint chunkType =
                BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            offset += ChunkHeaderLength;
            if (chunkLengthValue == 0 ||
                chunkLengthValue > int.MaxValue ||
                (chunkLengthValue & 3) != 0)
            {
                throw Invalid(
                    "A GLB chunk has an invalid or unaligned byte length.");
            }

            int chunkLength = checked((int)chunkLengthValue);
            if (chunkLength > data.Length - offset)
            {
                throw Invalid("A GLB chunk extends beyond the declared container.");
            }

            ReadOnlySpan<byte> chunk = data.Slice(offset, chunkLength);
            if (chunkType == JsonChunkType)
            {
                if (chunkIndex != 0)
                {
                    throw Invalid("The GLB JSON chunk must be the first chunk.");
                }

                if (!jsonChunk.IsEmpty)
                {
                    throw Invalid("The GLB container contains multiple JSON chunks.");
                }

                jsonChunk = chunk;
            }
            else if (chunkType == BinaryChunkType)
            {
                if (jsonChunk.IsEmpty)
                {
                    throw Invalid("The GLB binary chunk precedes its JSON chunk.");
                }

                if (binaryChunkLength is not null)
                {
                    throw Invalid("The GLB container contains multiple binary chunks.");
                }

                binaryChunk = chunk;
                binaryChunkLength = chunkLength;
            }
            else
            {
                throw Invalid("The GLB container contains an unsupported chunk type.");
            }

            offset += chunkLength;
            chunkIndex++;
        }

        if (jsonChunk.IsEmpty)
        {
            throw Invalid("The GLB container does not contain exactly one JSON chunk.");
        }

        ValidateJson(jsonChunk, binaryChunk);
        return new GlbContainerInfo(jsonChunk.Length, binaryChunkLength);
    }

    private static void ValidateJson(
        ReadOnlySpan<byte> jsonChunk,
        ReadOnlySpan<byte> binaryChunk)
    {
        if (jsonChunk.Length > MaximumJsonChunkBytes)
        {
            throw Invalid("The GLB JSON chunk exceeds its bounded size.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                jsonChunk.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The GLB JSON chunk root must be an object.");
            }

            JsonElement asset = GetRequiredUniqueObject(root, "asset");
            JsonElement version = GetRequiredUniqueProperty(asset, "version");
            if (version.ValueKind != JsonValueKind.String ||
                !IsVersion2(version.GetString()))
            {
                throw Invalid("The GLB asset.version must be a 2.x version.");
            }

            int bufferByteLength = ValidateBuffer(root, binaryChunk.Length);
            BufferViewInfo[] bufferViews =
                ValidateBufferViews(root, bufferByteLength);
            AccessorInfo[] accessors =
                ValidateAccessors(root, bufferViews);
            int materialCount =
                GetBoundedObjectArrayLength(root, "materials", MaximumMaterials);
            int imageCount = ValidateImages(root, bufferViews, binaryChunk);
            ValidateTextures(root, imageCount);
            MeshInfo[] meshes =
                ValidateMeshes(root, accessors, materialCount, binaryChunk);
            int skinCount =
                GetBoundedObjectArrayLength(root, "skins", MaximumSkins);
            int cameraCount =
                GetBoundedObjectArrayLength(root, "cameras", MaximumCameras);
            int nodeCount =
                GetBoundedObjectArrayLength(root, "nodes", MaximumNodes);
            ValidateNodes(root, nodeCount, meshes, skinCount, cameraCount);
            ValidateScenes(root, nodeCount);
            ValidateSkins(root, nodeCount, accessors.Length);
            ValidateAnimations(root, nodeCount, accessors.Length);
        }
        catch (JsonException exception)
        {
            throw new BridgeCallException(
                "glb_invalid",
                "The GLB JSON chunk is malformed.",
                exception);
        }
    }

    private static int ValidateBuffer(JsonElement root, int binaryChunkLength)
    {
        JsonElement? buffers = GetOptionalBoundedArray(root, "buffers", 1);
        if (buffers is null)
        {
            if (binaryChunkLength != 0)
            {
                throw Invalid(
                    "The GLB binary chunk does not have a matching buffer.");
            }

            return 0;
        }

        if (buffers.Value.GetArrayLength() != 1)
        {
            throw Invalid("A GLB binary container must describe one buffer.");
        }

        JsonElement buffer = GetObjectEntry(buffers.Value[0], "buffers");
        RejectProperty(buffer, "uri", "GLB buffers entries must not reference URIs.");
        int byteLength = GetRequiredBoundedInt32(
            buffer,
            "byteLength",
            1,
            checked((int)Math.Min(
                BridgeConstants.MaximumGlbArtifactBytes,
                int.MaxValue)));
        if (binaryChunkLength == 0 ||
            byteLength > binaryChunkLength ||
            binaryChunkLength - byteLength > 3)
        {
            throw Invalid(
                "The GLB buffer byte length does not match its binary chunk.");
        }

        return byteLength;
    }

    private static BufferViewInfo[] ValidateBufferViews(
        JsonElement root,
        int bufferByteLength)
    {
        JsonElement? views = GetOptionalBoundedArray(
            root,
            "bufferViews",
            MaximumBufferViews);
        if (views is null)
        {
            return [];
        }

        BufferViewInfo[] result = new BufferViewInfo[views.Value.GetArrayLength()];
        for (int index = 0; index < result.Length; index++)
        {
            JsonElement view = GetObjectEntry(views.Value[index], "bufferViews");
            int buffer = GetRequiredBoundedInt32(view, "buffer", 0, 0);
            int byteOffset = GetOptionalBoundedInt32(
                view,
                "byteOffset",
                0,
                bufferByteLength,
                defaultValue: 0);
            int byteLength = GetRequiredBoundedInt32(
                view,
                "byteLength",
                1,
                bufferByteLength);
            int byteStride = GetOptionalBoundedInt32(
                view,
                "byteStride",
                4,
                252,
                defaultValue: 0);
            if (buffer != 0 ||
                (byteStride != 0 && (byteStride & 3) != 0) ||
                (long)byteOffset + byteLength > bufferByteLength)
            {
                throw Invalid("A GLB bufferView is outside its embedded buffer.");
            }

            result[index] = new BufferViewInfo(
                byteOffset,
                byteLength,
                byteStride);
        }

        return result;
    }

    private static AccessorInfo[] ValidateAccessors(
        JsonElement root,
        IReadOnlyList<BufferViewInfo> bufferViews)
    {
        JsonElement? accessors = GetOptionalBoundedArray(
            root,
            "accessors",
            MaximumAccessors);
        if (accessors is null)
        {
            return [];
        }

        AccessorInfo[] result =
            new AccessorInfo[accessors.Value.GetArrayLength()];
        long totalElements = 0;
        long totalDecodedBytes = 0;
        for (int index = 0; index < result.Length; index++)
        {
            JsonElement accessor =
                GetObjectEntry(accessors.Value[index], "accessors");
            int count = GetRequiredBoundedInt32(
                accessor,
                "count",
                1,
                MaximumAccessorElements);
            totalElements += count;
            if (totalElements > MaximumTotalAccessorElements)
            {
                throw Invalid(
                    "The GLB accessors exceed the aggregate element limit.");
            }
            int componentType = GetRequiredBoundedInt32(
                accessor,
                "componentType",
                5120,
                5126);
            int componentSize = componentType switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                5125 or 5126 => 4,
                _ => throw Invalid("A GLB accessor has an unsupported component type."),
            };
            string type = GetRequiredString(accessor, "type");
            int elementSize = GetAccessorElementSize(type, componentSize);
            totalDecodedBytes = checked(
                totalDecodedBytes + (long)count * elementSize);
            if (totalDecodedBytes > MaximumTotalAccessorDecodedBytes)
            {
                throw Invalid(
                    "The GLB accessors exceed the aggregate decoded byte limit.");
            }

            JsonElement? bufferViewProperty =
                GetOptionalUniqueProperty(accessor, "bufferView");
            JsonElement? sparseProperty =
                GetOptionalUniqueProperty(accessor, "sparse");
            if (sparseProperty is not null)
            {
                throw Invalid(
                    "Sparse GLB accessors are not supported by the bounded native-import subset.");
            }

            if (bufferViewProperty is null)
            {
                if (GetOptionalUniqueProperty(accessor, "byteOffset") is not null)
                {
                    throw Invalid(
                        "A GLB accessor without a bufferView cannot have a byte offset.");
                }

                // Accessors without a bufferView are legal zero-initialized
                // accessors. Their decoded budget is still enforced here.
                result[index] = new AccessorInfo(
                    count,
                    type,
                    componentType,
                    elementSize,
                    DataOffset: -1,
                    RelativeByteOffset: 0,
                    Stride: elementSize);
                continue;
            }

            int bufferViewIndex = ReadBoundedInt32(
                bufferViewProperty.Value,
                "bufferView",
                0,
                bufferViews.Count - 1);
            int byteOffset = GetOptionalBoundedInt32(
                accessor,
                "byteOffset",
                0,
                bufferViews[bufferViewIndex].ByteLength,
                defaultValue: 0);
            BufferViewInfo view = bufferViews[bufferViewIndex];
            int stride = view.ByteStride == 0
                ? elementSize
                : view.ByteStride;
            long required = (long)byteOffset +
                elementSize +
                ((long)count - 1) * stride;
            int dataOffset = checked(view.ByteOffset + byteOffset);
            if (stride < elementSize ||
                required > view.ByteLength ||
                byteOffset % componentSize != 0 ||
                dataOffset % componentSize != 0 ||
                stride % componentSize != 0)
            {
                throw Invalid(
                    "A GLB accessor has invalid bounds, stride, or alignment.");
            }

            result[index] = new AccessorInfo(
                count,
                type,
                componentType,
                elementSize,
                dataOffset,
                byteOffset,
                stride);
        }

        return result;
    }

    private static MeshInfo[] ValidateMeshes(
        JsonElement root,
        IReadOnlyList<AccessorInfo> accessors,
        int materialCount,
        ReadOnlySpan<byte> binaryChunk)
    {
        JsonElement? meshes =
            GetOptionalBoundedArray(root, "meshes", MaximumMeshes);
        if (meshes is null)
        {
            return [];
        }

        MeshInfo[] result = new MeshInfo[meshes.Value.GetArrayLength()];
        int primitiveCount = 0;
        long triangleCount = 0;
        long vertexCount = 0;
        Dictionary<int, uint> maximumIndexByAccessor = [];
        int meshIndex = 0;
        foreach (JsonElement meshValue in meshes.Value.EnumerateArray())
        {
            JsonElement mesh = GetObjectEntry(meshValue, "meshes");
            long meshTriangleCount = 0;
            long meshVertexCount = 0;
            JsonElement primitives =
                GetRequiredUniqueProperty(mesh, "primitives");
            if (primitives.ValueKind != JsonValueKind.Array ||
                primitives.GetArrayLength() == 0)
            {
                throw Invalid("Every GLB mesh must contain primitives.");
            }

            primitiveCount = checked(primitiveCount + primitives.GetArrayLength());
            if (primitiveCount > MaximumPrimitives)
            {
                throw Invalid("The GLB meshes exceed the primitive limit.");
            }

            foreach (JsonElement primitiveValue in primitives.EnumerateArray())
            {
                JsonElement primitive =
                    GetObjectEntry(primitiveValue, "mesh primitives");
                JsonElement attributes =
                    GetRequiredUniqueProperty(primitive, "attributes");
                if (attributes.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid(
                        "Every GLB mesh primitive must contain attributes.");
                }

                HashSet<string> attributeNames = new(StringComparer.Ordinal);
                foreach (JsonProperty attribute in attributes.EnumerateObject())
                {
                    if (!attributeNames.Add(attribute.Name))
                    {
                        throw Invalid(
                            "A GLB primitive contains duplicate attribute semantics.");
                    }

                    _ = ReadBoundedInt32(
                        attribute.Value,
                        "attribute accessor",
                        0,
                        accessors.Count - 1);
                }

                int? indexAccessor = GetOptionalIndex(
                    primitive,
                    "indices",
                    accessors.Count);
                ValidateOptionalIndex(
                    primitive,
                    "material",
                    materialCount);
                int mode = GetOptionalBoundedInt32(
                    primitive,
                    "mode",
                    0,
                    6,
                    defaultValue: 4);
                int? positionAccessor = null;
                foreach (JsonProperty attribute in attributes.EnumerateObject())
                {
                    if (attribute.NameEquals("POSITION"))
                    {
                        positionAccessor = ReadBoundedInt32(
                            attribute.Value,
                            "POSITION accessor",
                            0,
                            accessors.Count - 1);
                    }
                }

                if (positionAccessor is null)
                {
                    throw Invalid(
                        "Every GLB mesh primitive must have a POSITION accessor.");
                }

                AccessorInfo position = accessors[positionAccessor.Value];
                if (!string.Equals(
                        position.Type,
                        "VEC3",
                        StringComparison.Ordinal) ||
                    position.ComponentType != 5126 ||
                    indexAccessor is not null &&
                    (!string.Equals(
                            accessors[indexAccessor.Value].Type,
                            "SCALAR",
                            StringComparison.Ordinal) ||
                        accessors[indexAccessor.Value].ComponentType is not
                            (5121 or 5123 or 5125) ||
                        accessors[indexAccessor.Value].Stride !=
                            accessors[indexAccessor.Value].ElementSize))
                {
                    throw Invalid(
                        "A GLB primitive has invalid position or index accessor types.");
                }

                foreach (JsonProperty attribute in attributes.EnumerateObject())
                {
                    int accessorIndex = ReadBoundedInt32(
                        attribute.Value,
                        "attribute accessor",
                        0,
                        accessors.Count - 1);
                    if (accessors[accessorIndex].Count != position.Count)
                    {
                        throw Invalid(
                            "A GLB primitive attribute count does not match POSITION.");
                    }

                    if ((accessors[accessorIndex].RelativeByteOffset & 3) != 0)
                    {
                        throw Invalid(
                            "A GLB vertex attribute accessor is not aligned to four bytes.");
                    }
                }

                if (indexAccessor is not null)
                {
                    int accessorIndex = indexAccessor.Value;
                    if (!maximumIndexByAccessor.TryGetValue(
                            accessorIndex,
                            out uint maximumIndex))
                    {
                        maximumIndex = ReadMaximumIndex(
                            accessors[accessorIndex],
                            binaryChunk);
                        maximumIndexByAccessor.Add(
                            accessorIndex,
                            maximumIndex);
                    }

                    if (maximumIndex >= position.Count)
                    {
                        throw Invalid(
                            "A GLB primitive index exceeds its POSITION accessor.");
                    }
                }

                meshVertexCount += position.Count;
                vertexCount += position.Count;
                if (vertexCount > BridgeConstants.MaximumVertices)
                {
                    throw Invalid(
                        "The GLB mesh exceeds the pre-native vertex limit.");
                }

                int elementCount = indexAccessor is null
                    ? position.Count
                    : accessors[indexAccessor.Value].Count;
                long primitiveTriangles = mode switch
                {
                    4 => elementCount / 3,
                    5 or 6 => Math.Max(0, elementCount - 2),
                    _ => 0,
                };
                meshTriangleCount += primitiveTriangles;
                triangleCount += primitiveTriangles;
                if (triangleCount > BridgeConstants.MaximumTriangles)
                {
                    throw Invalid(
                        "The GLB mesh exceeds the pre-native triangle limit.");
                }

                ValidatePrimitiveTargets(
                    primitive,
                    accessors,
                    position.Count);
            }

            result[meshIndex++] = new MeshInfo(
                meshVertexCount,
                meshTriangleCount);
        }

        return result;
    }

    private static void ValidatePrimitiveTargets(
        JsonElement primitive,
        IReadOnlyList<AccessorInfo> accessors,
        int positionCount)
    {
        JsonElement? targets = GetOptionalUniqueProperty(primitive, "targets");
        if (targets is null)
        {
            return;
        }

        if (targets.Value.ValueKind != JsonValueKind.Array ||
            targets.Value.GetArrayLength() > 64)
        {
            throw Invalid("A GLB primitive has invalid morph targets.");
        }

        foreach (JsonElement targetValue in targets.Value.EnumerateArray())
        {
            JsonElement target =
                GetObjectEntry(targetValue, "primitive targets");
            HashSet<string> attributeNames = new(StringComparer.Ordinal);
            foreach (JsonProperty attribute in target.EnumerateObject())
            {
                if (!attributeNames.Add(attribute.Name))
                {
                    throw Invalid(
                        "A GLB morph target contains duplicate attribute semantics.");
                }

                int accessorIndex = ReadBoundedInt32(
                    attribute.Value,
                    "target accessor",
                    0,
                    accessors.Count - 1);
                if (accessors[accessorIndex].Count != positionCount)
                {
                    throw Invalid(
                        "A GLB morph target count does not match POSITION.");
                }

                if ((accessors[accessorIndex].RelativeByteOffset & 3) != 0)
                {
                    throw Invalid(
                        "A GLB morph target accessor is not aligned to four bytes.");
                }
            }
        }
    }

    private static uint ReadMaximumIndex(
        AccessorInfo accessor,
        ReadOnlySpan<byte> binaryChunk)
    {
        if (accessor.DataOffset < 0)
        {
            return 0;
        }

        uint maximum = 0;
        for (int index = 0; index < accessor.Count; index++)
        {
            int offset = checked(
                accessor.DataOffset + index * accessor.Stride);
            uint value = accessor.ComponentType switch
            {
                5121 => binaryChunk[offset],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(
                    binaryChunk[offset..]),
                5125 => BinaryPrimitives.ReadUInt32LittleEndian(
                    binaryChunk[offset..]),
                _ => throw Invalid(
                    "A GLB primitive has an unsupported index component type."),
            };
            uint reservedMaximum = accessor.ComponentType switch
            {
                5121 => byte.MaxValue,
                5123 => ushort.MaxValue,
                5125 => uint.MaxValue,
                _ => throw Invalid(
                    "A GLB primitive has an unsupported index component type."),
            };
            if (value == reservedMaximum)
            {
                throw Invalid(
                    "A GLB primitive index uses its reserved maximum value.");
            }

            maximum = Math.Max(maximum, value);
        }

        return maximum;
    }

    private static int ValidateImages(
        JsonElement root,
        IReadOnlyList<BufferViewInfo> bufferViews,
        ReadOnlySpan<byte> binaryChunk)
    {
        JsonElement? images =
            GetOptionalBoundedArray(root, "images", MaximumImages);
        if (images is null)
        {
            return 0;
        }

        long totalPixels = 0;
        foreach (JsonElement imageValue in images.Value.EnumerateArray())
        {
            JsonElement image = GetObjectEntry(imageValue, "images");
            RejectProperty(
                image,
                "uri",
                "GLB images entries must not reference URIs.");
            int viewIndex = GetRequiredBoundedInt32(
                image,
                "bufferView",
                0,
                bufferViews.Count - 1);
            string mimeType = GetRequiredString(image, "mimeType");
            BufferViewInfo view = bufferViews[viewIndex];
            ReadOnlySpan<byte> encodedImage =
                binaryChunk.Slice(view.ByteOffset, view.ByteLength);
            (int width, int height) = mimeType switch
            {
                "image/png" => ReadPngDimensions(encodedImage),
                "image/jpeg" => ReadJpegDimensions(encodedImage),
                _ => throw Invalid(
                    "Only embedded PNG and JPEG GLB images are supported."),
            };
            ValidateImageDimensions(width, height);
            totalPixels += (long)width * height;
            if (totalPixels > MaximumTotalImagePixels)
            {
                throw Invalid(
                    "The embedded GLB images exceed the aggregate decoded pixel limit.");
            }
        }

        return images.Value.GetArrayLength();
    }

    private static void ValidateTextures(JsonElement root, int imageCount)
    {
        int samplerCount =
            GetBoundedObjectArrayLength(root, "samplers", MaximumSamplers);
        JsonElement? textures =
            GetOptionalBoundedArray(root, "textures", MaximumTextures);
        if (textures is null)
        {
            return;
        }

        foreach (JsonElement textureValue in textures.Value.EnumerateArray())
        {
            JsonElement texture = GetObjectEntry(textureValue, "textures");
            ValidateOptionalIndex(texture, "source", imageCount);
            ValidateOptionalIndex(texture, "sampler", samplerCount);
        }
    }

    private static void ValidateNodes(
        JsonElement root,
        int nodeCount,
        IReadOnlyList<MeshInfo> meshes,
        int skinCount,
        int cameraCount)
    {
        JsonElement? nodes = GetOptionalUniqueProperty(root, "nodes");
        if (nodes is null)
        {
            return;
        }

        List<int>[] children = Enumerable.Range(0, nodeCount)
            .Select(_ => new List<int>())
            .ToArray();
        int[] parentCounts = new int[nodeCount];
        long instancedVertices = 0;
        long instancedTriangles = 0;
        int nodeIndex = 0;
        foreach (JsonElement nodeValue in nodes.Value.EnumerateArray())
        {
            JsonElement node = GetObjectEntry(nodeValue, "nodes");
            int? mesh = GetOptionalIndex(node, "mesh", meshes.Count);
            if (mesh is not null)
            {
                instancedVertices += meshes[mesh.Value].VertexCount;
                instancedTriangles += meshes[mesh.Value].TriangleCount;
                if (instancedVertices > BridgeConstants.MaximumVertices ||
                    instancedTriangles > BridgeConstants.MaximumTriangles)
                {
                    throw Invalid(
                        "The GLB node instances exceed the pre-native mesh budget.");
                }
            }
            ValidateOptionalIndex(node, "skin", skinCount);
            ValidateOptionalIndex(node, "camera", cameraCount);
            JsonElement? childValues =
                GetOptionalUniqueProperty(node, "children");
            if (childValues is not null)
            {
                if (childValues.Value.ValueKind != JsonValueKind.Array ||
                    childValues.Value.GetArrayLength() > MaximumNodes)
                {
                    throw Invalid(
                        "The GLB node children exceed their bounded size.");
                }

                foreach (JsonElement childValue in
                         childValues.Value.EnumerateArray())
                {
                    int child = ReadBoundedInt32(
                        childValue,
                        "node child",
                        0,
                        nodeCount - 1);
                    if (child == nodeIndex ||
                        ++parentCounts[child] > 1)
                    {
                        throw Invalid(
                            "The GLB nodes must form a single-parent acyclic hierarchy.");
                    }

                    children[nodeIndex].Add(child);
                }
            }

            nodeIndex++;
        }

        ValidateAcyclicNodes(children, parentCounts);
    }

    private static void ValidateAcyclicNodes(
        IReadOnlyList<List<int>> children,
        IReadOnlyList<int> parentCounts)
    {
        int[] remainingParents = parentCounts.ToArray();
        Queue<int> ready = new();
        for (int index = 0; index < children.Count; index++)
        {
            if (remainingParents[index] == 0)
            {
                ready.Enqueue(index);
            }
        }

        int visited = 0;
        while (ready.TryDequeue(out int node))
        {
            visited++;
            foreach (int child in children[node])
            {
                if (--remainingParents[child] == 0)
                {
                    ready.Enqueue(child);
                }
            }
        }

        if (visited != children.Count)
        {
            throw Invalid("The GLB node hierarchy contains a cycle.");
        }
    }

    private static void ValidateScenes(JsonElement root, int nodeCount)
    {
        JsonElement? scenes =
            GetOptionalBoundedArray(root, "scenes", MaximumScenes);
        if (scenes is not null)
        {
            foreach (JsonElement sceneValue in scenes.Value.EnumerateArray())
            {
                JsonElement scene = GetObjectEntry(sceneValue, "scenes");
                ValidateIndexArray(scene, "nodes", nodeCount, MaximumNodes);
            }
        }

        ValidateOptionalIndex(
            root,
            "scene",
            scenes?.GetArrayLength() ?? 0);
    }

    private static void ValidateSkins(
        JsonElement root,
        int nodeCount,
        int accessorCount)
    {
        JsonElement? skins = GetOptionalUniqueProperty(root, "skins");
        if (skins is null)
        {
            return;
        }

        foreach (JsonElement skinValue in skins.Value.EnumerateArray())
        {
            JsonElement skin = GetObjectEntry(skinValue, "skins");
            ValidateIndexArray(skin, "joints", nodeCount, MaximumNodes);
            ValidateOptionalIndex(skin, "skeleton", nodeCount);
            ValidateOptionalIndex(
                skin,
                "inverseBindMatrices",
                accessorCount);
        }
    }

    private static void ValidateAnimations(
        JsonElement root,
        int nodeCount,
        int accessorCount)
    {
        JsonElement? animations =
            GetOptionalBoundedArray(root, "animations", MaximumAnimations);
        if (animations is null)
        {
            return;
        }

        foreach (JsonElement animationValue in animations.Value.EnumerateArray())
        {
            JsonElement animation =
                GetObjectEntry(animationValue, "animations");
            JsonElement samplers = GetRequiredUniqueProperty(
                animation,
                "samplers");
            JsonElement channels = GetRequiredUniqueProperty(
                animation,
                "channels");
            if (samplers.ValueKind != JsonValueKind.Array ||
                samplers.GetArrayLength() > MaximumAnimationEntries ||
                channels.ValueKind != JsonValueKind.Array ||
                channels.GetArrayLength() > MaximumAnimationEntries)
            {
                throw Invalid("A GLB animation exceeds its bounded size.");
            }

            foreach (JsonElement samplerValue in samplers.EnumerateArray())
            {
                JsonElement sampler =
                    GetObjectEntry(samplerValue, "animation samplers");
                _ = GetRequiredBoundedInt32(
                    sampler,
                    "input",
                    0,
                    accessorCount - 1);
                _ = GetRequiredBoundedInt32(
                    sampler,
                    "output",
                    0,
                    accessorCount - 1);
            }

            foreach (JsonElement channelValue in channels.EnumerateArray())
            {
                JsonElement channel =
                    GetObjectEntry(channelValue, "animation channels");
                _ = GetRequiredBoundedInt32(
                    channel,
                    "sampler",
                    0,
                    samplers.GetArrayLength() - 1);
                JsonElement target =
                    GetRequiredUniqueObject(channel, "target");
                ValidateOptionalIndex(target, "node", nodeCount);
            }
        }
    }

    private static void ValidateIndexArray(
        JsonElement parent,
        string propertyName,
        int referencedCount,
        int maximumCount)
    {
        JsonElement? property =
            GetOptionalUniqueProperty(parent, propertyName);
        if (property is null)
        {
            return;
        }

        if (property.Value.ValueKind != JsonValueKind.Array ||
            property.Value.GetArrayLength() > maximumCount)
        {
            throw Invalid(
                $"The GLB {propertyName} property exceeds its bounded size.");
        }

        foreach (JsonElement value in property.Value.EnumerateArray())
        {
            _ = ReadBoundedInt32(
                value,
                propertyName,
                0,
                referencedCount - 1);
        }
    }

    private static void ValidateOptionalIndex(
        JsonElement parent,
        string propertyName,
        int referencedCount)
    {
        _ = GetOptionalIndex(parent, propertyName, referencedCount);
    }

    private static int? GetOptionalIndex(
        JsonElement parent,
        string propertyName,
        int referencedCount)
    {
        JsonElement? property =
            GetOptionalUniqueProperty(parent, propertyName);
        return property is null
            ? null
            : ReadBoundedInt32(
                property.Value,
                propertyName,
                0,
                referencedCount - 1);
    }

    private static JsonElement? GetOptionalBoundedArray(
        JsonElement parent,
        string propertyName,
        int maximumCount)
    {
        JsonElement? property =
            GetOptionalUniqueProperty(parent, propertyName);
        if (property is null)
        {
            return null;
        }

        if (property.Value.ValueKind != JsonValueKind.Array ||
            property.Value.GetArrayLength() > maximumCount)
        {
            throw Invalid(
                $"The GLB {propertyName} property exceeds its bounded size.");
        }

        return property;
    }

    private static int GetBoundedObjectArrayLength(
        JsonElement root,
        string propertyName,
        int maximumCount)
    {
        JsonElement? collection =
            GetOptionalBoundedArray(root, propertyName, maximumCount);
        if (collection is null)
        {
            return 0;
        }

        foreach (JsonElement item in collection.Value.EnumerateArray())
        {
            _ = GetObjectEntry(item, propertyName);
        }

        return collection.Value.GetArrayLength();
    }

    private static JsonElement GetObjectEntry(
        JsonElement item,
        string collectionName)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(
                $"Every GLB {collectionName} entry must be an object.");
        }

        return item;
    }

    private static JsonElement GetRequiredUniqueObject(
        JsonElement parent,
        string propertyName)
    {
        JsonElement property = GetRequiredUniqueProperty(parent, propertyName);
        if (property.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"The GLB {propertyName} property must be an object.");
        }

        return property;
    }

    private static JsonElement GetRequiredUniqueProperty(
        JsonElement parent,
        string propertyName) =>
        GetOptionalUniqueProperty(parent, propertyName) ??
        throw Invalid(
            $"The GLB JSON is missing the required {propertyName} property.");

    private static JsonElement? GetOptionalUniqueProperty(
        JsonElement parent,
        string propertyName)
    {
        bool found = false;
        JsonElement result = default;
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName))
            {
                continue;
            }

            if (found)
            {
                throw Invalid(
                    $"The GLB JSON contains duplicate {propertyName} properties.");
            }

            found = true;
            result = property.Value;
        }

        return found ? result : null;
    }

    private static int GetRequiredBoundedInt32(
        JsonElement parent,
        string propertyName,
        int minimum,
        int maximum) =>
        ReadBoundedInt32(
            GetRequiredUniqueProperty(parent, propertyName),
            propertyName,
            minimum,
            maximum);

    private static int GetOptionalBoundedInt32(
        JsonElement parent,
        string propertyName,
        int minimum,
        int maximum,
        int defaultValue)
    {
        JsonElement? property =
            GetOptionalUniqueProperty(parent, propertyName);
        return property is null
            ? defaultValue
            : ReadBoundedInt32(
                property.Value,
                propertyName,
                minimum,
                maximum);
    }

    private static int ReadBoundedInt32(
        JsonElement value,
        string propertyName,
        int minimum,
        int maximum)
    {
        if (maximum < minimum ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result) ||
            result < minimum ||
            result > maximum)
        {
            throw Invalid(
                $"The GLB {propertyName} property is outside its bounded range.");
        }

        return result;
    }

    private static string GetRequiredString(
        JsonElement parent,
        string propertyName)
    {
        JsonElement value = GetRequiredUniqueProperty(parent, propertyName);
        string? result = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return !string.IsNullOrEmpty(result)
            ? result
            : throw Invalid(
                $"The GLB {propertyName} property must be a non-empty string.");
    }

    private static void RejectProperty(
        JsonElement parent,
        string propertyName,
        string message)
    {
        if (GetOptionalUniqueProperty(parent, propertyName) is not null)
        {
            throw Invalid(message);
        }
    }

    private static int GetAccessorElementSize(
        string type,
        int componentSize)
    {
        (int columns, int rows) = type switch
        {
            "SCALAR" => (1, 1),
            "VEC2" => (1, 2),
            "VEC3" => (1, 3),
            "VEC4" => (1, 4),
            "MAT2" => (2, 2),
            "MAT3" => (3, 3),
            "MAT4" => (4, 4),
            _ => throw Invalid("A GLB accessor has an unsupported type."),
        };
        int columnBytes = checked(rows * componentSize);
        if (columns > 1 && componentSize < 4)
        {
            columnBytes = (columnBytes + 3) & ~3;
        }

        return checked(columns * columnBytes);
    }

    private static (int Width, int Height) ReadPngDimensions(
        ReadOnlySpan<byte> image)
    {
        ReadOnlySpan<byte> signature =
        [
            0x89, (byte)'P', (byte)'N', (byte)'G',
            0x0D, 0x0A, 0x1A, 0x0A,
        ];
        if (image.Length < 24 ||
            !image[..8].SequenceEqual(signature) ||
            !image.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw Invalid("An embedded GLB PNG image has an invalid header.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(image[16..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(image[20..]);
        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw Invalid("An embedded GLB PNG image has invalid dimensions.");
        }

        return ((int)width, (int)height);
    }

    private static (int Width, int Height) ReadJpegDimensions(
        ReadOnlySpan<byte> image)
    {
        if (image.Length < 4 || image[0] != 0xFF || image[1] != 0xD8)
        {
            throw Invalid("An embedded GLB JPEG image has an invalid header.");
        }

        int limit = Math.Min(image.Length, MaximumImageHeaderScanBytes);
        int offset = 2;
        while (offset + 3 < limit)
        {
            while (offset < limit && image[offset] != 0xFF)
            {
                offset++;
            }

            while (offset < limit && image[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= limit)
            {
                break;
            }

            byte marker = image[offset++];
            if (marker is 0x01 or >= 0xD0 and <= 0xD9)
            {
                continue;
            }

            if (offset + 2 > limit)
            {
                break;
            }

            int segmentLength =
                BinaryPrimitives.ReadUInt16BigEndian(image[offset..]);
            if (segmentLength < 2 || offset + segmentLength > limit)
            {
                throw Invalid(
                    "An embedded GLB JPEG image has an invalid segment.");
            }

            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    throw Invalid(
                        "An embedded GLB JPEG image has an invalid frame.");
                }

                int height =
                    BinaryPrimitives.ReadUInt16BigEndian(image[(offset + 3)..]);
                int width =
                    BinaryPrimitives.ReadUInt16BigEndian(image[(offset + 5)..]);
                return (width, height);
            }

            offset += segmentLength;
        }

        throw Invalid(
            "An embedded GLB JPEG image has no bounded dimension header.");
    }

    private static bool IsJpegStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xC3 or
            >= 0xC5 and <= 0xC7 or
            >= 0xC9 and <= 0xCB or
            >= 0xCD and <= 0xCF;

    private static void ValidateImageDimensions(int width, int height)
    {
        if (width <= 0 ||
            height <= 0 ||
            width > MaximumImageDimension ||
            height > MaximumImageDimension ||
            (long)width * height > MaximumImagePixels)
        {
            throw Invalid(
                "An embedded GLB image exceeds the decoded dimension limit.");
        }
    }

    private static bool IsVersion2(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length < 3 ||
            value[0] != '2' ||
            value[1] != '.')
        {
            return false;
        }

        bool segmentHasDigit = false;
        for (int index = 2; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '.')
            {
                if (!segmentHasDigit)
                {
                    return false;
                }

                segmentHasDigit = false;
                continue;
            }

            if (character is < '0' or > '9')
            {
                return false;
            }

            segmentHasDigit = true;
        }

        return segmentHasDigit;
    }

    private static BridgeCallException Invalid(string message) =>
        new("glb_invalid", message);

    private sealed record BufferViewInfo(
        int ByteOffset,
        int ByteLength,
        int ByteStride);

    private sealed record AccessorInfo(
        int Count,
        string Type,
        int ComponentType,
        int ElementSize,
        int DataOffset,
        int RelativeByteOffset,
        int Stride);

    private sealed record MeshInfo(
        long VertexCount,
        long TriangleCount);
}

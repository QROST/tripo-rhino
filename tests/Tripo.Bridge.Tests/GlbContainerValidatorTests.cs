using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class GlbContainerValidatorTests
{
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinaryChunkType = 0x004E4942;

    [Fact]
    public void ValidateAcceptsEmbeddedGlbVersion2()
    {
        byte[] pngHeader = CreatePngHeader(width: 16, height: 8);
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":24}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":24}],"images":[{"bufferView":0,"mimeType":"image/png"}],"textures":[{"source":0}]}
            """,
            pngHeader);

        Tripo.Bridge.GlbContainerInfo info =
            Tripo.Bridge.GlbContainerValidator.Validate(glb);

        Assert.True(info.JsonChunkLength > 0);
        Assert.Equal(24, info.BinaryChunkLength);
    }

    [Fact]
    public void ValidateAcceptsJsonOnlyContainer()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.1"}}""");

        Tripo.Bridge.GlbContainerInfo info =
            Tripo.Bridge.GlbContainerValidator.Validate(glb);

        Assert.Null(info.BinaryChunkLength);
    }

    [Fact]
    public void ValidateAcceptsBoundedTexturedTriangleFixture()
    {
        byte[] glb = CreateTexturedTriangleGlb();

        Tripo.Bridge.GlbContainerInfo info =
            Tripo.Bridge.GlbContainerValidator.Validate(glb);

        Assert.True(info.BinaryChunkLength > 0);
    }

    [Fact]
    public void ValidateRejectsWrongMagic()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        glb[0] ^= 0xFF;

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsUnsupportedContainerVersion()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 1);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsDeclaredLengthMismatch()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(
            glb.AsSpan(8),
            checked((uint)glb.Length + 4));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsBinaryChunkBeforeJson()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (BinaryChunkType, new byte[4]),
            (JsonChunkType, json));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMultipleJsonChunks()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (JsonChunkType, json),
            (JsonChunkType, json));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMultipleBinaryChunks()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (JsonChunkType, json),
            (BinaryChunkType, new byte[4]),
            (BinaryChunkType, new byte[4]));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsUnknownChunkType()
    {
        byte[] json = PadJson("""{"asset":{"version":"2.0"}}""");
        byte[] glb = BuildChunks(
            (JsonChunkType, json),
            (0x12345678, new byte[4]));

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsTrailingBytes()
    {
        byte[] valid = BuildGlb("""{"asset":{"version":"2.0"}}""");
        byte[] withTrailing = [.. valid, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(
            withTrailing.AsSpan(8),
            checked((uint)withTrailing.Length));

        AssertInvalid(withTrailing);
    }

    [Fact]
    public void ValidateRejectsUnalignedChunkLength()
    {
        byte[] glb = BuildGlb("""{"asset":{"version":"2.0"}}""");
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), 2);

        AssertInvalid(glb);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"asset":{}}""")]
    [InlineData("""{"asset":{"version":"1.0"}}""")]
    [InlineData("""{"asset":{"version":"2.x"}}""")]
    [InlineData("""{"asset":{"version":2}}""")]
    public void ValidateRejectsMissingOrInvalidAssetVersion(string json)
    {
        AssertInvalid(BuildGlb(json));
    }

    [Theory]
    [InlineData(
        """{"asset":{"version":"2.0"},"buffers":[{"uri":"https://example.test/model.bin"}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"buffers":[{"uri":"data:application/octet-stream;base64,AA=="}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"images":[{"uri":"texture.png"}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"images":[{"uri":null}]}""")]
    public void ValidateRejectsAnyBufferOrImageUri(string json)
    {
        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsMalformedJson()
    {
        AssertInvalid(BuildGlb("""{"asset":{"version":"2.0"}"""));
    }

    [Fact]
    public void ValidateRejectsOversizedJsonBeforeParsing()
    {
        string padding = new(
            ' ',
            Tripo.Bridge.GlbContainerValidator.MaximumJsonChunkBytes);
        byte[] glb = BuildGlb(
            """{"asset":{"version":"2.0"},"extras":""" +
            JsonSerializer.Serialize(padding) +
            "}");

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsExcessiveNodeCount()
    {
        string nodes = string.Join(
            ',',
            Enumerable.Repeat(
                "{}",
                Tripo.Bridge.GlbContainerValidator.MaximumNodes + 1));

        AssertInvalid(BuildGlb(
            """{"asset":{"version":"2.0"},"nodes":[""" +
            nodes +
            "]}"));
    }

    [Fact]
    public void ValidateRejectsExcessiveAccessorElementCount()
    {
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":4}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteLength\":4}]," +
            "\"accessors\":[{\"bufferView\":0,\"componentType\":5121,\"count\":" +
            (Tripo.Bridge.GlbContainerValidator.MaximumAccessorElements + 1)
                .ToString(CultureInfo.InvariantCulture) +
            ",\"type\":\"SCALAR\"}]}";
        byte[] glb = BuildGlb(json, new byte[4]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsBufferViewIntegerOverflow()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":4}],"bufferViews":[{"buffer":0,"byteOffset":2147483647,"byteLength":4}]}
            """,
            new byte[4]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsDecodedImageDimensionBomb()
    {
        byte[] pngHeader = CreatePngHeader(
            Tripo.Bridge.GlbContainerValidator.MaximumImageDimension + 1,
            height: 1);
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":24}],"bufferViews":[{"buffer":0,"byteLength":24}],"images":[{"bufferView":0,"mimeType":"image/png"}]}
            """,
            pngHeader);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsAggregateAccessorExpansion()
    {
        string count = Tripo.Bridge.GlbContainerValidator
            .MaximumAccessorElements
            .ToString(CultureInfo.InvariantCulture);
        string accessor =
            "{\"componentType\":5121,\"count\":" +
            count +
            ",\"type\":\"SCALAR\"}";
        string json =
            "{\"asset\":{\"version\":\"2.0\"},\"accessors\":[" +
            string.Join(',', Enumerable.Repeat(accessor, 3)) +
            "]}";

        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsAggregateAccessorDecodedByteExpansion()
    {
        int count = checked((int)(
            Tripo.Bridge.GlbContainerValidator
                .MaximumTotalAccessorDecodedBytes /
            (sizeof(float) * 16) +
            1));
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":" +
            count.ToString(CultureInfo.InvariantCulture) +
            ",\"type\":\"MAT4\"}]}";

        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsSparseAccessorsInNativeImportSubset()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":8}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":1},{"buffer":0,"byteOffset":4,"byteLength":4}],"accessors":[{"componentType":5126,"count":1,"type":"SCALAR","sparse":{"count":1,"indices":{"bufferView":0,"componentType":5121},"values":{"bufferView":1}}}]}
            """,
            new byte[8]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsSparseRangeWhenBaseBufferViewExists()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":4}],"bufferViews":[{"buffer":0,"byteLength":4}],"accessors":[{"bufferView":0,"componentType":5121,"count":1,"type":"SCALAR","sparse":{"count":1,"indices":{"bufferView":4,"componentType":5121},"values":{"bufferView":0}}}]}
            """,
            new byte[4]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMisalignedAccessorData()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":8}],"bufferViews":[{"buffer":0,"byteOffset":1,"byteLength":2}],"accessors":[{"bufferView":0,"componentType":5123,"count":1,"type":"SCALAR"}]}
            """,
            new byte[8]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMisalignedRelativeAccessorOffset()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":16}],"bufferViews":[{"buffer":0,"byteOffset":1,"byteLength":15}],"accessors":[{"bufferView":0,"byteOffset":3,"componentType":5126,"count":1,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
            """,
            new byte[16]);

        AssertInvalid(glb);
    }

    [Theory]
    [InlineData(
        """{"asset":{"version":"2.0"},"nodes":[{"children":[0]}]}""")]
    [InlineData(
        """{"asset":{"version":"2.0"},"nodes":[{"children":[1]},{"children":[0]}]}""")]
    public void ValidateRejectsCyclicNodeHierarchy(string json)
    {
        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsAggregateDecodedImageExpansion()
    {
        byte[] pngHeader = CreatePngHeader(
            Tripo.Bridge.GlbContainerValidator.MaximumImageDimension,
            Tripo.Bridge.GlbContainerValidator.MaximumImageDimension);
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":24}],"bufferViews":[{"buffer":0,"byteLength":24}],"images":[{"bufferView":0,"mimeType":"image/png"},{"bufferView":0,"mimeType":"image/png"},{"bufferView":0,"mimeType":"image/png"}]}
            """,
            pngHeader);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsNonFloatPositionAccessor()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5123,"count":3,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsFloatIndexAccessor()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"componentType":5126,"count":3,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMismatchedPrimitiveAttributeCount()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"componentType":5126,"count":2,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1}}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsVertexAttributeOffsetNotAlignedToFourBytes()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":6}],"bufferViews":[{"buffer":0,"byteLength":6}],"accessors":[{"componentType":5126,"count":1,"type":"VEC3"},{"bufferView":0,"byteOffset":2,"componentType":5123,"count":1,"type":"VEC2"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1}}]}]}
            """,
            new byte[6]);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsMismatchedMorphTargetCount()
    {
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"componentType":5126,"count":2,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"targets":[{"POSITION":1}]}]}]}
            """);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsIndexOutsidePositionAccessor()
    {
        byte[] indices = [0, 0, 1, 0, 3, 0];
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":6}],"bufferViews":[{"buffer":0,"byteLength":6}],"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":0,"componentType":5123,"count":3,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}]}
            """,
            indices);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsStridedIndexAccessor()
    {
        byte[] indices = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(indices, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(8), 2);
        byte[] glb = BuildGlb(
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":10}],"bufferViews":[{"buffer":0,"byteLength":10,"byteStride":4}],"accessors":[{"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":0,"componentType":5123,"count":3,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}]}
            """,
            indices);

        AssertInvalid(glb);
    }

    [Theory]
    [InlineData(5121, 256)]
    [InlineData(5123, 65536)]
    public void ValidateRejectsReservedMaximumIndex(
        int componentType,
        int positionCount)
    {
        byte[] indices;
        if (componentType == 5121)
        {
            indices = [0, 1, byte.MaxValue];
        }
        else
        {
            indices = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(
                indices.AsSpan(2),
                1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                indices.AsSpan(4),
                ushort.MaxValue);
        }

        string byteLength =
            indices.Length.ToString(CultureInfo.InvariantCulture);
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":" + byteLength + "}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteLength\":" +
            byteLength + "}]," +
            "\"accessors\":[" +
            "{\"componentType\":5126,\"count\":" +
            positionCount.ToString(CultureInfo.InvariantCulture) +
            ",\"type\":\"VEC3\"}," +
            "{\"bufferView\":0,\"componentType\":" +
            componentType.ToString(CultureInfo.InvariantCulture) +
            ",\"count\":3,\"type\":\"SCALAR\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}," +
            "\"indices\":1}]}]}";
        byte[] glb = BuildGlb(json, indices);

        AssertInvalid(glb);
    }

    [Fact]
    public void ValidateRejectsPreNativeMeshVertexExpansion()
    {
        string count = (Tripo.Bridge.BridgeConstants.MaximumVertices + 1)
            .ToString(CultureInfo.InvariantCulture);
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":" +
            count +
            ",\"type\":\"VEC3\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]}";

        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsSharedPositionAccessorPrimitiveExpansion()
    {
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":200000,\"type\":\"VEC3\"}]," +
            "\"meshes\":[{\"primitives\":[" +
            "{\"attributes\":{\"POSITION\":0},\"mode\":0}," +
            "{\"attributes\":{\"POSITION\":0},\"mode\":0}]}]}";

        AssertInvalid(BuildGlb(json));
    }

    [Fact]
    public void ValidateRejectsRepeatedNodeMeshExpansion()
    {
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"accessors\":[{\"componentType\":5126,\"count\":200000,\"type\":\"VEC3\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":0}]}]," +
            "\"nodes\":[{\"mesh\":0},{\"mesh\":0}]}";

        AssertInvalid(BuildGlb(json));
    }

    private static void AssertInvalid(byte[] data)
    {
        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.GlbContainerValidator.Validate(data));
        Assert.Equal("glb_invalid", exception.Code);
    }

    internal static byte[] BuildGlb(
        string json,
        byte[]? binary = null)
    {
        byte[] jsonChunk = PadJson(json);
        return binary is null
            ? BuildChunks((JsonChunkType, jsonChunk))
            : BuildChunks(
                (JsonChunkType, jsonChunk),
                (BinaryChunkType, PadBinary(binary)));
    }

    private static byte[] BuildChunks(
        params (uint Type, byte[] Content)[] chunks)
    {
        int length = 12 + chunks.Sum(chunk => 8 + chunk.Content.Length);
        byte[] result = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(8),
            checked((uint)length));

        int offset = 12;
        foreach ((uint type, byte[] content) in chunks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(offset),
                checked((uint)content.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(offset + 4),
                type);
            content.CopyTo(result, offset + 8);
            offset += 8 + content.Length;
        }

        return result;
    }

    private static byte[] PadJson(string json)
    {
        byte[] raw = Encoding.UTF8.GetBytes(json);
        int paddedLength = (raw.Length + 3) & ~3;
        byte[] padded = Enumerable.Repeat((byte)' ', paddedLength).ToArray();
        raw.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] PadBinary(byte[] binary)
    {
        int paddedLength = (binary.Length + 3) & ~3;
        byte[] padded = new byte[paddedLength];
        binary.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] result =
        [
            0x89, (byte)'P', (byte)'N', (byte)'G',
            0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0, 0,
            0, 0, 0, 0,
        ];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(20), height);
        return result;
    }

    internal static byte[] CreateTexturedTriangleGlb()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
            "AAAAC0lEQVR42mP8/x8AAusB9Y9ZlP8AAAAASUVORK5CYII=");
        const int imageOffset = 44;
        byte[] binary = new byte[imageOffset + png.Length];
        WriteSingle(binary, 12, 1);
        WriteSingle(binary, 28, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(36), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(38), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(40), 2);
        png.CopyTo(binary, imageOffset);
        string byteLength =
            binary.Length.ToString(CultureInfo.InvariantCulture);
        string imageLength =
            png.Length.ToString(CultureInfo.InvariantCulture);
        string json =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":" + byteLength + "}]," +
            "\"bufferViews\":[" +
            "{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36,\"target\":34962}," +
            "{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6,\"target\":34963}," +
            "{\"buffer\":0,\"byteOffset\":44,\"byteLength\":" + imageLength + "}]," +
            "\"accessors\":[" +
            "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}," +
            "{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]," +
            "\"images\":[{\"bufferView\":2,\"mimeType\":\"image/png\"}]," +
            "\"textures\":[{\"source\":0}]," +
            "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0,\"roughnessFactor\":1}}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1,\"material\":0}]}]," +
            "\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
        return BuildGlb(json, binary);
    }

    private static void WriteSingle(byte[] destination, int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset),
            BitConverter.SingleToInt32Bits(value));
    }
}

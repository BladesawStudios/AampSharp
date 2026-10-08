using System.Numerics;

namespace AampSharp.Tests;

public class YamlTests
{
    [Fact]
    public void EveryParameterTypeRoundTrips()
    {
        ParameterObject obj = new();
        obj.Set("Bool", Parameter.FromBool(true));
        obj.Set("F32", Parameter.FromFloat(0.1f));
        obj.Set("Int", Parameter.FromInt(-7));
        obj.Set("U32", Parameter.FromUInt(uint.MaxValue));
        obj.Set("Vec2", Parameter.FromVector2(new(1, 2)));
        obj.Set("Vec3", Parameter.FromVector3(new(1, -2, 3.5f)));
        obj.Set("Vec4", Parameter.FromVector4(new(1, 2, 3, 4)));
        obj.Set("Color", Parameter.FromColor(new(0.5f, 0.25f, 1, 1)));
        obj.Set("Quat", Parameter.FromQuaternion(new Quaternion(0, 0, 0, 1)));
        obj.Set("Str32", Parameter.FromString(ParameterType.String32, "short"));
        obj.Set("Str64", Parameter.FromString(ParameterType.String64, "123"));
        obj.Set("Str256", Parameter.FromString(ParameterType.String256, ""));
        obj.Set("StrRef", Parameter.FromString(ParameterType.StringRef, "true"));
        obj.Set("Curve", Parameter.FromRaw(ParameterType.Curve2, Curve(2)));
        obj.Set("BufInt", Parameter.FromIntBuffer([-1, 0, int.MaxValue]));
        obj.Set("BufF32", Parameter.FromFloatBuffer([float.Epsilon, -0f, BitConverter.UInt32BitsToSingle(0x7FC00000), BitConverter.UInt32BitsToSingle(0xFFC00000)]));
        obj.Set("NaN", Parameter.FromFloat(BitConverter.UInt32BitsToSingle(0x7FC00001)));
        obj.Set("BufU32", Parameter.FromUIntBuffer([uint.MaxValue]));
        obj.Set("BufBin", Parameter.FromBinaryBuffer([0, 255, 7]));
        obj.Set(0xDEADBEEF, Parameter.FromInt(1));

        ParameterIO pio = new() { Version = 10, Type = "xml" };
        pio.Root.Objects.Add(new(Crc32.Hash("Params"), obj));
        pio.Root.Lists.Add(new(Crc32.Hash("Child"), new ParameterList()));

        string yaml = pio.ToYaml(new NameTable(["Params", "Child", .. Names]));
        ParameterIO back = ParameterIO.FromYaml(yaml);

        Assert.Equal(pio.ToBinary(), back.ToBinary());
        Assert.Contains($"{0xDEADBEEF}: 1", yaml);
    }

    private static readonly string[] Names =
        ["Bool", "F32", "Int", "U32", "Vec2", "Vec3", "Vec4", "Color", "Quat", "Str32", "Str64", "Str256", "StrRef", "Curve", "BufInt", "BufF32", "BufU32", "BufBin"];

    [Fact]
    public void NamesThatLookLikeHashesStayNames()
    {
        ParameterObject obj = new();
        obj.Set("123", Parameter.FromInt(1));
        obj.Set("0x10", Parameter.FromInt(2));
        ParameterIO pio = new();
        pio.Root.Objects.Add(new(Crc32.Hash("Params"), obj));

        ParameterIO back = ParameterIO.FromYaml(pio.ToYaml(new NameTable(["Params", "123", "0x10"])));

        Assert.Equal(1, back.Root.Object("Params")!["123"]!.AsInt());
        Assert.Equal(2, back.Root.Object("Params")!["0x10"]!.AsInt());
    }

    [Fact]
    public void ReadsOeadStyleDocuments()
    {
        ParameterIO pio = ParameterIO.FromYaml("""
            !io
            version: 0
            type: xml
            param_root: !list
              objects:
                ControllerInfo: !obj
                  BaseScale: !vec3 [2.0, 2.0, 2.0]
                  Name: !str64 Foo
                  Mask: !u 0x10
                  Enabled: true
                  3735928559: 4
              lists: {}
            """);

        ParameterObject info = pio.Root.Object("ControllerInfo")!;
        Assert.Equal(new Vector3(2, 2, 2), info["BaseScale"]!.AsVector3());
        Assert.Equal("Foo", info["Name"]!.AsString());
        Assert.Equal(ParameterType.String64, info["Name"]!.Type);
        Assert.Equal(16u, info["Mask"]!.AsUInt());
        Assert.True(info["Enabled"]!.AsBool());
        Assert.Equal(4, info[0xDEADBEEF]!.AsInt());
    }

    [Fact]
    public void BuiltDocumentsUseTheShippedLayout()
    {
        // ParamSet as BotW's physics files have it: one object before the first two lists, Ragdoll before the
        // next two, SupportBone after them all.
        ParameterIO pio = new();
        ParameterList set = new();
        set.Objects.Add(new(1, Single(1)));
        set.Objects.Add(new(Crc32.Hash("Ragdoll"), Single(2)));
        set.Objects.Add(new(Crc32.Hash("SupportBone"), Single(3)));
        for (int i = 0; i < 4; i++)
        {
            ParameterList child = new();
            child.Objects.Add(new(1, Single(10 + i)));
            set.Lists.Add(new((uint)(100 + i), child));
        }
        pio.Root.Lists.Add(new(Crc32.Hash("ParamSet"), set));

        byte[] binary = pio.ToBinary();
        int[] order = Values(binary);

        Assert.Equal([1, 10, 11, 2, 12, 13, 3], order);
        Assert.Equal(binary, ParameterIO.FromYaml(ParameterIO.FromBinary(binary).ToYaml()).ToBinary());
    }

    private static ParameterObject Single(int value)
    {
        ParameterObject obj = new();
        obj.Set(1, Parameter.FromInt(value));
        return obj;
    }

    // The int values in the order the data section holds them.
    private static int[] Values(byte[] binary)
    {
        int count = BitConverter.ToInt32(binary, 0x20);
        int listCount = BitConverter.ToInt32(binary, 0x18), objectCount = BitConverter.ToInt32(binary, 0x1C);
        int start = 0x30 + BitConverter.ToInt32(binary, 0x14) + listCount * 12 + objectCount * 8 + count * 8;
        int size = BitConverter.ToInt32(binary, 0x24);
        return [.. Enumerable.Range(0, size / 4).Select(i => BitConverter.ToInt32(binary, start + i * 4))];
    }

    private static byte[] Curve(int curves)
    {
        byte[] raw = new byte[0x80 * curves];
        for (int c = 0; c < curves; c++)
        {
            BitConverter.TryWriteBytes(raw.AsSpan(c * 0x80), (uint)c);
            BitConverter.TryWriteBytes(raw.AsSpan(c * 0x80 + 4), 30u);
            for (int i = 2; i < 32; i++) BitConverter.TryWriteBytes(raw.AsSpan(c * 0x80 + i * 4), i * 0.25f);
        }
        return raw;
    }
}

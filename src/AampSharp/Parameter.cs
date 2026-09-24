using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AampSharp;

// Strings are held as text, everything else as the little endian bytes the file stores
// (buffer elements without their count), so every type round trips untouched.
public sealed class Parameter
{
    private Parameter(ParameterType type, byte[]? raw, string? text)
    {
        Type = type;
        Raw = raw ?? [];
        Text = text;
    }

    public ParameterType Type { get; }

    public byte[] Raw { get; }

    public string? Text { get; }

    public bool IsString => IsStringType(Type);

    public bool IsBuffer => Type is ParameterType.BufferInt or ParameterType.BufferF32
        or ParameterType.BufferU32 or ParameterType.BufferBinary;

    // ---- building ---------------------------------------------------------

    public static Parameter FromRaw(ParameterType type, byte[] raw)
    {
        if (IsStringType(type))
            throw new ArgumentException("String parameters are built with FromString.", nameof(type));

        int size = FixedSize(type);
        if (size >= 0 && raw.Length != size)
            throw new ArgumentException($"{type} takes {size} bytes, not {raw.Length}.", nameof(raw));
        if (type is ParameterType.BufferInt or ParameterType.BufferF32 or ParameterType.BufferU32 && raw.Length % 4 != 0)
            throw new ArgumentException($"{type} takes whole 4 byte elements.", nameof(raw));

        return new(type, raw, null);
    }

    public static Parameter FromString(ParameterType type, string value)
    {
        if (!IsStringType(type)) throw new ArgumentException($"{type} is not a string type.", nameof(type));
        return new(type, null, value);
    }

    public static Parameter FromBool(bool value) => Words(ParameterType.Bool, value ? 1u : 0u);
    public static Parameter FromInt(int value) => Words(ParameterType.Int, unchecked((uint)value));
    public static Parameter FromUInt(uint value) => Words(ParameterType.U32, value);
    public static Parameter FromFloat(float value) => Floats(ParameterType.F32, value);
    public static Parameter FromVector2(Vector2 value) => Floats(ParameterType.Vec2, value.X, value.Y);
    public static Parameter FromVector3(Vector3 value) => Floats(ParameterType.Vec3, value.X, value.Y, value.Z);
    public static Parameter FromVector4(Vector4 value) => Floats(ParameterType.Vec4, value.X, value.Y, value.Z, value.W);
    public static Parameter FromColor(Vector4 rgba) => Floats(ParameterType.Color, rgba.X, rgba.Y, rgba.Z, rgba.W);
    public static Parameter FromQuaternion(Quaternion value) => Floats(ParameterType.Quat, value.X, value.Y, value.Z, value.W);

    public static Parameter FromIntBuffer(ReadOnlySpan<int> values) => new(ParameterType.BufferInt, Bytes(values), null);
    public static Parameter FromFloatBuffer(ReadOnlySpan<float> values) => new(ParameterType.BufferF32, Bytes(values), null);
    public static Parameter FromUIntBuffer(ReadOnlySpan<uint> values) => new(ParameterType.BufferU32, Bytes(values), null);
    public static Parameter FromBinaryBuffer(ReadOnlySpan<byte> values) => new(ParameterType.BufferBinary, values.ToArray(), null);

    // ---- reading ----------------------------------------------------------

    public bool AsBool() => Word(ParameterType.Bool) != 0;
    public int AsInt() => unchecked((int)Word(ParameterType.Int));
    public uint AsUInt() => Word(ParameterType.U32);
    public float AsFloat() => Float(ParameterType.F32, 0);

    public Vector2 AsVector2() => new(Float(ParameterType.Vec2, 0), Float(ParameterType.Vec2, 1));

    public Vector3 AsVector3()
        => new(Float(ParameterType.Vec3, 0), Float(ParameterType.Vec3, 1), Float(ParameterType.Vec3, 2));

    public Vector4 AsVector4() => Four(ParameterType.Vec4);
    public Vector4 AsColor() => Four(ParameterType.Color);

    public Quaternion AsQuaternion()
    {
        Vector4 q = Four(ParameterType.Quat);
        return new(q.X, q.Y, q.Z, q.W);
    }

    public string AsString() => Text ?? throw new InvalidOperationException($"Parameter is {Type}, not a string.");

    public int[] AsIntBuffer() => Elements<int>(ParameterType.BufferInt);
    public float[] AsFloatBuffer() => Elements<float>(ParameterType.BufferF32);
    public uint[] AsUIntBuffer() => Elements<uint>(ParameterType.BufferU32);
    public byte[] AsBinaryBuffer() => Expect(ParameterType.BufferBinary).Raw.ToArray();

    public override string ToString() => IsString ? $"{Type} \"{Text}\"" : $"{Type} ({Raw.Length} bytes)";

    // ---- helpers ----------------------------------------------------------

    internal static bool IsStringType(ParameterType type) => type is ParameterType.String32
        or ParameterType.String64 or ParameterType.String256 or ParameterType.StringRef;

    /// <summary>Byte size of a fixed size type, or -1 for strings and buffers.</summary>
    internal static int FixedSize(ParameterType type) => type switch
    {
        ParameterType.Bool or ParameterType.F32 or ParameterType.Int or ParameterType.U32 => 4,
        ParameterType.Vec2 => 8,
        ParameterType.Vec3 => 12,
        ParameterType.Vec4 or ParameterType.Color or ParameterType.Quat => 16,
        ParameterType.Curve1 => 0x80,
        ParameterType.Curve2 => 0x100,
        ParameterType.Curve3 => 0x180,
        ParameterType.Curve4 => 0x200,
        _ => -1,
    };

    private static Parameter Words(ParameterType type, uint value)
    {
        byte[] raw = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, value);
        return new(type, raw, null);
    }

    private static Parameter Floats(ParameterType type, params ReadOnlySpan<float> values) => new(type, Bytes(values), null);

    private static byte[] Bytes<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        byte[] raw = MemoryMarshal.AsBytes(values).ToArray();
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("AampSharp needs a little endian host.");
        return raw;
    }

    private T[] Elements<T>(ParameterType type) where T : unmanaged
        => MemoryMarshal.Cast<byte, T>(Expect(type).Raw).ToArray();

    private uint Word(ParameterType type) => BinaryPrimitives.ReadUInt32LittleEndian(Expect(type).Raw);

    private float Float(ParameterType type, int index)
        => BinaryPrimitives.ReadSingleLittleEndian(Expect(type).Raw.AsSpan(index * 4));

    private Vector4 Four(ParameterType type)
        => new(Float(type, 0), Float(type, 1), Float(type, 2), Float(type, 3));

    private Parameter Expect(ParameterType type)
        => Type == type ? this : throw new InvalidOperationException($"Parameter is {Type}, not {type}.");
}

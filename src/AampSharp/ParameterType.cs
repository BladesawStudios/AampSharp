namespace AampSharp;

public enum ParameterType : byte
{
    Bool = 0,
    F32 = 1,
    Int = 2,
    Vec2 = 3,
    Vec3 = 4,
    Vec4 = 5,
    Color = 6,
    String32 = 7,
    String64 = 8,
    Curve1 = 9,
    Curve2 = 10,
    Curve3 = 11,
    Curve4 = 12,
    BufferInt = 13,
    BufferF32 = 14,
    String256 = 15,
    Quat = 16,
    U32 = 17,
    BufferU32 = 18,
    BufferBinary = 19,
    StringRef = 20,
}

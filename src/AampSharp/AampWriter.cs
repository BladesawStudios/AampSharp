using System.Buffers.Binary;
using System.Text;
using static AampSharp.AampReader;

namespace AampSharp;

internal static class AampWriter
{
    public static byte[] Write(ParameterIO pio)
    {
        // Walking depth first, each list's children are laid out together as the list is reached,
        // and so are its objects. The parameter table puts a list's children before its own
        // objects; values and strings follow each list's DataOrder.
        List<(uint Hash, ParameterList List)> lists = [(pio.RootHash, pio.Root)];
        Dictionary<ParameterList, int> childBlock = new(ReferenceEqualityComparer.Instance);
        List<(uint Hash, ParameterObject Object)> objects = [];
        Dictionary<ParameterList, int> objectBlock = new(ReferenceEqualityComparer.Instance);
        Lay(pio.Root, lists, childBlock, objects, objectBlock);

        List<ParameterObject> parameterOrder = [];
        ChildrenFirst(pio.Root, parameterOrder);

        int parameterCount = objects.Sum(o => o.Object.Parameters.Count);

        byte[] typeBytes = Encoding.UTF8.GetBytes(pio.Type);
        int pioOffset = DataBuilder.Align4(typeBytes.Length + 1);

        int listStart = HeaderSize + pioOffset;
        int objectStart = listStart + lists.Count * ListSize;
        int parameterStart = objectStart + objects.Count * ObjectSize;
        int dataStart = parameterStart + parameterCount * ParameterSize;

        DataBuilder data = new();
        Dictionary<ParameterObject, int> firstPlacement = new(ReferenceEqualityComparer.Instance);
        List<(int Offset, bool IsString)> placements = new(parameterCount);
        WalkData(pio.Root, obj =>
        {
            firstPlacement[obj] = placements.Count;
            foreach (var (_, parameter) in obj.Parameters) placements.Add(data.Add(parameter));
        });

        byte[] values = data.Values.ToArray();
        byte[] strings = data.Strings.ToArray();
        int stringStart = dataStart + values.Length;
        int fileSize = stringStart + strings.Length;
        byte[] output = new byte[fileSize];
        Span<byte> o = output;

        "AAMP"u8.CopyTo(o);
        W32(o, 0x04, 2);
        W32(o, 0x08, 3); // little endian, UTF-8
        W32(o, 0x0C, (uint)fileSize);
        W32(o, 0x10, pio.Version);
        W32(o, 0x14, (uint)pioOffset);
        W32(o, 0x18, (uint)lists.Count);
        W32(o, 0x1C, (uint)objects.Count);
        W32(o, 0x20, (uint)parameterCount);
        W32(o, 0x24, (uint)values.Length);
        W32(o, 0x28, (uint)strings.Length);
        W32(o, 0x2C, 0);
        typeBytes.CopyTo(o[HeaderSize..]);

        for (int i = 0; i < lists.Count; i++)
        {
            int at = listStart + i * ListSize;
            ParameterList list = lists[i].List;
            W32(o, at, lists[i].Hash);
            W16(o, at + 4, Units(listStart + childBlock[list] * ListSize - at));
            W16(o, at + 6, list.Lists.Count);
            W16(o, at + 8, Units(objectStart + objectBlock[list] * ObjectSize - at));
            W16(o, at + 10, list.Objects.Count);
        }

        Dictionary<ParameterObject, int> blockStart = new(ReferenceEqualityComparer.Instance);
        int nextParameter = parameterStart;
        foreach (ParameterObject obj in parameterOrder)
        {
            blockStart[obj] = nextParameter;
            int placement = firstPlacement[obj];
            foreach (var (hash, parameter) in obj.Parameters)
            {
                var (offset, isString) = placements[placement++];
                int target = (isString ? stringStart : dataStart) + offset;
                int relative = (target - nextParameter) / 4;
                if (relative > 0xFFFFFF) throw new InvalidOperationException("AAMP document too large to address.");

                W32(o, nextParameter, hash);
                W32(o, nextParameter + 4, (uint)relative | (uint)parameter.Type << 24);
                nextParameter += ParameterSize;
            }
        }

        for (int i = 0; i < objects.Count; i++)
        {
            int at = objectStart + i * ObjectSize;
            ParameterObject obj = objects[i].Object;
            W32(o, at, objects[i].Hash);
            W16(o, at + 4, Units(blockStart[obj] - at));
            W16(o, at + 6, obj.Parameters.Count);
        }

        values.CopyTo(o[dataStart..]);
        strings.CopyTo(o[stringStart..]);
        return output;
    }

    /// <summary>Visits objects in the order their values and strings are laid out.</summary>
    internal static void WalkData(ParameterList list, Action<ParameterObject> visit)
    {
        int nextObject = 0, nextList = 0;
        if (list.DataOrder is { } order && order.Count == list.Objects.Count + list.Lists.Count)
        {
            foreach (bool isObject in order)
            {
                if (isObject) visit(list.Objects[nextObject++].Value);
                else WalkData(list.Lists[nextList++].Value, visit);
            }
            return;
        }

        foreach (var (_, child) in list.Lists) WalkData(child, visit);
        foreach (var (_, obj) in list.Objects) visit(obj);
    }

    private static void Lay(
        ParameterList list,
        List<(uint Hash, ParameterList List)> lists, Dictionary<ParameterList, int> childBlock,
        List<(uint Hash, ParameterObject Object)> objects, Dictionary<ParameterList, int> objectBlock)
    {
        childBlock[list] = lists.Count;
        foreach (var (hash, child) in list.Lists) lists.Add((hash, child));

        objectBlock[list] = objects.Count;
        foreach (var (hash, obj) in list.Objects) objects.Add((hash, obj));

        foreach (var (_, child) in list.Lists) Lay(child, lists, childBlock, objects, objectBlock);
    }

    private static void ChildrenFirst(ParameterList list, List<ParameterObject> into)
    {
        foreach (var (_, child) in list.Lists) ChildrenFirst(child, into);
        foreach (var (_, obj) in list.Objects) into.Add(obj);
    }

    private static int Units(int bytes)
        => bytes / 4 <= ushort.MaxValue ? bytes / 4 : throw new InvalidOperationException("AAMP document too large to address.");

    private static void W16(Span<byte> o, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(o[at..], (ushort)value);
    private static void W32(Span<byte> o, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(o[at..], value);
}

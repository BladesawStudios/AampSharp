using System.Buffers.Binary;
using System.Text;

namespace AampSharp;

internal static class AampReader
{
    internal const int HeaderSize = 0x30;
    internal const int ListSize = 12;
    internal const int ObjectSize = 8;
    internal const int ParameterSize = 8;

    public static ParameterIO Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..4].SequenceEqual("AAMP"u8))
            throw new InvalidDataException("Not an AAMP document: expected an AAMP magic.");

        uint version = U32(data, 0x04);
        if (version != 2)
            throw new InvalidDataException($"Unsupported AAMP version {version}; this reads version 2.");

        if ((U32(data, 0x08) & 1) == 0)
            throw new InvalidDataException("Big endian AAMP (Wii U) is not supported.");

        int pioOffset = checked((int)U32(data, 0x14));
        int typeEnd = data[HeaderSize..].IndexOf((byte)0);
        int root = HeaderSize + pioOffset;

        ParameterIO pio = new()
        {
            Version = U32(data, 0x10),
            Type = Encoding.UTF8.GetString(data.Slice(HeaderSize, Math.Max(typeEnd, 0))),
            RootHash = U32(data, root),
            Root = ReadList(data, root),
        };

        int dataStart = root + (int)(U32(data, 0x18) * ListSize + U32(data, 0x1C) * ObjectSize + U32(data, 0x20) * ParameterSize);
        int valuesSize = (int)U32(data, 0x24);
        if (dataStart + valuesSize + (int)U32(data, 0x28) <= data.Length)
        {
            ReadOnlySpan<byte> values = data.Slice(dataStart, valuesSize);
            ReadOnlySpan<byte> strings = data.Slice(dataStart + valuesSize, (int)U32(data, 0x28));
            Order(pio.Root, true, new DataBuilder(), values, strings);
        }
        return pio;
    }

    // The file keeps no record of how a list's objects and child lists were interleaved, but the
    // value and string sections were filled in that order. Rebuild it one item at a time, taking
    // the next object or the next list, whichever carries on reproducing this file. An item that
    // adds nothing new matches anywhere, and then where it goes makes no difference.
    private static bool Order(ParameterList list, bool root, DataBuilder data, ReadOnlySpan<byte> values, ReadOnlySpan<byte> strings)
    {
        var start = data.Mark();
        List<bool> order = new(list.Objects.Count + list.Lists.Count);
        list.DataOrder = order;
        int nextObject = 0, nextList = 0;

        while (nextObject < list.Objects.Count || nextList < list.Lists.Count)
        {
            var mark = data.Mark();

            if (nextObject < list.Objects.Count)
            {
                foreach (var (_, parameter) in list.Objects[nextObject].Value.Parameters) data.Add(parameter);
                if (Matches(data, values, strings))
                {
                    order.Add(true);
                    nextObject++;
                    continue;
                }
                data.Rewind(mark);
            }

            if (nextList < list.Lists.Count)
            {
                if (Order(list.Lists[nextList].Value, false, data, values, strings))
                {
                    order.Add(false);
                    nextList++;
                    continue;
                }
                data.Rewind(mark);
            }

            // Nothing reproduces the file from here; the writer will lay this list out the default way.
            list.DataOrder = null;
            data.Rewind(start);
            AampWriter.WalkData(list, obj => { foreach (var (_, parameter) in obj.Parameters) data.Add(parameter); }, root);
            return false;
        }

        return true;
    }

    private static bool Matches(DataBuilder data, ReadOnlySpan<byte> values, ReadOnlySpan<byte> strings)
        => values.StartsWith(data.Values) && strings.StartsWith(data.Strings);

    private static ParameterList ReadList(ReadOnlySpan<byte> data, int at)
    {
        ParameterList list = new();

        int lists = at + U16(data, at + 4) * 4;
        int listCount = U16(data, at + 6);
        int objects = at + U16(data, at + 8) * 4;
        int objectCount = U16(data, at + 10);

        for (int i = 0; i < listCount; i++)
        {
            int child = lists + i * ListSize;
            list.Lists.Add(new(U32(data, child), ReadList(data, child)));
        }

        for (int i = 0; i < objectCount; i++)
        {
            int obj = objects + i * ObjectSize;
            list.Objects.Add(new(U32(data, obj), ReadObject(data, obj)));
        }

        return list;
    }

    private static ParameterObject ReadObject(ReadOnlySpan<byte> data, int at)
    {
        ParameterObject obj = new();

        int parameters = at + U16(data, at + 4) * 4;
        int count = U16(data, at + 6);

        for (int i = 0; i < count; i++)
        {
            int p = parameters + i * ParameterSize;
            uint word = U32(data, p + 4);
            int valueAt = p + (int)(word & 0xFFFFFF) * 4;
            obj.Parameters.Add(new(U32(data, p), ReadValue(data, (ParameterType)(word >> 24), valueAt)));
        }

        return obj;
    }

    private static Parameter ReadValue(ReadOnlySpan<byte> data, ParameterType type, int at)
    {
        switch (type)
        {
            case ParameterType.String32 or ParameterType.String64
                or ParameterType.String256 or ParameterType.StringRef:
                int end = data[at..].IndexOf((byte)0);
                return Parameter.FromString(type, Encoding.UTF8.GetString(data.Slice(at, end < 0 ? data.Length - at : end)));

            case ParameterType.BufferInt or ParameterType.BufferF32 or ParameterType.BufferU32:
                return Parameter.FromRaw(type, data.Slice(at, checked((int)U32(data, at - 4) * 4)).ToArray());

            case ParameterType.BufferBinary:
                return Parameter.FromRaw(type, data.Slice(at, checked((int)U32(data, at - 4))).ToArray());

            default:
                int size = Parameter.FixedSize(type);
                if (size < 0) throw new InvalidDataException($"Unknown AAMP parameter type {(int)type}.");
                return Parameter.FromRaw(type, data.Slice(at, size).ToArray());
        }
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
}

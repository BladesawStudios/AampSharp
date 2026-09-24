using System.Buffers.Binary;
using System.Text;

namespace AampSharp;

// The value and string sections, filled in the order parameters are met. A value reuses any
// earlier 4 byte aligned run of the same bytes, even inside a longer value (a float can point
// into a Vec3); a string is reused only whole.
internal sealed class DataBuilder
{
    private readonly MemoryStream _values = new();
    private readonly MemoryStream _strings = new();
    private readonly Dictionary<string, int> _stringOffsets = [];
    private readonly List<string> _stringOrder = [];

    public ReadOnlySpan<byte> Values => _values.GetBuffer().AsSpan(0, (int)_values.Length);
    public ReadOnlySpan<byte> Strings => _strings.GetBuffer().AsSpan(0, (int)_strings.Length);

    /// <returns>The offset within its section, and whether that is the string section.</returns>
    public (int Offset, bool IsString) Add(Parameter parameter)
    {
        if (parameter.IsString)
        {
            string text = parameter.Text!;
            if (!_stringOffsets.TryGetValue(text, out int at))
            {
                at = (int)_strings.Length;
                _stringOffsets[text] = at;
                _stringOrder.Add(text);
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                _strings.Write(bytes);
                _strings.Write(new byte[Align4(bytes.Length + 1) - bytes.Length]);
            }
            return (at, true);
        }

        byte[] block = parameter.IsBuffer ? WithCount(parameter) : parameter.Raw;
        int found = FindAligned(Values, block);
        if (found < 0)
        {
            found = (int)_values.Length;
            _values.Write(block);
            _values.Write(new byte[Align4(block.Length) - block.Length]);
        }
        return (found + (parameter.IsBuffer ? 4 : 0), false);
    }

    public (int Values, int Strings) Mark() => ((int)_values.Length, _stringOrder.Count);

    public void Rewind((int Values, int Strings) mark)
    {
        _values.SetLength(mark.Values);
        while (_stringOrder.Count > mark.Strings)
        {
            _stringOffsets.Remove(_stringOrder[^1]);
            _stringOrder.RemoveAt(_stringOrder.Count - 1);
        }
        _strings.SetLength(_stringOrder.Count == 0 ? 0 : _stringOffsets[_stringOrder[^1]]
            + Align4(Encoding.UTF8.GetByteCount(_stringOrder[^1]) + 1));
    }

    private static byte[] WithCount(Parameter parameter)
    {
        int count = parameter.Type is ParameterType.BufferBinary ? parameter.Raw.Length : parameter.Raw.Length / 4;
        byte[] block = new byte[4 + parameter.Raw.Length];
        BinaryPrimitives.WriteInt32LittleEndian(block, count);
        parameter.Raw.CopyTo(block, 4);
        return block;
    }

    private static int FindAligned(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty) return -1;
        for (int at = 0; at + needle.Length <= haystack.Length; at += 4)
            if (haystack.Slice(at, needle.Length).SequenceEqual(needle)) return at;
        return -1;
    }

    internal static int Align4(int value) => (value + 3) & ~3;
}

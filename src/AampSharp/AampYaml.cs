using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AampSharp;

// oead's layout: !io { version, type, <root>: !list { objects, lists } }, !obj parameter maps,
// names where the table knows them and bare integer hashes where it doesn't.
internal static partial class AampYaml
{
    private static readonly Dictionary<ParameterType, string> Tags = new()
    {
        [ParameterType.Vec2] = "!vec2",
        [ParameterType.Vec3] = "!vec3",
        [ParameterType.Vec4] = "!vec4",
        [ParameterType.Color] = "!color",
        [ParameterType.Quat] = "!quat",
        [ParameterType.String32] = "!str32",
        [ParameterType.String64] = "!str64",
        [ParameterType.String256] = "!str256",
        [ParameterType.U32] = "!u",
        [ParameterType.Curve1] = "!curve",
        [ParameterType.Curve2] = "!curve",
        [ParameterType.Curve3] = "!curve",
        [ParameterType.Curve4] = "!curve",
        [ParameterType.BufferInt] = "!buffer_int",
        [ParameterType.BufferF32] = "!buffer_f32",
        [ParameterType.BufferU32] = "!buffer_u32",
        [ParameterType.BufferBinary] = "!buffer_binary",
    };

    private const int CurveSize = 0x80;

    public static string Write(ParameterIO pio, NameTable? names)
    {
        Func<uint, string?> find = names is null ? h => NameTable.BotW.Find(h) ?? NameTable.TotK.Find(h) : names.Find;

        StringWriter text = new();
        Emitter e = new(text, new EmitterSettings().WithBestWidth(int.MaxValue));
        e.Emit(new StreamStart());
        e.Emit(new DocumentStart());
        e.Emit(new MappingStart(AnchorName.Empty, new TagName("!io"), false, MappingStyle.Block));
        Plain(e, "version");
        Plain(e, pio.Version.ToString(CultureInfo.InvariantCulture));
        Plain(e, "type");
        Text(e, pio.Type);
        Key(e, pio.RootHash, find);
        List(e, pio.Root, find);
        e.Emit(new MappingEnd());
        e.Emit(new DocumentEnd(true));
        e.Emit(new StreamEnd());
        return text.ToString();
    }

    public static ParameterIO Read(string yaml)
    {
        Parser p = new(new StringReader(yaml));
        p.Consume<StreamStart>();
        p.Consume<DocumentStart>();
        MappingStart start = p.Consume<MappingStart>();
        if (start.Tag.IsEmpty || start.Tag.Value != "!io") throw Error(start, "expected an !io mapping");

        ParameterIO pio = new();
        bool hasRoot = false;
        while (!p.TryConsume<MappingEnd>(out _))
        {
            Scalar key = p.Consume<Scalar>();
            if (key.Style is ScalarStyle.Plain && key.Value == "version")
                pio.Version = checked((uint)(Integer(p.Consume<Scalar>()) ?? throw Error(key, "version must be an integer")));
            else if (key.Style is ScalarStyle.Plain && key.Value == "type")
                pio.Type = p.Consume<Scalar>().Value;
            else if (!hasRoot)
            {
                pio.RootHash = Hash(key);
                pio.Root = ReadList(p);
                hasRoot = true;
            }
            else throw Error(key, "an !io holds one root list");
        }

        p.Consume<DocumentEnd>();
        return pio;
    }

    // ---- writing ----------------------------------------------------------

    private static void List(IEmitter e, ParameterList list, Func<uint, string?> find)
    {
        e.Emit(new MappingStart(AnchorName.Empty, new TagName("!list"), false, MappingStyle.Block));

        Plain(e, "objects");
        e.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, list.Objects.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
        foreach (var (hash, obj) in list.Objects)
        {
            Key(e, hash, find);
            e.Emit(new MappingStart(AnchorName.Empty, new TagName("!obj"), false, obj.Parameters.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
            foreach (var (name, parameter) in obj.Parameters)
            {
                Key(e, name, find);
                Value(e, parameter);
            }
            e.Emit(new MappingEnd());
        }
        e.Emit(new MappingEnd());

        Plain(e, "lists");
        e.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, list.Lists.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
        foreach (var (hash, child) in list.Lists)
        {
            Key(e, hash, find);
            List(e, child, find);
        }
        e.Emit(new MappingEnd());

        e.Emit(new MappingEnd());
    }

    private static void Value(IEmitter e, Parameter p)
    {
        switch (p.Type)
        {
            case ParameterType.Bool: Plain(e, p.AsBool() ? "true" : "false"); break;
            case ParameterType.Int: Plain(e, p.AsInt().ToString(CultureInfo.InvariantCulture)); break;
            case ParameterType.F32: Plain(e, Real(p.AsFloat())); break;
            case ParameterType.StringRef: Text(e, p.AsString()); break;
            case ParameterType.U32: Tagged(e, "!u", $"0x{p.AsUInt():X}"); break;

            case ParameterType.String32 or ParameterType.String64 or ParameterType.String256:
                bool plain = p.AsString().Length > 0 && Resolve(p.AsString()) is string;
                e.Emit(new Scalar(AnchorName.Empty, new TagName(Tags[p.Type]), p.AsString(),
                    plain ? ScalarStyle.Any : ScalarStyle.DoubleQuoted, false, false));
                break;

            case ParameterType.Vec2 or ParameterType.Vec3 or ParameterType.Vec4 or ParameterType.Color
                or ParameterType.Quat or ParameterType.BufferF32:
                Sequence(e, Tags[p.Type], Words(p.Raw).Select(w => Real(BitConverter.UInt32BitsToSingle(w))));
                break;

            case ParameterType.Curve1 or ParameterType.Curve2 or ParameterType.Curve3 or ParameterType.Curve4:
                Sequence(e, "!curve", Words(p.Raw).Select((w, i) => i % (CurveSize / 4) < 2
                    ? w.ToString(CultureInfo.InvariantCulture)
                    : Real(BitConverter.UInt32BitsToSingle(w))));
                break;

            case ParameterType.BufferInt:
                Sequence(e, Tags[p.Type], Words(p.Raw).Select(w => unchecked((int)w).ToString(CultureInfo.InvariantCulture)));
                break;

            case ParameterType.BufferU32:
                Sequence(e, Tags[p.Type], Words(p.Raw).Select(w => w.ToString(CultureInfo.InvariantCulture)));
                break;

            case ParameterType.BufferBinary:
                Sequence(e, Tags[p.Type], p.Raw.Select(b => b.ToString(CultureInfo.InvariantCulture)));
                break;

            default: throw new InvalidOperationException($"{p.Type} has no YAML form.");
        }
    }

    private static IEnumerable<uint> Words(byte[] raw)
    {
        for (int i = 0; i + 4 <= raw.Length; i += 4) yield return BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i));
    }

    private static void Sequence(IEmitter e, string tag, IEnumerable<string> values)
    {
        e.Emit(new SequenceStart(AnchorName.Empty, new TagName(tag), false, SequenceStyle.Flow));
        foreach (string value in values) Plain(e, value);
        e.Emit(new SequenceEnd());
    }

    private static void Key(IEmitter e, uint hash, Func<uint, string?> find)
    {
        if (find(hash) is { } name) Text(e, name);
        else Plain(e, hash.ToString(CultureInfo.InvariantCulture));
    }

    private static void Plain(IEmitter e, string value)
        => e.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, ScalarStyle.Plain, true, false));

    private static void Tagged(IEmitter e, string tag, string value)
        => e.Emit(new Scalar(AnchorName.Empty, new TagName(tag), value, ScalarStyle.Plain, false, false));

    private static void Text(IEmitter e, string value)
    {
        bool plain = value.Length > 0 && Resolve(value) is string && Unsigned(value) is null;
        e.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value,
            plain ? ScalarStyle.Any : ScalarStyle.DoubleQuoted, plain, !plain));
    }

    private static string Real(float value)
    {
        if (float.IsNaN(value)) return ".nan";
        if (float.IsPositiveInfinity(value)) return ".inf";
        if (float.IsNegativeInfinity(value)) return "-.inf";

        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('.')) return text;
        int exponent = text.IndexOf('E');
        return exponent >= 0 ? text.Insert(exponent, ".0") : text + ".0";
    }

    // ---- reading ----------------------------------------------------------

    private static ParameterList ReadList(IParser p)
    {
        MappingStart start = p.Consume<MappingStart>();
        if (start.Tag.IsEmpty || start.Tag.Value != "!list") throw Error(start, "expected a !list mapping");

        ParameterList list = new();
        while (!p.TryConsume<MappingEnd>(out _))
        {
            Scalar section = p.Consume<Scalar>();
            p.Consume<MappingStart>();
            while (!p.TryConsume<MappingEnd>(out _))
            {
                uint hash = Hash(p.Consume<Scalar>());
                switch (section.Value)
                {
                    case "objects": list.Objects.Add(new(hash, ReadObject(p))); break;
                    case "lists": list.Lists.Add(new(hash, ReadList(p))); break;
                    default: throw Error(section, $"a !list holds objects and lists, not {section.Value}");
                }
            }
        }
        return list;
    }

    private static ParameterObject ReadObject(IParser p)
    {
        MappingStart start = p.Consume<MappingStart>();
        if (start.Tag.IsEmpty || start.Tag.Value != "!obj") throw Error(start, "expected an !obj mapping");

        ParameterObject obj = new();
        while (!p.TryConsume<MappingEnd>(out _))
            obj.Parameters.Add(new(Hash(p.Consume<Scalar>()), ReadParameter(p)));
        return obj;
    }

    private static Parameter ReadParameter(IParser p)
    {
        if (p.TryConsume<Scalar>(out Scalar? s))
        {
            if (s.Tag.IsEmpty)
            {
                object? value = s.Style is ScalarStyle.Plain ? Resolve(s.Value) : s.Value;
                return value switch
                {
                    bool b => Parameter.FromBool(b),
                    long l => Parameter.FromInt(checked((int)l)),
                    float f => Parameter.FromFloat(f),
                    string text => Parameter.FromString(ParameterType.StringRef, text),
                    _ => throw Error(s, "null is not a parameter value"),
                };
            }

            return s.Tag.Value switch
            {
                "!u" => Parameter.FromUInt(checked((uint)(Integer(s) ?? throw Error(s, "expected an integer")))),
                "!str32" => Parameter.FromString(ParameterType.String32, s.Value),
                "!str64" => Parameter.FromString(ParameterType.String64, s.Value),
                "!str256" => Parameter.FromString(ParameterType.String256, s.Value),
                "tag:yaml.org,2002:str" or "!" => Parameter.FromString(ParameterType.StringRef, s.Value),
                var tag => throw Error(s, $"unknown parameter tag {tag}"),
            };
        }

        SequenceStart start = p.Consume<SequenceStart>();
        List<Scalar> items = [];
        while (!p.TryConsume<SequenceEnd>(out _)) items.Add(p.Consume<Scalar>());

        string seqTag = start.Tag.IsEmpty ? "" : start.Tag.Value;
        switch (seqTag)
        {
            case "!vec2" or "!vec3" or "!vec4" or "!color" or "!quat" or "!buffer_f32":
                ParameterType type = Tags.First(t => t.Value == seqTag).Key;
                return Parameter.FromRaw(type, Pack(items.Select(i => BitConverter.SingleToUInt32Bits(Single(i)))));

            case "!curve":
                int words = CurveSize / 4;
                if (items.Count == 0 || items.Count % words != 0 || items.Count / words > 4)
                    throw Error(start, $"a !curve holds 1 to 4 curves of {words} values");
                return Parameter.FromRaw((ParameterType)((int)ParameterType.Curve1 + items.Count / words - 1), Pack(items.Select((item, i) => i % words < 2
                    ? checked((uint)(Integer(item) ?? throw Error(item, "expected an integer")))
                    : BitConverter.SingleToUInt32Bits(Single(item)))));

            case "!buffer_int":
                return Parameter.FromRaw(ParameterType.BufferInt, Pack(items.Select(i =>
                    unchecked((uint)checked((int)(Signed(i.Value) ?? throw Error(i, "expected an integer")))))));

            case "!buffer_u32":
                return Parameter.FromRaw(ParameterType.BufferU32, Pack(items.Select(i =>
                    checked((uint)(Integer(i) ?? throw Error(i, "expected an integer"))))));

            case "!buffer_binary":
                return Parameter.FromRaw(ParameterType.BufferBinary, items.Select(i =>
                    checked((byte)(Integer(i) ?? throw Error(i, "expected an integer")))).ToArray());

            default: throw Error(start, $"unknown sequence tag {seqTag}");
        }
    }

    private static byte[] Pack(IEnumerable<uint> words)
    {
        List<byte> raw = [];
        Span<byte> word = stackalloc byte[4];
        foreach (uint w in words)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, w);
            raw.AddRange(word);
        }
        return [.. raw];
    }

    private static uint Hash(Scalar key)
        => key.Style is ScalarStyle.Plain && Integer(key) is ulong hash ? checked((uint)hash) : Crc32.Hash(key.Value);

    // An untagged plain scalar, typed by the YAML core schema: null, bool, long, float or string.
    private static object? Resolve(string v)
    {
        if (v is "" or "~" or "null" or "Null" or "NULL") return null;
        if (v is "true" or "True" or "TRUE") return true;
        if (v is "false" or "False" or "FALSE") return false;
        if (Signed(v) is long l) return l;
        if (FloatPattern().IsMatch(v) && Real(v) is float f) return f;
        return v;
    }

    private static float Single(Scalar s) => Real(s.Value) ?? throw Error(s, "expected a number");

    private static float? Real(string v) => v switch
    {
        ".nan" or ".NaN" or ".NAN" => float.NaN,
        ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" => float.PositiveInfinity,
        "-.inf" or "-.Inf" or "-.INF" => float.NegativeInfinity,
        _ => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null,
    };

    private static ulong? Integer(Scalar s) => Unsigned(s.Value);

    private static long? Signed(string v)
    {
        bool negative = v.StartsWith('-');
        if (negative || v.StartsWith('+')) v = v[1..];
        if (v.StartsWith('-') || v.StartsWith('+')) return null;
        if (Unsigned(v) is not ulong magnitude) return null;
        if (negative) return magnitude <= (ulong)long.MaxValue + 1 ? unchecked(-(long)magnitude) : null;
        return magnitude <= long.MaxValue ? (long)magnitude : null;
    }

    private static ulong? Unsigned(string v)
    {
        if (v.StartsWith('+')) v = v[1..];
        if (v.StartsWith("0x") && ulong.TryParse(v.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex)) return hex;
        if (v.StartsWith("0o"))
        {
            try { return v.Length > 2 ? Convert.ToUInt64(v[2..], 8) : null; }
            catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
        }
        return v.Length > 0 && char.IsAsciiDigit(v[0]) && ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out ulong dec) ? dec : null;
    }

    private static YamlException Error(ParsingEvent at, string message) => new(at.Start, at.End, message);

    [GeneratedRegex(@"^([-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex FloatPattern();
}

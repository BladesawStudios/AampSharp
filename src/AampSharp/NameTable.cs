using System.Collections.Frozen;

namespace AampSharp;

// AAMP stores CRC32 hashes, not names; a table maps them back.
public sealed class NameTable
{
    private static readonly Lazy<NameTable> BotwTable = new(() => Load("AampSharp.BotwNames.txt"));
    private static readonly Lazy<NameTable> TotkTable = new(() => Load("AampSharp.TotkNames.txt"));

    private readonly Dictionary<uint, string> _names = [];
    private FrozenDictionary<uint, string>? _frozen;

    public NameTable() { }

    public NameTable(IEnumerable<string> names)
    {
        foreach (string name in names) Add(name);
    }

    /// <summary>Every name used by Breath of the Wild's AAMP files. Read only; copy it to extend.</summary>
    public static NameTable BotW => BotwTable.Value;

    /// <summary>Every known name used by Tears of the Kingdom's AAMP files. Read only; copy it to extend.</summary>
    public static NameTable TotK => TotkTable.Value;

    public int Count => _frozen?.Count ?? _names.Count;

    public void Add(string name)
    {
        if (_frozen is not null) throw new InvalidOperationException("This name table is read only; copy it to add names.");
        _names[Crc32.Hash(name)] = name;
    }

    public NameTable Copy()
    {
        NameTable copy = new();
        foreach (var (hash, name) in _frozen ?? (IReadOnlyDictionary<uint, string>)_names) copy._names[hash] = name;
        return copy;
    }

    public string? Find(uint hash)
        => (_frozen is not null ? _frozen.TryGetValue(hash, out string? name) : _names.TryGetValue(hash, out name)) ? name : null;

    public string? Find(string maybeName) => Find(Crc32.Hash(maybeName));

    /// <summary>The name, or the hash as 0x followed by eight hex digits.</summary>
    public string NameOf(uint hash) => Find(hash) ?? $"0x{hash:X8}";

    private static NameTable Load(string resource)
    {
        using Stream stream = typeof(NameTable).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"AampSharp was built without its name list {resource}.");
        using StreamReader reader = new(stream);

        NameTable table = new();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) table.Add(line);

        table._frozen = table._names.ToFrozenDictionary();
        table._names.Clear();
        return table;
    }
}

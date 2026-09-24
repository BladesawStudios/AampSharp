namespace AampSharp;

public sealed class ParameterList
{
    public List<KeyValuePair<uint, ParameterList>> Lists { get; } = [];
    public List<KeyValuePair<uint, ParameterObject>> Objects { get; } = [];

    // How this list's objects (true) and child lists (false) were interleaved in the document it
    // was made from, which decides the order of the value and string sections. Read back from
    // those sections; null means child lists first.
    internal List<bool>? DataOrder { get; set; }

    public ParameterObject? Object(string name) => Object(Crc32.Hash(name));

    public ParameterObject? Object(uint hash)
    {
        foreach (var (key, value) in Objects)
            if (key == hash) return value;
        return null;
    }

    public ParameterList? List(string name) => List(Crc32.Hash(name));

    public ParameterList? List(uint hash)
    {
        foreach (var (key, value) in Lists)
            if (key == hash) return value;
        return null;
    }
}

namespace AampSharp;

public sealed class ParameterObject
{
    public List<KeyValuePair<uint, Parameter>> Parameters { get; } = [];

    public Parameter? this[uint hash]
    {
        get
        {
            foreach (var (key, value) in Parameters)
                if (key == hash) return value;
            return null;
        }
    }

    public Parameter? this[string name] => this[Crc32.Hash(name)];

    public void Set(string name, Parameter value) => Set(Crc32.Hash(name), value);

    public void Set(uint hash, Parameter value)
    {
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (Parameters[i].Key != hash) continue;
            Parameters[i] = new(hash, value);
            return;
        }
        Parameters.Add(new(hash, value));
    }

    public bool Remove(string name) => Remove(Crc32.Hash(name));

    public bool Remove(uint hash) => Parameters.RemoveAll(p => p.Key == hash) > 0;
}

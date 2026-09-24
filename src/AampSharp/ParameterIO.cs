namespace AampSharp;

public sealed class ParameterIO
{
    public const string RootName = "param_root";

    public uint Version { get; set; }

    public string Type { get; set; } = "xml";

    public uint RootHash { get; set; } = Crc32.Hash(RootName);

    public ParameterList Root { get; set; } = new();

    public static ParameterIO FromBinary(ReadOnlySpan<byte> data) => AampReader.Read(data);

    public static ParameterIO FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    public byte[] ToBinary() => AampWriter.Write(this);

    public void ToFile(string path) => File.WriteAllBytes(path, ToBinary());
}

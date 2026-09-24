using System.Buffers.Binary;
using System.Collections.Concurrent;
using AampSharp;
using SarcSharp;

// Rewrites every AAMP document in a romfs dump (loose, Yaz0 compressed, or inside SARC packs at
// any depth) and checks the bytes come back identical and that the name table knows every name.
//
// usage: AampSharp.Verify <romfs> [failures.txt]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: AampSharp.Verify <romfs> [failures.txt]");
    return 2;
}

string romfs = args[0];
ConcurrentDictionary<string, int> byExtension = new();
ConcurrentBag<string> failures = [];
ConcurrentDictionary<uint, byte> hashes = new();
int total = 0, exact = 0;

Parallel.ForEach(Directory.EnumerateFiles(romfs, "*", SearchOption.AllDirectories), path =>
{
    byte[] data;
    try { data = File.ReadAllBytes(path); }
    catch { return; }
    Visit(Path.GetRelativePath(romfs, path), data, 0);
});

int unnamed = hashes.Keys.Count(h => NameTable.BotW.Find(h) is null);
Console.WriteLine($"{exact}/{total} AAMP documents rewrite byte for byte");
Console.WriteLine($"{hashes.Count - unnamed}/{hashes.Count} distinct names known to NameTable.BotW");
foreach (var (ext, count) in byExtension.OrderByDescending(e => e.Value)) Console.WriteLine($"  {count,6}  {ext}");
foreach (string f in failures.OrderBy(f => f).Take(25)) Console.WriteLine("  " + f);
if (args.Length > 1) File.WriteAllLines(args[1], failures.OrderBy(f => f));
return failures.IsEmpty ? 0 : 1;

void Visit(string name, byte[] data, int depth)
{
    if (data.AsSpan().StartsWith("Yaz0"u8)) data = Yaz0(data);

    if (data.AsSpan().StartsWith("SARC"u8) && depth < 4)
    {
        SarcFile sarc;
        try { sarc = SarcFile.FromBinary(data); }
        catch { return; }
        foreach (SarcEntry entry in sarc.Entries) Visit($"{name}/{entry.Name}", entry.Data, depth + 1);
        return;
    }

    if (!data.AsSpan().StartsWith("AAMP"u8)) return;
    Interlocked.Increment(ref total);
    byExtension.AddOrUpdate(Path.GetExtension(name), 1, (_, n) => n + 1);

    try
    {
        ParameterIO pio = ParameterIO.FromBinary(data);
        Collect(pio.RootHash, pio.Root);

        byte[] back = pio.ToBinary();
        if (back.AsSpan().SequenceEqual(data))
        {
            Interlocked.Increment(ref exact);
            return;
        }

        int i = 0;
        while (i < Math.Min(back.Length, data.Length) && back[i] == data[i]) i++;
        failures.Add($"{name}: first diff 0x{i:X}, size {back.Length} vs {data.Length}");
    }
    catch (Exception e)
    {
        failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
    }
}

void Collect(uint hash, ParameterList list)
{
    hashes.TryAdd(hash, 0);
    foreach (var (h, child) in list.Lists) Collect(h, child);
    foreach (var (h, obj) in list.Objects)
    {
        hashes.TryAdd(h, 0);
        foreach (var (p, _) in obj.Parameters) hashes.TryAdd(p, 0);
    }
}

static byte[] Yaz0(byte[] src)
{
    byte[] dst = new byte[BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(4))];
    int s = 16, d = 0;
    while (d < dst.Length)
    {
        byte header = src[s++];
        for (int bit = 7; bit >= 0 && d < dst.Length; bit--)
        {
            if ((header >> bit & 1) != 0)
            {
                dst[d++] = src[s++];
                continue;
            }

            int b1 = src[s++], b2 = src[s++];
            int from = d - ((b1 & 0x0F) << 8 | b2) - 1;
            int length = b1 >> 4 == 0 ? src[s++] + 0x12 : (b1 >> 4) + 2;
            for (int k = 0; k < length; k++) dst[d++] = dst[from + k];
        }
    }
    return dst;
}

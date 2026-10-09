using System.Text.RegularExpressions;

namespace AampSharp;

/// <summary>
/// Finds the name behind a hash the way oead does when it writes text: the table first, then names built from
/// the parent's name and the position (<c>bone_list</c> holding <c>bone_0</c>, <c>bone_1</c>, ...), then a list of
/// numbered templates. Names it works out are kept, so a repeat costs nothing.
/// </summary>
internal sealed partial class NameGuesser(Func<uint, string?> find, IReadOnlyList<string>? numbered)
{
    private static readonly string[] Formats = ["{0}{1}", "{0}_{1}", "{0}{1:00}", "{0}_{1:00}", "{0}{1:000}", "{0}_{1:000}"];

    private static readonly Lazy<string[]> NumberedTemplates = new(LoadNumbered);

    private readonly Dictionary<uint, string> _guessed = [];

    [GeneratedRegex(@"%(0?)([0-9]*)[diu]")]
    private static partial Regex Spec();

    [GeneratedRegex(@"%[-+ 0#]*[0-9]*(\.[0-9]+)?[a-zA-Z]+")]
    private static partial Regex AnySpec();

    /// <summary>The templates oead tries as a last resort, such as <c>Check_%d</c>.</summary>
    public static IReadOnlyList<string> DefaultNumbered => NumberedTemplates.Value;

    public static NameGuesser Default(Func<uint, string?> find) => new(find, DefaultNumbered);

    /// <summary>A table of the names that appear as text in the file itself, which has no numbered templates.</summary>
    public static NameGuesser Extra(IEnumerable<string> strings)
    {
        Dictionary<uint, string> names = [];
        foreach (string s in strings) names[Crc32.Hash(s)] = s;
        return new NameGuesser(h => names.GetValueOrDefault(h), null);
    }

    public string? Get(uint hash, int index, uint parent)
    {
        if (find(hash) is { } known) return known;
        if (_guessed.TryGetValue(hash, out string? seen)) return seen;

        if (find(parent) is { } parentName)
        {
            if (Test(parentName) is { } match) return match;
            if (Test("Children") is { } children) return children;
            if (Test("Child") is { } child) return child;

            // A plural parent often holds singular names.
            foreach (string suffix in (string[])["s", "es", "List"])
                if (parentName.EndsWith(suffix, StringComparison.Ordinal) && Test(parentName[..^suffix.Length]) is { } singular)
                    return singular;
        }

        if (numbered is not null)
        {
            foreach (string template in numbered)
            {
                for (int i = 0; i < index + 2; i++)
                {
                    if (Fill(template, i) is not { } candidate) break;
                    if (Crc32.Hash(candidate) == hash) return Keep(hash, candidate);
                }
            }
        }

        return null;

        string? Test(string prefix)
        {
            foreach (int i in (int[])[index, index + 1])
            {
                foreach (string format in Formats)
                {
                    string candidate = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, prefix, i);
                    if (Crc32.Hash(candidate) == hash) return Keep(hash, candidate);
                }
            }

            return null;
        }
    }

    private string Keep(uint hash, string name) => _guessed[hash] = name;

    // printf with one integer: %d, %u, %02d, %04d ... A template with anything else in it is skipped, as oead does.
    private static string? Fill(string template, int value)
    {
        if (AnySpec().Matches(template).Count != 1) return null;

        Match spec = Spec().Match(template);
        if (!spec.Success) return null;

        int width = spec.Groups[2].Length > 0 ? int.Parse(spec.Groups[2].Value) : 0;
        string number = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        number = spec.Groups[1].Length > 0 ? number.PadLeft(width, '0') : number.PadLeft(width, ' ');

        return template[..spec.Index] + number + template[(spec.Index + spec.Length)..];
    }

    private static string[] LoadNumbered()
    {
        using Stream stream = typeof(NameGuesser).Assembly.GetManifestResourceStream("AampSharp.NumberedNames.txt")
            ?? throw new InvalidOperationException("AampSharp was built without its numbered name list.");
        using StreamReader reader = new(stream);

        List<string> lines = [];
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) lines.Add(line);
        return [.. lines];
    }
}

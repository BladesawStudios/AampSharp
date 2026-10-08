using System.Collections.Concurrent;

namespace AampSharp.Tests;

public class CorpusTests
{
    // Written without value deduplication, which the writer always does, so no layout reproduces it.
    private static readonly HashSet<string> NotByteExact =
    [
        Path.Combine("Pack", "TitleBG.pack") + "/Terrain/System/tera_resource.Nin_NX_NVN.release.ssarc/vege.bvege",
    ];

    [BotwCorpusFact]
    public void BotwRewritesByteForByteDirectlyAndThroughYaml() => Check(Corpus.BotwRoot!, 105000);

    [TotkCorpusFact]
    public void TotkRewritesByteForByteDirectlyAndThroughYaml() => Check(Corpus.TotkRoot!, 1300);

    private static void Check(string root, int expected)
    {
        ConcurrentBag<string> failures = [];
        int count = 0;

        Corpus.ForEach(root, (name, data) =>
        {
            if (!data.AsSpan().StartsWith("AAMP"u8)) return;
            Interlocked.Increment(ref count);
            try
            {
                ParameterIO pio = ParameterIO.FromBinary(data);
                bool exact = !NotByteExact.Contains(name);

                if (exact && !pio.ToBinary().AsSpan().SequenceEqual(data))
                {
                    failures.Add($"{name}: rewrite differs");
                    return;
                }

                string yaml = pio.ToYaml();
                ParameterIO back = ParameterIO.FromYaml(yaml);
                if (back.ToYaml() != yaml) failures.Add($"{name}: YAML re-emit differs");
                else if (exact && !back.ToBinary().AsSpan().SequenceEqual(data)) failures.Add($"{name}: YAML rebuild differs");
                else if (!exact && !Same(pio, ParameterIO.FromBinary(back.ToBinary()))) failures.Add($"{name}: YAML rebuild changes the tree");
            }
            catch (Exception e)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
            }
        });

        Assert.True(count >= expected, $"Expected the full corpus, found {count} AAMP documents.");
        Assert.True(failures.IsEmpty, $"{failures.Count} failures:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Order().Take(20))}");
    }

    private static bool Same(ParameterIO a, ParameterIO b) => a.ToYaml() == b.ToYaml();
}

using PortGate.Client;

namespace Atsm.Tests.PortGate;

public class LinkVectorTests
{
    private static readonly byte[] Transfer = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    /// <summary>Same units as PortGate/tests/test_main.cpp; both must match conformance/link-v1/vectors.txt.</summary>
    public static IEnumerable<object[]> Units() =>
    [
        ["hello-controller", LinkUnit.Control(LinkType.Hello, 0, LinkCbor.Map((0, 1UL), (1, "controller"), (2, "edc-server-a")))],
        ["acquire", LinkUnit.Control(LinkType.Acquire, 1, LinkCbor.Map((0, 7UL), (1, 10L), (2, 5000UL)))],
        ["granted", LinkUnit.Control(LinkType.Granted, 1, LinkCbor.Map((0, 7UL), (1, 3UL), (2, Transfer), (3, 15000UL), (4, 0UL), (5, false), (6, 12UL)))],
        ["confirm-transfer", LinkUnit.Control(LinkType.ConfirmTransfer, 1, LinkCbor.Map((1, 3UL), (2, Transfer)))],
        ["serial-data", LinkUnit.Serial(1, 2, 300, 3, [0x05])],
        ["write-status", LinkUnit.Control(LinkType.WriteStatus, 1, LinkCbor.Map((0, 2UL), (1, 1UL), (2, 1UL), (3, 0UL)))],
        ["denied", LinkUnit.Control(LinkType.Denied, 1, LinkCbor.Map((0, 8UL), (1, "PORTGATE-QUEUE-FULL")))],
        ["heartbeat", LinkUnit.Control(LinkType.Heartbeat, 0, LinkCbor.Map((0, 4294967296UL)))],
        ["error-negative", LinkUnit.Control(LinkType.Error, 0, LinkCbor.Map((0, "PORTGATE-X"), (1, -500L)))],
    ];

    [Theory]
    [MemberData(nameof(Units))]
    public void Encoding_matches_the_shared_vector(string name, LinkUnit unit)
    {
        var expected = Vectors()[name];

        Assert.Equal(expected, Convert.ToHexStringLower(unit.Encode()));

        var decoded = new List<LinkUnit>();
        Assert.Equal(LinkDecodeError.None, new LinkDecoder().Feed(Convert.FromHexString(expected), decoded));
        Assert.Equal(expected, Convert.ToHexStringLower(Assert.Single(decoded).Encode()));
    }

    [Fact]
    public void Decoder_accepts_any_split_and_rejects_oversized_units()
    {
        var stream = Units().SelectMany(u => ((LinkUnit)u[1]).Encode()).ToArray();
        for (var split = 0; split <= stream.Length; split++)
        {
            var decoder = new LinkDecoder();
            var units = new List<LinkUnit>();
            decoder.Feed(stream.AsSpan(0, split), units);
            decoder.Feed(stream.AsSpan(split), units);
            Assert.Equal(9, units.Count);
        }

        var small = new LinkDecoder(maxUnitBytes: 16);
        Assert.Equal(LinkDecodeError.TooLarge, small.Feed(Convert.FromHexString("414c0120000000010000001100"), []));
    }

    [Fact]
    public void Cbor_rejects_indefinite_lengths_and_duplicate_keys()
    {
        Assert.False(LinkCbor.TryDecode([0x9F, 0x01, 0xFF], out _));
        Assert.False(LinkCbor.TryDecode([0xA2, 0x00, 0x01, 0x00, 0x02], out _));
        Assert.True(LinkCbor.TryDecode([0x39, 0x01, 0xF3], out var negative));
        Assert.Equal(-500L, negative);
    }

    private static Dictionary<string, string> Vectors()
    {
        var path = RepositoryFile("conformance", "link-v1", "vectors.txt");
        return File.ReadAllLines(path)
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' '))
            .ToDictionary(p => p[0], p => p[^1]);
    }

    internal static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}

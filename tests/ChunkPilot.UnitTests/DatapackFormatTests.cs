using System.IO.Compression;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class DatapackFormatTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-datapack-formats-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("26.1", "\"min_format\":101,\"max_format\":101", 101, CompatibilityState.Compatible)]
    [InlineData("26.1", "\"min_format\":[101],\"max_format\":[101]", 101, CompatibilityState.Compatible)]
    [InlineData("26.1", "\"min_format\":[101,1],\"max_format\":[101,1]", 101, CompatibilityState.Compatible)]
    [InlineData("26.1", "\"min_format\":[101,2],\"max_format\":101", 101, CompatibilityState.Incompatible)]
    [InlineData("26.1", "\"min_format\":[101,0],\"max_format\":[101,0]", 101, CompatibilityState.LikelyCompatible)]
    [InlineData("26.2", "\"min_format\":[107,1],\"max_format\":107", 107, CompatibilityState.Compatible)]
    [InlineData("26.3", "\"min_format\":121,\"max_format\":121", 121, CompatibilityState.Compatible)]
    [InlineData("1.21.11", "\"min_format\":[94,1],\"max_format\":94", 94, CompatibilityState.Compatible)]
    [InlineData("1.21.9", "\"min_format\":88,\"max_format\":88", 88, CompatibilityState.Compatible)]
    [InlineData("1.20.2", "\"pack_format\":15,\"supported_formats\":[15,18]", 15, CompatibilityState.Compatible)]
    [InlineData("1.20.2", "\"pack_format\":15,\"supported_formats\":{\"min_inclusive\":15,\"max_inclusive\":18}", 15, CompatibilityState.Compatible)]
    [InlineData("1.21.1", "\"pack_format\":48,\"supported_formats\":48", 48, CompatibilityState.Compatible)]
    [InlineData("1.21.1", "\"pack_format\":47", 47, CompatibilityState.Incompatible)]
    [InlineData("26.1", "\"pack_format\":48,\"supported_formats\":[48,81],\"min_format\":48,\"max_format\":101", 48, CompatibilityState.Compatible)]
    [InlineData("26.99", "\"pack_format\":48", 48, CompatibilityState.Unknown)]
    [InlineData("26.1-snapshot-1", "\"min_format\":101,\"max_format\":101", 101, CompatibilityState.Unknown)]
    [InlineData("1.21.12", "\"min_format\":94,\"max_format\":94", 94, CompatibilityState.Unknown)]
    public void Declared_ranges_preserve_major_minor_and_unknown_version_evidence(
        string minecraftVersion, string fields, int major, CompatibilityState compatibility)
    {
        var result = Inspect(fields, minecraftVersion);

        Assert.True(result.Valid, result.Detail);
        Assert.Equal(major, result.PackFormat);
        Assert.Equal(compatibility, result.Compatibility);
        Assert.Contains("format range", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"min_format\":101")]
    [InlineData("\"max_format\":101")]
    [InlineData("\"min_format\":-1,\"max_format\":101")]
    [InlineData("\"min_format\":101.1,\"max_format\":102")]
    [InlineData("\"min_format\":[101,0,0],\"max_format\":102")]
    [InlineData("\"min_format\":[],\"max_format\":102")]
    [InlineData("\"min_format\":{},\"max_format\":102")]
    [InlineData("\"min_format\":102,\"max_format\":101")]
    [InlineData("\"min_format\":101,\"max_format\":2147483648")]
    [InlineData("\"min_format\":101,\"max_format\":101,\"supported_formats\":101")]
    [InlineData("\"min_format\":48,\"max_format\":101")]
    [InlineData("\"pack_format\":101")]
    [InlineData("\"pack_format\":48,\"supported_formats\":[49,50]")]
    [InlineData("\"pack_format\":48,\"supported_formats\":[50,48]")]
    [InlineData("\"pack_format\":48,\"supported_formats\":{\"min_inclusive\":48}")]
    [InlineData("\"pack_format\":\"48\"")]
    [InlineData("\"pack_format\":null")]
    public void Malformed_or_game_rejected_metadata_is_reported_without_throwing(string fields)
    {
        var result = Inspect(fields, "26.1");

        Assert.False(result.Valid);
        Assert.NotEmpty(result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Oversized_plain_or_compressed_metadata_is_rejected_without_changing_the_source(bool zip)
    {
        Directory.CreateDirectory(root);
        var metadata = Path.Combine(root, "pack.mcmeta");
        File.WriteAllText(metadata, new string(' ', DatapackService.MaximumMetadataBytes + 1));
        var source = root;
        if (zip)
        {
            source = Path.Combine(root, "pack.zip");
            using var archive = ZipFile.Open(source, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(metadata, "pack.mcmeta", CompressionLevel.SmallestSize);
        }

        var result = new DatapackService().Inspect(source, "26.1");

        Assert.False(result.Valid);
        Assert.Contains("limit", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DatapackService.MaximumMetadataBytes + 1, new FileInfo(metadata).Length);
    }

    [Theory]
    [InlineData("{\"pack\":null}")]
    [InlineData("{\"pack\":[]}")]
    [InlineData("[]")]
    public void Invalid_pack_object_does_not_escape_inspection(string json)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pack.mcmeta"), json);
        Assert.False(new DatapackService().Inspect(root, "26.1").Valid);
    }

    private DatapackInspection Inspect(string fields, string minecraftVersion)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pack.mcmeta"), "{\"pack\":{" + fields + ",\"description\":\"fixture\"}}");
        return new DatapackService().Inspect(root, minecraftVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

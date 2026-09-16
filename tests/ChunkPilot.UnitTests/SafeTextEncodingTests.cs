using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class SafeTextEncodingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-text-" + Guid.NewGuid().ToString("N"));

    public SafeTextEncodingTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    public async Task Bom_text_round_trips_without_changing_encoding_line_endings_or_recovery(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        const string original = "# café 世界\r\nmotd=Original\r\n";
        var originalBytes = encoding.GetPreamble().Concat(encoding.GetBytes(original)).ToArray();
        var path = Path.Combine(root, "server.properties");
        await File.WriteAllBytesAsync(path, originalBytes);
        var files = CreateService();

        var loaded = await files.ReadTextAsync(root, "server.properties");
        Assert.Equal(original, loaded.Content);
        Assert.Equal(encoding.WebName, loaded.EncodingName);
        Assert.True(loaded.HasBom);
        Assert.Equal("\r\n", loaded.LineEnding);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(originalBytes)), loaded.LoadedSha256);

        var updated = original.Replace("Original", "Changed", StringComparison.Ordinal);
        var receipt = await files.WriteTextAtomicIfUnchangedAsync(root, loaded with { Content = updated });
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(updated)).ToArray(), await File.ReadAllBytesAsync(path));
        Assert.NotNull(receipt.RecoveryPath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(receipt.RecoveryPath));
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xC3, 0x28 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0xD8 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0xD8, 0x00 })]
    [InlineData(new byte[] { 0xC3, 0x28 })]
    public async Task Malformed_text_is_rejected_without_replacement_or_writes(byte[] original)
    {
        var path = Path.Combine(root, "config.txt");
        await File.WriteAllBytesAsync(path, original);
        var error = await Assert.ThrowsAsync<IOException>(() => CreateService().ReadTextAsync(root, "config.txt"));
        Assert.Contains("not valid", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(Directory.Exists(Path.Combine(root, "data", "Recovery")));
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    public async Task Decoded_binary_null_is_rejected_even_after_initial_scan_window(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(new string('a', 5_000) + "\0tail")).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(root, "config.txt"), bytes);
        var error = await Assert.ThrowsAsync<IOException>(() => CreateService().ReadTextAsync(root, "config.txt"));
        Assert.Contains("binary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_text_is_rejected_before_reading_payload()
    {
        var path = Path.Combine(root, "latest.log");
        await using (var stream = File.Create(path))
            stream.SetLength(10 * 1024 * 1024 + 1);
        var error = await Assert.ThrowsAsync<IOException>(() => CreateService().ReadTextAsync(root, "latest.log"));
        Assert.Contains("10 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_can_snapshot_a_file_that_another_writer_keeps_open()
    {
        var path = Path.Combine(root, "latest.log");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("server ready\n"));
        await writer.FlushAsync();
        var loaded = await CreateService().ReadTextAsync(root, "latest.log");
        Assert.Equal("server ready\n", loaded.Content);
        Assert.False(loaded.HasBom);
    }

    private SafeFileService CreateService() => new(new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed")));

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }
}

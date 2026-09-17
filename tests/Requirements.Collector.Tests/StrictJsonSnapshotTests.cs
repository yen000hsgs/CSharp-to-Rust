using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSharpToRust.Contracts;

namespace Requirements.Collector.Tests;

public sealed class StrictJsonSnapshotTests : IDisposable
{
    private const int MaximumInputBytes = 64 * 1024 * 1024;
    private const string EmptyDocument = """{"source":{"language":"csharp","kind":"library","root":"."},"features":[]}""";
    private readonly string directory;
    private readonly string path;

    public StrictJsonSnapshotTests()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "samples", "Calculator.sln")))
            current = current.Parent;
        var root = current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        directory = Path.Combine(root, "artifacts", "collector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "snapshot.json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureParsesAndHashesIdenticalBytesIncludingBomAndWhitespace(bool bom)
    {
        var json = Encoding.UTF8.GetBytes(EmptyDocument + "\r\n ");
        byte[] bytes = bom ? [0xef, 0xbb, 0xbf, .. json] : json;
        File.WriteAllBytes(path, bytes);

        var snapshot = StrictJson.ReadSnapshot<FeatureDocument>(path);
        var memoryValue = StrictJson.Read<FeatureDocument>((ReadOnlyMemory<byte>)bytes);
        var fileValue = StrictJson.Read<FeatureDocument>(path);

        Assert.Equal(Hash(bytes), snapshot.Sha256);
        Assert.Equal(JsonSerializer.Serialize(fileValue, ArtifactJson.Options), JsonSerializer.Serialize(snapshot.Value, ArtifactJson.Options));
        Assert.Equal(JsonSerializer.Serialize(fileValue, ArtifactJson.Options), JsonSerializer.Serialize(memoryValue, ArtifactJson.Options));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("partial", "complete")]
    [InlineData("complete", "partial")]
    public void CapturedContextStatusAndHashCannotFollowLaterPathReplacements(string original, string replacement)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DocumentContext
        {
            TaskId = "synthetic", ExtractionId = "snapshot", Status = original, DocumentPath = "document.json"
        }, ArtifactJson.Options);
        File.WriteAllBytes(path, bytes);
        var captured = StrictJson.ReadSnapshot<DocumentContext>(path);
        var retained = Path.Combine(directory, "retained.json");
        File.Move(path, retained);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new DocumentContext
        {
            TaskId = "synthetic", ExtractionId = "snapshot", Status = replacement, DocumentPath = "document.json"
        }, ArtifactJson.Options));

        Assert.Equal(replacement, StrictJson.Read<DocumentContext>(path).Status);
        Assert.Equal(original, captured.Value.Status);
        Assert.Equal(Hash(bytes), captured.Sha256);
        Assert.NotEqual(Hash(File.ReadAllBytes(path)), captured.Sha256);

        File.Move(retained, path, overwrite: true);
        Assert.Equal(original, captured.Value.Status);
        Assert.Equal(Hash(File.ReadAllBytes(path)), captured.Sha256);
    }

    [Fact]
    public void CapturedDocumentDoesNotReopenAPathBeforeParsingOrHashing()
    {
        var bytes = Encoding.UTF8.GetBytes(EmptyDocument);
        File.WriteAllBytes(path, bytes);
        var snapshot = StrictJson.ReadSnapshot<FeatureDocument>(path);
        File.Delete(path);
        Assert.Empty(snapshot.Value.Features);
        Assert.Equal(Hash(bytes), snapshot.Sha256);
    }

    public static IEnumerable<object[]> MalformedDocuments()
    {
        yield return ["missing-required", """{"source":{"language":"csharp","kind":"library","root":"."}}"""];
        yield return ["duplicate", EmptyDocument.Replace("\"features\":", "\"features\":[],\"features\":", StringComparison.Ordinal)];
        yield return ["unknown", EmptyDocument.Replace("\"features\":", "\"unexpected\":true,\"features\":", StringComparison.Ordinal)];
        yield return ["wrong-case", EmptyDocument.Replace("\"features\":", "\"Features\":", StringComparison.Ordinal)];
        yield return ["wrong-type", EmptyDocument.Replace("\"features\":[]", "\"features\":42", StringComparison.Ordinal)];
        yield return ["invalid-unicode-property", EmptyDocument.Replace("\"features\":", "\"\\ud800\":true,\"features\":", StringComparison.Ordinal)];
        yield return ["invalid-unicode-string", EmptyDocument.Replace("\"root\":\".\"", "\"root\":\"\\ud800\"", StringComparison.Ordinal)];
        yield return ["null-optional-source", EmptyDocument.Replace("\"root\":\".\"", "\"name\":null,\"root\":\".\"", StringComparison.Ordinal)];
        yield return ["blank-optional-source", EmptyDocument.Replace("\"root\":\".\"", "\"name\":\" \",\"root\":\".\"", StringComparison.Ordinal)];
        yield return ["root-null", "null"];
        yield return ["root-array", "[]"];
        yield return ["invalid-json", "{"];
        yield return ["trailing-json", EmptyDocument + "{}"];
        yield return ["excessive-depth", new string('[', 70) + "null" + new string(']', 70)];
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void BufferAndSnapshotReadersRetainFileReaderStrictErrors(string scenario, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(scenario));
        var bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(path, bytes);
        var fileError = Record.Exception(() => StrictJson.Read<FeatureDocument>(path));
        var memoryError = Record.Exception(() => StrictJson.Read<FeatureDocument>((ReadOnlyMemory<byte>)bytes));
        var snapshotError = Record.Exception(() => StrictJson.ReadSnapshot<FeatureDocument>(path));

        Assert.NotNull(fileError);
        Assert.True(fileError is JsonException or InvalidDataException, fileError.ToString());
        Assert.NotNull(memoryError);
        Assert.NotNull(snapshotError);
        Assert.Equal(fileError.GetType(), memoryError.GetType());
        Assert.Equal(fileError.Message, memoryError.Message);
        Assert.Equal(fileError.GetType(), snapshotError.GetType());
        Assert.Equal(fileError.Message, snapshotError.Message);
    }

    [Fact]
    public void BufferAndSnapshotReadersRejectOversizeInputBeforeParsing()
    {
        var bytes = new byte[MaximumInputBytes + 1];
        File.WriteAllBytes(path, bytes);
        Assert.Contains("input limit", Assert.Throws<InvalidDataException>(() =>
            StrictJson.Read<FeatureDocument>((ReadOnlyMemory<byte>)bytes)).Message);
        Assert.Contains("input limit", Assert.Throws<InvalidDataException>(() =>
            StrictJson.ReadSnapshot<FeatureDocument>(path)).Message);
    }

    [Fact]
    public void Exactly64MiBIsAllowedAndEveryCapturedByteContributesToHash()
    {
        var bytes = new byte[MaximumInputBytes];
        Array.Fill(bytes, (byte)' ');
        Encoding.UTF8.GetBytes(EmptyDocument).CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        var snapshot = StrictJson.ReadSnapshot<FeatureDocument>(path);
        Assert.Equal(Hash(bytes), snapshot.Sha256);
        Assert.Empty(snapshot.Value.Features);
        Assert.Empty(StrictJson.Read<FeatureDocument>((ReadOnlyMemory<byte>)bytes).Features);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

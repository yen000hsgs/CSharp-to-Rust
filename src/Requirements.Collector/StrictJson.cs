using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CSharpToRust.Contracts;

namespace Requirements.Collector;

internal static class StrictJson
{
    private const long MaximumInputBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type.Namespace == typeof(ExtractionArtifact).Namespace)
                foreach (var property in info.Properties)
                    property.IsRequired = info.Type != typeof(DocumentSource)
                        || property.Name is not ("name" or "target_crate");
        });
        return new(ArtifactJson.Options)
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            TypeInfoResolver = resolver
        };
    }

    public static T Read<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateSize(stream.Length);
        using var document = JsonDocument.Parse(stream);
        return Deserialize<T>(document.RootElement);
    }

    public static T Read<T>(ReadOnlyMemory<byte> bytes)
    {
        ValidateSize(bytes.Length);
        // Stream parsing accepts a UTF-8 BOM; exclude it only from parsing, never from the snapshot hash.
        if (bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            bytes = bytes[3..];
        using var document = JsonDocument.Parse(bytes);
        return Deserialize<T>(document.RootElement);
    }

    public static (T Value, string Sha256) ReadSnapshot<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        ValidateSize(length);
        // This private buffer is never exposed or modified: parsing and hashing cannot observe different path versions.
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        ArtifactValidation.Require(stream.ReadByte() == -1, "Artifact grew while loading; retry with stable input files.");
        var value = Read<T>((ReadOnlyMemory<byte>)bytes);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (value, digest);
    }

    private static void ValidateSize(long length)
    {
        if (length > MaximumInputBytes)
            throw new InvalidDataException($"Artifact exceeds the {MaximumInputBytes} byte input limit.");
    }

    private static T Deserialize<T>(JsonElement root)
    {
        ValidateTokens(root, "$");
        if (typeof(T) == typeof(FeatureDocument)
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("source", out var source)
            && source.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "name", "target_crate" })
                if (source.TryGetProperty(name, out var value))
                    ArtifactValidation.Require(value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()), $"source.{name} must be a nonempty string when supplied.");
        return root.Deserialize<T>(Options)
            ?? throw new InvalidDataException("Artifact root must be a non-null object.");
    }

    private static void ValidateTokens(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                string name;
                try
                {
                    name = property.Name;
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidDataException($"Invalid Unicode in JSON property name at {path}.", exception);
                }
                if (!names.Add(name))
                    throw new InvalidDataException($"Duplicate JSON property at {path}.{name}.");
                ValidateTokens(property.Value, $"{path}.{name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                ValidateTokens(item, $"{path}[{index++}]");
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            try
            {
                _ = element.GetString();
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException($"Invalid Unicode in JSON string at {path}.", exception);
            }
        }
    }
}

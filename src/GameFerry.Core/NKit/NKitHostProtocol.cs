using System.Text.Json;

namespace GameFerry.Core.NKit;

/// <summary>What NKit found in a source file.</summary>
public sealed record NKitImageInfo
{
    public required string Path { get; init; }
    public required bool Ok { get; init; }
    public string? Message { get; init; }
    public string? Entry { get; init; }
    public string? Id { get; init; }
    public string? Title { get; init; }
    public bool IsWii { get; init; }
    public bool IsGameCube { get; init; }
    public bool IsNkit { get; init; }
    public bool IsWbfs { get; init; }
    public bool IsGcz { get; init; }
    public bool UpdateRemoved { get; init; }
    public long SourceSize { get; init; }
    public long ImageSize { get; init; }
}

/// <summary>Outcome of an NKit conversion.</summary>
public sealed record NKitResult
{
    public required bool Ok { get; init; }
    public string? Output { get; init; }
    public string? Id { get; init; }
    public string? Title { get; init; }
    public long OutputSize { get; init; }
    public string? Crc { get; init; }

    /// <summary>Whether the rebuilt image matched the checksum recorded in the NKit header.</summary>
    public string? ReadResult { get; init; }
    public string? OutputResult { get; init; }

    /// <summary><c>Redump</c>, <c>Custom</c> or <c>MatchFail</c>.</summary>
    public string? RedumpMatch { get; init; }
    public string? RedumpName { get; init; }
    public bool Recoverable { get; init; }
    public double Seconds { get; init; }
    public string? Message { get; init; }

    public bool MatchesRedump => RedumpMatch == "Redump";
}

/// <summary>Progress of the current NKit pass.</summary>
/// <param name="Total">Fraction of the whole conversion (all passes) completed.</param>
public readonly record struct NKitProgress(string? Pass, double Value, double Total);

/// <summary>Parses the JSON lines the host process writes.</summary>
internal static class NKitHostProtocol
{
    public static string? Type(JsonElement message) => String(message, "t");

    public static NKitImageInfo ToImageInfo(JsonElement message) => new()
    {
        Path = String(message, "path") ?? string.Empty,
        Ok = Bool(message, "ok"),
        Message = String(message, "message"),
        Entry = String(message, "entry"),
        Id = String(message, "id"),
        Title = String(message, "title"),
        IsWii = Bool(message, "isWii"),
        IsGameCube = Bool(message, "isGameCube"),
        IsNkit = Bool(message, "isNkit"),
        IsWbfs = Bool(message, "isWbfs"),
        IsGcz = Bool(message, "isGcz"),
        UpdateRemoved = Bool(message, "updateRemoved"),
        SourceSize = Long(message, "sourceSize"),
        ImageSize = Long(message, "imageSize"),
    };

    public static NKitResult ToResult(JsonElement message) => new()
    {
        Ok = Bool(message, "ok"),
        Output = String(message, "output"),
        Id = String(message, "id"),
        Title = String(message, "title"),
        OutputSize = Long(message, "outputSize"),
        Crc = String(message, "crc"),
        ReadResult = String(message, "readResult"),
        OutputResult = String(message, "outputResult"),
        RedumpMatch = String(message, "redumpMatch"),
        RedumpName = String(message, "redumpName"),
        Recoverable = Bool(message, "recoverable"),
        Seconds = Double(message, "seconds"),
        Message = String(message, "message"),
    };

    public static NKitProgress ToProgress(JsonElement message) =>
        new(String(message, "pass"), Double(message, "value"), Double(message, "total"));

    public static string? String(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long number) ? number : 0;

    private static double Double(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double number) ? number : 0;
}

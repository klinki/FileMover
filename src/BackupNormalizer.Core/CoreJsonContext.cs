using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupNormalizer;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(PlanDoc))]
[JsonSerializable(typeof(LocationChangesJson))]
[JsonSerializable(typeof(GroupedFileReportsJson))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(LogEntry))]
[JsonSerializable(typeof(ExecutionLogFields))]
internal partial class CoreJsonContext : JsonSerializerContext
{
    internal static CoreJsonContext Compact { get; } =
        new(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}

internal sealed record LocationChangesJson(
    string Direction,
    LocationChangeSource A,
    LocationChangeSource B,
    string CreatedUtc,
    string Filter,
    IReadOnlyDictionary<string, int> Summary,
    int UnverifiedFiles,
    bool FilenameMatchingEnabled,
    IReadOnlyList<string> FilenameExtensions,
    IReadOnlyList<string> ExcludedPaths,
    IReadOnlyList<LocationChangeGroup> Groups
);

internal sealed record GroupedFileReportsJson(
    string Direction,
    LocationChangeSource A,
    LocationChangeSource B,
    string CreatedUtc,
    string Filter,
    bool FilenameMatchingEnabled,
    IReadOnlyList<string> FilenameExtensions,
    IReadOnlyList<string> ExcludedPaths,
    IReadOnlyDictionary<string, int> Summary,
    int UnverifiedFiles,
    IReadOnlyList<FilenameDifferenceGroup> FilenameGroups,
    IReadOnlyList<DuplicateContentGroup> DuplicateGroups,
    IReadOnlyList<UnverifiedFileLocation> UnverifiedLocations
);

internal sealed record LogEntry(string Ts, string Level, string Msg, JsonElement? Fields);

internal sealed record ExecutionLogFields(long OpId, string Level);

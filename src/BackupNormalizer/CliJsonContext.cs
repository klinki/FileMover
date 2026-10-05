using System.Text.Json.Serialization;

namespace BackupNormalizer;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(BuildInfo))]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<InventoryStatusRow>))]
[JsonSerializable(typeof(CoverageReport))]
[JsonSerializable(typeof(ScanErrorsJson))]
[JsonSerializable(typeof(DatabaseExportJson))]
internal partial class CliJsonContext : JsonSerializerContext;

internal sealed record ScanErrorsJson(ScanDetailsRow Scan, IReadOnlyList<ScanDiagnosticRow> Errors);

internal sealed record DatabaseExportJson(string Source, string Destination);

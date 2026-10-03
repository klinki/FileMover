using System.Reflection;
using System.Runtime.InteropServices;

namespace BackupNormalizer;

public sealed record BuildInfo(
    string Product,
    string Version,
    string? Commit,
    string Configuration,
    string RuntimeIdentifier,
    string SourceState
)
{
    public string ShortVersion =>
        Version
        + (Commit == null ? "+unknown" : "+" + Commit[..Math.Min(12, Commit.Length)])
        + (
            SourceState == "dirty" ? ".dirty"
            : SourceState == "unknown" ? ".unverified"
            : ""
        );

    public static BuildInfo FromAssembly(Assembly assembly)
    {
        string information =
            assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        int separator = information.IndexOf('+');
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value);
        metadata.TryGetValue("BuildRuntimeIdentifier", out string? runtime);
        metadata.TryGetValue("BuildSourceState", out string? state);
        return new BuildInfo(
            assembly.GetName().Name ?? "BackupNormalizer",
            separator < 0 ? information : information[..separator],
            separator < 0 ? null : information[(separator + 1)..],
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
                ?? "unknown",
            string.IsNullOrWhiteSpace(runtime) ? RuntimeInformation.RuntimeIdentifier : runtime,
            state is "clean" or "dirty" ? state : "unknown"
        );
    }

    public override string ToString() =>
        $"{Product} {ShortVersion} ({Configuration}; {RuntimeIdentifier}; source {SourceState})";
}

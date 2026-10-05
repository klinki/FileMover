using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BackupNormalizer;

public static class Log
{
    public static bool Json { get; set; } = false;

    public static void Info(string msg, JsonElement? fields = null) => Write("INFO", msg, fields);

    public static void Info<T>(string msg, T fields, JsonTypeInfo<T> typeInfo) =>
        Write("INFO", msg, JsonSerializer.SerializeToElement(fields, typeInfo));

    public static void Warn(string msg, JsonElement? fields = null) => Write("WARN", msg, fields);

    public static void Error(string msg, JsonElement? fields = null) => Write("ERROR", msg, fields);

    private static void Write(string level, string msg, JsonElement? fields)
    {
        var ts = DateTime.UtcNow.ToString("o");
        if (Json)
        {
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new LogEntry(ts, level, msg, fields),
                    CoreJsonContext.Compact.LogEntry
                )
            );
        }
        else if (fields == null)
        {
            Console.WriteLine($"[{ts}] {level} {msg}");
        }
        else
        {
            Console.WriteLine($"[{ts}] {level} {msg} {fields.Value.GetRawText()}");
        }
    }
}

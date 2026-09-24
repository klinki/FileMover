namespace BackupNormalizer;

public static class Log
{
    public static bool Json { get; set; } = false;
    public static void Info(string msg, object? fields = null) => Write("INFO", msg, fields);
    public static void Warn(string msg, object? fields = null) => Write("WARN", msg, fields);
    public static void Error(string msg, object? fields = null) => Write("ERROR", msg, fields);

    private static void Write(string level, string msg, object? fields)
    {
        var ts = DateTime.UtcNow.ToString("o");
        if (Json)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ts, level, msg, fields }));
        else if (fields == null)
            Console.WriteLine($"[{ts}] {level} {msg}");
        else
            Console.WriteLine($"[{ts}] {level} {msg} {System.Text.Json.JsonSerializer.Serialize(fields)}");
    }
}

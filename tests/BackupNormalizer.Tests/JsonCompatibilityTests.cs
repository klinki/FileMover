using System.Text.Json;
using BackupNormalizer;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class JsonCompatibilityTests
{
    [Fact]
    public void Tests_Run_With_Reflection_Serialization_Disabled() =>
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);

    [Fact]
    public void Plan_Import_Accepts_Existing_Casing_And_Extra_Fields_And_Export_Keeps_Contract()
    {
        string path = Path.Combine(Path.GetTempPath(), "bn-plan-json-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "PlanId": "vienna",
                  "CreatedUtc": "2026-10-05T00:00:00Z",
                  "EstimatedBytesCopied": 0,
                  "SourceDatabasePath": null,
                  "SourceRoot": "disk",
                  "SourcePath": "D:\\photos",
                  "TargetRoot": "disk",
                  "TargetPath": "D:\\photos",
                  "extra": true,
                  "Operations": [{
                    "Id": 1,
                    "Type": "MOVE",
                    "SourceKind": "Target",
                    "SourceRoot": "disk",
                    "SourcePath": "old/žluťoučký.jpg",
                    "DestinationRoot": "disk",
                    "DestinationPath": "new/žluťoučký.jpg",
                    "ExpectedSize": 12,
                    "ExpectedHash": null,
                    "extra": "ignored"
                  }]
                }
                """
            );
            var plan = PlanStaging.ImportJson(path);
            var operation = Assert.Single(plan.Operations);
            Assert.Equal("old/žluťoučký.jpg", operation.SourcePath);
            Assert.Null(operation.SkipReason);
            using var exported = JsonDocument.Parse(Planner.ToJson(plan));
            var root = exported.RootElement;
            Assert.Equal(
                new[]
                {
                    "planId",
                    "createdUtc",
                    "estimatedBytesCopied",
                    "sourceDatabasePath",
                    "sourceRoot",
                    "sourcePath",
                    "targetRoot",
                    "targetPath",
                    "operations",
                },
                root.EnumerateObject().Select(p => p.Name)
            );
            Assert.Equal(JsonValueKind.Null, root.GetProperty("sourceDatabasePath").ValueKind);
            Assert.Equal("D:\\photos", root.GetProperty("targetPath").GetString());
            Assert.Equal(
                JsonValueKind.Null,
                root.GetProperty("operations")[0].GetProperty("skipReason").ValueKind
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structured_Log_Preserves_Field_Names_And_Compact_Output(bool json)
    {
        var originalOut = Console.Out;
        bool originalJson = Log.Json;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Log.Json = json;
            Log.Info(
                "MOVE žluťoučký.jpg",
                new ExecutionLogFields(17, "INFO"),
                CoreJsonContext.Compact.ExecutionLogFields
            );
            string text = output.ToString();
            if (json)
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                Assert.Equal(
                    new[] { "ts", "level", "msg", "fields" },
                    root.EnumerateObject().Select(p => p.Name)
                );
                Assert.Equal("MOVE žluťoučký.jpg", root.GetProperty("msg").GetString());
                Assert.Equal("INFO", root.GetProperty("level").GetString());
                Assert.Equal(17, root.GetProperty("fields").GetProperty("opId").GetInt64());
                Assert.Equal("INFO", root.GetProperty("fields").GetProperty("level").GetString());
                Assert.Single(
                    text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                );
            }
            else
            {
                Assert.Contains("INFO MOVE žluťoučký.jpg ", text);
                Assert.EndsWith("{\"opId\":17,\"level\":\"INFO\"}" + Environment.NewLine, text);
            }
            output.GetStringBuilder().Clear();
            Log.Json = true;
            Log.Warn("No fields");
            using var warning = JsonDocument.Parse(output.ToString());
            Assert.Equal(JsonValueKind.Null, warning.RootElement.GetProperty("fields").ValueKind);
        }
        finally
        {
            Console.SetOut(originalOut);
            Log.Json = originalJson;
        }
    }
}

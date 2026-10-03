using BackupNormalizer;
using BackupNormalizer.Ui.Views;
using System.Text.Json;

namespace BackupNormalizer.Tests;

[Collection("Console")]
public sealed class BuildInfoTests
{
    [Fact]
    public void Application_Components_Share_Version_And_Read_Embedded_Build_Metadata()
    {
        var builds = new[] { typeof(Cli).Assembly, typeof(Scanner).Assembly, typeof(MainWindow).Assembly }
            .Select(BuildInfo.FromAssembly).ToList();
        Assert.All(builds, build =>
        {
            Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+", build.Version);
            Assert.False(string.IsNullOrWhiteSpace(build.Configuration));
            Assert.False(string.IsNullOrWhiteSpace(build.RuntimeIdentifier));
            if (build.Commit != null) Assert.Matches("^[0-9a-f]{40}$", build.Commit);
        });
        Assert.Single(builds.Select(b => b.Commit).Distinct());
        Assert.Single(builds.Select(b => b.Version).Distinct());
        Assert.Contains(".dirty", new BuildInfo("app", "1.2.3", "abcdef", "Release", "win-x64", "dirty").ShortVersion);
        Assert.Contains(".unverified", new BuildInfo("app", "1.2.3", null, "Release", "win-x64", "unknown").ShortVersion);
    }

    [Fact]
    public void Cli_Version_Provides_Json_Without_Opening_A_Database()
    {
        var original = Console.Out;
        bool originalJson = Log.Json;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, Cli.Run(["--version", "--json"]));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(BuildInfo.FromAssembly(typeof(Cli).Assembly).Version, json.RootElement.GetProperty("version").GetString());
            Assert.Equal("BackupNormalizer", json.RootElement.GetProperty("product").GetString());
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Cli.Run(["--version"]));
            Assert.Contains(BuildInfo.FromAssembly(typeof(Cli).Assembly).ToString(), output.ToString());
        }
        finally { Console.SetOut(original); Log.Json = originalJson; }
    }

    [Fact]
    public void Ui_Version_Does_Not_Initialize_Avalonia_Or_Open_A_Window()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var program = typeof(MainWindow).Assembly.GetType("BackupNormalizer.Ui.Program")!;
            program.GetMethod("Main")!.Invoke(null, [new[] { "--version" }]);
            Assert.Contains(BuildInfo.FromAssembly(typeof(MainWindow).Assembly).ToString(), output.ToString());
        }
        finally { Console.SetOut(original); }
    }
}

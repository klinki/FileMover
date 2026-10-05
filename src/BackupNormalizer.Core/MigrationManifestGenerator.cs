#if !NATIVE_AOT
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupNormalizer;

/// <summary>Build-machine SQL generation; never included in the NAS executable.</summary>
public static class MigrationManifestGenerator
{
    public static MigrationManifest Create()
    {
        using var context = new BackupNormalizerDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var steps = new List<MigrationStep>();
        foreach (var pair in assembly.Migrations.OrderBy(pair => pair.Key))
        {
            var migration = assembly.CreateMigration(pair.Value, context.Database.ProviderName!);
            var model = context
                .GetService<IModelRuntimeInitializer>()
                .Initialize(migration.TargetModel, designTime: true);
            var commands = generator.Generate(migration.UpOperations, model);
            if (commands.Any(command => command.TransactionSuppressed))
                throw new InvalidOperationException(
                    $"Migration {pair.Key} has transaction-suppressed commands and requires explicit upgrade support."
                );
            steps.Add(
                new MigrationStep(
                    pair.Key,
                    typeof(DbContext).Assembly.GetName().Version!.ToString(3),
                    commands.Select(command => command.CommandText).ToArray()
                )
            );
        }
        return new MigrationManifest(1, steps.ToArray());
    }
}
#endif

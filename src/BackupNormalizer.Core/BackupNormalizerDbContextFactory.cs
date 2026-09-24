using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BackupNormalizer;

public sealed class BackupNormalizerDbContextFactory : IDesignTimeDbContextFactory<BackupNormalizerDbContext>
{
    public BackupNormalizerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BACKUP_NORMALIZER_DB")
            ?? "Data Source=backup-normalizer.db";
        var options = new DbContextOptionsBuilder<BackupNormalizerDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new BackupNormalizerDbContext(options);
    }
}

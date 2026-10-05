using BackupNormalizer;

try
{
    if (args is not ["--db", var path, "--manifest-hash", var hash])
        throw new ArgumentException(
            "Usage: BackupNormalizer.Migrations --db PATH --manifest-hash HASH"
        );
    string? backup = DatabaseSchema.Upgrade(path, hash);
    Console.WriteLine(
        backup == null ? "Database schema is current." : $"Database upgraded. Backup: {backup}"
    );
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

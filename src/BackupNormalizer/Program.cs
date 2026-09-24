using BackupNormalizer;

try
{
    SQLitePCL.Batteries.Init();
}
catch { }
return Cli.Run(args);

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BackupNormalizer.Ui.Models;

public sealed record PanelSession(string? DatabasePath, string? RootId, string Folder);
public sealed record ColumnSession(string Key, double Width, bool IsStar);
public sealed record GuiSession(PanelSession Left, PanelSession Right, bool IsLeftActive,
    double LeftFraction = 0.5, List<ColumnSession>? LeftColumns = null, List<ColumnSession>? RightColumns = null);

/// <summary>Local browsing preferences. Plans and filesystem operations are never restored.</summary>
public sealed class GuiSessionStore
{
    public string Path { get; }

    public GuiSessionStore(string? path = null) => Path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupNormalizer", "ui-session.json");

    public GuiSession? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            var session = JsonSerializer.Deserialize<GuiSession>(File.ReadAllText(Path));
            if (session?.Left == null || session.Right == null) return null;
            return session with
            {
                Left = session.Left with { Folder = session.Left.Folder ?? "" },
                Right = session.Right with { Folder = session.Right.Folder ?? "" },
                LeftColumns = ValidColumns(session.LeftColumns),
                RightColumns = ValidColumns(session.RightColumns),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(GuiSession session)
    {
        string full = System.IO.Path.GetFullPath(Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static List<ColumnSession>? ValidColumns(List<ColumnSession>? columns) => columns?
        .Where(column => column != null && !string.IsNullOrWhiteSpace(column.Key) &&
            double.IsFinite(column.Width) && column.Width > 0)
        .ToList();
}

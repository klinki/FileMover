using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupNormalizer.Ui.Models;

[JsonSerializable(typeof(GuiSession))]
[JsonSerializable(typeof(InventoryDrag))]
internal partial class GuiJsonContext : JsonSerializerContext
{
    internal static GuiJsonContext Indented { get; } =
        new(new JsonSerializerOptions { WriteIndented = true });
}

internal sealed record InventoryDrag(
    string Side,
    string DatabasePath,
    string RootId,
    string[] Paths
);

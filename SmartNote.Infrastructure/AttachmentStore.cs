namespace SmartNote.Infrastructure;

public sealed record Attachment(string Id, string Src);
public sealed class AttachmentStore(string directory)
{
    public Task ValidateReferencesAsync(System.Text.Json.Nodes.JsonObject document) => Task.Run(() =>
    {
        SmartNote.Core.Documents.Validate(document);
        foreach (var node in SmartNote.Core.Documents.Nodes(document).Where(n => n["type"]?.GetValue<string>() == "image"))
            if (!File.Exists(Path.Combine(directory, node["attrs"]!["attachmentId"]!.GetValue<string>() + ".img")))
                throw new InvalidDataException("添付画像が見つからないため保存を中止しました。元ファイルを復旧してください。");
    });
    public async Task<Attachment> ImportAsync(byte[] bytes)
    {
        if (bytes.Length is < 4 or > 10_000_000)
            throw new InvalidDataException("画像サイズは10MB以下にしてください。");
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpg = bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        if (!png && !jpg)
            throw new InvalidDataException("PNG/JPGのみ対応しています。");
        var id = Guid.NewGuid().ToString();
        var path = Path.Combine(directory, id + ".img");
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch { await Task.Run(() => { if (File.Exists(path)) File.Delete(path); }); throw; }
        return new(id, $"https://media.smartnote.local/{id}.img");
    }
}

using System.Text.Json.Nodes;
namespace SmartNote.Core;

public sealed record Note(string Id, string Title, JsonObject Document, long Revision, string CreatedAt, string UpdatedAt, bool IsQuick = false);
public sealed record NoteTask(string Id, string NoteId, string BlockId, string Name, bool Completed, string CreatedAt, string UpdatedAt, long Revision, string? DueAt = null, string? Recurrence = null, int Priority = 0);
public sealed record Checklist(string BlockId, string Name, bool Completed);
public sealed record Preferences(bool Topmost = false, bool QuickTopmost = true, bool AutoStart = false, string Theme = "system", string QuickShortcut = "Ctrl+Alt+M", bool ShowQuickMemo = true);
public sealed class RevisionConflictException() : Exception("保存先が更新されています。未保存内容を保持しています。再読み込み前に内容を退避してください。");
public static class Documents
{
    public static JsonObject Empty() => new() { ["type"] = "doc", ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph" }) };
    public static IEnumerable<JsonObject> Nodes(JsonObject root)
    {
        yield return root;
        if (root["content"] is JsonArray content)
            foreach (var child in content.OfType<JsonObject>())
                foreach (var node in Nodes(child))
                    yield return node;
    }
    public static string Text(JsonObject root) => string.Concat(Nodes(root).Where(n => n["type"]?.GetValue<string>() == "text").Select(n => n["text"]?.GetValue<string>() ?? ""));
    public static void Validate(JsonObject document)
    {
        if (document["type"]?.GetValue<string>() == "textDocument")
        {
            if (document["text"] is not JsonValue text || !text.TryGetValue<string>(out _) || document.ToJsonString().Length > 4_000_000 ||
                document["attrs"]?["language"]?.GetValue<string>() is not ("plaintext" or "markdown" or "powershell" or "json" or "xml") || document.ContainsKey("content"))
                throw new InvalidDataException("テキストメモ形式が不正です。");
            return;
        }
        if (document["type"]?.GetValue<string>() != "doc" || document.ToJsonString().Length > 4_000_000)
            throw new InvalidDataException("ノート形式またはサイズが不正です。");
        var ids = new HashSet<string>();
        foreach (var node in Nodes(document))
        {
            var type = node["type"]?.GetValue<string>() ?? throw new InvalidDataException("ノード種別がありません。");
            if (type == "text" && string.IsNullOrEmpty(node["text"]?.GetValue<string>()))
                throw new InvalidDataException("空のテキスト要素は保存できません。");
            if (type == "taskItem")
            {
                var id = node["attrs"]?["blockId"]?.GetValue<string>();
                if (!Guid.TryParse(id, out _) || !ids.Add(id!))
                    throw new InvalidDataException("チェック項目のBlockIdが不正または重複しています。");
                _ = node["attrs"]?["checked"]?.GetValue<bool>() ?? throw new InvalidDataException("チェック状態がありません。");
            }
            if (type == "image")
            {
                var id = node["attrs"]?["attachmentId"]?.GetValue<string>();
                var src = node["attrs"]?["src"]?.GetValue<string>();
                if (!Guid.TryParse(id, out _) || src is null || !Uri.TryCreate(src, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "media.smartnote.local" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" + id + ".img" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new InvalidDataException("画像参照が不正です。");
            }
        }
    }
    public static IReadOnlyList<Checklist> Checklist(JsonObject document)
    {
        Validate(document);
        return Nodes(document).Where(n => n["type"]?.GetValue<string>() == "taskItem").Select(n => new Checklist(n["attrs"]!["blockId"]!.GetValue<string>(), Text(n), n["attrs"]!["checked"]!.GetValue<bool>())).ToList();
    }
    public static void SetChecked(JsonObject document, string blockId, bool value)
    {
        var node = Nodes(document).SingleOrDefault(n => n["type"]?.GetValue<string>() == "taskItem" && n["attrs"]?["blockId"]?.GetValue<string>() == blockId) ?? throw new InvalidDataException("関連するチェック項目がありません。");
        node["attrs"]!["checked"] = value;
    }
}

namespace AIHub.Core;

// A provider turn may contain several outward assistant messages around tool calls.
// Preserve them in order, coalesce updates by native ID, and remove exact duplicate finals.
internal sealed class CompletedReplyBuffer
{
    private readonly Dictionary<string, string> messages = [];
    private int characters;
    public void Clear() { messages.Clear(); characters = 0; }
    public void Add(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (id.Length == 0) id = Guid.NewGuid().ToString("N");
        var size = characters - (messages.GetValueOrDefault(id)?.Length ?? 0) + text.Length;
        if (size > BoundedText.MaxFrameCharacters) throw new IOException("Completed provider reply exceeded AI Hub's reply limit.");
        messages[id] = text; characters = size;
    }
    public string Complete(string result)
    {
        var parts = messages.Values.Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (!string.IsNullOrWhiteSpace(result) && !parts.Contains(result.Trim(), StringComparer.Ordinal)) parts.Add(result.Trim());
        var text = string.Join("\n\n", parts);
        if (text.Length > BoundedText.MaxFrameCharacters) throw new IOException("Completed provider reply exceeded AI Hub's reply limit.");
        return text;
    }
}

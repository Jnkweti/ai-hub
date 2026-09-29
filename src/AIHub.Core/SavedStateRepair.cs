using System.Text.Json;

namespace AIHub.Core;

// LocalStore backs up the untouched original whenever validation repairs anything.
public static class SavedStateRepair
{
    public static bool Settings(HubSettings value)
    {
        var before = JsonSerializer.Serialize(value);
        value.Workspace = ValidPath(value.Workspace, AppContext.BaseDirectory);
        value.CodexPath ??= ""; value.ClaudePath ??= "";
        value.CodexModel ??= ""; value.ClaudeModel ??= ""; value.LastRoomId ??= "";
        value.MaxAutoRounds = Math.Clamp(value.MaxAutoRounds, 1, 50);
        if (!Enum.IsDefined(value.StatusInspector)) value.StatusInspector = Agent.Codex;
        return before != JsonSerializer.Serialize(value);
    }
    public static bool Rooms(List<Room> rooms)
    {
        var before = JsonSerializer.Serialize(rooms);
        rooms.RemoveAll(r => r is null);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var room in rooms)
        {
            if (!SafeId(room.Id) || !ids.Add(room.Id))
            { room.Id = Guid.NewGuid().ToString("N"); ids.Add(room.Id); }
            room.Title ??= "Recovered conversation";
            // Keep missing folders so users can reconnect them explicitly.
            room.Workspace = ValidPath(room.Workspace, "");
            room.SessionOptions ??= ""; room.Draft ??= ""; room.PauseReason ??= ""; room.LastTask ??= "";
            room.ActiveTaskId ??= "";
            if (room.Target is not ("Both" or "Codex" or "Claude")) room.Target = "Both";
            room.CodexContext = Cursor(room.CodexContext); room.ClaudeContext = Cursor(room.ClaudeContext);
            room.Messages ??= [];
            Messages(room.Messages);
        }
        return before != JsonSerializer.Serialize(rooms);
    }
    /// <summary>Repairs one room's transcript (its own file since 0.23.0, or inline in a legacy rooms.json).</summary>
    public static bool Messages(List<SavedMessage> messages)
    {
        var before = JsonSerializer.Serialize(messages);
        messages.RemoveAll(m => m is null);
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (!SafeId(message.Id) || !messageIds.Add(message.Id))
            { message.Id = Guid.NewGuid().ToString("N"); messageIds.Add(message.Id); }
            message.Speaker ??= "Unknown"; message.Text ??= ""; message.Route ??= "Shared room";
            message.TaskId ??= "";
            if (message.Input is not { } input) continue;
            input.Title ??= "Question";
            input.Options = (input.Options ?? []).Where(o => o is not null)
                .Select(o => new QuestionChoice(o.Label ?? "", o.Description ?? "")).ToArray();
            if (!Enum.IsDefined(input.Status)) input.Status = InputStatus.Cancelled;
        }
        return before != JsonSerializer.Serialize(messages);
    }
    private static bool SafeId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static ConversationCursor Cursor(ConversationCursor? cursor) => new()
    {
        SessionId = cursor?.SessionId,
        TaskId = cursor?.TaskId is { Length: > 0 and <= 128 } task ? task : null,
        StreamSequence = cursor?.StreamSequence is { } sequence && sequence >= 0 ? sequence : null,
        SessionInputBytes = Math.Max(0, cursor?.SessionInputBytes ?? 0),
        MessageIds = (cursor?.MessageIds ?? []).Where(SafeId).Distinct().TakeLast(ConversationTurns.ContextMessageLimit).ToArray(),
        MessageHashes = (cursor?.MessageHashes ?? []).Where(p => SafeId(p.Key) &&
            (cursor?.MessageIds ?? []).Contains(p.Key) && p.Value is { Length: 64 } && p.Value.All(Uri.IsHexDigit))
            .TakeLast(ConversationTurns.ContextMessageLimit).ToDictionary(p => p.Key, p => p.Value)
    };
    private static string ValidPath(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return fallback; }
    }
}

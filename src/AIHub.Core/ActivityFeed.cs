using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHub.Core;

public sealed class ActivityEntry : INotifyPropertyChanged
{
    public const int DetailLimit = 64000;
    public string Agent { get; }
    public string Title { get; private set; }
    public string State { get; private set; }
    public string Time { get; } = DateTime.Now.ToString("HH:mm:ss");
    public bool IsRunning { get; private set; }
    private string metadata;
    private readonly StringBuilder output = new();
    private bool clipped;
    public string Detail => (State.Length > 0 ? State + "\n\n" : "") + metadata +
        (output.Length > 0 ? "\n\nOutput:\n" + output : "") +
        (clipped ? "\n\n[Display shortened. Open the full log for all recorded output.]" : "");
    public event PropertyChangedEventHandler? PropertyChanged;
    public ActivityEntry(string agent, string title, string detail, string state = "", bool running = false)
    { Agent = agent; Title = title; State = state; IsRunning = running; metadata = Limit(detail); }
    private string Limit(string text)
    {
        if (text.Length <= DetailLimit) return text;
        clipped = true; return text[^DetailLimit..];
    }
    public void Update(string? title = null, string? detail = null, string? state = null, bool? running = null)
    {
        if (title is not null && title != Title) { Title = title; Changed(nameof(Title)); }
        if (state is not null && state != State) { State = state; Changed(nameof(State)); }
        if (running is not null) IsRunning = running.Value;
        if (detail is not null) metadata = Limit(detail);
        Changed(nameof(Detail));
    }
    public void AppendOutput(string text)
    {
        if (text.Length >= DetailLimit) { output.Clear(); output.Append(text[^DetailLimit..]); clipped = true; }
        else
        {
            var excess = output.Length + text.Length - DetailLimit;
            if (excess > 0) { output.Remove(0, excess); clipped = true; }
            output.Append(text);
        }
        Changed(nameof(Detail));
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

// UI projection only: callers continue persisting the original events to the full log.
// Tool IDs are scoped to an agent's dispatch, never just a display title or command.
public sealed class ActivityFeed
{
    public const int Capacity = 200;
    public ObservableCollection<ActivityEntry> Actions { get; } = [];
    public ObservableCollection<ActivityEntry> Diagnostics { get; } = [];
    private readonly Dictionary<(Agent, string), ActivityEntry> tools = [];
    private readonly Dictionary<Agent, ActivityEntry> plans = [];

    public void BeginTurn(Agent agent)
    {
        FinishOpen(agent, "Stopped");
        foreach (var key in tools.Keys.Where(k => k.Item1 == agent).ToArray()) tools.Remove(key);
        plans.Remove(agent);
    }
    public void Clear() { tools.Clear(); plans.Clear(); Actions.Clear(); Diagnostics.Clear(); }
    public void FinishOpen(Agent? agent, string state)
    {
        foreach (var entry in tools.Where(p => agent is null || p.Key.Item1 == agent).Select(p => p.Value).Distinct())
            if (entry.IsRunning) entry.Update(state: state, running: false);
    }
    public void AddNotice(string agent, string title, string detail)
    {
        AddAction(new(agent, Short(title), detail));
        AddDiagnostic(new(agent, Short(title), detail));
    }
    public ActivityEntry? Record(AgentEvent item)
    {
        if (item.Kind is EventKind.Message or EventKind.TextDelta or EventKind.Session) return null;
        AddDiagnostic(new(item.Agent.ToString(), string.IsNullOrWhiteSpace(item.Text) ? "Whitespace output" : Short(item.Text),
            item.Detail.Length > 0 ? item.Detail + (item.Kind == EventKind.ToolOutput ? "\n\n" + item.Text : "") : item.Text));
        if (item.Kind == EventKind.Usage) return null;
        if (item.Kind == EventKind.Status)
        {
            if (item.Text == "Ready") FinishOpen(item.Agent, "Finished");
            // Connection/heartbeat/token counters belong in diagnostics. Explicit warnings remain visible.
            if (!Regex.IsMatch(item.Text, @"^(?:\d{4}-\d{2}-\d{2}\S*\s+)?(?:WARN(?:ING)?|ERROR|FATAL)\b", RegexOptions.IgnoreCase)) return null;
            return AddAction(new(item.Agent.ToString(), Short(item.Text), item.Detail.Length > 0 ? item.Detail : item.Text, "Needs attention"));
        }
        if (item.Kind == EventKind.Error)
            return AddAction(new(item.Agent.ToString(), Short(item.Text), item.Detail.Length > 0 ? item.Detail : item.Text, "Failed"));

        var data = Parse(item.Detail);
        if (item.Kind == EventKind.Tool && item.Text == "Plan updated")
        {
            if (!plans.TryGetValue(item.Agent, out var plan))
            { plan = AddAction(new(item.Agent.ToString(), "Plan updated", item.Detail)); plans[item.Agent] = plan; }
            else plan.Update(detail: item.Detail);
            return plan;
        }
        var key = (item.Agent, item.ItemId);
        if (item.Kind == EventKind.ToolOutput)
        {
            var failed = Boolean(data, "isError");
            // Empty chunks are retained only inside an existing action, never as standalone rows.
            if (!tools.TryGetValue(key, out var entry))
            {
                if (string.IsNullOrWhiteSpace(item.Text) && !failed) return null;
                entry = AddAction(new(item.Agent.ToString(), "Tool output", "Output arrived without an action header.", "Output received", true));
                tools[key] = entry;
            }
            entry.AppendOutput(item.Text);
            if (failed) entry.Update(state: "Failed", running: false);
            else if (Boolean(data, "isFinal")) entry.Update(state: "Completed", running: false);
            return entry;
        }
        if (item.Kind != EventKind.Tool) return null;
        var title = Describe(item, data);
        var state = ToolState(item, data);
        var running = state == "Running";
        if (item.ItemId.Length == 0)
            return AddAction(new(item.Agent.ToString(), title, item.Detail.Length > 0 ? item.Detail : item.Text, state, false));
        if (!tools.TryGetValue(key, out var action))
        {
            action = AddAction(new(item.Agent.ToString(), title, item.Detail, state, running)); tools[key] = action;
        }
        else action.Update(title, item.Detail, state, running);
        return action;
    }
    private ActivityEntry AddAction(ActivityEntry entry)
    {
        Actions.Insert(0, entry);
        while (Actions.Count > Capacity)
        {
            var removed = Actions[^1]; Actions.RemoveAt(Actions.Count - 1);
            foreach (var key in tools.Where(p => ReferenceEquals(p.Value, removed)).Select(p => p.Key).ToArray()) tools.Remove(key);
            foreach (var key in plans.Where(p => ReferenceEquals(p.Value, removed)).Select(p => p.Key).ToArray()) plans.Remove(key);
        }
        return entry;
    }
    private void AddDiagnostic(ActivityEntry entry)
    { Diagnostics.Insert(0, entry); if (Diagnostics.Count > Capacity) Diagnostics.RemoveAt(Diagnostics.Count - 1); }
    private static JsonObject? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
    }
    private static string Value(JsonObject? data, string key) => data?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static bool Boolean(JsonObject? data, string key) => data?[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static string Short(string text) { text = Regex.Replace(text, @"\s+", " ").Trim(); return text.Length > 110 ? text[..107] + "…" : text; }
    private static string FileName(string path) => path.Replace('\\', '/').Split('/').LastOrDefault() ?? path;
    private static string ToolState(AgentEvent item, JsonObject? data)
    {
        var state = Value(data, "status");
        if (data?["exitCode"] is JsonValue exit && exit.TryGetValue<int>(out var code) && code != 0) return "Exit " + code;
        if (state is "failed" or "error" || item.Text.EndsWith("· failed")) return "Failed";
        if (state is "completed" or "success" || item.Text.EndsWith("· completed")) return "Completed";
        if (state is "cancelled" or "canceled" or "interrupted") return "Stopped";
        if (item.Text is "You approved this action" or "Action declined") return "";
        return item.ItemId.Length > 0 ? "Running" : "";
    }
    private static string Describe(AgentEvent item, JsonObject? data)
    {
        var type = Value(data, "type");
        var tool = item.Agent == Agent.Claude ? item.Text : type;
        if (tool == "commandExecution")
        {
            if (data?["commandActions"] is JsonArray actions && actions.FirstOrDefault() is JsonObject action)
            {
                var kind = Value(action, "type");
                if (kind == "read") return Short("Read " + (Value(action, "name") is { Length: > 0 } name ? name : FileName(Value(action, "path"))));
                if (kind == "search") return Short("Search " + Value(action, "query"));
                if (kind == "listFiles") return Short("List files " + Value(action, "path"));
                if (Value(action, "command") is { Length: > 0 } command) return Short("Run: " + command);
            }
            return Short("Run: " + Value(data, "command"));
        }
        var path = Value(data, "file_path");
        if (tool == "Read") return Short("Read " + FileName(path));
        if (tool is "Edit" or "MultiEdit" or "NotebookEdit") return Short("Edit " + FileName(path));
        if (tool == "Write") return Short("Write " + FileName(path));
        if (tool == "Glob") return Short("Find files: " + Value(data, "pattern"));
        if (tool == "Grep") return Short("Search: " + Value(data, "pattern"));
        if (tool is "Bash" or "PowerShell") return Short("Run: " + Value(data, "command"));
        if (tool is "Task" or "Agent") return Short("Delegate: " + Value(data, "description"));
        if (tool == "fileChange" && data?["changes"] is JsonArray changes)
            return changes.Count == 1 && changes[0] is JsonObject change ? Short("Change " + FileName(Value(change, "path"))) : $"Change {changes.Count} files";
        if (tool == "mcpToolCall") return Short("Use " + Value(data, "server") + ": " + Value(data, "tool"));
        return Short(item.Text.Split('·')[0]);
    }
}

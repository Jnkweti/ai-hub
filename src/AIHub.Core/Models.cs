using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Core;

public enum Agent { Codex, Claude }
public enum EventKind { Status, TextDelta, Message, Tool, ToolOutput, Error, Session, Usage }
public record AgentEvent(Agent Agent, EventKind Kind, string Text, string ItemId = "", string Detail = "");
public record AgentReply(string Text, string? SessionId);
public record ConversationEntry(string Id, string Speaker, string Text, string Route = "", DateTimeOffset? CreatedAt = null);
public class ConversationCursor
{
    public string? SessionId { get; set; }
    public string[] MessageIds { get; set; } = [];
    public Dictionary<string, string> MessageHashes { get; set; } = [];
    // Structured phases: the task whose common context this native session received, the last stream event it was shown,
    // and the host input bytes fed to the session so far. Together they decide whether the next phase resumes the session.
    public string? TaskId { get; set; }
    public long? StreamSequence { get; set; }
    public long SessionInputBytes { get; set; }
    public ConversationCursor Copy() => new()
    {
        SessionId = SessionId, MessageIds = MessageIds.ToArray(), MessageHashes = new(MessageHashes ?? []),
        TaskId = TaskId, StreamSequence = StreamSequence, SessionInputBytes = SessionInputBytes
    };
}
public record Decision(bool Allow, string Answer = "");
public record QuestionChoice(string Label, string Description);
public record Approval(Agent Agent, string Title, string Detail, bool IsQuestion = false,
    QuestionChoice[]? Options = null, bool MultiSelect = false, bool AllowFreeText = true, bool IsSecret = false)
{
    public static Approval Question(Agent agent, JsonNode? question)
    {
        if (question is not JsonObject || question["options"] is JsonArray { Count: > 64 })
            throw new IOException("The agent sent an unsupported or oversized question. This turn was stopped.");
        var options = (question?["options"] as JsonArray)?.Select(o => new QuestionChoice(o.Str("label"), o.Str("description"))).ToArray() ?? [];
        if (options.Any(o => string.IsNullOrWhiteSpace(o.Label) || o.Label.Length > 256 || o.Description.Length > 4000) ||
            question.Str("header").Length > 256 || question.Str("question").Length > 32000)
            throw new IOException("The agent sent an oversized or empty question choice. This turn was stopped.");
        return new(agent, question.Str("header"), question.Str("question"), true, options,
            agent == Agent.Claude && question.Bool("multiSelect"),
            agent == Agent.Claude || options.Length == 0 || question.Bool("isOther"), question.Bool("isSecret"));
    }
}
public enum InputStatus { Pending, Answered, Declined, Cancelled }
public class SavedInput
{
    public string Title { get; set; } = "";
    public bool IsQuestion { get; set; }
    public QuestionChoice[] Options { get; set; } = [];
    public bool MultiSelect { get; set; }
    public bool AllowFreeText { get; set; }
    public bool IsSecret { get; set; }
    public InputStatus Status { get; set; }
}
public record AgentOptions(string Workspace, bool AllowEdits, string Model = "", string Executable = "")
{
    public CollaborationMcpHost? Collaboration { get; init; }
    public bool PreparationOnly { get; init; }
    /// <summary>Claude Code only: load the AI Hub MCP server as a channel so host notifications reach a running turn.</summary>
    public bool MidTurnPush { get; init; }
}

public interface IAgentClient : IAsyncDisposable
{
    Agent Agent { get; }
    string? SessionId { get; }
    event Action<AgentEvent>? Event;
    Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
    Task<AgentReply> SendAsync(string prompt, CancellationToken token);
}

public class HubSettings
{
    public string Workspace { get; set; } = AppContext.BaseDirectory;
    public string CodexPath { get; set; } = "";
    public string ClaudePath { get; set; } = "";
    public string CodexModel { get; set; } = "";
    public string ClaudeModel { get; set; } = "";
    public bool AllowEdits { get; set; }
    public bool AutoExchange { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public int MaxAutoRounds { get; set; } = CollaborationGuard.DefaultMaxRounds;
    public string LastRoomId { get; set; } = "";
    public Agent StatusInspector { get; set; } = Agent.Codex;
    public bool ShowActivityDiagnostics { get; set; }
    public bool CollectLocalDiagnostics { get; set; } = true;
    public int TurnInactivitySeconds { get; set; } = HubCoordinator.DefaultTurnInactivitySeconds;
    /// <summary>Experimental: deliver your messages into Claude Code's running turn (requires a Claude Code build with channels).</summary>
    public bool MidTurnPush { get; set; }
    /// <summary>Experimental: each agent edits in its own git worktree; the host merges into an integration branch you apply to the project when ready.</summary>
    public bool IsolateAgentWorktrees { get; set; }
    /// <summary>Providers over their usage limit, by agent name, with the moment they become usable again.</summary>
    public Dictionary<string, DateTimeOffset> ProviderUnavailableUntil { get; set; } = [];
}

public class SavedMessage
{
    public string TaskId { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Speaker { get; set; } = "You";
    public string Text { get; set; } = "";
    public string Route { get; set; } = "Everyone";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public bool Complete { get; set; } = true;
    public SavedInput? Input { get; set; }
    public CollaborationMessage? Collaboration { get; set; }
}

public class Room
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New conversation";
    public string Workspace { get; set; } = "";
    public bool IsArchived { get; set; }
    public bool IsAuditReview { get; set; }
    public bool EffectiveAllowEdits(HubSettings settings) => settings.AllowEdits && !IsAuditReview;
    public string? CodexSession { get; set; }
    public string? ClaudeSession { get; set; }
    public ConversationCursor CodexContext { get; set; } = new();
    public ConversationCursor ClaudeContext { get; set; } = new();
    public string SessionOptions { get; set; } = "";
    public string Draft { get; set; } = "";
    public string Target { get; set; } = "Both";
    public string PauseReason { get; set; } = "";
    public string LastTask { get; set; } = "";
    public string ActiveTaskId { get; set; } = "";
    public List<SavedMessage> Messages { get; set; } = [];
    /// <summary>A copy without the transcript, for the rooms index; each room's messages live in their own file.</summary>
    public Room Header() => new()
    {
        Id = Id, Title = Title, Workspace = Workspace, IsArchived = IsArchived, IsAuditReview = IsAuditReview, CodexSession = CodexSession, ClaudeSession = ClaudeSession,
        CodexContext = CodexContext, ClaudeContext = ClaudeContext, SessionOptions = SessionOptions, Draft = Draft, Target = Target, PauseReason = PauseReason,
        LastTask = LastTask, ActiveTaskId = ActiveTaskId, Messages = []
    };
}

public static class Json
{
    public static string Str(this JsonNode? n, string key) => n?[key]?.ToString() ?? "";
    public static bool Bool(this JsonNode? n, string key) => n?[key]?.GetValue<bool>() == true;
    public static JsonObject Obj(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
}

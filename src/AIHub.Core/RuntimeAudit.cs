using System.Text;
using System.Text.Json;

namespace AIHub.Core;

public enum AuditCode { ProviderError, StorageError, RepeatedContribution, RoundLimit, SuspectedStall, UnhandledError, RecoveryNotice, StreamImbalance }
public sealed record AuditFinding(AuditCode Code, string Room, string Task, Agent? Agent, DateTimeOffset First,
    DateTimeOffset Last, int Count, string ExceptionType = "", string Version = "");
public sealed record AuditTrace(DateTimeOffset Time, string Room, string Task, Agent? Agent, string Kind);
public sealed class AuditSnapshot
{
    public List<AuditFinding> Findings { get; set; } = [];
    public List<AuditTrace> Recent { get; set; } = [];
    public long Evicted { get; set; }
}

/// <summary>Bounded, metadata-only diagnostics. No provider/user text enters this store.</summary>
public sealed class RuntimeAudit : IAsyncDisposable
{
    public const int FindingLimit = 100, TraceLimit = 200;
    private readonly object gate = new();
    private readonly string directory;
    private readonly string version;
    private readonly CancellationTokenSource stop = new();
    private readonly Task writer;
    private readonly SemaphoreSlim persist = new(1);
    private readonly Dictionary<string, (string Task, DateTimeOffset Last, bool Reported)> active = [];
    // Pending approval/question waits are tracked apart from running rooms so toggling collection
    // during a wait cannot turn that wait into a suspected stall.
    private readonly Dictionary<string, int> waits = [];
    private AuditSnapshot state = new();
    private bool enabled;
    private long revision, savedRevision;
    private string storageStatus = "";
    public bool Enabled { get { lock (gate) return enabled; } set { lock (gate) { enabled = value; active.Clear(); } } }
    public string StorageStatus { get { lock (gate) return storageStatus; } }
    public string DirectoryPath => directory;
    public RuntimeAudit(string profileDirectory, string version, bool enabled = true)
    {
        directory = Path.Combine(profileDirectory, "diagnostics"); this.version = version; this.enabled = enabled;
        // A corrupt diagnostic file must never prevent opening the user's conversations.
        try
        {
            var path = Path.Combine(directory, "audit.json");
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 512_000) throw new IOException("Oversized diagnostics");
                var loaded = JsonSerializer.Deserialize<AuditSnapshot>(File.ReadAllText(path)) ?? throw new IOException("Invalid diagnostics");
                if (loaded.Findings is null || loaded.Recent is null || loaded.Findings.Count > FindingLimit || loaded.Recent.Count > TraceLimit ||
                    loaded.Findings.Any(f => f is null || !Enum.IsDefined(f.Code) || !ValidId(f.Room) || !ValidId(f.Task) || f.ExceptionType is null || f.ExceptionType.Length > 120 || f.Version is null || f.Version.Length > 40) ||
                    loaded.Recent.Any(t => t is null || !ValidId(t.Room) || !ValidId(t.Task) || t.Kind.Length > 40)) throw new IOException("Invalid diagnostics");
                state = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        { storageStatus = "Previous diagnostics could not be loaded. Conversation storage was not changed."; }
        writer = Task.Run(WriteLoopAsync);
    }
    private static bool ValidId(string? value) => value is not null && (value.Length == 0 || Guid.TryParse(value, out _));
    private static string Id(string? value) => ValidId(value) ? value! : "";
    public void Progress(string room, string task, Agent? agent, EventKind? kind = null, DateTimeOffset? now = null)
    {
        lock (gate)
        {
            if (!enabled) return;
            room = Id(room); task = Id(task); var time = now ?? DateTimeOffset.UtcNow;
            if (active.ContainsKey(room)) active[room] = (task, time, false);
            if (kind is EventKind.TextDelta or EventKind.ToolOutput) return; // Heartbeat only; no token flood.
            state.Recent.Add(new(time, room, task, agent, kind?.ToString() ?? "Dispatch"));
            if (state.Recent.Count > TraceLimit) { state.Recent.RemoveAt(0); state.Evicted++; }
            revision++;
        }
    }
    public void Running(string room, string task, bool running, DateTimeOffset? now = null)
    {
        lock (gate)
        {
            room = Id(room); task = Id(task);
            if (!running) { active.Remove(room); return; } // Clearing works even while collection is off.
            if (!enabled) return;
            if (!active.TryGetValue(room, out var run) || run.Task != task)
            {
                if (active.Count >= 100) active.Remove(active.Keys.First());
                active[room] = (task, now ?? DateTimeOffset.UtcNow, false);
            }
        }
    }
    public void Waiting(string room, bool waiting)
    {
        lock (gate)
        {
            room = Id(room);
            var count = Math.Max(0, waits.GetValueOrDefault(room) + (waiting ? 1 : -1));
            if (count == 0) waits.Remove(room); else waits[room] = count;
            if (active.TryGetValue(room, out var run)) active[room] = (run.Task, DateTimeOffset.UtcNow, false);
        }
    }
    /// <summary>A disposed or deleted room can no longer stall; drop its live bookkeeping (findings are kept).</summary>
    public void Forget(string room)
    { lock (gate) { room = Id(room); active.Remove(room); waits.Remove(room); } }
    public void CheckStalls(DateTimeOffset? now = null)
    {
        lock (gate)
        {
            if (!enabled) return;
            var time = now ?? DateTimeOffset.UtcNow;
            foreach (var (room, run) in active.ToArray())
                if (!waits.ContainsKey(room) && !run.Reported && time - run.Last >= TimeSpan.FromMinutes(5))
                {
                    Record(AuditCode.SuspectedStall, room, run.Task, now: time);
                    active[room] = (run.Task, run.Last, true);
                }
        }
    }
    public void Record(AuditCode code, string room = "", string task = "", Agent? agent = null, Exception? exception = null, DateTimeOffset? now = null)
    {
        lock (gate)
        {
            if (!enabled) return;
            room = Id(room); task = Id(task); var time = now ?? DateTimeOffset.UtcNow;
            var type = exception?.GetType().Name ?? ""; // Never persist exception messages, paths or provider output.
            if (type.Length > 120) type = type[..120];
            // Findings from different application versions stay separate so upgrades can be compared.
            var index = state.Findings.FindIndex(f => f.Code == code && f.Room == room && f.Task == task && f.Agent == agent && f.ExceptionType == type && f.Version == version);
            if (index >= 0)
            {
                var old = state.Findings[index]; state.Findings.RemoveAt(index);
                state.Findings.Add(old with { Last = time, Count = old.Count == int.MaxValue ? old.Count : old.Count + 1 });
            }
            else state.Findings.Add(new(code, room, task, agent, time, time, 1, type, version));
            if (state.Findings.Count > FindingLimit) { state.Findings.RemoveAt(0); state.Evicted++; }
            revision++;
        }
    }
    /// <summary>
    /// Records a crash-class failure and persists immediately, because the periodic writer may not get another turn.
    /// Returns whether the write completed within the timeout; the caller's exception handling is unchanged.
    /// </summary>
    public bool RecordUnhandled(Exception exception, TimeSpan? timeout = null)
    {
        Record(AuditCode.UnhandledError, exception: exception);
        try { return FlushAsync().Wait(timeout ?? TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { return false; }
    }
    public AuditSnapshot Snapshot()
    { lock (gate) return new() { Findings = [.. state.Findings], Recent = [.. state.Recent], Evicted = state.Evicted }; }
    public string Report()
    {
        var snapshot = Snapshot();
        var text = new StringBuilder($"# AI Hub local audit\n\nVersion: {version}\nGenerated UTC: {DateTimeOffset.UtcNow:O}\nCollection: {(Enabled ? "enabled" : "disabled")}\n\n");
        text.AppendLine("These are observed events and suspected problems, not proof of root causes. A quiet interval can be legitimate model or tool work. No automatic AI reviews run. Chat text, prompts and tool output are excluded. This report cannot detect every semantic or UI bug.");
        if (StorageStatus.Length > 0) text.AppendLine("\nStorage notice: " + StorageStatus);
        text.AppendLine($"\nRetained findings: {snapshot.Findings.Count}; older records evicted: {snapshot.Evicted}.");
        foreach (var f in snapshot.Findings)
            text.AppendLine($"\n- {f.Code} ({f.Count} observations), agent {f.Agent?.ToString() ?? "Hub"}; version {(f.Version.Length > 0 ? f.Version : "unknown")}; first {f.First:O}; last {f.Last:O}; room {f.Room}; task {f.Task}; exception type {f.ExceptionType}.");
        text.AppendLine("\n## Recent event metadata");
        foreach (var t in snapshot.Recent.TakeLast(80)) text.AppendLine($"- {t.Time:O} {t.Kind}, {t.Agent?.ToString() ?? "Hub"}, room {t.Room}, task {t.Task}");
        text.AppendLine("\nUse room/task references to locate activity and task history. Request relevant details before concluding a cause. Findings are bounded; export this report to keep a snapshot.");
        return text.ToString();
    }
    private async Task WriteLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try { while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false)) await FlushAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    // No captured synchronization context: RecordUnhandled may block a UI thread on this method.
    public async Task FlushAsync()
    {
        await persist.WaitAsync().ConfigureAwait(false);
        try
        {
            AuditSnapshot snapshot; long writing;
            lock (gate) { if (revision == savedRevision) return; snapshot = Snapshot(); writing = revision; }
            await Task.Run(() => new LocalStore(directory).Save("audit.json", snapshot)).ConfigureAwait(false);
            lock (gate) { savedRevision = writing; storageStatus = ""; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (gate) storageStatus = "Diagnostics could not be saved. Recent observations remain in memory; check disk space and folder access."; }
        finally { persist.Release(); }
    }
    public async ValueTask DisposeAsync()
    { stop.Cancel(); await writer.ConfigureAwait(false); await FlushAsync().ConfigureAwait(false); stop.Dispose(); persist.Dispose(); }
}

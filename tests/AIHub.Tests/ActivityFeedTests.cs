using AIHub.Core;
using System.Text.Json;

static class ActivityFeedTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("activity coalesces streamed output and completion into one readable action", () =>
        {
            var feed = new ActivityFeed(); feed.BeginTurn(Agent.Codex);
            feed.Record(Command("read-1", "inProgress"));
            for (var i = 0; i < 1500; i++) feed.Record(new(Agent.Codex, EventKind.ToolOutput, i % 2 == 0 ? "line\n" : "\r\n", "read-1"));
            feed.Record(Command("read-1", "completed"));
            Check(feed.Actions.Count == 1 && feed.Actions[0].Title == "Read README.md" && feed.Actions[0].State == "Completed", "Output flooded actions or title/state missing");
            Check(feed.Actions[0].Detail.Contains("line\n\r\nline"), "Output whitespace/content not preserved inside action");
            Check(feed.Diagnostics.Count == ActivityFeed.Capacity, "Diagnostics not separately bounded");
            return Task.CompletedTask;
        });
        await test("activity hides routine counters but preserves failures and warnings", () =>
        {
            var feed = new ActivityFeed();
            foreach (var text in new[] { "Working", "Ready", "Claude · thinking_tokens", "Claude · init", "Connected to Codex" }) feed.Record(new(Agent.Codex, EventKind.Status, text));
            feed.Record(new(Agent.Codex, EventKind.Usage, "Token usage updated", Detail: "{\"input_tokens\":200}"));
            feed.Record(new(Agent.Codex, EventKind.ToolOutput, "\r\n", "orphan"));
            Check(feed.Actions.Count == 0 && feed.Diagnostics.Count == 7, "Routine events became action rows");
            feed.Record(new(Agent.Codex, EventKind.Error, "Connection failed"));
            feed.Record(new(Agent.Claude, EventKind.Status, "WARN: provider disconnected"));
            Check(feed.Actions.Count == 2 && feed.Actions.Any(a => a.State == "Failed") && feed.Actions.Any(a => a.State == "Needs attention"), "Important problem hidden");
            return Task.CompletedTask;
        });
        await test("activity scope separates agents, reused item IDs and identical commands", () =>
        {
            var feed = new ActivityFeed(); feed.BeginTurn(Agent.Codex);
            feed.Record(Command("same-id", "inProgress"));
            feed.Record(new(Agent.Claude, EventKind.Tool, "Read", "same-id", "{\"file_path\":\"README.md\"}"));
            feed.Record(new(Agent.Claude, EventKind.ToolOutput, "Claude only", "same-id", "{\"isFinal\":true}"));
            feed.BeginTurn(Agent.Codex); feed.Record(Command("same-id", "inProgress"));
            feed.Record(Command("different-id", "inProgress"));
            Check(feed.Actions.Count == 4, "Separate actions were merged");
            Check(feed.Actions.Count(a => a.Detail.Contains("Claude only")) == 1 && feed.Actions.Last().State == "Stopped", "Output crossed agent/run scope");
            return Task.CompletedTask;
        });
        await test("activity completion failures remain in their original action", () =>
        {
            var feed = new ActivityFeed();
            feed.Record(new(Agent.Claude, EventKind.Tool, "Read", "c", "{\"file_path\":\"missing.cs\"}"));
            feed.Record(new(Agent.Claude, EventKind.ToolOutput, "File missing", "c", "{\"isFinal\":true,\"isError\":true}"));
            feed.Record(Command("code", "failed", 2));
            Check(feed.Actions.Count == 2 && feed.Actions.Any(a => a.State == "Failed" && a.Detail.Contains("File missing")) && feed.Actions.Any(a => a.State == "Exit 2"), "Failed action lost or duplicated");
            return Task.CompletedTask;
        });
        await test("activity updates details in place and preserves approvals and handoffs", () =>
        {
            var feed = new ActivityFeed(); feed.Record(Command("live", "inProgress"));
            var entry = feed.Actions[0]; var changes = 0; entry.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActivityEntry.Detail)) changes++; };
            feed.Record(new(Agent.Codex, EventKind.ToolOutput, "Live result", "live"));
            feed.AddNotice("Codex", "Approve command", "Approval detail");
            feed.AddNotice("Hub", "Codex → Claude", "Handoff detail");
            Check(changes > 0 && entry.Detail.Contains("Live result") && feed.Actions.Count == 3, "Live detail or important hub events were lost");
            return Task.CompletedTask;
        });
        await test("activity retains one plan per turn and bounds output and cleared history", () =>
        {
            var feed = new ActivityFeed();
            feed.Record(new(Agent.Codex, EventKind.Tool, "Plan updated", Detail: "first plan"));
            feed.Record(new(Agent.Codex, EventKind.Tool, "Plan updated", Detail: "revised plan"));
            Check(feed.Actions.Count == 1 && feed.Actions[0].Detail.Contains("revised plan"), "Plan revisions became separate rows");
            feed.Record(new(Agent.Codex, EventKind.ToolOutput, new string('x', ActivityEntry.DetailLimit * 3), "large"));
            Check(feed.Actions[0].Detail.Length < ActivityEntry.DetailLimit + 250 && feed.Actions[0].Detail.Contains("Display shortened"), "Unbounded or silently truncated output");
            for (var i = 0; i < 250; i++) feed.Record(Command("action-" + i, "completed"));
            Check(feed.Actions.Count == ActivityFeed.Capacity, "Unbounded action history");
            feed.Clear(); Check(feed.Actions.Count == 0 && feed.Diagnostics.Count == 0, "Room history leaked across reset");
            return Task.CompletedTask;
        });
        await test("activity accepts malformed tool metadata and output before its header", () =>
        {
            var feed = new ActivityFeed();
            feed.Record(new(Agent.Codex, EventKind.ToolOutput, "before header", "late"));
            feed.Record(new(Agent.Codex, EventKind.Tool, "custom tool · running", "late", "invalid JSON"));
            Check(feed.Actions.Count == 1 && feed.Actions[0].Title == "custom tool" && feed.Actions[0].Detail.Contains("before header"), "Out-of-order event lost output or crashed");
            return Task.CompletedTask;
        });
    }
    private static AgentEvent Command(string id, string status, int? exitCode = null) => new(Agent.Codex, EventKind.Tool,
        "commandExecution · " + (status == "inProgress" ? "running" : status), id,
        JsonSerializer.Serialize(new { type = "commandExecution", status, exitCode, commandActions = new[] { new { type = "read", name = "README.md", path = "README.md" } } }));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

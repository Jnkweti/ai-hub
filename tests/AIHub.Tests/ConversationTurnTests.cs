using AIHub.Core;
using System.Collections.Concurrent;

internal static class ConversationTurnTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (var edits in new[] { false, true }) await test("named speaker leads one shared conversation, edits=" + edits, async () =>
        {
            await using var f = new Fixture(edits);
            await f.Send("Claude, review this together.");
            var calls = f.Calls.ToArray();
            Check(calls.Length == 2 && calls[0].Agent == Agent.Claude && calls[1].Agent == Agent.Codex, "Codex was hardcoded as the leader");
            Check(f.Maximum == 1, "Overlapping provider turns");
            Check(calls[1].Prompt.Contains("Claude result 1") && calls[1].Prompt.Contains("Claude, review this together."), "Peer lacked the question or preceding answer");
            Check(!calls[1].Prompt.Contains("USER MESSAGE:\n"), "Peer received a second independent assignment");
        });
        await test("follow-up continues with the recent speaker and an explicit address overrides it", async () =>
        {
            await using var f = new Fixture();
            f.Add("Claude", "Earlier Claude answer");
            await f.Send("Please explain that further.");
            Check(f.Calls.First().Agent == Agent.Claude, "Follow-up lost its speaker");
            await f.Send("Claude, take the next turn.");
            Check(f.Calls.ToArray()[2].Agent == Agent.Claude, "Explicit address was ignored");
        });
        await test("agents can hand off and pass without another empty exchange", async () =>
        {
            await using var f = new Fixture(); f.Hub.AutoExchange = true;
            f.Reply = (agent, count) => count == 1 ? "Passing to Claude Code." : "No further contribution.";
            await f.Send("Plan the approach.");
            Check(f.Calls.Count == 2 && f.Calls.Last().Agent == Agent.Claude, "Handoff was ignored");
            Check(f.Pause.Contains("no further contribution"), "Pass did not end empty back-and-forth");
            Check(ConversationTurns.Handoff("Example:\n> Passing to Codex.") is null && !ConversationTurns.IsPass("`No further contribution.`"), "Quoted control text was executed");
        });
        await test("first speaker waiting for the user does not dispatch the peer", async () =>
        {
            await using var f = new Fixture(); f.Hub.AutoExchange = true;
            f.Reply = (_, _) => "Which folder should I use?\n\nWaiting for your input.";
            await f.Send("Review the project.");
            Check(f.Calls.Count == 1 && f.Pause.Contains("input"), "Peer answered before the human");
        });
        await test("inline user answers enter the next speaker's context", async () =>
        {
            await using var f = new Fixture();
            f.DuringTurn = agent =>
            {
                if (agent == Agent.Codex) { f.Add("Codex", "Which layout?"); f.Add("You", "Human selected violet", "Codex"); }
                return Task.CompletedTask;
            };
            await f.Send("Choose a layout.");
            Check(f.Calls.Last().Prompt.Contains("Human selected violet"), "Inline human answer was missing from the peer context");
        });
        await test("seen messages are skipped across turns and coordinator recreation", async () =>
        {
            await using var f = new Fixture();
            f.Add("You", "Unique historical marker");
            await f.Send("Inspect the design.");
            await f.Restart();
            await f.Send("Codex, explain the latest finding.");
            var calls = f.Calls.ToArray();
            Check(!calls[2].Prompt.Contains("Unique historical marker") && !calls[3].Prompt.Contains("Unique historical marker"), "Already delivered history was resent");
            Check(calls[2].Prompt.Contains("Claude result 2"), "Codex missed its peer's latest reply");
            Check(calls[3].Prompt.Contains("Codex result 3"), "Claude missed the new answer");
            f.SessionSuffix = "-new";
            await f.Restart();
            await f.Send("Codex, start with fresh session context.");
            Check(f.Calls.ToArray()[4].Prompt.Contains("Unique historical marker"), "A replaced native session inherited stale seen-message state");
        });
        await test("delivery cursors exclude omitted partial and mid-turn messages", async () =>
        {
            await using var f = new Fixture();
            f.Add("Claude", "Omitted marker"); f.Add("Claude", new string('x', 45000));
            var original = f.Entries.ToArray();
            f.DuringTurn = agent => { if (agent == Agent.Codex) f.Add("You", "Arrived during execution"); return Task.CompletedTask; };
            await f.Send("Codex, inspect");
            var cursor = f.Cursors[Agent.Codex];
            Check(!cursor.MessageIds.Contains(original[0].Id) && !cursor.MessageIds.Contains(original[1].Id) &&
                !cursor.MessageIds.Contains(f.Entries.First(e => e.Text == "Arrived during execution").Id), "Undelivered content was marked seen");
            var selection = ConversationTurns.BuildPrompt(Agent.Codex, original, null, null, true);
            Check(selection.Fragments.Length == 1 && !selection.Fragments[0].Complete && selection.Fragments[0].Start > 0, "Partial delivery was not identified");
            Check(ConversationTurns.ContentHash(original[0]) != ConversationTurns.ContentHash(original[0] with { Text = "Edited" }), "Content changes did not invalidate delivery");
        });
        await test("conversation context and persisted cursors stay bounded", async () =>
        {
            await using var f = new Fixture();
            for (var i = 0; i < 300; i++) f.Add("You", "Old entry " + i + " " + new string('x', 500));
            await f.Send("Codex, review the latest information.");
            Check(f.Calls.All(c => c.Prompt.Length < ConversationTurns.ContextCharacterLimit + 2500), "Context grew without a bound");
            Check(f.Cursors.Values.All(c => c.MessageIds.Length <= ConversationTurns.ContextMessageLimit), "Persisted delivery cursor grew without a bound");
        });
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public HubCoordinator Hub = null!;
        public readonly ConcurrentQueue<ConversationEntry> Entries = new();
        public readonly ConcurrentQueue<(Agent Agent, string Prompt)> Calls = new();
        public readonly ConcurrentDictionary<Agent, ConversationCursor> Cursors = new();
        public Func<Agent, int, string> Reply = (agent, count) => $"{agent} result {count}";
        public Func<Agent, Task>? DuringTurn;
        public int Active, Maximum;
        public string Pause = "", SessionSuffix = "";
        private readonly bool edits;
        private TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture(bool edits = false) { this.edits = edits; CreateHub(); }
        private void CreateHub()
        {
            Hub = new(a => new TurnAgent(a, this)) { AutoExchange = false, AllowEdits = edits };
            foreach (var entry in Cursors) Hub.RestoreContext(entry.Key, entry.Value);
            Hub.ReadConversation = _ => Task.FromResult<IReadOnlyList<ConversationEntry>>(Entries.ToArray());
            Hub.ContextSynchronized += (agent, cursor) => Cursors[agent] = cursor;
            Hub.Event += e => { if (e.Kind == EventKind.Message) Add(e.Agent.ToString(), e.Text); if (e.Kind == EventKind.Error) finished.TrySetException(new Exception(e.Text)); };
            Hub.AutoPaused += reason => { Pause = reason; finished.TrySetResult(); };
            Hub.State += state => { if (state == "Ready") finished.TrySetResult(); };
        }
        public void Add(string speaker, string text, string route = "") => Entries.Enqueue(new(Guid.NewGuid().ToString("N"), speaker, text, route));
        public async Task Send(string prompt)
        {
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Add("You", prompt, "Both");
            await Hub.SubmitAsync(prompt, "Both");
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public async Task Restart() { await Hub.DisposeAsync(); CreateHub(); }
        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }
    private sealed class TurnAgent(Agent agent, Fixture f) : IAgentClient
    {
        public Agent Agent => agent;
        public string? SessionId { get; } = "session-" + agent + f.SessionSuffix;
        public event Action<AgentEvent>? Event;
        public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
        public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
        {
            f.Calls.Enqueue((agent, prompt));
            var count = f.Calls.Count;
            var active = Interlocked.Increment(ref f.Active); f.Maximum = Math.Max(f.Maximum, active);
            try
            {
                await Task.Delay(20, token);
                if (f.DuringTurn is not null) await f.DuringTurn(agent);
                var text = f.Reply(agent, count); Event?.Invoke(new(agent, EventKind.Message, text)); return new(text, SessionId);
            }
            finally { Interlocked.Decrement(ref f.Active); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

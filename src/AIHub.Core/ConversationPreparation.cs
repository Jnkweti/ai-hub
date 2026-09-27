namespace AIHub.Core;

internal sealed record PreparedContribution(string Notes, string? SessionId);

internal sealed class ConversationPreparation : IAsyncDisposable
{
    internal const string Instructions = """
        AI HUB PREPARATION ONLY. You have received the user's message at the same time as your teammate.
        Your teammate is speaking first. Prepare a tentative contribution using ONLY the supplied context.
        Do not use tools, inspect files, execute commands, edit, delegate, ask the user, or publish chat.
        Return at most 6000 characters of concise outcome notes: possible additions, uncertainties, and
        questions worth checking. Do not provide a reasoning transcript. These are provisional agent claims.
        A later turn will supply your teammate's completed response and updated user context. Revise your
        contribution then; do not repeat your draft. You may have nothing to add. Do not submit_message or
        append legacy control phrases. Preparation does not claim ownership of execution work.
        """;
    private readonly CancellationTokenSource lifetime;
    private int disposed;
    internal Task<PreparedContribution?> Completion { get; }
    internal Agent Agent { get; }
    internal ConversationPreparation(CollaborationStore store, TaskClaim claim, Agent agent, CommonContext common,
        string prompt, Func<Agent, IAgentClient> factory, Action<AgentEvent> emit, CancellationToken token)
    {
        Agent = agent; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        var id = Guid.NewGuid().ToString("N");
        store.Assign(claim, new(id, agent, "preparation", prompt, [], new([], []),
            "Prepare tentative notes; revise after the first response before speaking.", claim.Generation, "running", DateTimeOffset.UtcNow));
        Completion = Task.Run(async () =>
        {
            ContextInputManifest? input = null;
            try
            {
                await using var client = factory(agent);
                client.RequestApproval = (_, _) => Task.FromResult(new Decision(false));
                client.Event += e =>
                {
                    // Adapter tool restrictions are the primary boundary. Never accept a draft if a provider
                    // unexpectedly reports tool execution despite that configuration.
                    if (e.Kind == EventKind.Tool) lifetime.Cancel();
                    if (!lifetime.IsCancellationRequested && e.Kind is EventKind.Usage) emit(e);
                };
                emit(new(agent, EventKind.Status, "Preparing contribution"));
                var text = Instructions + "\n\n" + common.Text + "\nCURRENT USER MESSAGE:\n" + prompt;
                input = store.PrepareInput(claim, agent, id, common, text);
                var reply = await client.SendAsync(text, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (reply.Text.Length > 6000) throw new IOException("Preparation exceeded its 6000-character note limit.");
                store.FinishInput(claim, input.Id, "responded", reply.SessionId);
                store.FinishAssignment(claim, id, "completed");
                emit(new(agent, EventKind.Status, "Prepared; waiting to speak"));
                return new PreparedContribution(reply.Text, reply.SessionId);
            }
            catch (Exception ex)
            {
                var state = lifetime.IsCancellationRequested ? "interrupted" : "failed";
                if (input is not null) store.FinishInput(claim, input.Id, state, null);
                store.FinishAssignment(claim, id, state);
                if (!lifetime.IsCancellationRequested) emit(new(agent, EventKind.Status, "Preparation unavailable; will use current context: " + ex.Message));
                return null;
            }
        }, CancellationToken.None);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync();
        try { await Completion; } finally { lifetime.Dispose(); }
    }
}

namespace AIHub.Core;

internal static class ContextResearchWorkflow
{
    internal static async Task RunAsync(CollaborationStore store, TaskClaim claim, CollaborationMessage request,
        string userPrompt, string bridge, Func<Agent, CollaborationMcpHost, IAgentClient> readOnlyFactory,
        Action<AgentEvent> emit, Action<string, string, string> dispatch, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workspace = store.Read(claim.TaskId).Workspace;
        foreach (var assignment in request.Content.Assignments!)
            store.Assign(claim, new(request.Envelope.MessageId + "-" + assignment.Agent, assignment.Agent, "research",
                string.Join("; ", assignment.Scope.Focus), [request.Envelope.MessageId], assignment.Scope,
                "Publish scoped findings and finish the read-only provider turn successfully.", claim.Generation, "running", DateTimeOffset.UtcNow));
        // Both workers receive this same frozen common core, irrespective of who connects first.
        var common = await Task.Run(() => store.BuildCommon(claim, lifetime.Token), lifetime.Token);
        await Task.WhenAll(request.Content.Assignments!.Select(assignment => Task.Run(async () =>
        {
            var workId = request.Envelope.MessageId + "-" + assignment.Agent;
            ContextInputManifest? manifest = null;
            try
            {
                var before = await CollaborationStore.CaptureContextAsync(workspace, assignment.Scope, lifetime.Token);
                using var research = store.OpenResearch(claim, request, assignment, before, lifetime.Token);
                await using var host = new CollaborationMcpHost(research, assignment.Agent, bridge, lifetime.Token, research.Id) { StartFreshSession = true };
                await using var client = readOnlyFactory(assignment.Agent, host);
                // Research sessions must never replace the room's normal resumable session IDs.
                client.Event += item =>
                {
                    // Research prose lives in the shared notebook; the main conversation combines it once.
                    if (!lifetime.IsCancellationRequested && item.Kind is not (EventKind.Session or EventKind.TextDelta or EventKind.Message)) emit(item);
                };
                client.RequestApproval = (_, _) => Task.FromResult(new Decision(false));
                dispatch("Shared context", assignment.Agent.ToString(), string.Join("; ", assignment.Scope.Focus));
                emit(new(assignment.Agent, EventKind.Status, "Gathering context"));
                var prompt = research.Instructions + "\n\n" + common.Text + "\n\nUSER TASK (research only in this phase):\n" + userPrompt +
                    "\n\nYOUR AREA: " + string.Join(", ", assignment.Scope.Files) + "\nQUESTIONS: " + string.Join("; ", assignment.Scope.Focus);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    manifest = store.PrepareInput(claim, assignment.Agent, research.Id, common, prompt, workId);
                    var reply = await client.SendAsync(prompt, lifetime.Token);
                    store.FinishInput(claim, manifest.Id, "responded", reply.SessionId);
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (store.Read(claim.TaskId).ContextSections.Any(s => s.RequestId == request.Envelope.MessageId && s.Agent == assignment.Agent))
                    { store.FinishAssignment(claim, workId, "completed"); emit(new(assignment.Agent, EventKind.Status, "Context saved")); return; }
                    prompt += "\nHOST REPAIR: Save your findings using publish_context before finishing. Include unresolved questions if blocked. Do not repeat research already performed.";
                }
                throw new IOException(assignment.Agent + " did not publish shared context after a repair attempt.");
            }
            catch
            {
                var state = lifetime.IsCancellationRequested ? "interrupted" : "failed";
                lifetime.Cancel();
                if (manifest is not null) store.FinishInput(claim, manifest.Id, state == "failed" ? "failed" : "interrupted", null);
                store.FinishAssignment(claim, workId, state); throw;
            }
        }, lifetime.Token)));
    }
}

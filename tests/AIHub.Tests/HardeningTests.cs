using AIHub.Core;
using System.Text.Json.Nodes;

/// <summary>Regression cases for the 0.15.0 hardening of the design-review findings.</summary>
internal static class HardeningTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("a failed final task write releases the claim so the next send is not refused", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false);
            using (new FileStream(Path.Combine(f.Root, "tasks.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Exception? failure = null;
                try { f.Memory.End(claim, WorkState.Ready, "done"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
                var after = f.Memory.Get(f.TaskId)!;
                Check(failure is not null && after.State == WorkState.Ready && after.Owner.Length == 0, $"End did not fail or left the task running: failure={failure?.GetType().Name}, state={after.State}, owner='{after.Owner}'");
            }
            var again = f.Memory.Begin(f.TaskId, false); // Would throw "This task still owns a worker" if the claim leaked.
            f.Memory.End(again, WorkState.Ready, "done");
            return Task.CompletedTask;
        });
        await test("only invalid structured submissions spend the repair budget", async () =>
        {
            using var f = new CollaborationTests.Fixture(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var host = new CollaborationMcpHost(f.Probe, Agent.Codex, CollaborationTests.Bridge, timeout.Token); host.BindSession("test-session");
            for (var i = 0; i < 5; i++) Check(host.InvokeTool("get_task_context", new JsonObject { ["unexpected"] = 1 }).Bool("isError"), "Bad arguments accepted");
            for (var i = 0; i < 5; i++) Check(host.InvokeTool("no_such_tool", new JsonObject()).Bool("isError"), "Unknown tool accepted");
            Check(!host.LimitReached, "Read-tool argument errors counted as repairs");
            for (var i = 0; i < 3; i++) host.InvokeTool("submit_message", new JsonObject());
            Check(host.LimitReached, "Invalid submissions did not reach the repair limit");
        });
        await test("input manifests are pruned oldest-first across generations and old prompt text is stripped", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture();
            var common = new CommonContext(0, new string('a', 64), "core", [], [], 4);
            for (var generation = 0; generation < 3; generation++)
            {
                var claim = f.Memory.Begin(f.TaskId, false);
                for (var i = 0; i < 50; i++) f.Store.PrepareInput(claim, Agent.Codex, Guid.NewGuid().ToString("N"), common, $"prompt {generation}-{i}");
                f.Memory.End(claim, WorkState.Ready, "done");
            }
            var inputs = f.Store.Read(f.TaskId).ContextInputs;
            Check(inputs.Count == CollaborationStore.MaxInputs, $"Manifest cap not enforced by eviction: {inputs.Count}");
            // 150 manifests over three generations: the 22 evicted all come from the oldest finished generation.
            Check(inputs.Count(i => i.Generation == 1) == 28 && inputs.Count(i => i.Generation == 2) == 50 && inputs.Count(i => i.Generation == 3) == 50, "Oldest generation was not evicted first");
            Check(inputs.Count(i => i.PromptRetained) == CollaborationStore.RetainedPrompts && inputs.TakeLast(CollaborationStore.RetainedPrompts).All(i => i.PromptRetained && i.Prompt.Length > 0), "Prompt text retention wrong");
            Check(inputs.Where(i => !i.PromptRetained).All(i => i.Prompt.Length == 0 && i.PromptHash.Length == 64 && i.InputBytes > 0), "Stripped manifests lost their hash or size");
            var reloaded = new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId);
            Check(reloaded.ContextInputs.Count == CollaborationStore.MaxInputs, "Pruned ledger did not reload");
            return Task.CompletedTask;
        });
        await test("evidence capture runs off the event thread and is drained before the terminal commit", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var observed = new List<string>();
            f.Store.BeforeSnapshotCapture = () => { lock (observed) observed.Add(Environment.CurrentManagedThreadId.ToString()); };
            await using var hub = f.Hub((agent, host, turn, _, _) =>
            {
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message());
                return Task.FromResult("Ran a check " + turn);
            });
            StructuredClientEvents.Hook = client =>
            {
                client.Emit(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd-1", "{\"type\":\"commandExecution\",\"status\":\"inProgress\",\"command\":\"dotnet test\"}"));
                client.Emit(new(Agent.Codex, EventKind.Tool, "commandExecution", "cmd-1", "{\"type\":\"commandExecution\",\"status\":\"completed\",\"command\":\"dotnet test\",\"exitCode\":0,\"aggregatedOutput\":\"ok\"}"));
            };
            try { await hub.SubmitAsync("Run the checks", "Codex"); await f.Finished(); }
            finally { StructuredClientEvents.Hook = null; }
            var doc = f.Store.Read(f.TaskId);
            var evidence = doc.Evidence.SingleOrDefault(e => e.SourceEventId == "cmd-1");
            Check(evidence is { Finished: true, ExitCode: 0 } && doc.Events.Any(e => e.Kind == "tool" && e.Text.Contains("dotnet test")), "Evidence was not recorded before the commit");
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Turn did not finish: " + f.Memory.Get(f.TaskId)!.Reason);
        });
        // 0.16.0: the remaining design-review findings.
        await test("oversized files are fingerprinted by size and time so a large asset never blocks reviews", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ah-snap-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "small.txt"), "small");
                using (var big = new FileStream(Path.Combine(root, "asset.bin"), FileMode.CreateNew)) big.SetLength(9L * 1024 * 1024);
                var first = await ProjectSnapshot.CaptureAsync(root, default);
                Check(first.Reusable && first.Limitation.Length == 0 && first.Files["asset.bin"].StartsWith("stat:"), $"A large asset made the snapshot incomplete: '{first.Limitation}' {first.Files.GetValueOrDefault("asset.bin")}");
                await Task.Delay(20);
                using (var big = new FileStream(Path.Combine(root, "asset.bin"), FileMode.Open)) { big.Seek(0, SeekOrigin.End); big.WriteByte(1); }
                var second = await ProjectSnapshot.CaptureAsync(root, default);
                Check(second.Reusable && second.Fingerprint != first.Fingerprint, "A change to the large asset went undetected");
            }
            finally { Directory.Delete(root, true); }
        });
        await test("a re-keyed native session is reported to the phase instead of killing the provider", async () =>
        {
            using var f = new CollaborationTests.Fixture(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var host = new CollaborationMcpHost(f.Probe, Agent.Codex, CollaborationTests.Bridge, timeout.Token);
            Check(host.BindSession("session-a") is null && host.BindSession("session-a") is null, "Rebinding the same session was reported as a change");
            Check(host.BindSession("session-b") == "session-a", "A new native session was not reported");
            Check(!host.InvokeTool("get_task_context", new JsonObject()).Bool("isError"), "Tools stopped working after the session re-keyed");
        });
        await test("preparation ignores plan updates and approval notices but stops on real tool use", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false);
            var common = new CommonContext(0, new string('a', 64), "core", [], [], 4);
            async Task<PreparedContribution?> Prepare(params AgentEvent[] events)
            {
                await using var preparation = new ConversationPreparation(f.Store, claim, Agent.Claude, common, "prompt",
                    _ => new ScriptedAgent(Agent.Claude, events, "tentative notes"), _ => { }, CancellationToken.None);
                return await preparation.Completion;
            }
            var benign = await Prepare(new AgentEvent(Agent.Claude, EventKind.Tool, "Plan updated", Detail: "{}"), new AgentEvent(Agent.Claude, EventKind.Tool, "Action declined", Detail: "{}"));
            Check(benign?.Notes == "tentative notes", "Benign tool notices discarded the preparation");
            var executed = await Prepare(new AgentEvent(Agent.Claude, EventKind.Tool, "Bash", "toolu_1", "{\"command\":\"ls\"}"));
            Check(executed is null, "Real tool execution was accepted in a preparation");
            f.Memory.End(claim, WorkState.Ready, "done");
        });
        await test("check reuse depends only on the curated environment the operation names", () =>
        {
            var noise = "AIHUB_TEST_NOISE_" + Guid.NewGuid().ToString("N")[..8];
            var before = CollaborationStore.WorkEnvironment("dotnet test");
            Environment.SetEnvironmentVariable(noise, "changed");
            try
            {
                Check(CollaborationStore.WorkEnvironment("dotnet test") == before, "An unrelated environment variable changed the check environment");
                var named = CollaborationStore.WorkEnvironment("echo $env:" + noise);
                Environment.SetEnvironmentVariable(noise, "changed again");
                Check(CollaborationStore.WorkEnvironment("echo $env:" + noise) != named, "A variable the operation names was ignored");
            }
            finally { Environment.SetEnvironmentVariable(noise, null); }
            return Task.CompletedTask;
        });
        await test("context records archive the oldest agent replies at the cap and archived entries are not re-imported", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false);
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            var conversation = Enumerable.Range(0, 1100).Select(i => new ConversationEntry("m" + i, i % 2 == 0 ? "You" : "Codex", "message " + i, "Both", start.AddSeconds(i))).ToArray();
            f.Store.SynchronizeContext(claim, conversation, "message 1098");
            var doc = f.Store.Read(f.TaskId);
            Check(doc.ContextRecords.Count == CollaborationStore.MaxRecords && doc.ArchivedRecords == 76, $"Cap not held by archiving: {doc.ContextRecords.Count} records, {doc.ArchivedRecords} archived");
            Check(doc.ContextRecords.Count(r => r.Kind == "user_message") == 550 && doc.ContextRecords.Where(r => r.Kind == "agent_message").All(r => int.Parse(r.Id["chat:m".Length..]) >= 153), "Archiving did not take the oldest agent replies first");
            f.Store.SynchronizeContext(claim, conversation, "message 1098");
            var again = f.Store.Read(f.TaskId);
            Check(again.ArchivedRecords == 76 && again.ContextRecords.Count == CollaborationStore.MaxRecords, $"Archived entries were re-imported: {again.ArchivedRecords} archived");
            Check(new CollaborationStore(f.Local, new TaskMemory(f.Local)).Read(f.TaskId).ArchivedThrough == again.ArchivedThrough, "Archive watermark did not persist");
            f.Memory.End(claim, WorkState.Ready, "done");
            return Task.CompletedTask;
        });
        await test("the ledger cache is bounded and evicted tasks reload on demand", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var task = f.Memory.Get(f.TaskId)!;
            var ids = Enumerable.Range(0, CollaborationStore.CacheLimit + 4).Select(i => f.Memory.Create(task.RoomId, task.Workspace, "objective " + i)).ToList();
            foreach (var id in ids) f.Store.Read(id);
            Check(f.Store.CachedDocuments <= CollaborationStore.CacheLimit, $"Cache grew past its bound: {f.Store.CachedDocuments}");
            Check(f.Store.Read(ids[0]).TaskId == ids[0] && f.Store.Read(f.TaskId).TaskId == f.TaskId, "Evicted ledgers did not reload");
            return Task.CompletedTask;
        });
        await test("a file already in a project subfolder is referenced in place rather than copied to the root", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var workspace = f.Memory.Get(f.TaskId)!.Workspace;
            var sub = Path.Combine(workspace, "docs"); Directory.CreateDirectory(sub); var file = Path.Combine(sub, "notes.md"); File.WriteAllText(file, "notes");
            var imported = await WorkspaceImports.CopyAsync(file, workspace);
            Check(imported.AlreadyInWorkspace && imported.Name == "docs/notes.md" && imported.FullPath == file && !File.Exists(Path.Combine(workspace, "notes.md")), $"Subfolder file was copied to the root: {imported.Name}");
        });
        await test("Codex stops asking after the first declined question, as Claude does", async () =>
        {
            await using var client = new CodexClient(new(AppContext.BaseDirectory, false, Executable: Environment.ProcessPath!));
            var asked = 0; client.RequestApproval = (_, _) => { asked++; return Task.FromResult(new Decision(false)); };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await client.SendAsync("input-fixture-batch", timeout.Token);
            var answers = JsonNode.Parse(reply.Text)!["answers"]!;
            Check(asked == 1 && answers["layout"]!["answers"]!.AsArray().Count == 0 && answers["notes"]!["answers"]!.AsArray().Count == 0, $"Codex kept asking after a decline: asked {asked}");
        });
        // 0.17.0: responsiveness.
        await test("workspace fingerprints reuse content hashes for unchanged files and rehash only what changed", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ah-inc-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(root);
            try
            {
                for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(root, $"file{i}.txt"), "content " + i);
                var before = Interlocked.Read(ref ProjectSnapshot.FilesHashed);
                var cold = await ProjectSnapshot.CaptureAsync(root, default);
                Check(Interlocked.Read(ref ProjectSnapshot.FilesHashed) - before == 5, "Cold capture did not hash every file");
                var warm = await ProjectSnapshot.CaptureAsync(root, default);
                Check(Interlocked.Read(ref ProjectSnapshot.FilesHashed) - before == 5 && warm.Fingerprint == cold.Fingerprint && warm.Reusable, "Warm capture rehashed unchanged files or changed the fingerprint");
                await Task.Delay(20); File.AppendAllText(Path.Combine(root, "file2.txt"), " changed");
                var changed = await ProjectSnapshot.CaptureAsync(root, default);
                Check(Interlocked.Read(ref ProjectSnapshot.FilesHashed) - before == 6 && changed.Fingerprint != cold.Fingerprint, "A changed file was not rehashed or not detected");
            }
            finally { Directory.Delete(root, true); }
        });
        await test("a provider over its usage limit sits out the phase and the other agent still answers", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var errors = new List<string>();
            await using var hub = f.Hub((agent, host, _, _, _) =>
            {
                if (agent == Agent.Codex) throw new IOException("You’ve hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at Oct 3rd, 2026 11:21 PM.");
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message()); return Task.FromResult("Claude answers alone.");
            });
            hub.Event += e => { if (e.Kind == EventKind.Error) lock (errors) errors.Add(e.Text); };
            await hub.SubmitAsync("Discuss our options", "Both"); await f.Finished();
            var task = f.Memory.Get(f.TaskId)!; var doc = f.Store.Read(f.TaskId);
            Check(task.State == WorkState.Ready, $"Run did not finish with the available agent: {task.State} {task.Reason}");
            Check(f.Speakers.SequenceEqual([Agent.Codex, Agent.Claude]) && doc.Entries.Count(e => e.SenderSucceeded) == 1, "Claude did not get its turn after Codex was unavailable");
            Check(errors.Any(t => t.Contains("unavailable for this phase")) && doc.Events.Any(e => e.Kind == "system" && e.Text.Contains("unavailable")), "The user and the stream were not told");
            Check(task.Reason.Contains("unavailable"), "Outcome did not record the unavailable provider: " + task.Reason);
        });
        await test("both providers over their limits still pause the run for the user", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture();
            await using var hub = f.Hub((_, _, _, _, _) => throw new IOException("You've hit your usage limit."));
            await hub.SubmitAsync("Discuss our options", "Both"); await f.Finished();
            Check(f.Memory.Get(f.TaskId)!.State == WorkState.Failed && f.Calls == 2, "A second unavailable provider was not reported as a failure");
        });
        await test("casual greetings and acknowledgements get one quick reply without a preparation session", async () =>
        {
            foreach (var social in new[] { "yooo", "yo", "Yo!", "sup", "what's up?", "hey guys", "good night", "gm", "haha", "lol", "thx", "nice work", "ok thanks" })
                Check(CollaborationGuard.IsSocialOnly(social), $"Not recognised as social: {social}");
            foreach (var real in new[] { "yes", "no", "sure", "go ahead", "Hello, review my project", "what's up with the build?", "ok now run the tests" })
                Check(!CollaborationGuard.IsSocialOnly(real), $"Instruction mistaken for small talk: {real}");
            using var f = new CollaborationRoutingTests.Fixture(); var prepared = false;
            await using var hub = f.Hub((_, host, _, _, _) => { CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message()); return Task.FromResult("Hey! Ready when you are."); });
            hub.PreparationFactory = agent => { prepared = true; return new ScriptedAgent(agent, [], "notes"); };
            await hub.SubmitAsync("yooo", "Both"); await f.Finished();
            Check(f.Calls == 1 && !prepared && f.Memory.Get(f.TaskId)!.State == WorkState.Ready, $"A greeting ran the full pipeline: calls={f.Calls} prepared={prepared}");
        });
        // 0.18.0: batch 1 of the peer-project survey.
        await test("a turn with no provider output is stopped by the inactivity watchdog, but streaming or waiting on the user is not", async () =>
        {
            using (var f = new CollaborationRoutingTests.Fixture())
            {
                await using var hub = f.Hub(async (_, _, _, _, token) => { await Task.Delay(Timeout.Infinite, token); return ""; });
                hub.TurnInactivitySeconds = 1;
                await hub.SubmitAsync("Do the work", "Codex"); await f.Finished();
                var task = f.Memory.Get(f.TaskId)!;
                Check(task.State == WorkState.Failed && task.Reason.Contains("no output for 1 seconds"), $"Silent turn was not stopped: {task.State} {task.Reason}");
            }
            using (var f = new CollaborationRoutingTests.Fixture())
            {
                CollaborationRoutingTests.StructuredFake? captured = null;
                StructuredClientEvents.Hook = client => { captured = client; _ = Task.Run(async () => { for (var i = 0; i < 8; i++) { await Task.Delay(300); client.Emit(new(Agent.Codex, EventKind.Status, "Claude · thinking")); } }); };
                try
                {
                    await using var hub = f.Hub(async (_, host, _, _, token) =>
                    {
                        await Task.Delay(2500, token);
                        CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message()); return "Done after a long think.";
                    });
                    hub.TurnInactivitySeconds = 1;
                    await hub.SubmitAsync("Do the work", "Codex"); await f.Finished();
                    Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "A streaming turn was stopped by the watchdog: " + f.Memory.Get(f.TaskId)!.Reason);
                }
                finally { StructuredClientEvents.Hook = null; }
            }
            using (var f = new CollaborationRoutingTests.Fixture())
            {
                CollaborationRoutingTests.StructuredFake? captured = null;
                StructuredClientEvents.Hook = client => captured = client;
                try
                {
                    await using var hub = f.Hub(async (_, host, _, _, token) =>
                    {
                        await captured!.RequestApproval!(new Approval(Agent.Codex, "Approve command", "dotnet test"), token);
                        CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message()); return "Ran it after approval.";
                    });
                    hub.TurnInactivitySeconds = 1;
                    hub.RequestApproval = async (_, token) => { await Task.Delay(2500, token); return new Decision(true); };
                    await hub.SubmitAsync("Do the work", "Codex"); await f.Finished();
                    Check(f.Memory.Get(f.TaskId)!.State == WorkState.Ready, "Waiting on the user counted as provider inactivity: " + f.Memory.Get(f.TaskId)!.Reason);
                }
                finally { StructuredClientEvents.Hook = null; }
            }
        });
        await test("limit messages yield the moment the provider is usable again", () =>
        {
            var now = new DateTimeOffset(2026, 9, 28, 16, 0, 0, TimeSpan.FromHours(-4));
            var codex = ProviderLimits.UnavailableUntil("You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at Oct 3rd, 2026 11:21 PM.", now);
            Check(codex == new DateTimeOffset(2026, 10, 3, 23, 21, 0, now.Offset), "Codex reset date not parsed: " + codex);
            var claude = ProviderLimits.UnavailableUntil("Claude usage limit reached. Your limit will reset at 3pm (America/New_York).", now);
            Check(claude > now && claude.ToUniversalTime().Hour is 19 or 18 && claude - now < TimeSpan.FromDays(1), "Zoned clock reset not parsed: " + claude);
            Check(ProviderLimits.UnavailableUntil("Rate limit exceeded; retry in 20 minutes.", now) == now.AddMinutes(20), "Relative reset not parsed");
            Check(ProviderLimits.UnavailableUntil("429 Too Many Requests", now) == now.AddMinutes(5), "Rate-limit cooldown wrong");
            Check(ProviderLimits.UnavailableUntil("You have reached your usage limit.", now) == now.AddHours(1), "Usage-limit cooldown wrong");
            return Task.CompletedTask;
        });
        await test("a provider known to be over its limit is not dispatched to, and the other agent answers alone", async () =>
        {
            using (var f = new CollaborationRoutingTests.Fixture())
            {
                await using var hub = f.Hub((_, host, _, _, _) => { CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message()); return Task.FromResult("Claude answers."); });
                hub.ProviderUnavailableUntil = agent => agent == Agent.Codex ? DateTimeOffset.Now.AddHours(1) : null;
                await hub.SubmitAsync("Discuss our options", "Both"); await f.Finished();
                var task = f.Memory.Get(f.TaskId)!;
                Check(f.Speakers.SequenceEqual([Agent.Claude]) && task.State == WorkState.Ready && task.Reason.Contains("unavailable until"), $"Unavailable provider was dispatched to or the outcome hid it: {string.Join(",", f.Speakers)} {task.State} {task.Reason}");
            }
            using (var f = new CollaborationRoutingTests.Fixture())
            {
                await using var hub = f.Hub((_, _, _, _, _) => throw new Exception("No provider should run"));
                hub.ProviderUnavailableUntil = _ => DateTimeOffset.Now.AddHours(1);
                await hub.SubmitAsync("Discuss our options", "Both"); await f.Finished();
                Check(f.Calls == 0 && f.Memory.Get(f.TaskId)!.State == WorkState.Failed && f.Memory.Get(f.TaskId)!.Reason.Contains("Wait for the limit"), "Both unavailable did not fail fast");
            }
        });
        await test("delta prompts tier the stream: full peer and user entries, one line per run of tool calls, one line per pass", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false);
            for (var i = 0; i < 6; i++) f.Store.AppendEvent(claim, "tool", "Codex", $"command: dotnet test {i} → exit {(i == 2 ? 1 : 0)}{(i == 2 ? " (error)" : "")}", "ev" + i, "d1");
            f.Store.AppendEvent(claim, "agent_message", "Codex", "The tests pass except one boundary case.", "m1", "d1");
            f.Store.AppendEvent(claim, "agent_pass", "Codex", "Reviewed; nothing to add.", "m2", "d2");
            f.Store.AppendEvent(claim, "user_message", "You", "Please fix the boundary case.", "chat:u1");
            var delta = f.Store.EventsSince(claim, 0, "Claude");
            Check(delta.Text.Contains("6 native tool calls finished, 1 with errors") && !delta.Text.Contains("dotnet test 0 →"), "Tool calls were not summarised: " + delta.Text);
            Check(delta.Text.Contains("The tests pass except one boundary case.") && delta.Text.Contains("Please fix the boundary case."), "A peer or user entry was not delivered in full");
            Check(delta.Text.Contains("agent_pass · Codex: passed.") && delta.Count == 4 && delta.LastSequence == 9, $"Pass line or bookkeeping wrong: count={delta.Count} last={delta.LastSequence}");
            f.Memory.End(claim, WorkState.Ready, "done");
            return Task.CompletedTask;
        });
        await test("the second agent to change a file in a phase is told once and the stream records the collision", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture(); var claim = f.Memory.Begin(f.TaskId, false); var workspace = f.Memory.Get(f.TaskId)!.Workspace;
            var codex = f.Dispatch(claim, Agent.Codex);
            Check(codex.Observe(new(Agent.Codex, EventKind.Tool, "fileChange · completed", "fc1", "{\"type\":\"fileChange\",\"status\":\"completed\",\"changes\":[{\"path\":\"src/app.cs\",\"kind\":\"modify\"}]}")) is null, "First edit was reported as a collision");
            codex.Abort("turn over"); f.Memory.ReleaseSpeaker(claim, Agent.Codex);
            var claude = f.Dispatch(claim, Agent.Claude);
            var notice = claude.Observe(new(Agent.Claude, EventKind.Tool, "Edit", "toolu_1", "{\"file_path\":\"" + Path.Combine(workspace, "src", "app.cs").Replace("\\", "\\\\") + "\",\"old_string\":\"a\",\"new_string\":\"b\"}"));
            Check(notice is not null && notice.Contains("src/app.cs"), "Second agent was not told about the collision: " + notice);
            var again = claude.Observe(new(Agent.Claude, EventKind.Tool, "Write", "toolu_2", "{\"file_path\":\"src/app.cs\",\"content\":\"c\"}"));
            var doc = f.Store.Read(f.TaskId);
            Check(again is null && doc.Events.Count(e => e.Kind == "system" && e.Text.StartsWith("Edit collision")) == 1 && doc.Touches.Count == 3, $"Collision not recorded once: notice={again} events={doc.Events.Count(e => e.Text.StartsWith("Edit collision"))} touches={doc.Touches.Count}");
            claude.Abort("done"); f.Memory.End(claim, WorkState.Ready, "done");
            return Task.CompletedTask;
        });
        await test("the phase completion gate names an unanswered peer request", async () =>
        {
            using var f = new CollaborationRoutingTests.Fixture();
            await using var hub = f.Hub((agent, host, _, _, _) =>
            {
                if (agent == Agent.Codex) throw new IOException("You've hit your usage limit. Try again at Oct 3rd, 2026 11:21 PM.");
                CollaborationRoutingTests.Tool(host, "submit_message", CollaborationRoutingTests.Message("review_request", Agent.Codex)); return Task.FromResult("Codex, please review src/app.cs.");
            });
            await hub.SubmitAsync("Claude, ask Codex to review the change", "Both"); await f.Finished();
            var task = f.Memory.Get(f.TaskId)!; var doc = f.Store.Read(f.TaskId);
            Check(task.State == WorkState.Ready && task.Reason.Contains("1 peer request unanswered"), $"Gate did not report the unanswered request: {task.State} {task.Reason}");
            Check(doc.Events.Any(e => e.Kind == "system" && e.Text.StartsWith("Outstanding at phase end")), "Gate summary not in the stream");
        });
        await test("a review packet is generated from the ledger with attributed sections", () =>
        {
            using var f = new CollaborationRoutingTests.Fixture();
            f.Store.PinInstruction(f.TaskId, "Keep the public API unchanged.");
            var packet = f.Store.ReviewPacket(f.TaskId, default);
            Check(packet.StartsWith("# Review packet") && packet.Contains("## Objective and active instructions") && packet.Contains("Keep the public API unchanged.") &&
                packet.Contains("## Findings") && packet.Contains("None recorded.") && packet.Contains("## Evidence") && packet.Contains("## Provenance") && packet.Contains("agent claims"), "Packet sections missing:\n" + packet);
            return Task.CompletedTask;
        });
    }
}

/// <summary>A provider stand-in that emits scripted native events during its turn, then replies; cancellation is honoured.</summary>
internal sealed class ScriptedAgent(Agent agent, AgentEvent[] events, string reply) : IAgentClient
{
    public Agent Agent => agent;
    public string? SessionId => "scripted";
    public event Action<AgentEvent>? Event;
    public Func<Approval, CancellationToken, Task<Decision>>? RequestApproval { get; set; }
    public async Task<AgentReply> SendAsync(string prompt, CancellationToken token)
    {
        foreach (var e in events) Event?.Invoke(e);
        await Task.Delay(20, token);
        return new(reply, SessionId);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Lets a test emit native-looking events from the routing fixture's fake client during a turn.</summary>
internal static class StructuredClientEvents
{
    public static Action<CollaborationRoutingTests.StructuredFake>? Hook;
}

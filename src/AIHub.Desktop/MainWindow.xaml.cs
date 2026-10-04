using AIHub.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AIHub.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalStore store = new(Environment.GetEnvironmentVariable("AIHUB_DATA_DIR"));
    private readonly ProjectStatusStore statusStore;
    private HubSettings settings;
    private bool dirty; // Unsaved conversation state: streamed text, drafts, routing and pause changes between explicit saves.
    private readonly HashSet<string> dirtyRooms = []; // Rooms whose transcript changed in place (streamed text, [Stopped], input status); additions are caught by count.
    private void TouchRoom(Room room) { dirtyRooms.Add(room.Id); dirty = true; }
    private readonly ObservableCollection<Room> rooms;
    private readonly ObservableCollection<MessageView> messages = [];
    private ActivityFeed activity = new();
    private Dictionary<string, MessageView> streaming = [];
    private readonly Dictionary<string, RoomWorker> workers = [];
    private readonly TaskMemory taskMemory;
    private readonly CollaborationStore collaborationStore;
    private readonly FeedbackStore feedback; // Explicit developer feedback on messages and tasks; local only, never supplied to agents.
    private readonly PreferenceStore preferences; // Developer-confirmed preferences, supplied in scope below instructions (0.30.0).
    private readonly ShadowStrategyLog shadowLog; // What the shadow strategy policy would have chosen per phase; never executed (0.32.0).
    private string? ShadowAdvice(string prompt, string taskId, long generation, HubCoordinator.CollaborationStrategy executed)
    {
        var context = StrategyAdvisor.Describe(prompt, taskId, generation, 2, feedback.All());
        var (suggested, reason) = StrategyAdvisor.Suggest(context);
        return ShadowStrategyLog.Describe(shadowLog.Record(context, suggested, reason, HubCoordinator.StrategySetting(executed)));
    }
    // Evaluation tasks (0.34.0): the policy's choice runs, with a stated exploration rate, and the decision is recorded with its probability.
    private (HubCoordinator.CollaborationStrategy Chosen, string Note) ChooseStrategy(string prompt, string taskId, long generation, HubCoordinator.CollaborationStrategy configured)
    {
        var context = StrategyAdvisor.Describe(prompt, taskId, generation, 2, feedback.All());
        var (chosen, suggested, reason, probability, explored) = StrategyAdvisor.Choose(context, Random.Shared.NextDouble());
        var decision = shadowLog.Record(context, suggested, reason, chosen, "evaluation", probability, explored);
        return (HubCoordinator.ParseStrategy(chosen), ShadowStrategyLog.Describe(decision));
    }
    private readonly RuntimeAudit audit;
    private sealed class RoomWorker(Room room, HubCoordinator hub)
    {
        public Room Room { get; } = room;
        public HubCoordinator Hub { get; } = hub;
        public ActivityFeed Activity { get; } = new();
        public Dictionary<string, MessageView> Streaming { get; } = [];
        public Dictionary<Agent, AgentEvent> AgentStates { get; } = [];
        public string State { get; set; } = "Ready";
    }
    private Room current = new();
    private HubCoordinator? hub;
    private bool switching, closing, closeAllowed, closeAfterSwitch, ready, sending, loadingComposer, filteringRooms;
    private bool codexWorking, claudeWorking, activityVisible = true;
    private bool? activityPreference;
    private bool followChat = true;
    private ICollectionView? roomView;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    public MainWindow()
    {
        settings = store.Load("settings.json", () => new HubSettings { Workspace = FindProjectRoot() }, SavedStateRepair.Settings);
        statusStore = new(store.DirectoryPath);
        ProjectSnapshot.CacheDirectory = Path.Combine(store.DirectoryPath, "fingerprints"); // Workspace hash maps survive restarts.
        rooms = new(store.LoadRooms());
        taskMemory = new(store);
        collaborationStore = new(store, taskMemory, preserveUnavailableTasks: true, deferRecovery: true);
        feedback = new(store);
        preferences = new(store);
        shadowLog = new(store);
        collaborationStore.Preferences = task => preferences.Relevant(task.Workspace, task.Id);
        audit = new(store.DirectoryPath, typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown", settings.CollectLocalDiagnostics);
        if (store.RecoveryNotices.Count > 0) audit.Record(AuditCode.RecoveryNotice);
        // Crash-class failures are recorded and flushed immediately; the failure itself is not suppressed.
        Application.Current.DispatcherUnhandledException += (_, e) => audit.RecordUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) audit.RecordUnhandled(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => audit.Record(AuditCode.UnhandledError, exception: e.Exception.GetBaseException());
        // Native requests cannot survive an application restart.
        foreach (var room in rooms)
            foreach (var message in room.Messages)
                if (message.Input is { Status: InputStatus.Pending } input) { input.Status = InputStatus.Cancelled; dirtyRooms.Add(room.Id); }
        Motion.Configure(settings.ReduceMotion);
        InitializeComponent();
        VersionLabel.Text = "AI Hub  /  " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MessageList.ItemsSource = messages;
        DiagnosticsToggle.IsChecked = settings.ShowActivityDiagnostics; RefreshActivity();
        roomView = CollectionViewSource.GetDefaultView(rooms);
        roomView.Filter = value => value is Room room && room.IsArchived == (RoomFilter.SelectedIndex == 1) && (room.Title.Contains(SearchBox.Text.Trim(),StringComparison.OrdinalIgnoreCase) || room.Workspace.Contains(SearchBox.Text.Trim(),StringComparison.OrdinalIgnoreCase));
        RoomList.ItemsSource = roomView;
        if (rooms.Count == 0) rooms.Add(new Room { Workspace = settings.Workspace });
        var selected = rooms.FirstOrDefault(r => r.Id == settings.LastRoomId) ?? rooms.FirstOrDefault(r => !r.IsArchived) ?? rooms[0];
        RoomFilter.SelectedIndex = selected.IsArchived ? 1 : 0;
        ready = true; RoomList.SelectedItem = selected; AutoToggle.IsChecked = settings.AutoExchange;
        saveTimer.Tick += (_, _) => { if (dirty) Save(); RefreshTaskSummary(); CheckRuntimeAudit(); ReflectAvailability(); }; saveTimer.Start(); Loaded += Window_Loaded;
        if (store.RecoveryNotices.Count > 0) StateLabel.Text = string.Join("\n", store.RecoveryNotices);
        // Ledgers are recovered on first use; the startup pass runs in the background instead of blocking the window.
        collaborationStore.RecoveryNotice += notice => Dispatcher.BeginInvoke(() => { audit.Record(AuditCode.RecoveryNotice); StateLabel.Text = notice; });
        collaborationStore.BeginRecovery();
    }
    private static string FindProjectRoot()
    {
        for (var p = new DirectoryInfo(AppContext.BaseDirectory); p is not null; p = p.Parent)
            if (Directory.Exists(Path.Combine(p.FullName, "src", "AIHub.Core"))) return p.FullName;
        return AppContext.BaseDirectory;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        Motion.Enter(NavigationRail, 280);
        Motion.Enter(CollaborationInstrument, 320, 50);
        Motion.Enter(ConversationSurface, 340, 90);
        Motion.Enter(CommandSurface, 300, 130);
        var args = Environment.GetCommandLineArgs(); var i = Array.IndexOf(args, "--screenshot");
        if (i < 0 || i + 1 >= args.Length) return;
        await Task.Delay(1000);
        var visual = (FrameworkElement)Content;
        var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(args[i + 1])) png.Save(file);
        Close();
    }
    private void Save()
    {
        try { store.Save("settings.json", settings); store.SaveRooms(rooms, dirtyRooms); dirty = false; dirtyRooms.Clear(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { audit.Record(AuditCode.StorageError, current.Id, current.ActiveTaskId, exception: ex); StateLabel.Text = "Save failed: " + ex.Message; }
    }
    private void BuildHub()
    {
        var room = current;
        var allowEdits = room.EffectiveAllowEdits(settings);
        if (workers.TryGetValue(room.Id, out var existing))
        { hub = existing.Hub; activity = existing.Activity; streaming = existing.Streaming; UpdateWorkspace(); return; }
        // Preserve pre-task native sessions when an older room is simply opened.
        // Its first durable task explicitly establishes the new session boundary.
        var signature = room.ActiveTaskId.Length == 0
            ? JsonSerializer.Serialize(new { settings.CodexModel, settings.ClaudeModel, AllowEdits = allowEdits, room.Workspace })
            : JsonSerializer.Serialize(new { settings.CodexModel, settings.ClaudeModel, AllowEdits = allowEdits, room.Workspace, room.ActiveTaskId, CollaborationVersion = 1 });
        if (room.SessionOptions != signature) { room.CodexSession = null; room.ClaudeSession = null; room.SessionOptions = signature; }
        var codexOptions = new AgentOptions(room.Workspace, allowEdits, settings.CodexModel, settings.CodexPath);
        var claudeOptions = new AgentOptions(room.Workspace, allowEdits, settings.ClaudeModel, settings.ClaudePath);
        HubCoordinator self = null!; // Structured clients ask the coordinator for the agent's worktree, if any, when they are created.
        string Cwd(Agent agent) => self.WorktreeFor(agent) ?? room.Workspace;
        var coordinator = new HubCoordinator(agent => agent == Agent.Codex
            ? new CodexClient(codexOptions, room.CodexSession)
            : new ClaudeClient(claudeOptions, room.ClaudeSession))
        {
            AutoExchange = settings.AutoExchange, AllowEdits = allowEdits, MaxAutoRounds = settings.MaxAutoRounds, TurnInactivitySeconds = settings.TurnInactivitySeconds, TaskMemory = taskMemory, TaskId = room.ActiveTaskId,
            Strategy = HubCoordinator.ParseStrategy(settings.Strategy), StrategyShadow = settings.ShadowStrategy ? ShadowAdvice : null, StrategyChooser = settings.EvaluationChooser ? ChooseStrategy : null,
            ProviderUnavailableUntil = agent => settings.ProviderUnavailableUntil.TryGetValue(agent.ToString(), out var until) && until > DateTimeOffset.Now ? until : null,
            CollaborationStore = collaborationStore,
            CollaborationBridgePath = Path.Combine(AppContext.BaseDirectory, "bridge", "AIHub.McpBridge.exe"),
            CollaborationWorkflowDirectory = Path.Combine(AppContext.BaseDirectory, "plugins", "ai-hub-collaboration"),
            MidTurnPush = settings.MidTurnPush, IsolateWorktrees = settings.IsolateAgentWorktrees,
            CollaborationFactory = (agent, connection) => agent == Agent.Codex
                ? new CodexClient(codexOptions with { Workspace = Cwd(agent), Collaboration = connection }, room.CodexSession)
                : new ClaudeClient(claudeOptions with { Workspace = Cwd(agent), Collaboration = connection, MidTurnPush = settings.MidTurnPush }, room.ClaudeSession),
            ContextResearchFactory = (agent, connection) => agent == Agent.Codex
                ? new CodexClient(codexOptions with { Workspace = Cwd(agent), AllowEdits = false, Collaboration = connection })
                : new ClaudeClient(claudeOptions with { Workspace = Cwd(agent), AllowEdits = false, Collaboration = connection }),
            PreparationFactory = agent => agent == Agent.Codex
                ? new CodexClient(codexOptions with { AllowEdits = false, PreparationOnly = true })
                : new ClaudeClient(claudeOptions with { AllowEdits = false, PreparationOnly = true })
        };
        self = coordinator;
        var worker = new RoomWorker(room, coordinator); workers.Add(room.Id, worker);
        activity = worker.Activity; streaming = worker.Streaming;
        hub = coordinator;
        coordinator.RequestApproval = async (approval, token) =>
        {
            audit.Running(room.Id, coordinator.TaskId, true); audit.Waiting(room.Id, true);
            try { return await AskAsync(approval, room, coordinator, token); }
            finally { audit.Waiting(room.Id, false); }
        };
        coordinator.Diagnostic += (code, agent) => audit.Record(code, room.Id, coordinator.TaskId, agent);
        coordinator.ProviderUnavailable += (agent, until, reason) => Dispatcher.BeginInvoke(() =>
        {
            settings.ProviderUnavailableUntil[agent.ToString()] = until; Save(); ReflectAvailability();
            AddActivity("Hub", ConversationTurns.Name(agent) + " unavailable until " + until.ToLocalTime().ToString("g"), reason);
        });
        bool IsCurrentHub() => ReferenceEquals(current, room) && ReferenceEquals(hub, coordinator) && !room.IsArchived;
        bool IsOwned() => workers.TryGetValue(room.Id, out var live) && ReferenceEquals(live, worker) && !room.IsArchived;
        coordinator.StructuredMessage += message => Dispatcher.BeginInvoke(() =>
        {
            if (!IsOwned()) return;
            if (message.Content.Type == "status") return; // Routine control receipts remain in task history, outside the conversation.
            var saved = new SavedMessage { Speaker = "AI Hub", Route = "Structured collaboration", TaskId = coordinator.TaskId,
                Text = CollaborationPresentation.Message(message), Collaboration = message, DispatchId = message.Envelope.DispatchId };
            room.Messages.Add(saved);
            if (IsCurrentHub()) { messages.Add(new(saved)); ShowConversation(); }
            Save();
        });
        coordinator.RestoreContext(Agent.Codex, room.CodexContext);
        coordinator.RestoreContext(Agent.Claude, room.ClaudeContext);
        coordinator.ReadConversation = async token => await Dispatcher.InvokeAsync<IReadOnlyList<ConversationEntry>>(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!IsOwned()) throw new OperationCanceledException(token);
            return room.Messages.Where(m => m.Complete && (coordinator.TaskId.Length == 0 || m.TaskId == coordinator.TaskId))
                .Select(m => new ConversationEntry(m.Id, m.Speaker, m.Text + (m.Input is { Options.Length: > 0 } input ?
                    "\nChoices: " + string.Join("; ", input.Options.Select(o => o.Label + " — " + o.Description)) : ""), m.Route, m.Time)).ToArray();
        }, DispatcherPriority.Normal, token);
        coordinator.ContextSynchronized += (agent, cursor) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsOwned()) return;
            if (agent == Agent.Codex) room.CodexContext = cursor; else room.ClaudeContext = cursor;
        });
        coordinator.Event += item =>
        {
            audit.Progress(room.Id, coordinator.TaskId, item.Agent, item.Kind);
            if (item.Kind == EventKind.Error) audit.Record(AuditCode.ProviderError, room.Id, coordinator.TaskId, item.Agent);
            Dispatcher.BeginInvoke(() => { if (IsOwned()) HandleWorkerEvent(worker, item); });
        };
        coordinator.State += state => Dispatcher.BeginInvoke(() =>
        {
            if (!IsOwned()) return;
            worker.State = state;
            if (IsCurrentHub()) UpdateState(state);
            RefreshTaskSummary();
        });
        coordinator.ProjectStatusNotice += text => Dispatcher.BeginInvoke(() =>
        {
            if (!IsOwned()) return;
            var saved = new SavedMessage { Speaker = "AI Hub", Route = "Project memory", Text = text };
            room.Messages.Add(saved);
            if (IsCurrentHub()) { messages.Add(new(saved)); ShowConversation(); if (followChat) ChatScroll.ScrollToEnd(); }
            worker.Activity.AddNotice("AI Hub", text.Split('\n')[0].Replace("**", ""), text);
            if (IsCurrentHub()) RefreshActivity();
            try { store.AppendHandoff(room.Id, "AI Hub", "Project memory", text); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StateLabel.Text = ex.Message; }
            Save();
        });
        coordinator.AutoPaused += reason => Dispatcher.BeginInvoke(() =>
        {
            if (!IsOwned()) return;
            var saved = new SavedMessage { Speaker = "AI Hub", Route = "Collaboration guard", TaskId = coordinator.TaskId, Text = "**Collaboration paused**\n\n" + reason + "\n\nSend a task, answer the agents, or ask them to continue when you are ready." };
            room.Messages.Add(saved); room.PauseReason = reason;
            if (IsCurrentHub()) { messages.Add(new(saved)); SetPause(reason); }
            worker.Activity.AddNotice("AI Hub", "Collaboration paused", reason);
            if (IsCurrentHub()) RefreshActivity();
            try { store.AppendHandoff(room.Id, "AI Hub", "You", saved.Text); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StateLabel.Text = "Activity log: " + ex.Message; }
            Save();
        });
        coordinator.Dispatch += (from, to, text) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsOwned()) return;
            if (Enum.TryParse<Agent>(to, out var recipient)) worker.Activity.BeginTurn(recipient);
            worker.Activity.AddNotice("Hub", from + " → " + to, text);
            if (IsCurrentHub()) RefreshActivity();
            if (IsCurrentHub() && from is "Codex" or "Claude")
            {
                HandoffLink.Text = from == "Codex" ? "Codex → Claude" : "Claude → Codex";
                HandoffSignal.Send(to == "Claude");
            }
            else if (IsCurrentHub()) HandoffLink.Text = ConversationTurns.Speaker(to) is { } nextSpeaker ? ConversationTurns.Name(nextSpeaker) + " is responding" : "Your direction";
            try { store.AppendHandoff(room.Id, from, to, text); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StateLabel.Text = ex.Message; }
        });
        UpdateWorkspace();
    }
    private void UpdateWorkspace()
    {
        WorkspaceLabel.Text = Path.GetFileName(current.Workspace.TrimEnd(Path.DirectorySeparatorChar)); WorkspaceLabel.ToolTip = current.Workspace;
        ModeLabel.Text = current.IsAuditReview ? "Audit review · read only" : current.EffectiveAllowEdits(settings) ? "Edits enabled" : "Read only";
        ProjectStatusButton.ToolTip = $"{settings.StatusInspector} inspects; the other agent reviews when Both agents is selected. Reuses a matching report. This task is read only.";
        RefreshTaskSummary();
    }
    private bool OwnsWorker(Room room, HubCoordinator owner) => workers.TryGetValue(room.Id, out var worker) && ReferenceEquals(worker.Hub, owner) && !room.IsArchived;
    private void HandleWorkerEvent(RoomWorker worker, AgentEvent item)
    {
        dirty = true;
        if (item.Kind == EventKind.Status) worker.AgentStates[item.Agent] = item;
        if (ReferenceEquals(current, worker.Room) && ReferenceEquals(hub, worker.Hub)) { Handle(item); return; }
        if (item.Kind is EventKind.TextDelta or EventKind.Message)
        {
            TouchRoom(worker.Room);
            var key = item.Agent + "|" + item.ItemId;
            if (!worker.Streaming.TryGetValue(key, out var view))
            {
                var saved = new SavedMessage { Speaker = item.Agent.ToString(), Route = "Shared room", TaskId = worker.Hub.TaskId, Complete = false, DispatchId = item.ItemId.Length > 0 ? item.ItemId : null };
                worker.Room.Messages.Add(saved); view = new(saved); worker.Streaming[key] = view;
            }
            view.Text = item.Kind == EventKind.TextDelta ? view.Text + item.Text : item.Text;
            view.Saved.Complete = item.Kind == EventKind.Message;
            if (item.Kind == EventKind.Message) { worker.Streaming.Remove(key); Save(); } // A finished message no longer needs its live view.
        }
        else if (item.Kind == EventKind.Session)
        { if (item.Agent == Agent.Codex) worker.Room.CodexSession = item.Text; else worker.Room.ClaudeSession = item.Text; Save(); }
        else
        {
            worker.Activity.Record(item);
            try { store.AppendActivity(worker.Room.Id, item); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StateLabel.Text = "Background activity log: " + ex.Message; }
        }
    }
    private void Handle(AgentEvent item)
    {
        dirty = true;
        if (item.Kind is EventKind.TextDelta or EventKind.Message)
        {
            TouchRoom(current);
            var key = item.Agent + "|" + item.ItemId;
            if (!streaming.TryGetValue(key, out var view))
            {
                var saved = new SavedMessage { Speaker = item.Agent.ToString(), Route = "Shared room", TaskId = hub?.TaskId ?? "", Complete = false, DispatchId = item.ItemId.Length > 0 ? item.ItemId : null };
                current.Messages.Add(saved); view = new(saved); streaming[key] = view; messages.Add(view);
            }
            view.Text = item.Kind == EventKind.TextDelta ? view.Text + item.Text : item.Text;
            view.Saved.Complete = item.Kind == EventKind.Message;
            ShowConversation();
            if (item.Kind == EventKind.TextDelta) SetAgentState(item.Agent, "Responding", true);
            if (followChat) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ChatScroll.ScrollToEnd());
            if (item.Kind == EventKind.Message) { streaming.Remove(key); Save(); } // A finished message no longer needs its live view.
            return;
        }
        if (item.Kind == EventKind.Session)
        { if (item.Agent == Agent.Codex) current.CodexSession = item.Text; else current.ClaudeSession = item.Text; Save(); return; }
        if (item.Kind == EventKind.Status) ReflectStatus(item);
        var action = activity.Record(item);
        if (item.Kind == EventKind.Tool && action is not null)
        {
            SetAgentState(item.Agent, action.IsRunning ? "Using a tool" : "Reviewing results", true);
            var detail = item.Agent == Agent.Codex ? CodexDetail : ClaudeDetail;
            detail.Text = action.Title;
            detail.ToolTip = action.Title;
        }
        RefreshActivity();
        if (item.Kind == EventKind.Error) { StateLabel.Text = item.Text; SetAgentState(item.Agent, "Needs attention", false); }
        try { store.AppendActivity(current.Id, item); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StateLabel.Text = "Activity log: " + ex.Message; }
    }
    // A provider over its limit shows as unavailable with the reset time; the entry clears itself once that time has passed.
    private void ReflectAvailability()
    {
        var now = DateTimeOffset.Now; var changed = false;
        foreach (var agent in Enum.GetValues<Agent>())
        {
            var key = agent.ToString(); var label = agent == Agent.Codex ? CodexStatus : ClaudeStatus; var detail = agent == Agent.Codex ? CodexDetail : ClaudeDetail;
            if (settings.ProviderUnavailableUntil.TryGetValue(key, out var until) && until > now)
            {
                if (!(agent == Agent.Codex ? codexWorking : claudeWorking)) SetAgentState(agent, "Unavailable", false);
                var remaining = until - now; var local = until.ToLocalTime();
                detail.Text = "Until " + (local.Date == now.Date ? local.ToString("t") : local.ToString("ddd d MMM, t")) + (remaining.TotalHours >= 1 ? $" · {(int)remaining.TotalHours} h {remaining.Minutes} m left" : $" · {Math.Max(1, (int)remaining.TotalMinutes)} m left");
                detail.ToolTip = detail.Text;
            }
            else if (settings.ProviderUnavailableUntil.Remove(key)) { changed = true; if (label.Text == "Unavailable") SetAgentState(agent, "Standby", false); }
        }
        if (changed) Save();
    }
    // Status to agent-card mapping, shared by live events and the replay when a room is reopened; the replay must not log again.
    private void ReflectStatus(AgentEvent item)
    {
        if (item.Text == "Working") SetAgentState(item.Agent, "Thinking", true);
        else if (item.Text == "Listening") SetAgentState(item.Agent, "Listening", false);
        else if (item.Text is "Preparing contribution" or "Prepared; waiting to speak") SetAgentState(item.Agent, item.Text, item.Text == "Preparing contribution");
        else if (item.Text == "Standby") SetAgentState(item.Agent, "Standby", false);
        else if (item.Text == "Ready") SetAgentState(item.Agent, "Ready", false);
        else if (item.Text is "Gathering context" or "Context saved" or "Reviewed; nothing to add") SetAgentState(item.Agent, item.Text, item.Text == "Gathering context");
        else if (item.Text is "Inspecting project" or "Reviewing findings" or "Waiting for inspection") SetAgentState(item.Agent, item.Text, item.Text != "Waiting for inspection");
        else if (item.Text.StartsWith("Connected to ") && !(item.Agent == Agent.Codex ? codexWorking : claudeWorking)) SetAgentState(item.Agent, "Connected", false);
    }
    private void AddActivity(string agent, string title, string detail)
    {
        activity.AddNotice(agent, title, detail); RefreshActivity();
    }
    private void RefreshActivity()
    {
        var showDiagnostics = DiagnosticsToggle.IsChecked == true;
        var entries = showDiagnostics ? activity.Diagnostics : activity.Actions;
        if (!ReferenceEquals(ActivityList.ItemsSource, entries)) ActivityList.ItemsSource = entries;
        ActivityEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityCount.Text = entries.Count.ToString();
        ActivityScopeLabel.Text = showDiagnostics ? "Recent diagnostic events" : "Recent actions";
    }
    private void DiagnosticsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings.ShowActivityDiagnostics = DiagnosticsToggle.IsChecked == true;
        RefreshActivity(); Save();
    }
    private void OpenActivityLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = store.CreateActivitySnapshot(current.Id);
            if (path is null) { StateLabel.Text = "No activity has been recorded in this conversation yet"; return; }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(path); Process.Start(start);
            StateLabel.Text = "Opened an activity log copy; open again for the latest events";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { StateLabel.Text = "Could not open activity log: " + ex.Message; }
    }
    private async Task SendAsync(string? explicitPrompt = null, bool refreshStatus = false)
    {
        if (explicitPrompt is null && activeQuestion is { } question) { SubmitQuestion(question); return; }
        if (switching || sending || closing || current.IsArchived || string.IsNullOrWhiteSpace(explicitPrompt ?? Composer.Text)) return;
        // Sending to a single recipient that was marked over its limit is the user's word that it is back: the mark is cleared and
        // the message goes through. A fresh limit reply re-marks it with the new reset time.
        if (Target.SelectedIndex is 1 or 2 && settings.ProviderUnavailableUntil.Remove(Target.SelectedIndex == 1 ? "Codex" : "Claude", out var unavailableUntil) && unavailableUntil > DateTimeOffset.Now)
        {
            var name = Target.SelectedIndex == 1 ? "Codex" : "Claude Code";
            AddActivity("Hub", name + " marked available again", $"It was marked unavailable until {unavailableUntil.ToLocalTime():g}; sending to it directly cleared the mark.");
            Save(); ReflectAvailability();
        }
        sending = true; UpdateConversationControls(); var prompt = (explicitPrompt ?? Composer.Text).Trim();
        if (explicitPrompt is null) Composer.Clear();
        try
        {
            // The progress answer reads the task ledger, so it runs off the UI thread.
            var owner = hub!;
            var (hasProgress, progress) = await Task.Run(() => { var found = owner.TryGetProgress(prompt, out var report); return (found, report); });
            if (hasProgress)
            {
                var progressQuestion = new SavedMessage { Text = prompt, Route = "Task progress", TaskId = current.ActiveTaskId };
                var answer = new SavedMessage { Speaker = "AI Hub", Text = progress, Route = "Task progress", TaskId = current.ActiveTaskId };
                current.Messages.Add(progressQuestion); current.Messages.Add(answer); messages.Add(new(progressQuestion)); messages.Add(new(answer));
                ShowConversation(); Save(); return;
            }
            // A message during a running phase joins the live stream; Stop all remains the way to interrupt work.
            if (!ProjectStatusWorkflow.IsStatusRequest(prompt) && current.ActiveTaskId.Length > 0 && hub!.TaskId == current.ActiveTaskId && await hub.InterjectAsync(prompt))
            {
                var aside = new SavedMessage { Text = prompt, Route = Target.SelectedIndex == 0 ? "Both" : Target.SelectedIndex == 1 ? "Codex" : "Claude", TaskId = current.ActiveTaskId };
                current.Messages.Add(aside); messages.Add(new(aside));
                AddActivity("Hub", "Message joined the live stream", "Both agents see it at their next opportunity; the running turn was not interrupted. Use Stop all to interrupt work.");
                followChat = true; ShowConversation(); ChatScroll.ScrollToEnd(); Save(); RefreshTaskSummary(); return;
            }
            await hub!.StopAsync(); MarkInterrupted();
            SetPause("");
            if (!CollaborationGuard.IsSocialOnly(prompt)) current.LastTask = prompt;
            var isStatus = ProjectStatusWorkflow.IsStatusRequest(prompt);
            var target = Target.SelectedIndex == 0 ? "Both" : Target.SelectedIndex == 1 ? "Codex" : "Claude";
            var saved = new SavedMessage { Text = prompt, Route = target }; current.Messages.Add(saved); messages.Add(new(saved));
            if (current.Title == "New conversation") { current.Title = prompt.Length > 30 ? prompt[..30] + "…" : prompt; RoomList.Items.Refresh(); }
            RoomTitle.Text = current.Title; followChat = true; ShowConversation(); ChatScroll.ScrollToEnd(); Save();
            if (isStatus)
            {
                hub.TaskId = "";
                var workspace = current.Workspace;
                var codexOptions = new AgentOptions(workspace, false, settings.CodexModel, settings.CodexPath);
                var claudeOptions = new AgentOptions(workspace, false, settings.ClaudeModel, settings.ClaudePath);
                var config = JsonSerializer.Serialize(new { settings.CodexModel, settings.ClaudeModel, settings.CodexPath, settings.ClaudePath });
                await hub.SubmitProjectStatusAsync(new(workspace, current.Id, target, settings.StatusInspector, config, refreshStatus), statusStore,
                    agent => agent == Agent.Codex ? new CodexClient(codexOptions) : new ClaudeClient(claudeOptions));
            }
            else
            {
                var task = taskMemory.Get(current.ActiveTaskId);
                if (task is null || task.RoomId != current.Id || !string.Equals(task.Workspace, Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.Workspace)), StringComparison.OrdinalIgnoreCase))
                {
                    current.ActiveTaskId = taskMemory.Create(current.Id, current.Workspace, prompt.Length <= 32000 ? prompt : "Review the long user message saved in this conversation. Retrieve its full shared source before answering.");
                    foreach (var legacy in current.Messages.Where(m => m.TaskId.Length == 0)) legacy.TaskId = current.ActiveTaskId;
                    TouchRoom(current);
                    await DisposeCurrentHubAsync(); BuildHub(); Save();
                }
                hub!.TaskId = current.ActiveTaskId;
                saved.TaskId = current.ActiveTaskId;
                Save(); await hub!.SubmitAsync(prompt, target);
                RefreshTaskSummary();
            }
        }
        catch (Exception ex) { AddActivity("Hub", "Could not send", ex.ToString()); StateLabel.Text = ex.Message; }
        finally { sending = false; UpdateConversationControls(); Composer.Focus(); }
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();
    private async void ProjectStatus_Click(object sender, RoutedEventArgs e) => await SendAsync("Check project status");
    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) => await SendAsync("Check project status", true);
    private void StatusActions_Click(object sender, RoutedEventArgs e)
    { StatusActionsButton.ContextMenu.PlacementTarget = StatusActionsButton; StatusActionsButton.ContextMenu.IsOpen = true; }
    private async void ForgetStatus_Click(object sender, RoutedEventArgs e)
    {
        if (switching || sending || closing || current.IsArchived) return;
        switching = true; UpdateConversationControls();
        try
        {
            await DisposeCurrentHubAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await statusStore.ForgetAsync(current.Workspace, null, timeout.Token);
            StateLabel.Text = "Saved project status cleared · existing conversation messages are kept";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { StateLabel.Text = "Could not clear saved status: " + (ex is OperationCanceledException ? "Another status check owns this project. Stop it and retry." : ex.Message); }
        finally { BuildHub(); FinishConversationSwitch(); }
    }
    private async void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        // Preview handles Enter before TextBox's multiline editing command consumes it.
        e.Handled = true;
        if (!e.IsRepeat) await SendAsync();
    }
    private void MarkInterrupted()
    { CancelPendingInputs(); foreach (var view in messages.Where(m => !m.Saved.Complete)) if (!view.Text.EndsWith("[Stopped]")) { view.Text += "\n\n[Stopped]"; TouchRoom(current); } }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    { if (switching || sending || closing) return; await StopAllWorkersAsync(); }
    private async Task StopAllWorkersAsync()
    {
        var owned = workers.Values.ToArray();
        await Task.WhenAll(owned.Select(worker => worker.Hub.StopAsync()));
        CancelPendingInputs(allRooms: true);
        foreach (var worker in owned)
        {
            worker.Room.PauseReason = "You stopped the agents. Completed file changes are kept.";
            foreach (var view in worker.Streaming.Values.Where(v => !v.Saved.Complete))
                if (!view.Text.EndsWith("[Stopped]")) { view.Text += "\n\n[Stopped]"; TouchRoom(worker.Room); }
        }
        if (!current.IsArchived)
        { MarkInterrupted(); SetAgentState(Agent.Codex, "Stopped", false); SetAgentState(Agent.Claude, "Stopped", false); SetPause("You stopped the agents. Completed file changes are kept."); }
        RefreshTaskSummary(); Save();
    }
    private async void NewRoom_Click(object sender, RoutedEventArgs e)
    {
        if (switching || sending || closing) return;
        await CreateRoomAsync();
    }
    private void RoomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || switching || filteringRooms || RoomList.SelectedItem is not Room selected || (ReferenceEquals(current,selected) && (hub is not null || current.IsArchived))) return;
        switching = true; UpdateConversationControls();
        try
        {
            DetachSelectedView();
            ActivateRoom(selected); Save();
        }
        finally { FinishConversationSwitch(); }
    }
    private async Task DisposeCurrentHubAsync()
    {
        var outgoing = hub;
        hub = null; // Queued callbacks cannot recreate a removed room's messages or activity log.
        workers.Remove(current.Id); audit.Forget(current.Id);
        if (outgoing is not null) await outgoing.DisposeAsync();
        MarkInterrupted();
    }
    private void DetachSelectedView()
    {
        if (activeQuestion is not null) activeQuestion.Activate(false);
        activeQuestion = null; hub = null;
    }
    private async Task DisposeAllWorkersAsync()
    {
        var owned = workers.Values.ToArray(); workers.Clear(); hub = null;
        foreach (var worker in owned) audit.Forget(worker.Room.Id);
        await Task.WhenAll(owned.Select(async worker => await worker.Hub.DisposeAsync()));
        CancelPendingInputs(allRooms: true);
        foreach (var room in rooms)
            foreach (var message in room.Messages.Where(m => !m.Complete))
                if (!message.Text.EndsWith("[Stopped]")) { message.Text += "\n\n[Stopped]"; TouchRoom(room); }
    }
    private void ActivateRoom(Room selected)
    {
        current = selected; settings.LastRoomId = current.Id;
        if (string.IsNullOrWhiteSpace(current.Workspace)) current.Workspace = settings.Workspace;
        loadingComposer = true;
        Composer.Text = current.Draft;
        Target.SelectedIndex = current.Target == "Codex" ? 1 : current.Target == "Claude" ? 2 : 0;
        loadingComposer = false;
        followChat = true;
        HandoffSignal.Stop(); HandoffLink.Text = "Shared workspace"; RoundLabel.Text = current.IsArchived ? "Archived" : "Ready";
        messages.Clear(); ActivityEmpty.Visibility = Visibility.Visible; ActivityCount.Text = "0";
        RoomTitle.Text = current.Title;
        if (!current.IsArchived) BuildHub(); else { hub = null; activity = new(); streaming = []; UpdateWorkspace(); }
        foreach (var saved in current.Messages)
            messages.Add(pendingInputs.Values.FirstOrDefault(p => ReferenceEquals(p.Room, current) && ReferenceEquals(p.Message.Saved, saved))?.Message
                ?? streaming.Values.FirstOrDefault(v => ReferenceEquals(v.Saved, saved)) ?? new(saved));
        ApplyFeedbackLabels();
        RefreshActivity();
        Welcome.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatScroll.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StateLabel.Text = current.IsArchived ? "Archived · restore this conversation to continue" : current.PauseReason.Length > 0 ? "Paused · send a message to resume" : "Ready · send a message to begin";
        SetAgentState(Agent.Codex, "Standby", false); SetAgentState(Agent.Claude, "Standby", false); SetPause(current.PauseReason); ReflectAvailability();
        if (workers.TryGetValue(current.Id, out var worker))
        {
            if (worker.State != "Ready") UpdateState(worker.State);
            foreach (var state in worker.AgentStates.Values) ReflectStatus(state); // Into the view only: the feed and log already have these.
        }
        var question = pendingInputs.Values.FirstOrDefault(p => ReferenceEquals(p.Room, current) && p.View.IsQuestion);
        if (question is not null) ActivateQuestion(question.View); else RefreshInputComposer();
        foreach (var pending in pendingInputs.Values.Where(p => ReferenceEquals(p.Room, current)))
            SetAgentState(pending.Approval.Agent, "Needs your input", true);
        UpdateConversationControls(); Motion.Enter(ConversationSurface);
    }
    private async Task CreateRoomAsync()
    {
        switching = true; UpdateConversationControls();
        try
        {
            DetachSelectedView();
            var room = new Room { Workspace = settings.Workspace };
            rooms.Insert(0, room); SelectActiveRoom(room); Save();
        }
        finally { FinishConversationSwitch(); }
    }
    private void FinishConversationSwitch()
    {
        switching = false; UpdateConversationControls();
        if (closeAfterSwitch) { closeAfterSwitch = false; Close(); }
    }
    private void SelectActiveRoom(Room? room = null)
    {
        room ??= rooms.FirstOrDefault(r => !r.IsArchived);
        if (room is null) { room = new Room { Workspace = settings.Workspace }; rooms.Insert(0, room); }
        SearchBox.Clear(); RoomFilter.SelectedIndex = 0; RefreshRoomFilter();
        RoomList.SelectedItem = room; ActivateRoom(room);
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (switching || sending || closing) return;
        var picker = new OpenFolderDialog { Title = "Choose the project both agents will work with" };
        if (picker.ShowDialog(this) != true) return;
        settings.Workspace = picker.FolderName;
        await CreateRoomAsync();
    }
    private void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings.AutoExchange = AutoToggle.IsChecked == true;
        foreach (var worker in workers.Values) worker.Hub.AutoExchange = settings.AutoExchange;
        if (!settings.AutoExchange) StateLabel.Text = "Automatic handoffs off · current replies may finish"; Save();
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (switching || sending || closing) return;
        var window = new SettingsWindow(settings) { Owner = this };
        if (window.ShowDialog() != true) return;
        var updated = window.Settings;
        var connectionsChanged = settings.CodexPath != updated.CodexPath || settings.ClaudePath != updated.ClaudePath || settings.CodexModel != updated.CodexModel || settings.ClaudeModel != updated.ClaudeModel || settings.AllowEdits != updated.AllowEdits || settings.MidTurnPush != updated.MidTurnPush || settings.IsolateAgentWorktrees != updated.IsolateAgentWorktrees;
        switching = true; UpdateConversationControls();
        try
        {
            if (connectionsChanged) await DisposeAllWorkersAsync();
            settings = updated; Motion.Configure(settings.ReduceMotion);
            audit.Enabled = settings.CollectLocalDiagnostics;
            foreach (var worker in workers.Values)
            { worker.Hub.MaxAutoRounds = settings.MaxAutoRounds; worker.Hub.AutoExchange = settings.AutoExchange; worker.Hub.TurnInactivitySeconds = settings.TurnInactivitySeconds; worker.Hub.MidTurnPush = settings.MidTurnPush; worker.Hub.IsolateWorktrees = settings.IsolateAgentWorktrees; worker.Hub.Strategy = HubCoordinator.ParseStrategy(settings.Strategy); worker.Hub.StrategyShadow = settings.ShadowStrategy ? ShadowAdvice : null; worker.Hub.StrategyChooser = settings.EvaluationChooser ? ChooseStrategy : null; }
            RefreshMotion();
            if (connectionsChanged && !current.IsArchived) BuildHub();
            UpdateWorkspace();
            Save(); AddActivity("Hub", "Settings updated", "Your connection and appearance preferences have been saved.");
        }
        finally { FinishConversationSwitch(); }
    }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not MessageView message) return;
        try { Clipboard.SetText(message.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { StateLabel.Text = "Clipboard is busy · try copying again"; return; }
        var icon = button.Content as Controls.HubIcon;
        if (icon is not null) icon.Kind = "check";
        button.ToolTip = "Copied";
        await Task.Delay(1200);
        if (icon is not null) icon.Kind = "copy";
        button.ToolTip = "Copy message";
    }
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        Composer.Text = (sender as Button)?.Tag?.ToString() == "review"
            ? "Review the selected project together. Compare your findings, identify the biggest issues, and propose fixes."
            : "Help me plan a new project together. Start by asking me what I want to build."; Composer.Focus();
    }
    private void ActivityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivityList.SelectedItem is not ActivityEntry item) return;
        var window = new Window { Owner = this, Title = item.Agent + " — " + item.Title, Width = 760, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var details = new TextBox { Name = "ActivityDetailsText", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(15) };
        details.SetBinding(TextBox.TextProperty, new Binding(nameof(ActivityEntry.Detail)) { Source = item, Mode = BindingMode.OneWay });
        window.Content = details;
        window.Show(); ActivityList.SelectedItem = null;
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (switching || closing) return;
        var picker = new SaveFileDialog { Filter = "Markdown|*.md", FileName = "AI Hub conversation.md" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var history = string.Join("\n\n", taskMemory.ForWorkspace(current.Workspace).Where(t => t.RoomId == current.Id)
                .Select(t => CollaborationPresentation.History(collaborationStore.Read(t.Id)) + "\n" + collaborationStore.ContextStateReport(t.Id)));
            File.WriteAllText(picker.FileName, "# " + current.Title + "\n\n" + string.Join("\n\n---\n\n", current.Messages.Select(m => $"## {m.Speaker} · {m.Time:g}\n\n{m.Text}")) + FeedbackExportSection(current) + history);
            StateLabel.Text = "Conversation exported";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StateLabel.Text = "Export failed: " + ex.Message; }
    }
    private void ShowConversation()
    {
        if (ChatScroll.Visibility == Visibility.Visible) return;
        Welcome.Visibility = Visibility.Collapsed; ChatScroll.Visibility = Visibility.Visible;
        Motion.Enter(ChatScroll);
    }
    private void SetAgentState(Agent agent, string status, bool working)
    {
        if (working && pendingInputs.Values.Any(p => p.Approval.Agent == agent)) status = "Needs your input";
        var previous = agent == Agent.Codex ? codexWorking : claudeWorking;
        if (agent == Agent.Codex) { codexWorking = working; CodexStatus.Text = status; }
        else { claudeWorking = working; ClaudeStatus.Text = status; }
        var label = agent == Agent.Codex ? CodexStatus : ClaudeStatus;
        label.Foreground = Theme.Brush(working ? agent == Agent.Codex ? "CyanBrush" : "ClaudeBrush" : status.Contains("input") || status == "Needs attention" ? "ClaudeBrush" : "MutedBrush");
        var detail = agent == Agent.Codex ? CodexDetail : ClaudeDetail;
        detail.Text = status switch
        {
            "Thinking" => "Working on your task",
            "Listening" => "Waiting for the current speaker",
            "Responding" => "Writing a response",
            "Ready" => "Response delivered",
            "Connected" => "Connection established",
            "Paused" => "Waiting for your direction",
            "Stopped" => "Stopped by you",
            "Needs attention" => "Check the activity details",
            "Needs your input" => "Answer in the chat",
            "Standby" => "Ready for your direction",
            "Unavailable" => "Provider limit reached",
            "Inspecting project" => "Owns the project status inspection",
            "Reviewing findings" => "Checking the inspector's evidence",
            "Waiting for inspection" => "Review starts when findings arrive",
            _ => detail.Text
        };
        detail.ToolTip = detail.Text;
        (agent == Agent.Codex ? CodexCard : ClaudeCard).Background = working ? Theme.Brush(agent == Agent.Codex ? "CodexSurfaceBrush" : "ClaudeSurfaceBrush") : Brushes.Transparent;
        var awaitingInput = status == "Needs your input";
        if (previous != working || !working || awaitingInput || status == "Thinking") RefreshMotion();
    }
    private void UpdateState(string state)
    {
        StateLabel.Text = state;
        if (state == "Working") RoundLabel.Text = "Taking turns";
        else if (state.StartsWith("Automatic exchange")) RoundLabel.Text = $"Round {hub?.ExchangeCount ?? 0} / {settings.MaxAutoRounds}";
        else if (state == "Stopped") { RoundLabel.Text = "Stopped"; HandoffSignal.Stop(); }
        else if (state.StartsWith("Paused")) { RoundLabel.Text = "Paused"; HandoffSignal.Stop(); }
        else if (state == "Ready") RoundLabel.Text = current.PauseReason.Length > 0 ? "Paused" : "Ready";
        else if (state.StartsWith("Project status")) { RoundLabel.Text = "Project status"; HandoffLink.Text = "Shared project memory"; }
        Motion.Pulse(SystemPulse, WindowState != WindowState.Minimized && (state is "Working" || state.StartsWith("Automatic exchange")));
        if (state == "Stopped" || state.StartsWith("Paused"))
        {
            activity.FinishOpen(null, "Stopped");
            var label = state == "Stopped" ? "Stopped" : "Paused";
            SetAgentState(Agent.Codex, label, false); SetAgentState(Agent.Claude, label, false);
        }
    }
    private void RefreshMotion()
    {
        var visible = WindowState != WindowState.Minimized;
        var activeCodex = codexWorking && CodexStatus.Text != "Needs your input";
        var activeClaude = claudeWorking && ClaudeStatus.Text != "Needs your input";
        Motion.Pulse(CodexPulse, activeCodex && visible); Motion.Pulse(ClaudePulse, activeClaude && visible);
        Motion.Pulse(SystemPulse, (activeCodex || activeClaude) && visible);
    }
    private void Composer_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ComposerHint is not null) ComposerHint.Visibility = string.IsNullOrEmpty(Composer.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (ready && !loadingComposer)
        {
            if (activeQuestion is { } question) question.SetAnswerText(Composer.Text);
            else { current.Draft = Composer.Text; dirty = true; }
        }
        UpdateSendButton();
    }
    private void UpdateSendButton()
    { if (SendButton is not null) SendButton.IsEnabled = ready && !switching && !sending && !closing && !current.IsArchived && (activeQuestion?.CanSend ?? !string.IsNullOrWhiteSpace(Composer.Text)); }
    private void UpdateConversationControls()
    {
        var available = ready && !switching && !sending && !closing;
        RoomList.IsEnabled = available; RoomFilter.IsEnabled = available;
        NewRoomButton.IsEnabled = available; ChooseFolderButton.IsEnabled = available;
        RenameButton.IsEnabled = available; ArchiveButton.IsEnabled = available;
        SettingsButton.IsEnabled = available; ExportButton.IsEnabled = available;
        DeleteConversationButton.IsEnabled = available; RestoreConversationButton.IsEnabled = available;
        ArchiveButton.Content = current.IsArchived ? "Restore conversation" : "Archive conversation";
        ArchiveButton.ToolTip = current.IsArchived ? "Move this conversation back to the active list" : "Keep this conversation in the archive";
        ArchivedBanner.Visibility = current.IsArchived ? Visibility.Visible : Visibility.Collapsed;
        Composer.IsReadOnly = current.IsArchived; Target.IsEnabled = available && !current.IsArchived;
        ImportFilesButton.IsEnabled = available && !current.IsArchived && activeQuestion is null;
        AutoToggle.IsEnabled = available && !current.IsArchived; StopButton.IsEnabled = available;
        TasksButton.IsEnabled = available;
        ProjectStatusButton.IsEnabled = available && !current.IsArchived; StatusActionsButton.IsEnabled = available && !current.IsArchived;
        Welcome.IsEnabled = !current.IsArchived;
        RefreshInputComposer();
    }
    private void Target_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (ready && !loadingComposer) { current.Target = Target.SelectedIndex == 1 ? "Codex" : Target.SelectedIndex == 2 ? "Claude" : "Both"; dirty = true; } }
    private void SetPause(string reason)
    {
        current.PauseReason = reason; PauseReasonLabel.Text = reason; dirty = true;
        if (reason.Length > 0 && !current.IsArchived) { RoundLabel.Text = "Paused"; HandoffSignal.Stop(); }
        PauseBanner.Visibility = reason.Length > 0 && !current.IsArchived ? Visibility.Visible : Visibility.Collapsed;
        var resumable = current.LastTask.Length > 0 && (reason.Contains("round", StringComparison.OrdinalIgnoreCase) || reason.Contains("repeated", StringComparison.OrdinalIgnoreCase) || reason.StartsWith("You stopped") || reason.StartsWith("Instructions changed") || reason.Contains("Task context storage"));
        ContinueButton.Visibility = resumable ? Visibility.Visible : Visibility.Collapsed;
        ComposerHint.Text = current.IsArchived ? "Restore this conversation to send a message" : reason.Length > 0 ? "Tell your agents what to do next…" : "Give your agents a direction…";
        RefreshInputComposer();
    }
    private void FocusMessage_Click(object sender, RoutedEventArgs e) => Composer.Focus();
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null) SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshRoomFilter();
    }
    private void RoomFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRoomFilter();
    private void RefreshRoomFilter()
    {
        if (roomView is null) return;
        filteringRooms = true;
        try { roomView.Refresh(); if (roomView.Contains(current)) RoomList.SelectedItem = current; }
        finally { filteringRooms = false; }
        SearchEmpty.Text = SearchBox.Text.Length > 0 ? "No matching conversations" : RoomFilter.SelectedIndex == 1 ? "No archived conversations" : "No active conversations";
        SearchEmpty.Visibility = roomView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (sending || switching || closing) return;
        var room = current;
        var wasArchived = room.IsArchived;
        switching = true; UpdateConversationControls();
        try
        {
            await DisposeCurrentHubAsync();
            room.IsArchived = !wasArchived;
            try { store.SaveRooms(rooms, dirtyRooms); dirtyRooms.Clear(); }
            catch { room.IsArchived = wasArchived; throw; }
            SelectActiveRoom(wasArchived ? room : null); Save();
            StateLabel.Text = wasArchived ? "Conversation restored · send a message when you are ready" : "Conversation archived · find it in Archived conversations";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivateRoom(room);
            StateLabel.Text = "Could not " + (wasArchived ? "restore" : "archive") + " conversation: " + ex.Message;
        }
        finally { FinishConversationSwitch(); }
    }
    private async void DeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sending || switching || closing) return;
        var room = current;
        var dialog = new Window { Owner = this, Title = "Delete conversation?", Width = 510, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Delete this conversation?", FontFamily = Theme.Font("DisplayFont"), FontSize = 20, Margin = new(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text = room.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, Margin = new(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text = "Permanently remove this conversation's local AI Hub transcript, unsent draft, activity log, tasks and task notes. The latest shared project status is also cleared if this conversation created it. This cannot be undone.\n\nAny agents running in this conversation will stop. Project files, other conversations (including any copied reports), and native Codex / Claude account history are kept.", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedBrush"), LineHeight = 22 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,22,0,0) };
        var cancel = new Button { Name = "CancelDeleteConversationButton", Content = "Keep conversation", IsCancel = true, IsDefault = true, Margin = new(0,0,10,0) };
        var delete = new Button { Name = "ConfirmDeleteConversationButton", Content = "Delete permanently", Foreground = Theme.Brush("DangerBrush") };
        delete.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(delete); panel.Children.Add(buttons); dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        switching = true; UpdateConversationControls();
        try
        {
            await DisposeCurrentHubAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await statusStore.ForgetAsync(room.Workspace, room.Id, timeout.Token);
            collaborationStore.DeleteRoom(room.Id, () => store.DeleteRoom(rooms.ToList(), room.Id));
            rooms.Remove(room);
            try { foreach (var removed in feedback.DeleteRoom(room.Id)) preferences.FeedbackDeleted(removed); } // The conversation is gone; its feedback goes with it, and preferences that rested only on it are disabled.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { AddActivity("Hub", "Feedback cleanup", "Feedback for the deleted conversation could not be fully removed: " + ex.Message); }
            SelectActiveRoom(); Save();
            StateLabel.Text = "Conversation deleted · project files and provider history were kept";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            ActivateRoom(room);
            StateLabel.Text = "Could not delete conversation: " + ex.Message;
        }
        finally { FinishConversationSwitch(); }
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sending || switching || closing) return;
        var room = current;
        var window = new Window { Owner = this, Title = "Rename conversation", Width = 460, Height = 210, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = "Conversation name", Margin = new(0,0,0,10) });
        var input = new TextBox { Name = "ConversationNameInput", Text = room.Title, MaxLength = 120 }; panel.Children.Add(input);
        var save = new Button { Name = "SaveConversationNameButton", Content = "Save name", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,14,0,0) }; panel.Children.Add(save);
        save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; } room.Title = input.Text.Trim(); window.DialogResult = true; };
        window.Content = panel; window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (window.ShowDialog() == true) { SearchBox.Clear(); RoomList.Items.Refresh(); RoomTitle.Text = current.Title; Save(); }
    }
    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) followChat = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 45;
        if (followChat && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)) ChatScroll.ScrollToEnd();
        if (LatestButton is not null) LatestButton.Visibility = !followChat && ChatScroll.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Latest_Click(object sender, RoutedEventArgs e) { followChat = true; ChatScroll.ScrollToEnd(); LatestButton.Visibility = Visibility.Collapsed; }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || switching || closing) return;
        if (e.Key == Key.Escape && !Target.IsDropDownOpen && (codexWorking || claudeWorking))
        { e.Handled = true; await StopAllWorkersAsync(); return; }
        if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) { e.Handled = true; SearchBox.Clear(); return; }
        if (e.Key == Key.F2) { e.Handled = true; Rename_Click(sender,e); return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.L: e.Handled = true; FocusAnswer(); break;
            case Key.K: e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); break;
            case Key.N: e.Handled = true; NewRoom_Click(sender,e); break;
            case Key.O: e.Handled = true; ChooseFolder_Click(sender,e); break;
            case Key.OemComma: e.Handled = true; Settings_Click(sender,e); break;
        }
    }
    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (current.IsArchived || sending || switching || closing) return;
        if (!string.IsNullOrWhiteSpace(Composer.Text)) { Composer.Focus(); StateLabel.Text = "Your draft is ready · send or clear it before continuing"; return; }
        var task = current.LastTask;
        if (string.IsNullOrWhiteSpace(task)) { Composer.Focus(); return; }
        if (ProjectStatusWorkflow.IsStatusRequest(task)) { await SendAsync(task); return; }
        Composer.Text = "Continue the previous task only if useful unfinished work remains. Otherwise report that it is complete.\n\n" +
            (task.Length > 24000 ? "Use the full long message already saved in this conversation; retrieve its shared source as needed." : "Task: " + task);
        Target.SelectedIndex = 0; await SendAsync();
        current.LastTask = task; Save();
    }
    private void Composer_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => ComposerFrame.BorderBrush = Theme.Brush("CyanBrush");
    private void Composer_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ComposerFrame.BorderBrush = Theme.Brush("LineBrush");
    private void ActivityToggle_Click(object sender, RoutedEventArgs e)
    {
        activityPreference = !activityVisible;
        SetActivityVisibility(activityPreference.Value, true);
    }
    private void SetActivityVisibility(bool visible, bool animate)
    {
        activityVisible = visible;
        ActivityToggleIcon.Kind = activityVisible ? "panel-right-close" : "panel-right-open";
        ActivityRail.IsHitTestVisible = activityVisible;
        ActivityRail.Visibility = Visibility.Visible;
        if (animate && Motion.Enabled)
        {
            var transition = Motion.Tween(activityVisible ? 294 : 0, 220);
            transition.Completed += (_, _) => { if (!activityVisible) ActivityRail.Visibility = Visibility.Collapsed; };
            ActivityRail.BeginAnimation(WidthProperty, transition);
        }
        else
        {
            ActivityRail.BeginAnimation(WidthProperty, null);
            ActivityRail.Width = activityVisible ? 294 : 0;
            ActivityRail.Visibility = activityVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        ActivityToggle.ToolTip = activityVisible ? "Hide the activity timeline" : "Show tools, handoffs, and requests for input";
    }
    private void ApplyResponsiveLayout()
    {
        if (!ready) return;
        var compactHeight = ActualHeight < 800;
        WorkspaceHeading.Visibility = compactHeight ? Visibility.Collapsed : Visibility.Visible;
        ConversationsHeading.Visibility = compactHeight ? Visibility.Collapsed : Visibility.Visible;
        NavigationRail.Padding = new Thickness(16, compactHeight ? 16 : 26, 16, 16);
        SharedProjectCard.Margin = new Thickness(0, compactHeight ? 12 : 16, 0, compactHeight ? 12 : 16);
        var desired = activityPreference ?? ActualWidth >= 1280;
        if (desired != activityVisible) SetActivityVisibility(desired, false);
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (!ready) return;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        WindowFrame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        RefreshMotion();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeAllowed) return;
        e.Cancel = true;
        if (fileImport is not null) { closeAfterSwitch = true; fileImport.Cancel(); return; }
        // Let an archive/delete/switch finish disposing its outgoing provider before closing.
        if (switching) { closeAfterSwitch = true; return; }
        if (closing) return;
        closing = true; saveTimer.Stop();
        // Closing must return before Close is called again, including when stopping completes synchronously.
        await Dispatcher.Yield(DispatcherPriority.Background);
        // Bounded and fail-safe: a provider that will not stop, or a failing save, must not leave a window that cannot close.
        try { await DisposeAllWorkersAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { audit.Record(AuditCode.UnhandledError, exception: ex); }
        try { MarkInterrupted(); Save(); } catch (Exception ex) { audit.Record(AuditCode.StorageError, exception: ex); }
        try { await audit.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        closeAllowed = true; Close();
    }
}

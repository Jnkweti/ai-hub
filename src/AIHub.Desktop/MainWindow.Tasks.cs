using AIHub.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AIHub.Desktop;

public partial class MainWindow
{
    private void RefreshTaskSummary()
    {
        if (TaskSummaryLabel is null || string.IsNullOrWhiteSpace(current.Workspace)) return;
        var tasks = taskMemory.ForWorkspace(current.Workspace);
        var running = tasks.Count(t => t.State == WorkState.Running);
        var waiting = pendingInputs.Values.Count(p => string.Equals(p.Room.Workspace, current.Workspace, StringComparison.OrdinalIgnoreCase));
        TaskSummaryLabel.Text = $"{running} working · {waiting} awaiting you";
        var active = tasks.FirstOrDefault(t => t.Id == current.ActiveTaskId);
        TaskSummaryLabel.ToolTip = active is null ? "Open Tasks and notes to start a task or find work in another conversation." :
            $"Current task: {active.Objective}\n{active.State}{(active.Owner.Length > 0 ? " · " + active.Owner : "")}\n{active.Reason}";
    }

    private sealed record TaskRow(WorkTask Task, string Label)
    {
        public override string ToString() => Label;
    }
    private void Tasks_Click(object sender, RoutedEventArgs e)
    {
        if (switching || sending || closing) return;
        var workspace = current.Workspace;
        string? shown = null; // Signature of the task list as last rendered; the per-second tick rebuilds only when it changes.
        var window = new Window { Owner = this, Title = "Project tasks and notes", Width = 820, Height = 690,
            MinWidth = 620, MinHeight = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { Margin = new(22) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(150), new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        grid.Children.Add(new TextBlock { Text = "Tasks in " + Path.GetFileName(workspace), FontSize = 20, Margin = new(0,0,0,12) });
        var list = new ListBox { Name = "ProjectTaskList", Margin = new(0,0,0,12), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var details = new TextBox { Name = "TaskDetails", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0,0,0,12) };
        Grid.SetRow(details, 2); grid.Children.Add(details);
        var note = new TextBox { Name = "TaskNoteInput", MaxLength = 4000, MinHeight = 58, MaxHeight = 90, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "Add a task note. Recent notes are included in the next worker briefing." };
        Grid.SetRow(note, 3); grid.Children.Add(note);
        var notice = new TextBlock { Text = "Saved replies are agent reports. Check current files before relying on them.", TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.Brush("MutedBrush"), Margin = new(0,9,0,9) };
        Grid.SetRow(notice, 4); grid.Children.Add(notice);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 5); grid.Children.Add(buttons);
        Button Button(string name, string text) { var b = new Button { Name = name, Content = text, Margin = new(6,0,0,6), Padding = new(10,8,10,8) }; buttons.Children.Add(b); return b; }
        var add = Button("AddTaskNoteButton", "Save note");
        var stop = Button("StopTaskButton", "Stop task");
        var open = Button("OpenTaskButton", "Open conversation");
        var resume = Button("SelectTaskButton", "Use this task");
        var create = Button("NewTaskButton", "New task");
        var evidence = Button("TaskEvidenceButton", "Check evidence");
        var packet = Button("ReviewPacketButton", "Review packet");
        var merge = Button("MergeWorktreesButton", "Merge into project");
        var dropWorktrees = Button("RemoveWorktreesButton", "Remove worktrees");
        merge.Click += async (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row) return;
            merge.IsEnabled = false;
            try
            {
                var conflicts = await collaborationStore.MergeWorktreesIntoProjectAsync(row.Task.Id, CancellationToken.None);
                notice.Text = conflicts.Length == 0 ? "The agents' integration branch was merged into your project checkout." : "Merge aborted; these files conflict with your checkout: " + string.Join(", ", conflicts) + ". Resolve them in git or ask an agent to reconcile.";
            }
            catch (IOException ex) { notice.Text = "Merge failed: " + ex.Message; }
            finally { merge.IsEnabled = true; }
        };
        dropWorktrees.Click += async (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row) return;
            dropWorktrees.IsEnabled = false;
            try { await collaborationStore.RemoveWorktreesAsync(row.Task.Id, CancellationToken.None); notice.Text = "Agent worktrees and their branches were removed. Unmerged changes are gone."; Refresh(true); }
            catch (IOException ex) { notice.Text = "Could not remove worktrees: " + ex.Message; }
            finally { dropWorktrees.IsEnabled = true; }
        };
        packet.Click += async (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row) return;
            packet.IsEnabled = false;
            try
            {
                var markdown = await Task.Run(() => collaborationStore.ReviewPacket(row.Task.Id, CancellationToken.None));
                var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown|*.md", FileName = "AI-Hub-review-packet-" + row.Task.Id[..8] + ".md" };
                if (picker.ShowDialog(window) == true) { File.WriteAllText(picker.FileName, markdown); notice.Text = "Review packet saved: " + picker.FileName; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notice.Text = "Review packet failed: " + ex.Message; }
            finally { packet.IsEnabled = true; }
        };
        var context = Button("SharedContextButton", "Shared context");
        context.Click += async (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row) return;
            context.IsEnabled = false;
            try
            {
                var report = await Task.Run(() => collaborationStore.ContextReportAsync(row.Task.Id, CancellationToken.None));
                var text = new TextBox { Name = "SharedContextReport", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = report, Margin = new(16) };
                var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
                layout.Children.Add(text);
                var controls = new StackPanel { Margin = new(16,0,16,16) }; Grid.SetRow(controls, 1); layout.Children.Add(controls);
                controls.Children.Add(new TextBlock { Text = "Pin an instruction, or replace an active instruction below. This stops affected work before changing its instructions.", TextWrapping = TextWrapping.Wrap });
                var choices = new ComboBox { Name = "SharedInstructionSelection", Margin = new(0,6,0,6) }; controls.Children.Add(choices);
                var editor = new TextBox { Name = "SharedInstructionInput", MaxLength = 4000, Height = 64, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                controls.Children.Add(editor);
                var actions = new WrapPanel { Margin = new(0,6,0,0) }; controls.Children.Add(actions);
                var pin = new Button { Name = "PinInstructionButton", Content = "Pin instruction", Padding = new(10,6,10,6) };
                var replace = new Button { Name = "ReplaceInstructionButton", Content = "Replace selected instruction", Padding = new(10,6,10,6), Margin = new(8,0,0,0) };
                actions.Children.Add(pin); actions.Children.Add(replace);
                var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0,6,0,0) }; controls.Children.Add(result);
                void RefreshChoices()
                {
                    choices.Items.Clear();
                    foreach (var instruction in TaskContextBuilder.ActiveInstructions(collaborationStore.Read(row.Task.Id)))
                        choices.Items.Add(new ComboBoxItem { Tag = instruction.Id, Content = instruction.Text.Length <= 85 ? instruction.Text : instruction.Text[..85] + "…" });
                    if (choices.Items.Count > 0) choices.SelectedIndex = choices.Items.Count - 1;
                }
                async Task ChangeInstruction(bool supersede)
                {
                    if (string.IsNullOrWhiteSpace(editor.Text)) { result.Text = "Write the instruction first."; return; }
                    var oldId = supersede ? (choices.SelectedItem as ComboBoxItem)?.Tag as string : null;
                    if (supersede && oldId is null) { result.Text = "Select an active instruction to replace."; return; }
                    pin.IsEnabled = replace.IsEnabled = false;
                    try
                    {
                        if (workers.TryGetValue(row.Task.RoomId, out var owner) && owner.Hub.TaskId == row.Task.Id)
                        {
                            await owner.Hub.StopAsync();
                            foreach (var message in owner.Room.Messages.Where(m => !m.Complete))
                                if (!message.Text.EndsWith("[Stopped]")) message.Text += "\n\n[Stopped]";
                            owner.Room.PauseReason = "Instructions changed. Review them, then continue the task.";
                            if (ReferenceEquals(current, owner.Room)) { MarkInterrupted(); SetPause(owner.Room.PauseReason); }
                        }
                        collaborationStore.PinInstruction(row.Task.Id, editor.Text, oldId);
                        editor.Clear(); RefreshChoices(); text.Text = await Task.Run(() => collaborationStore.ContextReportAsync(row.Task.Id, CancellationToken.None));
                        result.Text = "Saved. Send a message to continue with these instructions; no agent was started.";
                        Save();
                    }
                    catch (Exception ex) { result.Text = ex.Message; }
                    finally { pin.IsEnabled = replace.IsEnabled = true; }
                }
                pin.Click += async (_, _) => await ChangeInstruction(false);
                replace.Click += async (_, _) => await ChangeInstruction(true);
                RefreshChoices();
                var shared = new Window { Owner = window, Title = "Shared task context", Width = 850, Height = 650,
                    Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                System.Windows.Automation.AutomationProperties.SetName(shared, "Shared task context");
                System.Windows.Automation.AutomationProperties.SetAutomationId(shared, "SharedContextWindow");
                shared.ShowDialog();
            }
            catch (Exception ex) { notice.Text = "Context could not be loaded: " + ex.Message; }
            finally { context.IsEnabled = true; }
        };
        evidence.Click += async (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row) return;
            evidence.IsEnabled = false;
            try
            {
                var inspected = await Task.Run(() => collaborationStore.Inspect(row.Task.Id, CancellationToken.None));
                var report = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = CollaborationPresentation.History(inspected.Document, inspected.Current), Margin = new(16) };
                var evidenceWindow = new Window { Owner = window, Title = "Task evidence and review freshness", Width = 850, Height = 650,
                    Content = report, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                System.Windows.Automation.AutomationProperties.SetName(evidenceWindow, "Task evidence and review freshness");
                System.Windows.Automation.AutomationProperties.SetAutomationId(evidenceWindow, "TaskEvidenceWindow");
                evidenceWindow.ShowDialog();
            }
            catch (Exception ex) { notice.Text = "Evidence check failed: " + ex.Message; }
            finally { evidence.IsEnabled = true; }
        };
        var close = Button("CloseTasksButton", "Close"); close.Click += (_, _) => window.Close();
        void ShowDetails()
        {
            var task = (list.SelectedItem as TaskRow)?.Task;
            add.IsEnabled = task is not null && rooms.Any(r => r.Id == task.RoomId && !r.IsArchived);
            open.IsEnabled = task is not null;
            stop.IsEnabled = task is not null && workers.TryGetValue(task.RoomId, out var live) && live.Hub.TaskId == task.Id && task.State == WorkState.Running;
            resume.IsEnabled = task is not null && rooms.Any(r => r.Id == task.RoomId && !r.IsArchived);
            create.IsEnabled = !current.IsArchived;
            WorktreeLayout? layout = null;
            if (task is not null) try { layout = collaborationStore.Worktrees(task.Id); } catch (IOException) { }
            merge.IsEnabled = dropWorktrees.IsEnabled = layout is not null && task!.State != WorkState.Running;
            merge.Visibility = dropWorktrees.Visibility = layout is not null ? Visibility.Visible : Visibility.Collapsed;
            details.Text = task is null ? "Send a task in the conversation or choose New task." :
                $"Objective: {task.Objective}\nState: {task.State}\nOwner: {(task.Owner.Length == 0 ? "No active worker" : task.Owner)}\nUpdated: {task.Updated:g}\n{task.Reason}\n\nTask notes:\n" +
                string.Join("\n\n", task.Notes.Select(n => $"{n.Time:g} · {n.Text}")) + "\n\nLatest agent reports:\n" +
                string.Join("\n\n", task.LatestReplies.Select(p => p.Key + ": " + p.Value));
            if (task is not null)
            {
                if (layout is not null)
                    details.Text += $"\n\nAgent worktrees (project folder unchanged until you merge):\nCodex: {layout.Codex} on {layout.BranchFor(Agent.Codex)}\nClaude Code: {layout.Claude} on {layout.BranchFor(Agent.Claude)}\nMerged result: {layout.Integration} on {layout.IntegrationBranch} (from commit {layout.BaseCommit[..Math.Min(8, layout.BaseCommit.Length)]})";
                // Resume handles: the same native threads can be reopened in each CLI.
                var room = rooms.FirstOrDefault(r => r.Id == task.RoomId);
                details.Text += "\n\nNative sessions (reopen the same thread in the CLI):\n" +
                    (room?.CodexSession is { Length: > 0 } codexThread ? $"Codex thread {codexThread}\n  codex resume {codexThread}\n" : "Codex: no thread yet\n") +
                    (room?.ClaudeSession is { Length: > 0 } claudeSession ? $"Claude session {claudeSession}\n  claude --resume {claudeSession}\n" : "Claude Code: no session yet\n");
                try
                {
                    var ledger = collaborationStore.Read(task.Id);
                    details.Text += "\nProvider usage:\n" + CollaborationPresentation.UsageSummary(ledger, task.Generation);
                    details.Text += CollaborationPresentation.History(ledger, includeOutput: false);
                }
                catch (IOException ex) { details.Text += "\nCollaboration history unavailable: " + ex.Message; }
            }
        }
        bool refreshing = false;
        void Refresh(bool force = false)
        {
            // The per-second tick rebuilds the list and rereads the ledger only when a task actually changed.
            var tasks = taskMemory.ForWorkspace(workspace).Where(t => rooms.Any(r => r.Id == t.RoomId)).ToArray();
            var signature = string.Join("|", tasks.Select(t => $"{t.Id}:{t.State}:{t.Owner}:{t.Updated.UtcTicks}:{pendingInputs.Values.Any(p => p.Room.Id == t.RoomId && p.Owner.TaskId == t.Id)}"));
            if (!force && signature == shown) return;
            shown = signature;
            var selected = (list.SelectedItem as TaskRow)?.Task.Id ?? current.ActiveTaskId;
            refreshing = true;
            list.ItemsSource = tasks.Select(t =>
            {
                var room = rooms.First(r => r.Id == t.RoomId);
                var waiting = pendingInputs.Values.Any(p => p.Room.Id == t.RoomId && p.Owner.TaskId == t.Id);
                var title = t.Objective.Length > 60 ? t.Objective[..60] + "…" : t.Objective;
                return new TaskRow(t, $"{title.Replace('\n', ' ')} · {(waiting ? "Awaiting you" : t.State.ToString())}{(t.Owner.Length > 0 ? " · " + t.Owner : "")} · {room.Title}");
            }).ToArray();
            list.SelectedItem = list.Items.Cast<TaskRow>().FirstOrDefault(r => r.Task.Id == selected) ?? list.Items.Cast<TaskRow>().FirstOrDefault();
            refreshing = false; ShowDetails();
        }
        list.SelectionChanged += (_, _) => { if (!refreshing) ShowDetails(); };
        add.Click += (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row) return;
            try { taskMemory.AddNote(row.Task.Id, note.Text); note.Clear(); notice.Text = "Note saved. It will be included in the next worker briefing."; Refresh(true); }
            catch (Exception ex) { notice.Text = ex.Message; }
        };
        stop.Click += async (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row || !workers.TryGetValue(row.Task.RoomId, out var worker)) return;
            stop.IsEnabled = false;
            await worker.Hub.StopAsync();
            foreach (var message in worker.Room.Messages.Where(m => !m.Complete))
                if (!message.Text.EndsWith("[Stopped]")) message.Text += "\n\n[Stopped]";
            worker.Room.PauseReason = "You stopped the agents. Completed file changes are kept.";
            if (ReferenceEquals(current, worker.Room)) { MarkInterrupted(); SetPause(worker.Room.PauseReason); }
            Save(); Refresh(true);
        };
        void OpenRoom(WorkTask task)
        {
            var room = rooms.FirstOrDefault(r => r.Id == task.RoomId);
            if (room is null) return;
            DetachSelectedView(); SearchBox.Clear(); RoomFilter.SelectedIndex = room.IsArchived ? 1 : 0;
            // Avoid a second activation from SelectionChanged while assigning the view.
            switching = true;
            try { RoomList.SelectedItem = room; ActivateRoom(room); Save(); }
            finally { switching = false; UpdateConversationControls(); }
        }
        open.Click += (_, _) => { if (list.SelectedItem is TaskRow row) { OpenRoom(row.Task); window.Close(); } };
        resume.Click += async (_, _) =>
        {
            if (list.SelectedItem is not TaskRow row) return;
            try
            {
                OpenRoom(row.Task); await DisposeCurrentHubAsync();
                current.ActiveTaskId = row.Task.Id; current.LastTask = row.Task.Objective;
                current.CodexSession = null; current.ClaudeSession = null;
                current.CodexContext = new(); current.ClaudeContext = new();
                BuildHub(); SetPause("Saved task selected. Send a message to continue after reviewing the current project.");
                Save(); window.Close();
            }
            catch (Exception ex) { notice.Text = ex.Message; }
        };
        create.Click += (_, _) => { window.Close(); NewTask(); };
        window.Content = grid; Refresh(true);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh(); timer.Start();
        window.Closed += (_, _) => timer.Stop(); window.ShowDialog();
    }
    private void NewTask()
    {
        if (current.IsArchived || switching || sending || closing) return;
        var window = new Window { Owner = this, Title = "New task", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = "What should the agents work on?", FontSize = 18, Margin = new(0,0,0,12) });
        var objective = new TextBox { Name = "NewTaskObjective", MaxLength = 32000, Height = 140, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(objective);
        panel.Children.Add(new TextBlock { Text = "Starts a separate conversation with fresh agent sessions. Existing work keeps running.", TextWrapping = TextWrapping.Wrap, Margin = new(0,10,0,10) });
        var start = new Button { Name = "StartNewTaskButton", Content = "Start task", HorizontalAlignment = HorizontalAlignment.Right };
        start.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(objective.Text)) window.DialogResult = true; };
        panel.Children.Add(start); window.Content = panel;
        if (window.ShowDialog() != true) return;
        StartTask(objective.Text.Trim());
    }
    private async void StartTask(string objective)
    {
        try { await CreateRoomAsync(); await SendAsync(objective); }
        catch (Exception ex) { StateLabel.Text = "Could not start task: " + ex.Message; }
    }
}

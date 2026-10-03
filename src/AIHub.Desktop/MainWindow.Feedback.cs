using AIHub.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AIHub.Desktop;

// Explicit developer feedback (0.29.0): a judgement on one message or one task, kept locally with the IDs it judges,
// inspectable and editable, never fed to the agents. Absence of feedback means unknown.
public partial class MainWindow
{
    private string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    private void ApplyFeedbackLabels()
    {
        var byMessage = feedback.ForRoom(current.Id).Where(r => r.MessageId is not null).GroupBy(r => r.MessageId!).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Updated).First());
        foreach (var view in messages)
            view.FeedbackLabel = byMessage.TryGetValue(view.Saved.Id, out var record) ? FeedbackBadge(record) : "";
    }
    private static string FeedbackBadge(FeedbackRecord record) => "You: " + FeedbackStore.Label(record.Kind).ToLowerInvariant() +
        (record.Scope == FeedbackScope.Task ? "" : " · " + FeedbackStore.Label(record.Scope));
    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not MessageView view || switching || closing) return;
        var saved = view.Saved;
        var existing = feedback.ForMessage(saved.Id).OrderByDescending(r => r.Updated).FirstOrDefault();
        var task = saved.TaskId.Length > 0 ? taskMemory.Get(saved.TaskId) : null;
        var draft = existing ?? new FeedbackRecord
        {
            RoomId = current.Id, MessageId = saved.Id, DispatchId = saved.DispatchId, LedgerMessageId = saved.Collaboration?.Envelope.MessageId,
            TaskId = saved.TaskId, Workspace = current.Workspace, Agent = view.Speaker
        };
        if (ShowFeedbackDialog(draft, "Feedback on " + view.Speaker + "'s message", existing is not null, saved.Text, task))
            ApplyFeedbackLabels();
    }
    /// <summary>Feedback on a task as a whole, from the Tasks window.</summary>
    private void TaskFeedback(WorkTask task, Window owner, Action<string> notice)
    {
        var existing = feedback.ForTask(task.Id).Where(r => r.MessageId is null).OrderByDescending(r => r.Updated).FirstOrDefault();
        var draft = existing ?? new FeedbackRecord { RoomId = task.RoomId, TaskId = task.Id, Workspace = task.Workspace, Agent = "" };
        if (ShowFeedbackDialog(draft, "Feedback on the task", existing is not null, task.Objective, task, owner))
            notice("Task feedback saved. It stays local; agents are not shown it.");
    }
    /// <summary>
    /// Builds and shows the feedback dialog. Returns true when a record was saved or deleted. The dialog edits a copy; the
    /// store validates on save and the original record is untouched on cancel or failure.
    /// </summary>
    private bool ShowFeedbackDialog(FeedbackRecord draft, string title, bool editing, string judgedText, WorkTask? task, Window? owner = null)
    {
        var window = new Window { Owner = owner ?? this, Title = title, Width = 560, Height = 600, MinWidth = 460, MinHeight = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Margin = new(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Your judgement is stored locally with the message, dispatch and task IDs, and the outcome at the time. Agents are not shown it; it is for your own review and later learning you control.", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedBrush"), Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "What is your judgement?", FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 4) });
        var kinds = new Dictionary<FeedbackKind, RadioButton>();
        foreach (var kind in new[] { FeedbackKind.Useful, FeedbackKind.NeedsCorrection, FeedbackKind.PreferredAlternative })
        {
            var radio = new RadioButton { Content = FeedbackStore.Label(kind), GroupName = "FeedbackKind", IsChecked = draft.Kind == kind, Margin = new(0, 2, 0, 2), Name = "FeedbackKind" + kind };
            kinds[kind] = radio; panel.Children.Add(radio);
        }
        panel.Children.Add(new TextBlock { Text = "Which aspects (optional)?", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 4) });
        var dimensions = new WrapPanel();
        var checks = FeedbackStore.KnownDimensions.Select(d => new CheckBox { Content = d, IsChecked = draft.Dimensions.Contains(d), Margin = new(0, 2, 14, 2), Tag = d }).ToArray();
        foreach (var check in checks) dimensions.Children.Add(check);
        panel.Children.Add(dimensions);
        panel.Children.Add(new TextBlock { Text = "Explanation (optional; what you preferred instead, or what was wrong)", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 4) });
        var explanation = new TextBox { Name = "FeedbackExplanation", Text = draft.Explanation, MaxLength = FeedbackStore.MaxExplanation, MinHeight = 90, MaxHeight = 160, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(explanation);
        panel.Children.Add(new TextBlock { Text = "Where does this apply?", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 4) });
        var scope = new ComboBox { Name = "FeedbackScope" };
        foreach (var s in new[] { FeedbackScope.Task, FeedbackScope.Project, FeedbackScope.Category, FeedbackScope.General })
            scope.Items.Add(new ComboBoxItem { Content = FeedbackStore.Label(s), Tag = s, IsSelected = draft.Scope == s });
        panel.Children.Add(scope);
        var category = new TextBox { Name = "FeedbackCategory", Text = draft.Category, MaxLength = FeedbackStore.MaxCategory, Margin = new(0, 6, 0, 0), ToolTip = "Name the kind of task, for example 'bug diagnosis' or 'release notes'", Visibility = draft.Scope == FeedbackScope.Category ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(category);
        scope.SelectionChanged += (_, _) => category.Visibility = (scope.SelectedItem as ComboBoxItem)?.Tag is FeedbackScope.Category ? Visibility.Visible : Visibility.Collapsed;
        var notice = new TextBlock { Foreground = Theme.Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0),
            Text = task is null ? "" : $"Task outcome now: {task.State}{(task.Reason.Length > 0 ? " — " + Truncate(task.Reason, 140) : "")}" };
        panel.Children.Add(notice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        Button Button(string name, string text) { var b = new Button { Name = name, Content = text, Margin = new(6, 0, 0, 0), Padding = new(12, 8, 12, 8) }; buttons.Children.Add(b); return b; }
        var delete = Button("DeleteFeedbackButton", "Delete"); delete.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        var cancel = Button("CancelFeedbackButton", "Cancel"); var save = Button("SaveFeedbackButton", editing ? "Save changes" : "Save feedback");
        panel.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var changed = false;
        cancel.Click += (_, _) => window.Close();
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(window, "Delete this feedback record? This cannot be undone.", "Delete feedback", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { changed = feedback.Delete(draft.Id); if (changed) preferences.FeedbackDeleted(draft.Id); window.Close(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { notice.Text = "Could not delete: " + ex.Message; }
        };
        save.Click += (_, _) =>
        {
            void Fill(FeedbackRecord r)
            {
                r.Kind = kinds.First(p => p.Value.IsChecked == true).Key;
                r.Dimensions = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
                r.Explanation = explanation.Text.Trim();
                r.Scope = (scope.SelectedItem as ComboBoxItem)?.Tag is FeedbackScope s ? s : FeedbackScope.Task;
                r.Category = r.Scope == FeedbackScope.Category ? category.Text.Trim() : "";
                r.OutcomeState = task?.State.ToString() ?? ""; r.OutcomeReason = Truncate(task?.Reason ?? "", 2000);
                r.AppVersion = AppVersion; r.StrategyVersion = FeedbackStore.StrategyFingerprint(settings); r.ExcerptHash = FeedbackStore.Fingerprint(judgedText);
            }
            try
            {
                if (editing) feedback.Update(draft.Id, Fill); else { Fill(draft); feedback.Add(draft); }
                changed = true; window.Close();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { notice.Text = ex.Message; }
        };
        window.ShowDialog();
        if (changed) StateLabel.Text = editing ? "Feedback updated" : "Feedback saved · local only";
        return changed;
    }
    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
    /// <summary>All feedback, newest first: open a record to edit or delete it, or export everything as Markdown.</summary>
    private void FeedbackList_Click(object sender, RoutedEventArgs e)
    {
        if (switching || closing) return;
        var window = new Window { Owner = this, Title = "Your feedback", Width = 820, Height = 560, MinWidth = 600, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { Margin = new(22) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        grid.Children.Add(new TextBlock { Text = "Feedback you recorded (local only; agents are not shown it)", FontSize = 18, Margin = new(0, 0, 0, 12) });
        var list = new ListBox { Name = "FeedbackList", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var notice = new TextBlock { Foreground = Theme.Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 9, 0, 9) };
        Grid.SetRow(notice, 2); grid.Children.Add(notice);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Button Button(string name, string text) { var b = new Button { Name = name, Content = text, Margin = new(6, 0, 0, 6), Padding = new(10, 8, 10, 8) }; buttons.Children.Add(b); return b; }
        var edit = Button("EditFeedbackButton", "Open"); var promote = Button("MakePreferenceButton", "Make preference"); var export = Button("ExportFeedbackButton", "Export all"); var close = Button("CloseFeedbackButton", "Close");
        promote.ToolTip = "Turn this judgement into a standing preference you confirm and can edit; nothing is promoted on its own.";
        promote.Click += (_, _) =>
        {
            if (list.SelectedItem is not FeedbackRow row || feedback.Get(row.Record.Id) is not { } record) return;
            if (MakePreferenceFromFeedback(record, window)) notice.Text = "Preference saved. It applies from the next phase of tasks in its scope; see Preferences in the Tasks window.";
        };
        void Refresh()
        {
            var records = feedback.All();
            list.ItemsSource = records.Select(r => new FeedbackRow(r, FeedbackStore.Summary(r) + "\n    " + RoomTitle(r.RoomId) + (r.MessageId is null ? "" : " · message " + r.MessageId[..Math.Min(8, r.MessageId.Length)]))).ToArray();
            notice.Text = records.Length == 0 ? "No feedback yet. Use the feedback button on an agent's message, or Feedback in the Tasks window." : $"{records.Length} record{(records.Length == 1 ? "" : "s")}. Feedback from deleted conversations is removed with them.";
        }
        string RoomTitle(string roomId) => rooms.FirstOrDefault(r => r.Id == roomId)?.Title ?? "conversation " + roomId[..Math.Min(8, roomId.Length)];
        edit.Click += (_, _) =>
        {
            if (list.SelectedItem is not FeedbackRow row) return;
            var record = feedback.Get(row.Record.Id); if (record is null) { Refresh(); return; }
            var room = rooms.FirstOrDefault(r => r.Id == record.RoomId);
            var judged = record.MessageId is null ? taskMemory.Get(record.TaskId)?.Objective ?? "" : room?.Messages.FirstOrDefault(m => m.Id == record.MessageId)?.Text ?? "";
            var task = record.TaskId.Length > 0 ? taskMemory.Get(record.TaskId) : null;
            if (ShowFeedbackDialog(record, record.MessageId is null ? "Feedback on the task" : "Feedback on " + (record.Agent.Length > 0 ? record.Agent + "'s message" : "a message"), true, judged, task, window))
            { Refresh(); if (room is not null && ReferenceEquals(room, current)) ApplyFeedbackLabels(); }
        };
        list.MouseDoubleClick += (_, _) => edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        export.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Markdown|*.md", FileName = "AI-Hub-feedback.md" };
            if (picker.ShowDialog(window) != true) return;
            try { File.WriteAllText(picker.FileName, FeedbackStore.Export(feedback.All())); notice.Text = "Feedback exported: " + picker.FileName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notice.Text = "Export failed: " + ex.Message; }
        };
        close.Click += (_, _) => window.Close();
        Refresh();
        window.Content = grid; window.ShowDialog();
    }
    private sealed record FeedbackRow(FeedbackRecord Record, string Label) { public override string ToString() => Label; }
    /// <summary>The conversation export's feedback section: one entry per record for this room, after the messages.</summary>
    private string FeedbackExportSection(Room room)
    {
        var records = feedback.ForRoom(room.Id);
        if (records.Length == 0) return "";
        return "\n\n---\n\n## Your feedback\n\n" + string.Join("\n", records.OrderBy(r => r.Updated).Select(r => "- " + FeedbackStore.Summary(r) + (r.MessageId is null ? "" : $" (message {r.MessageId})")));
    }
}

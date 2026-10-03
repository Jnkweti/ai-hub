using AIHub.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AIHub.Desktop;

// Inspectable preference memory (0.30.0): the developer writes or confirms every preference; the host supplies the enabled
// ones in scope below the task's instructions and records which version each agent received.
public partial class MainWindow
{
    /// <summary>The Preferences window: list, new, edit, enable or disable, delete, export.</summary>
    private void PreferenceList_Click(object sender, RoutedEventArgs e)
    {
        if (switching || closing) return;
        var window = new Window { Owner = this, Title = "Preferences", Width = 820, Height = 560, MinWidth = 600, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { Margin = new(22) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        grid.Children.Add(new TextBlock { Text = "Your preferences (supplied to agents on tasks in their scope, below your instructions)", FontSize = 18, Margin = new(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        var list = new ListBox { Name = "PreferenceList", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var notice = new TextBlock { Foreground = Theme.Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 9, 0, 9) };
        Grid.SetRow(notice, 2); grid.Children.Add(notice);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Button Button(string name, string text) { var b = new Button { Name = name, Content = text, Margin = new(6, 0, 0, 6), Padding = new(10, 8, 10, 8) }; buttons.Children.Add(b); return b; }
        var create = Button("NewPreferenceButton", "New"); var edit = Button("EditPreferenceButton", "Edit"); var toggle = Button("TogglePreferenceButton", "Disable");
        var delete = Button("DeletePreferenceButton", "Delete"); var export = Button("ExportPreferencesButton", "Export all"); var close = Button("ClosePreferencesButton", "Close");
        void Refresh()
        {
            var records = preferences.All();
            list.ItemsSource = records.Select(r => new PreferenceRow(r, PreferenceStore.Describe(r))).ToArray();
            notice.Text = records.Length == 0 ? "No preferences yet. Write one with New, or make one from a feedback record in All feedback."
                : $"{records.Length} preference{(records.Length == 1 ? "" : "s")}. Changes apply to the next phase of a task; a running or resumed native session may still hold the earlier text. Category preferences are kept but not supplied automatically yet.";
            Selected();
        }
        void Selected()
        {
            var row = list.SelectedItem as PreferenceRow;
            edit.IsEnabled = delete.IsEnabled = toggle.IsEnabled = row is not null;
            toggle.Content = row is { Record.Enabled: false } ? "Enable" : "Disable";
        }
        list.SelectionChanged += (_, _) => Selected();
        create.Click += (_, _) => { if (ShowPreferenceDialog(new PreferenceRecord { Scope = FeedbackScope.Project, Workspace = current.Workspace, TaskId = current.ActiveTaskId }, "New preference", false, window)) Refresh(); };
        edit.Click += (_, _) => { if (list.SelectedItem is PreferenceRow row && preferences.Get(row.Record.Id) is { } record && ShowPreferenceDialog(record, "Edit preference", true, window)) Refresh(); };
        list.MouseDoubleClick += (_, _) => edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        toggle.Click += (_, _) =>
        {
            if (list.SelectedItem is not PreferenceRow row) return;
            try { preferences.SetEnabled(row.Record.Id, !row.Record.Enabled, row.Record.Enabled ? "Disabled by you." : ""); Refresh(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { notice.Text = ex.Message; }
        };
        delete.Click += (_, _) =>
        {
            if (list.SelectedItem is not PreferenceRow row) return;
            if (MessageBox.Show(window, "Delete this preference? Agents will not receive it on later phases. This cannot be undone.", "Delete preference", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { preferences.Delete(row.Record.Id); Refresh(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notice.Text = "Could not delete: " + ex.Message; }
        };
        export.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Markdown|*.md", FileName = "AI-Hub-preferences.md" };
            if (picker.ShowDialog(window) != true) return;
            try { File.WriteAllText(picker.FileName, PreferenceStore.Export(preferences.All())); notice.Text = "Preferences exported: " + picker.FileName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notice.Text = "Export failed: " + ex.Message; }
        };
        close.Click += (_, _) => window.Close();
        Refresh();
        window.Content = grid; window.ShowDialog();
    }
    private sealed record PreferenceRow(PreferenceRecord Record, string Label) { public override string ToString() => Label; }
    /// <summary>Makes a preference from a feedback record: the developer edits and confirms the text, so nothing is promoted silently.</summary>
    private bool MakePreferenceFromFeedback(FeedbackRecord feedbackRecord, Window owner)
    {
        var draft = new PreferenceRecord
        {
            Text = feedbackRecord.Explanation.Trim(), Scope = feedbackRecord.Scope == FeedbackScope.Task && feedbackRecord.TaskId.Length == 0 ? FeedbackScope.Project : feedbackRecord.Scope,
            Workspace = feedbackRecord.Workspace, TaskId = feedbackRecord.TaskId, Category = feedbackRecord.Category,
            Origin = "from_feedback", SupportingFeedbackIds = [feedbackRecord.Id], Confidence = "stated"
        };
        return ShowPreferenceDialog(draft, "Make a preference from this feedback", false, owner);
    }
    private bool ShowPreferenceDialog(PreferenceRecord draft, string title, bool editing, Window owner)
    {
        var window = new Window { Owner = owner, Title = title, Width = 560, Height = 440, MinWidth = 460, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Margin = new(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Write the preference as you would say it to a colleague. It is supplied to agents on tasks in its scope, below your instructions; a newer message of yours always wins.", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedBrush"), Margin = new(0, 0, 0, 12) });
        var text = new TextBox { Name = "PreferenceText", Text = draft.Text, MaxLength = PreferenceStore.MaxText, MinHeight = 90, MaxHeight = 160, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(text);
        panel.Children.Add(new TextBlock { Text = "Where does it apply?", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 4) });
        var scope = new ComboBox { Name = "PreferenceScope" };
        foreach (var s in new[] { FeedbackScope.Project, FeedbackScope.Task, FeedbackScope.Category, FeedbackScope.General })
            scope.Items.Add(new ComboBoxItem { Content = s == FeedbackScope.Project ? "this project (" + Path.GetFileName(draft.Workspace.Length > 0 ? draft.Workspace : current.Workspace) + ")" : FeedbackStore.Label(s), Tag = s, IsSelected = draft.Scope == s });
        panel.Children.Add(scope);
        var category = new TextBox { Name = "PreferenceCategory", Text = draft.Category, MaxLength = FeedbackStore.MaxCategory, Margin = new(0, 6, 0, 0), ToolTip = "Name the kind of task", Visibility = draft.Scope == FeedbackScope.Category ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(category);
        scope.SelectionChanged += (_, _) => category.Visibility = (scope.SelectedItem as ComboBoxItem)?.Tag is FeedbackScope.Category ? Visibility.Visible : Visibility.Collapsed;
        var notice = new TextBlock { Foreground = Theme.Brush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0),
            Text = draft.SupportingFeedbackIds.Length > 0 ? "Made from your feedback; deleting that feedback disables this preference until you restate it." : "" };
        panel.Children.Add(notice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        var cancel = new Button { Name = "CancelPreferenceButton", Content = "Cancel", Margin = new(6, 0, 0, 0), Padding = new(12, 8, 12, 8) };
        var save = new Button { Name = "SavePreferenceButton", Content = editing ? "Save changes" : "Save preference", Margin = new(6, 0, 0, 0), Padding = new(12, 8, 12, 8) };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var changed = false;
        cancel.Click += (_, _) => window.Close();
        save.Click += (_, _) =>
        {
            void Fill(PreferenceRecord r)
            {
                r.Text = text.Text.Trim();
                r.Scope = (scope.SelectedItem as ComboBoxItem)?.Tag is FeedbackScope s ? s : FeedbackScope.Project;
                r.Workspace = r.Scope == FeedbackScope.Project ? (draft.Workspace.Length > 0 ? draft.Workspace : current.Workspace) : "";
                r.TaskId = r.Scope == FeedbackScope.Task ? (draft.TaskId.Length > 0 ? draft.TaskId : current.ActiveTaskId) : "";
                r.Category = r.Scope == FeedbackScope.Category ? category.Text.Trim() : "";
            }
            try
            {
                if (editing) preferences.Update(draft.Id, Fill); else { Fill(draft); preferences.Add(draft); }
                changed = true; window.Close();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { notice.Text = ex.Message; }
        };
        window.ShowDialog();
        if (changed) StateLabel.Text = editing ? "Preference updated · applies from the next phase" : "Preference saved · applies from the next phase";
        return changed;
    }
}

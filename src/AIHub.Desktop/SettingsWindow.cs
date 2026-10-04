using AIHub.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIHub.Desktop;

public sealed class SettingsWindow : Window
{
    public HubSettings Settings { get; private set; }
    public SettingsWindow(HubSettings source)
    {
        Style = (Style)FindResource(typeof(Window));
        Settings = JsonSerializer.Deserialize<HubSettings>(JsonSerializer.Serialize(source))!;
        Title = "AI Hub · Settings & connections"; Width = 680; Height = Math.Min(720, SystemParameters.WorkArea.Height);
        MinWidth = 560; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("PanelBrush"); Foreground = Theme.Brush("TextBrush"); FontFamily = Theme.Font("BodyFont");
        var root = new DockPanel { Margin = new(25) };
        var heading = new StackPanel { Margin = new(0,0,0,20) };
        heading.Children.Add(new TextBlock { Text = "Make AI Hub work your way", FontFamily = Theme.Font("DisplayFont"), FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Collaboration, permissions, and appearance", Foreground = Theme.Brush("MutedBrush"), Margin = new(0,8,0,0) });
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);

        var footer = new StackPanel { Margin = new(0,15,0,0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("CyanBrush"), Margin = new(0,0,0,10) }; footer.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new(0,0,10,0) };
        var save = new Button { Name = "SaveSettingsButton", Content = "Save settings", Style = (Style)FindResource("PrimaryButton") };
        buttons.Children.Add(cancel); buttons.Children.Add(save); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var panel = new StackPanel { Margin = new(0,0,12,0) };
        void Description(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedBrush"), FontSize = 13, Margin = new(0,7,0,16) });
        panel.Children.Add(new TextBlock { Text = "Collaboration guard", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        Description("Auto collaborate lets agents contribute again when a peer adds something useful. A quiet pass, repeated reply, request for your input, or round limit ends the exchange.");
        var roundRow = new StackPanel { Orientation = Orientation.Horizontal };
        var rounds = new TextBox { Name = "MaxRoundsInput", Text = Math.Clamp(Settings.MaxAutoRounds,1,50).ToString(), Width = 70, VerticalContentAlignment = VerticalAlignment.Center };
        roundRow.Children.Add(rounds); roundRow.Children.Add(new TextBlock { Text = "automatic rounds before pausing", VerticalAlignment = VerticalAlignment.Center, Margin = new(12,0,0,0) }); panel.Children.Add(roundRow);
        Description("One round is one turn per agent. Choose 1–50; you can continue after reviewing their progress.");
        panel.Children.Add(new TextBlock { Text = "Collaboration strategy", FontWeight = FontWeights.SemiBold, Margin = new(0,8,0,8) });
        var strategy = new ComboBox { Name = "StrategyInput", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 300 };
        strategy.Items.Add("Reaction rounds (default)"); strategy.Items.Add("Independent answers, then synthesis (experimental)");
        strategy.SelectedIndex = Settings.Strategy == "independent" ? 1 : 0;
        panel.Children.Add(strategy);
        Description("Reaction rounds: the first agent answers, the other adds or passes, and each contribution gives the other a chance to react. Independent answers: both answer the same message without seeing each other, then the first speaker synthesizes the two; the phase ends there. Messages that name one agent keep the usual routing.");
        var shadow = new CheckBox { Name = "ShadowStrategyToggle", Content = "Record shadow strategy suggestions", IsChecked = Settings.ShadowStrategy, Margin = new(0,0,0,0) };
        panel.Children.Add(shadow); Description("For each message to both agents, note which strategy a fixed, versioned policy would have picked and why, beside the one that ran. Suggestions are only recorded, never executed; the Tasks window shows them.");
        var chooser = new CheckBox { Name = "EvaluationChooserToggle", Content = "Let the policy choose the strategy on evaluation tasks", IsChecked = Settings.EvaluationChooser, Margin = new(0,0,0,0) };
        panel.Children.Add(chooser); Description("Only on tasks you mark as evaluation tasks in the Tasks window: the policy's choice runs, with the alternative tried a quarter of the time so both strategies are seen under comparable conditions. Every choice, its probability and the policy version are recorded. Ordinary tasks are never affected.");
        var idleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var idle = new TextBox { Name = "TurnInactivityInput", Text = Math.Clamp(Settings.TurnInactivitySeconds, 30, 3600).ToString(), Width = 70, VerticalContentAlignment = VerticalAlignment.Center };
        idleRow.Children.Add(idle); idleRow.Children.Add(new TextBlock { Text = "seconds without provider output before a turn is stopped", VerticalAlignment = VerticalAlignment.Center, Margin = new(12,0,0,0) }); panel.Children.Add(idleRow);
        Description("A stuck provider pauses the conversation with a reason instead of waiting forever. Waiting for your approval or answer does not count. Choose 30–3600.");
        panel.Children.Add(new TextBlock { Text = "Project status inspector", FontWeight = FontWeights.SemiBold, Margin = new(0,8,0,8) });
        var inspector = new ComboBox { Name = "StatusInspectorInput", SelectedIndex = Settings.StatusInspector == Agent.Claude ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 170 };
        inspector.Items.Add("Codex"); inspector.Items.Add("Claude Code");
        inspector.SelectedIndex = Settings.StatusInspector == Agent.Claude ? 1 : 0;
        panel.Children.Add(inspector);
        Description("For a status check to both agents, this agent inspects; the other reviews the findings. A single-agent recipient inspects alone. Status tasks are read only and reuse matching reports for up to 30 minutes.");
        var edits = new CheckBox { Name = "AllowEditsToggle", Content = "Allow project edits", IsChecked = Settings.AllowEdits, Margin = new(0,8,0,0) };
        panel.Children.Add(edits); Description("Off: read and discuss files. On: edit the selected project, taking turns. Provider approval requests appear in AI Hub.");
        var reduced = new CheckBox { Name = "ReduceMotionToggle", Content = "Reduce motion", IsChecked = Settings.ReduceMotion, Margin = new(0,8,0,0) };
        panel.Children.Add(reduced); Description("Keep the interface still: disable transitions, handoff motion, and status pulses.");
        var diagnostics = new CheckBox { Name = "LocalDiagnosticsToggle", Content = "Collect local diagnostics", IsChecked = Settings.CollectLocalDiagnostics, Margin = new(0,8,0,0) };
        panel.Children.Add(diagnostics); Description("Keep bounded local error and activity metadata, excluding chat and tool text. Reviews run only when you request them. Turning this off stops collection and retains existing findings.");
        var push = new CheckBox { Name = "MidTurnPushToggle", Content = "Deliver my messages to Claude Code mid-turn (experimental)", IsChecked = Settings.MidTurnPush, Margin = new(0,8,0,0) };
        panel.Children.Add(push); Description("While Claude Code is working, a message you send reaches it immediately through a channel instead of waiting for its next turn. Codex always sees messages at its next turn. Needs a Claude Code build with channels; saving checks that before enabling.");
        var worktrees = new CheckBox { Name = "IsolateWorktreesToggle", Content = "Give each agent its own git worktree for edit-enabled tasks (experimental)", IsChecked = Settings.IsolateAgentWorktrees, Margin = new(0,8,0,0) };
        panel.Children.Add(worktrees); Description("In a git project with edits enabled, Codex and Claude Code each edit in their own worktree. The Hub commits each turn and merges it into an integration branch; conflicts are reported instead of resolved silently. Your project folder changes only when you choose Merge into project in Tasks.");

        var advancedPanel = new StackPanel { Margin = new(0,12,0,0) };
        TextBox Field(string label, string value)
        {
            advancedPanel.Children.Add(new TextBlock { Text = label, Margin = new(0,10,0,5), TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedBrush") });
            var input = new TextBox { Text = value }; advancedPanel.Children.Add(input); return input;
        }
        var codex = Field("Codex executable (blank = automatically detect)", Settings.CodexPath);
        var claude = Field("Claude Code executable (blank = automatically detect)", Settings.ClaudePath);
        var codexModel = Field("Codex model (blank = " + CodexClient.DefaultModel + ", AI Hub's default; the CLI's own default is used if this install rejects it)", Settings.CodexModel);
        var claudeModel = Field("Claude model (blank = CLI default)", Settings.ClaudeModel);
        advancedPanel.Children.Add(new TextBlock { Text = "Uses your existing CLI sign-ins. AI Hub does not store API keys.", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("MutedBrush"), FontSize = 12, Margin = new(0,12,0,12) });
        var check = new Button { Content = "Check installed tools", HorizontalAlignment = HorizontalAlignment.Left };
        advancedPanel.Children.Add(check);
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            try
            {
                var results = new List<string>();
                foreach (var pair in new[] { ("codex", codex.Text), ("claude", claude.Text) })
                {
                    var path = JsonProcess.FindExecutable(pair.Item2, pair.Item1);
                    using var process = Process.Start(new ProcessStartInfo(path,"--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        var output = Capture(process.StandardOutput);
                        var error = Capture(process.StandardError);
                        await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
                        results.Add(process.ExitCode == 0 ? (await output).Trim() : (await error).Trim());
                        async Task<string> Capture(System.IO.StreamReader reader)
                        {
                            try { return await BoundedText.ReadAsync(reader, 16000, timeout.Token); }
                            catch { timeout.Cancel(); throw; }
                        }
                    }
                    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree:true); throw new TimeoutException(pair.Item1 + " did not respond to the version check."); }
                    finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                }
                status.Text = string.Join("\n",results) + "\nInstallation check only; a reply verifies sign-in.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { check.IsEnabled = true; }
        };
        panel.Children.Add(new Expander { Name = "AdvancedSettings", Header = "Advanced connections and models", Content = advancedPanel, Foreground = Foreground, Margin = new(0,12,0,0) });
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        save.Click += async (_, _) =>
        {
            if (!int.TryParse(rounds.Text,out var count) || count is < 1 or > 50) { status.Text = "Enter a whole number between 1 and 50 for automatic rounds."; rounds.Focus(); rounds.SelectAll(); return; }
            if (!int.TryParse(idle.Text, out var seconds) || seconds is < 30 or > 3600) { status.Text = "Enter a whole number between 30 and 3600 seconds for the inactivity limit."; idle.Focus(); idle.SelectAll(); return; }
            if (push.IsChecked == true && !Settings.MidTurnPush)
            {
                // Enabling is refused unless this Claude Code build accepts the channels flag; otherwise every structured turn would fail to start.
                save.IsEnabled = false; status.Text = "Checking Claude Code for channel support…";
                try
                {
                    var path = JsonProcess.FindExecutable(claude.Text.Trim(), "claude");
                    using var process = Process.Start(new ProcessStartInfo(path) { ArgumentList = { ClaudeClient.ChannelFlag, "server:ai_hub", "--version" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var error = BoundedText.ReadAsync(process.StandardError, 16000, timeout.Token); var output = BoundedText.ReadAsync(process.StandardOutput, 16000, timeout.Token);
                    try { await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)); }
                    finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    if (process.ExitCode != 0) { status.Text = "This Claude Code build does not accept the channels flag, so mid-turn delivery stays off: " + (await error).Trim(); push.IsChecked = false; return; }
                }
                catch (Exception ex) { status.Text = "Could not check Claude Code for channel support: " + ex.Message; push.IsChecked = false; return; }
                finally { save.IsEnabled = true; }
            }
            Settings.MidTurnPush = push.IsChecked == true;
            Settings.IsolateAgentWorktrees = worktrees.IsChecked == true;
            Settings.TurnInactivitySeconds = seconds;
            Settings.CodexPath = codex.Text.Trim(); Settings.ClaudePath = claude.Text.Trim();
            Settings.CodexModel = codexModel.Text.Trim(); Settings.ClaudeModel = claudeModel.Text.Trim();
            Settings.AllowEdits = edits.IsChecked == true; Settings.ReduceMotion = reduced.IsChecked == true; Settings.MaxAutoRounds = count;
            Settings.StatusInspector = inspector.SelectedIndex == 1 ? Agent.Claude : Agent.Codex;
            Settings.Strategy = strategy.SelectedIndex == 1 ? "independent" : "reaction";
            Settings.ShadowStrategy = shadow.IsChecked == true;
            Settings.EvaluationChooser = chooser.IsChecked == true;
            Settings.CollectLocalDiagnostics = diagnostics.IsChecked == true;
            DialogResult = true;
        };
    }
}

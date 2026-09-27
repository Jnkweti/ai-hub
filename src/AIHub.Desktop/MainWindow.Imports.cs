using AIHub.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace AIHub.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? fileImport;
    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || switching || sending || closing || current.IsArchived || activeQuestion is not null) return;
        var dialog = new OpenFileDialog { Title = "Import files into the current project folder", Multiselect = true,
            CheckFileExists = true, Filter = "All files (*.*)|*.*", InitialDirectory = current.Workspace };
        if (dialog.ShowDialog(this) != true) return;
        sending = true; UpdateConversationControls();
        using var lifetime = new CancellationTokenSource(); fileImport = lifetime;
        var imported = 0; var failures = new List<string>();
        try
        {
            foreach (var source in dialog.FileNames)
            {
                try
                {
                    var result = await WorkspaceImports.CopyAsync(source, current.Workspace, lifetime.Token);
                    current.Draft += (current.Draft.Length == 0 ? "" : "\n\n") + result.Reference + "\n";
                    if (activeQuestion is null) Composer.Text = current.Draft;
                    Save(); imported++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { failures.Add(Path.GetFileName(source) + ": " + ex.Message); }
            }
            StateLabel.Text = imported > 0 ? $"{imported} file(s) ready in {current.Workspace}. Add your question and send." : "No files imported.";
            if (failures.Count > 0) StateLabel.Text += "\n" + string.Join("\n", failures);
        }
        catch (OperationCanceledException) { StateLabel.Text = "Import stopped. Completed imports remain in your draft."; }
        finally
        {
            fileImport = null; sending = false; UpdateConversationControls();
            if (closeAfterSwitch) { closeAfterSwitch = false; Close(); }
            else { Composer.Focus(); Composer.CaretIndex = Composer.Text.Length; }
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AIHub.Desktop.Controls;

public partial class AgentRequestCard : UserControl
{
    public event EventHandler? Activated;
    public event EventHandler? Submitted;
    public event EventHandler? Declined;
    public event EventHandler? Allowed;
    public AgentRequestCard() => InitializeComponent();
    private void Activate_Click(object sender, RoutedEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);
    private void Submit_Click(object sender, RoutedEventArgs e) => Submitted?.Invoke(this, EventArgs.Empty);
    private void Decline_Click(object sender, RoutedEventArgs e) => Declined?.Invoke(this, EventArgs.Empty);
    private void Allow_Click(object sender, RoutedEventArgs e) => Allowed?.Invoke(this, EventArgs.Empty);
    private void Choices_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (!e.IsRepeat) Submitted?.Invoke(this, EventArgs.Empty);
    }
}

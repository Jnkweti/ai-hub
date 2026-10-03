using AIHub.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;
namespace AIHub.Desktop;
public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class MessageView(SavedMessage saved) : Observable
{
    public SavedMessage Saved { get; } = saved;
    public RequestView? Request { get; } = saved.Input is null ? null : new(saved.Input, saved.Speaker);
    public string Speaker => Saved.Speaker == "Claude" ? "Claude Code" : Saved.Speaker;
    public string IconKind => Speaker == "Codex" ? "terminal" : Speaker == "Claude Code" ? "sparkles" : Speaker == "AI Hub" ? "activity" : "circle-dot";
    public string Meta => Saved.Time.ToString("h:mm tt") + (Saved.Speaker == "You" && Saved.Route is "Codex" or "Claude" ? "  to " + (Saved.Route == "Claude" ? "Claude Code" : "Codex") : "");
    public Brush Color => Theme.Brush(Speaker == "Codex" ? "CyanBrush" : Speaker == "Claude Code" ? "ClaudeBrush" : "TextBrush");
    public Brush AvatarBackground => Theme.Brush(Speaker == "Codex" ? "CodexSurfaceBrush" : Speaker == "Claude Code" ? "ClaudeSurfaceBrush" : "HoverBrush");
    public bool IsStructured => Saved.Collaboration is not null;
    public string BodyText => Saved.Collaboration is { } message
        ? $"**{message.Content.Type.Replace('_', ' ')}** · {message.Envelope.Sender} → {message.Envelope.Recipient?.ToString() ?? "AI Hub"}\n\n{message.Content.Summary}"
        : Text;
    public string Text { get => Saved.Text; set { Saved.Text = value; Changed(); Changed(nameof(BodyText)); } }
    // Developer feedback recorded on this message, shown as a small badge; set by the window from the feedback store.
    private string feedbackLabel = "";
    public string FeedbackLabel { get => feedbackLabel; set { if (feedbackLabel == value) return; feedbackLabel = value; Changed(); Changed(nameof(HasFeedback)); } }
    public bool HasFeedback => feedbackLabel.Length > 0;
    public bool CanRate => Saved.Speaker is "Codex" or "Claude" or "AI Hub" && Saved.Complete;
}
public sealed class ChoiceView(QuestionChoice choice, Action<ChoiceView> selected) : Observable
{
    public string Label => choice.Label;
    public string Description => choice.Description;
    private bool isSelected;
    public bool IsSelected { get => isSelected; set { if (isSelected == value) return; isSelected = value; Changed(); selected(this); } }
}
public sealed class RequestView : Observable
{
    public SavedInput Saved { get; }
    public string AgentName { get; }
    public ChoiceView[] Choices { get; }
    public string Title => Saved.Title.Length > 0 ? Saved.Title : Saved.IsQuestion ? "Your input" : "Permission requested";
    public bool IsQuestion => Saved.IsQuestion;
    public bool IsSecret => Saved.IsSecret;
    public bool AllowFreeText => Saved.AllowFreeText;
    public bool IsPending { get; private set; }
    public bool IsActive { get; private set; }
    public string AnswerText { get; set; } = "";
    public bool CanSend => IsPending && IsQuestion && (AllowFreeText && !string.IsNullOrWhiteSpace(AnswerText) || Choices.Any(c => c.IsSelected));
    public string Answer => AllowFreeText && !string.IsNullOrWhiteSpace(AnswerText) ? AnswerText.Trim() : string.Join(", ", Choices.Where(c => c.IsSelected).Select(c => c.Label));
    public string Hint => IsQuestion ? Choices.Length == 0 ? "Type your answer below. Enter to send." :
        (Saved.MultiSelect ? "Select one or more choices" : "Select a choice") + (AllowFreeText ? ", or type your own answer below. Enter to send." : ". Enter to send.") : "Review this action, then allow it once or decline.";
    public string Status => IsPending ? IsActive ? "Answering here" : "Waiting for you" : Saved.Status switch
    { InputStatus.Answered => IsQuestion ? "Answered" : "Allowed once", InputStatus.Declined => "Declined", _ => "Cancelled" };
    public event Action<RequestView>? InputChanged;
    private bool selecting;
    public RequestView(SavedInput saved, string agent)
    {
        Saved = saved; AgentName = agent == "Claude" ? "Claude Code" : agent;
        Choices = saved.Options.Select(o => new ChoiceView(o, Selected)).ToArray();
    }
    private void Selected(ChoiceView selected)
    {
        if (selecting || !IsPending) return;
        selecting = true;
        if (selected.IsSelected && !Saved.MultiSelect) foreach (var choice in Choices.Where(c => c != selected)) choice.IsSelected = false;
        selecting = false;
        AnswerText = ""; Changed(nameof(CanSend)); InputChanged?.Invoke(this);
    }
    public void Activate(bool active) { IsActive = active; Changed(nameof(IsActive)); Changed(nameof(Status)); }
    public void SetAnswerText(string text)
    {
        AnswerText = text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            selecting = true;
            foreach (var choice in Choices) choice.IsSelected = false;
            selecting = false;
        }
        Changed(nameof(CanSend));
    }
    public void Start() { IsPending = true; Refresh(); }
    public void Finish(InputStatus status) { Saved.Status = status; IsPending = false; IsActive = false; AnswerText = ""; Refresh(); }
    public void Refresh() { Changed(nameof(IsPending)); Changed(nameof(IsActive)); Changed(nameof(Status)); Changed(nameof(CanSend)); }
}
public sealed class ActivityAgentColor : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Theme.Brush(value as string == "Codex" ? "CyanBrush" : value as string is "Claude" or "Claude Code" ? "ClaudeBrush" : "MutedBrush");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

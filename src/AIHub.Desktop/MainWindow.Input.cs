using AIHub.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIHub.Desktop;

public partial class MainWindow
{
    private sealed class PendingInput(Approval approval, Room room, HubCoordinator owner, MessageView message,
        CancellationToken token, TaskCompletionSource<Decision> completion)
    {
        public Approval Approval { get; } = approval;
        public Room Room { get; } = room;
        public HubCoordinator Owner { get; } = owner;
        public MessageView Message { get; } = message;
        public RequestView View => Message.Request!;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<Decision> Completion { get; } = completion;
        public CancellationTokenRegistration Registration { get; set; }
    }
    private readonly Dictionary<RequestView, PendingInput> pendingInputs = [];
    private RequestView? activeQuestion;

    private Task<Decision> AskAsync(Approval approval, Room room, HubCoordinator owner, CancellationToken token)
    {
        var completion = new TaskCompletionSource<Decision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            if (token.IsCancellationRequested || closing || !OwnsWorker(room, owner))
            { completion.TrySetCanceled(); return; }
            var saved = new SavedMessage
            {
                Speaker = approval.Agent.ToString(), Route = "You",
                TaskId = owner.TaskId,
                Text = approval.IsQuestion ? approval.Detail : "**" + approval.Title + "**\n\n```json\n" + approval.Detail + "\n```",
                Input = new() { Title = approval.Title, IsQuestion = approval.IsQuestion, Options = approval.Options ?? [],
                    MultiSelect = approval.MultiSelect, AllowFreeText = approval.AllowFreeText, IsSecret = approval.IsSecret }
            };
            var message = new MessageView(saved);
            var pending = new PendingInput(approval, room, owner, message, token, completion);
            pendingInputs.Add(pending.View, pending);
            pending.View.Start(); pending.View.InputChanged += RequestChoiceChanged;
            room.Messages.Add(saved);
            if (ReferenceEquals(current, room)) messages.Add(message);
            pending.Registration = token.Register(() =>
            {
                // Unblock the provider immediately, even while the dispatcher is closing a room.
                completion.TrySetCanceled(token);
                Dispatcher.BeginInvoke(() => FinishInput(pending, null));
            });
            workers[room.Id].Activity.AddNotice(approval.Agent.ToString(), "Waiting for your input", approval.Title);
            if (ReferenceEquals(current, room))
            {
                RefreshActivity(); SetAgentState(approval.Agent, "Needs your input", true); ShowConversation();
                if (approval.IsQuestion && activeQuestion is null) ActivateQuestion(pending.View);
                else RefreshInputComposer();
                if (activeQuestion is null) { followChat = true; ChatScroll.ScrollToEnd(); }
                else RevealQuestion(activeQuestion);
            }
            RefreshTaskSummary();
            Save();
        });
        return completion.Task;
    }

    private void ActivateQuestion(RequestView question)
    {
        if (!question.IsQuestion || !pendingInputs.TryGetValue(question, out var pending) || !ReferenceEquals(current, pending.Room) || !question.IsPending) return;
        activeQuestion?.Activate(false);
        activeQuestion = question; question.Activate(true);
        LoadInputComposer();
        RevealQuestion(question);
    }
    private void RequestChoiceChanged(RequestView question)
    {
        ActivateQuestion(question);
        UpdateSendButton();
    }
    private void LoadInputComposer()
    {
        loadingComposer = true;
        Composer.Text = activeQuestion?.IsSecret == true ? "" : activeQuestion?.AnswerText ?? current.Draft;
        SecretAnswer.Password = activeQuestion?.IsSecret == true ? activeQuestion.AnswerText : "";
        loadingComposer = false;
        RefreshInputComposer();
    }
    private void RefreshInputComposer()
    {
        var question = activeQuestion;
        var count = pendingInputs.Values.Count(p => ReferenceEquals(current, p.Room));
        AnswerBanner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnswerLabel.Text = question is null ? "An agent is waiting for permission in the chat" : "Answering " + question.AgentName + ": " + question.Title;
        if (count > 1) AnswerLabel.Text += $" ({count} requests waiting)";
        SendLabel.Text = question is null ? "Send" : "Send answer";
        AutomationProperties.SetName(SendButton, question is null ? "Send message" : "Send answer to " + question.AgentName);
        AutomationProperties.SetName(Composer, question is null ? "Message your agents" : "Answer " + question.AgentName);
        Composer.Visibility = question?.IsSecret == true ? Visibility.Collapsed : Visibility.Visible;
        SecretAnswer.Visibility = question?.IsSecret == true ? Visibility.Visible : Visibility.Collapsed;
        Composer.IsReadOnly = current.IsArchived || question is { AllowFreeText: false };
        ComposerHint.Text = question is not null ? !question.AllowFreeText ? "Select a choice in the chat, then press Enter" :
            question.IsSecret ? "Enter your private answer" : "Answer " + question.AgentName + "…" :
            current.IsArchived ? "Restore this conversation to send a message" : current.PauseReason.Length > 0 ? "Tell your agents what to do next…" : "Give your agents a direction…";
        ComposerHint.Visibility = string.IsNullOrEmpty(question?.IsSecret == true ? SecretAnswer.Password : Composer.Text) ? Visibility.Visible : Visibility.Collapsed;
        Target.IsEnabled = ready && !switching && !sending && !closing && !current.IsArchived && question is null;
        ImportFilesButton.IsEnabled = Target.IsEnabled;
        UpdateSendButton();
    }
    private void SecretAnswer_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingComposer || activeQuestion is not { IsSecret: true } question) return;
        question.SetAnswerText(SecretAnswer.Password); RefreshInputComposer();
    }
    private void FocusAnswer()
    { if (activeQuestion?.IsSecret == true) SecretAnswer.Focus(); else Composer.Focus(); }
    private void Request_Activated(object? sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RequestView request) return;
        ActivateQuestion(request); FocusAnswer();
    }
    private void Request_Submitted(object? sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RequestView request) SubmitQuestion(request);
    }
    private void Request_Declined(object? sender, EventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is RequestView request) ResolveInput(request, new(false)); }
    private void Request_Allowed(object? sender, EventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is RequestView { IsQuestion: false } request) ResolveInput(request, new(true)); }
    private void SubmitQuestion(RequestView request)
    {
        if (!request.CanSend) { ActivateQuestion(request); FocusAnswer(); return; }
        ResolveInput(request, new(true, request.Answer));
    }
    private void ResolveInput(RequestView request, Decision decision)
    {
        if (switching || sending || closing || current.IsArchived || !pendingInputs.TryGetValue(request, out var pending)) return;
        if (pending.Token.IsCancellationRequested || pending.Completion.Task.IsCompleted || !ReferenceEquals(current, pending.Room) || !ReferenceEquals(hub, pending.Owner))
        { FinishInput(pending, null); return; }
        FinishInput(pending, decision);
        FocusAnswer();
    }
    private void FinishInput(PendingInput pending, Decision? decision)
    {
        if (!pendingInputs.Remove(pending.View)) return;
        pending.Registration.Dispose(); pending.View.InputChanged -= RequestChoiceChanged;
        pending.View.Finish(decision is null ? InputStatus.Cancelled : decision.Allow ? InputStatus.Answered : InputStatus.Declined);
        if (decision is not null)
        {
            var text = decision.Allow ? pending.Approval.IsQuestion ? pending.Approval.IsSecret ? "[Private answer sent]" : decision.Answer : "Allowed once: " + pending.Approval.Title : "Declined: " + pending.Approval.Title;
            var answer = new SavedMessage { Text = text, Route = pending.Approval.Agent.ToString(), TaskId = pending.Owner.TaskId };
            pending.Room.Messages.Add(answer);
            if (ReferenceEquals(current, pending.Room)) { messages.Add(new(answer)); followChat = true; ChatScroll.ScrollToEnd(); }
        }
        if (ReferenceEquals(activeQuestion, pending.View))
        {
            activeQuestion = null;
            var next = pendingInputs.Values.FirstOrDefault(p => ReferenceEquals(current, p.Room) && p.View.IsQuestion)?.View;
            if (next is not null) ActivateQuestion(next); else LoadInputComposer();
        }
        else if (ReferenceEquals(current, pending.Room)) RefreshInputComposer();
        if (decision is not null && ReferenceEquals(current, pending.Room) && !pending.Token.IsCancellationRequested)
            SetAgentState(pending.Approval.Agent, pendingInputs.Values.Any(p => ReferenceEquals(current, p.Room) && p.Approval.Agent == pending.Approval.Agent) ? "Needs your input" : "Thinking", true);
        RefreshTaskSummary();
        Save();
        if (decision is null) pending.Completion.TrySetCanceled(); else pending.Completion.TrySetResult(decision);
    }
    private void CancelPendingInputs(bool allRooms = false)
    { foreach (var pending in pendingInputs.Values.Where(p => allRooms || ReferenceEquals(current, p.Room)).ToArray()) FinishInput(pending, null); }
    private void ShowQuestion_Click(object sender, RoutedEventArgs e)
    {
        var question = activeQuestion ?? pendingInputs.Values.FirstOrDefault(p => ReferenceEquals(current, p.Room))?.View;
        if (question is not null) RevealQuestion(question);
    }
    private void RevealQuestion(RequestView question)
    {
        if (!pendingInputs.TryGetValue(question, out var pending)) return;
        followChat = false;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (!pendingInputs.ContainsKey(question) || !ReferenceEquals(current, pending.Room)) return;
            (MessageList.ItemContainerGenerator.ContainerFromItem(pending.Message) as FrameworkElement)?.BringIntoView();
        });
    }
}

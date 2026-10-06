using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TodoTracker.Desktop;

/// <summary>A question the agent asks before acting (e.g. "Change your tasks: create_task").</summary>
public sealed record AskQuestion(string Id, string Text, IReadOnlyList<string> Choices);

/// <summary>What the sidebar shows of the chat: the latest answer, a question waiting for you, or what to install.</summary>
public sealed record AskState(string Status, bool HasAgent, string? AgentName, string? Hint, string? Reply, AskQuestion? Question, string? Problem);

/// <summary>The in-app agent chat (the Ask AI plugin), as the sidebar needs it.</summary>
public interface IAskAgent
{
    event EventHandler<AskState>? Changed;

    AskState State { get; }

    Task SendAsync(string text);

    Task AnswerAsync(string questionId, string choice);

    Task CancelAsync();
}

/// <summary>One choice on a question, ready for a button.</summary>
public sealed record AskChoice(string Id, string Label, bool IsPrimary);

/// <summary>
/// "Ask AI" at the bottom of the sidebar: one line to type in, the latest answer under it, and the agent's
/// questions as buttons. The full conversation lives in the dashboard's Ask AI panel.
/// </summary>
public sealed partial class AskViewModel : ObservableObject, IDisposable
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["allow"] = "Allow",
        ["allow-chat"] = "Allow for this chat",
        ["reject"] = "Don't allow",
    };

    private readonly IAskAgent _agent;
    private readonly Action<Action> _runOnUi;
    private readonly Action _openFullChat;

    public AskViewModel(IAskAgent agent, Action<Action> runOnUi, Action openFullChat)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _runOnUi = runOnUi ?? throw new ArgumentNullException(nameof(runOnUi));
        _openFullChat = openFullChat ?? throw new ArgumentNullException(nameof(openFullChat));
        _agent.Changed += OnChanged;
        Apply(_agent.State);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasAgent { get; set; }

    [ObservableProperty]
    public partial string Placeholder { get; set; } = "Ask AI…";

    /// <summary>The latest answer, a problem, or what to install; null when there's nothing to show.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsProblem { get; set; }

    [ObservableProperty]
    public partial string? QuestionText { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<AskChoice> Choices { get; } = [];

    private string? QuestionId { get; set; }

    public void Dispose() => _agent.Changed -= OnChanged;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Draft.Trim();
        try
        {
            var turn = _agent.SendAsync(text);
            Draft = string.Empty;
            IsBusy = true;
            await turn.ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            Message = ex.Message;
            IsProblem = true;
        }
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand]
    private Task Answer(string? choice) =>
        QuestionId is { } id && choice is not null ? _agent.AnswerAsync(id, choice) : Task.CompletedTask;

    [RelayCommand]
    private Task Stop() => _agent.CancelAsync();

    [RelayCommand]
    private void OpenFullChat() => _openFullChat();

    [RelayCommand]
    private void Dismiss()
    {
        Message = null;
        IsProblem = false;
    }

    private void OnChanged(object? sender, AskState state) => _runOnUi(() => Apply(state));

    private void Apply(AskState state)
    {
        HasAgent = state.HasAgent;
        IsBusy = state.Status is "busy" or "starting";
        Placeholder = state.HasAgent ? $"Ask {state.AgentName ?? "AI"}… e.g. add: call the bank tomorrow" : "Ask AI (install Copilot CLI or Claude Code)";
        if (!state.HasAgent)
        {
            Message = state.Hint;
            IsProblem = false;
        }
        else if (state.Status == "error" && state.Problem is { } problem)
        {
            Message = problem;
            IsProblem = true;
        }
        else if (state.Status == "starting")
        {
            Message = "Starting…";
            IsProblem = false;
        }
        else if (state.Reply is { Length: > 0 } reply)
        {
            Message = reply;
            IsProblem = false;
        }
        else if (IsBusy)
        {
            Message = "Thinking…";
            IsProblem = false;
        }
        else
        {
            Message = null;
            IsProblem = false;
        }

        QuestionId = state.Question?.Id;
        QuestionText = state.Question?.Text;
        Choices.Clear();
        foreach (var choice in state.Question?.Choices ?? [])
        {
            Choices.Add(new AskChoice(choice, Labels.GetValueOrDefault(choice, choice), choice == "allow"));
        }
    }
}

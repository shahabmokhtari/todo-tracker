using TodoTracker.Desktop;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Windows;

/// <summary>The sidebar's view of the Ask AI plugin's chat (the same chat the dashboard shows).</summary>
internal sealed class AgentChatAsk : IAskAgent, IDisposable
{
    private readonly AgentChatService _chat;

    public AgentChatAsk(AgentChatService chat)
    {
        _chat = chat;
        _chat.Changed += OnChanged;
    }

    public event EventHandler<AskState>? Changed;

    public AskState State => Map(_chat.State);

    public Task SendAsync(string text) => _chat.SendAsync(text);

    public Task AnswerAsync(string questionId, string choice) => _chat.AnswerAsync(questionId, choice);

    public Task CancelAsync() => _chat.CancelAsync();

    public void Dispose() => _chat.Changed -= OnChanged;

    internal static AskState Map(ChatState state)
    {
        var agent = state.Agents.FirstOrDefault(a => a.Id == state.Agent);
        var lastUser = state.Entries.ToList().FindLastIndex(e => e.Kind == "user");
        var reply = state.Entries.Skip(lastUser + 1).LastOrDefault(e => e.Kind is "agent" or "note")?.Text;
        var question = state.Entries.LastOrDefault(e => e.Kind == "permission" && e.Status == "waiting" && e.Choices is { Count: > 0 });
        return new AskState(
            state.Status,
            agent is { Installed: true },
            agent?.Name,
            agent is { Installed: true } ? null : state.Agents.Select(a => a.Hint).FirstOrDefault(h => h is not null),
            reply,
            question is null ? null : new AskQuestion(question.Id, question.Text, question.Choices!),
            state.Problem,
            state.Version);
    }

    private void OnChanged(object? sender, ChatState state) => Changed?.Invoke(this, Map(state));
}

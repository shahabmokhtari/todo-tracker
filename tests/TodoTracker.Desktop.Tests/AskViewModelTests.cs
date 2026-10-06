namespace TodoTracker.Desktop.Tests;

public sealed class AskViewModelTests
{
    private sealed class FakeAgent : IAskAgent
    {
        public event EventHandler<AskState>? Changed;

        public AskState State { get; private set; } = new("idle", true, "GitHub Copilot", null, null, null, null);

        public List<string> Sent { get; } = [];

        public List<(string Id, string Choice)> Answers { get; } = [];

        public TaskCompletionSource Turn { get; set; } = new();

        public bool Busy { get; set; }

        public int Cancels { get; private set; }

        public Task SendAsync(string text)
        {
            if (Busy)
            {
                throw new InvalidOperationException("Wait for the answer (or stop it) first.");
            }

            Sent.Add(text);
            Set(State with { Status = "busy" });
            return Turn.Task;
        }

        public Task AnswerAsync(string questionId, string choice)
        {
            Answers.Add((questionId, choice));
            return Task.CompletedTask;
        }

        public Task CancelAsync()
        {
            Cancels++;
            return Task.CompletedTask;
        }

        public void Set(AskState state)
        {
            State = state;
            Changed?.Invoke(this, state);
        }
    }

    private readonly FakeAgent _agent = new();
    private int _opened;

    private AskViewModel Create() => new(_agent, run => run(), () => _opened++);

    [Fact]
    public async Task Sending_clears_the_box_and_shows_the_answer_when_it_comes()
    {
        using var vm = Create();
        vm.Draft = "add: call the bank tomorrow";

        var sending = vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.Draft);
        Assert.True(vm.IsBusy);
        Assert.Equal("Thinking…", vm.Message);

        _agent.Set(_agent.State with { Status = "ready", Reply = "Added \"Call the bank\" for tomorrow 9:00." });
        _agent.Turn.SetResult();
        await sending;

        Assert.Equal(["add: call the bank tomorrow"], _agent.Sent);
        Assert.False(vm.IsBusy);
        Assert.Equal("Added \"Call the bank\" for tomorrow 9:00.", vm.Message);
    }

    [Fact]
    public void Questions_become_buttons_and_answers_go_back_to_the_agent()
    {
        using var vm = Create();

        _agent.Set(_agent.State with { Status = "busy", Question = new AskQuestion("e4", "Change your tasks: create_task", ["allow", "allow-chat", "reject"]) });

        Assert.Equal("Change your tasks: create_task", vm.QuestionText);
        Assert.Equal(["Allow", "Allow for this chat", "Don't allow"], vm.Choices.Select(c => c.Label));
        Assert.True(vm.Choices[0].IsPrimary);

        vm.AnswerCommand.Execute("allow-chat");
        Assert.Equal([("e4", "allow-chat")], _agent.Answers);

        _agent.Set(_agent.State with { Question = null });
        Assert.Null(vm.QuestionText);
        Assert.Empty(vm.Choices);
    }

    [Fact]
    public void Without_an_agent_it_says_what_to_install_and_problems_are_flagged()
    {
        _agent.Set(new AskState("idle", false, null, "Install GitHub Copilot CLI…", null, null, null));
        using var vm = Create();

        Assert.False(vm.HasAgent);
        Assert.Equal("Install GitHub Copilot CLI…", vm.Message);

        _agent.Set(new AskState("error", true, "Claude Code", null, null, null, "The agent stopped."));
        Assert.True(vm.IsProblem);
        Assert.Equal("The agent stopped.", vm.Message);
        vm.DismissCommand.Execute(null);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task Busy_errors_are_shown_and_stop_and_full_chat_work()
    {
        using var vm = Create();
        _agent.Busy = true;
        vm.Draft = "hi";

        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsProblem);
        Assert.Equal("hi", vm.Draft);

        await vm.StopCommand.ExecuteAsync(null);
        vm.OpenFullChatCommand.Execute(null);
        Assert.Equal(1, _agent.Cancels);
        Assert.Equal(1, _opened);
    }

    [Fact]
    public void Send_needs_text()
    {
        using var vm = Create();

        Assert.False(vm.SendCommand.CanExecute(null));
        vm.Draft = "  ";
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.Draft = "hi";
        Assert.True(vm.SendCommand.CanExecute(null));
    }
}

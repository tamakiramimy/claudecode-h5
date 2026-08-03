using System.Collections.Concurrent;

namespace ClaudeCodeCliHarness.Services;

public sealed class ClaudeConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();

    public bool TryBegin(string browserSessionId, out ClaudeRun run)
    {
        var state = _conversations.GetOrAdd(browserSessionId, _ => new ConversationState());
        lock (state)
        {
            if (state.ActiveRun is not null)
            {
                run = default!;
                return false;
            }

            var cancellation = new CancellationTokenSource();
            run = new ClaudeRun(cancellation);
            state.ActiveRun = run;
            return true;
        }
    }

    public string? GetClaudeSessionId(string browserSessionId) =>
        _conversations.TryGetValue(browserSessionId, out var state) ? state.ClaudeSessionId : null;

    public void SetClaudeSessionId(string browserSessionId, string sessionId)
    {
        var state = _conversations.GetOrAdd(browserSessionId, _ => new ConversationState());
        lock (state)
        {
            state.ClaudeSessionId = sessionId;
        }
    }

    public void Complete(string browserSessionId, ClaudeRun run)
    {
        if (!_conversations.TryGetValue(browserSessionId, out var state))
        {
            run.Dispose();
            return;
        }

        lock (state)
        {
            if (ReferenceEquals(state.ActiveRun, run))
            {
                state.ActiveRun = null;
            }
        }

        run.Dispose();
    }

    public bool Stop(string browserSessionId)
    {
        if (!_conversations.TryGetValue(browserSessionId, out var state))
        {
            return false;
        }

        lock (state)
        {
            if (state.ActiveRun is null)
            {
                return false;
            }

            state.ActiveRun.Cancel();
            return true;
        }
    }

    public void Reset(string browserSessionId)
    {
        if (_conversations.TryRemove(browserSessionId, out var state))
        {
            lock (state)
            {
                state.ActiveRun?.Cancel();
            }
        }
    }

    private sealed class ConversationState
    {
        public string? ClaudeSessionId { get; set; }

        public ClaudeRun? ActiveRun { get; set; }
    }
}

public sealed class ClaudeRun(CancellationTokenSource source) : IDisposable
{
    public CancellationToken Token => source.Token;

    public void Cancel() => source.Cancel();

    public void Dispose() => source.Dispose();
}
using System.Collections.Concurrent;

namespace AetherfitSignaling;

// In-memory only - a restart just means any in-flight pairing codes need to be regenerated.
internal sealed class SessionStore
{
    private static readonly TimeSpan UnmatchedTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, PairSession> sessions = new(StringComparer.OrdinalIgnoreCase);

    public PairSession GetOrCreate(string code) => sessions.GetOrAdd(code, static c => new PairSession(c));

    public void RemoveIfCurrent(PairSession session)
        => sessions.TryRemove(new KeyValuePair<string, PairSession>(session.Code, session));

    public async Task RunSweepLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var (code, session) in sessions)
            {
                var idle = now - session.LastActivityAt > IdleTtl;
                var abandoned = !session.IsMatched && now - session.CreatedAt > UnmatchedTtl;
                if (idle || abandoned)
                    sessions.TryRemove(code, out _);
            }
        }
    }
}

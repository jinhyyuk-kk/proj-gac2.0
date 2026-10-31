using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gac.Shared.Contracts;

namespace Gac.Shared.Security;

public enum CommandVerifyResult { Ok, BadSignature, Expired, FromFuture, Replayed, WrongAgent, UnknownAction }

/// <summary>HMAC-SHA256 signing of server → agent commands (CLAUDE.md section 8 rule 1).</summary>
public static class CommandSigner
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxClockSkewAhead = TimeSpan.FromMinutes(1);

    public static string Canonical(AgentCommand c) => string.Join('\n',
        c.Id.ToString("N"),
        c.AgentId,
        c.Module,
        c.Action,
        c.IssuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        c.PayloadJson);

    public static AgentCommand Sign(AgentCommand command, byte[] key)
    {
        var sig = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(command)));
        return command with { Signature = Convert.ToBase64String(sig) };
    }

    /// <summary>Verifies signature, agent, age and replay. On success the command ID is recorded in <paramref name="replay"/>.</summary>
    public static CommandVerifyResult Verify(AgentCommand command, byte[] key, string agentId, DateTimeOffset now, ReplayGuard replay)
    {
        byte[] given;
        try { given = Convert.FromBase64String(command.Signature); }
        catch (FormatException) { return CommandVerifyResult.BadSignature; }

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(command)));
        if (!CryptographicOperations.FixedTimeEquals(given, expected)) return CommandVerifyResult.BadSignature;
        if (!string.Equals(command.AgentId, agentId, StringComparison.Ordinal)) return CommandVerifyResult.WrongAgent;
        if (now - command.IssuedAt > MaxAge) return CommandVerifyResult.Expired;
        if (command.IssuedAt - now > MaxClockSkewAhead) return CommandVerifyResult.FromFuture;
        if (!ModuleActions.IsKnown(command.Module, command.Action)) return CommandVerifyResult.UnknownAction;
        if (!replay.TryRegister(command.Id)) return CommandVerifyResult.Replayed;
        return CommandVerifyResult.Ok;
    }
}

/// <summary>Remembers recently executed command IDs. Thread-safe. Can be persisted by the caller.</summary>
public sealed class ReplayGuard
{
    private readonly int _capacity;
    private readonly LinkedList<Guid> _order = new();
    private readonly HashSet<Guid> _seen = new();
    private readonly Lock _lock = new();

    public ReplayGuard(int capacity = 2000, IEnumerable<Guid>? initial = null)
    {
        _capacity = capacity;
        if (initial != null) foreach (var id in initial) TryRegister(id);
    }

    public bool TryRegister(Guid id)
    {
        lock (_lock)
        {
            if (!_seen.Add(id)) return false;
            _order.AddLast(id);
            while (_order.Count > _capacity)
            {
                _seen.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            return true;
        }
    }

    public Guid[] Snapshot() { lock (_lock) return _order.ToArray(); }
}

namespace Gac.Shared.Contracts;

/// <summary>
/// A Console-created class-mode preset that runs for a fixed period (CHANGE-001 section 2.3). During the period the
/// agent runs it at every logon; after the end it stops by itself. Signed with the server ECDSA key so the agent can
/// trust a preset that may contain the privileged <c>lock_pc</c> action.
/// </summary>
public sealed record PeriodPreset(
    string Id,
    string PeriodName,
    string PresetJson,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string Signature)
{
    public bool IsActive(DateTimeOffset now) => now >= StartAt && now < EndAt;
}

public sealed record PeriodPresetInfo(
    string Id, string PeriodName, DateTimeOffset StartAt, DateTimeOffset EndAt, string StartedBy,
    int TargetCount, bool Running, bool HasLock);

public sealed record CreatePeriodRequest(
    string PeriodName, string PresetJson, DateTimeOffset StartAt, int Days, IReadOnlyList<string> PcIds, IReadOnlyList<int> GroupIds);

/// <summary>Credentials a locked PC sends to the server to be unlocked (CHANGE-001 section 2.4). Never stored on the client.</summary>
public sealed record UnlockRequest(string PeriodId, string UserName, string Password);
public sealed record UnlockResponse(bool Unlocked, string Message);

using System.Buffers.Binary;
using System.Text;
using Gac.Shared.Contracts;

namespace Gac.Shared.Pipe;

public static class PipeNames
{
    public const string Tray = "GAC.Agent.Tray.v1";
}

/// <summary>
/// The fixed, enumerated set of requests the tray may send to the SYSTEM service (CLAUDE.md section 3 rule 4).
/// Anything that does not parse as one of these values is rejected. There is no free-form command.
/// </summary>
public enum TrayRequest
{
    Hello,
    GetStatus,
    NetRepair,
    ShutdownExtend,
    ShutdownNow,
    SubmitNetDiagReport,
    /// <summary>Read the per-PC teacher presets (CHANGE-001 2.1: stored by the SYSTEM service in ProgramData, per PC).</summary>
    GetPresets,
    /// <summary>Save the per-PC teacher presets. The service validates them in teacher context (no run_command/lock_pc).</summary>
    SavePresets,
    /// <summary>Unlock a locked PC: the service forwards the credentials to the server and, on success, lifts the lock until restart.</summary>
    Unlock,
}

/// <summary>Privileged network repairs the tray may request. Each maps to one hard-coded operation in the service.</summary>
public enum NetRepairAction
{
    FlushDns,
    RenewDhcp,
    ResetDnsToDhcp,
    ResetWinsock,
}

/// <summary>Events the service pushes to the tray.</summary>
public enum ServiceEvent
{
    ShutdownWarning,
    ShutdownCancelled,
    LedTest,
    ResetWallpaper,
    /// <summary>Lock this PC for an active period (CHANGE-001 2.4): the tray ends explorer and shows the lock window.</summary>
    Lock,
    /// <summary>Clear a lock the service no longer wants shown (period ended/stopped).</summary>
    Unlock,
}

public enum PipeKind { Request, Event, Response }

public sealed record PipeMessage(Guid Id, PipeKind Kind, string Type, Guid? ReplyTo, string? BodyJson)
{
    public static PipeMessage Request(TrayRequest type, object? body = null) =>
        new(Guid.NewGuid(), PipeKind.Request, type.ToString(), null, body == null ? null : GacJson.Serialize(body));

    public static PipeMessage Event(ServiceEvent type, object? body = null) =>
        new(Guid.NewGuid(), PipeKind.Event, type.ToString(), null, body == null ? null : GacJson.Serialize(body));

    public static PipeMessage Reply(Guid replyTo, PipeResponse response) =>
        new(Guid.NewGuid(), PipeKind.Response, "Response", replyTo, GacJson.Serialize(response));

    public T? Body<T>() => BodyJson == null ? default : GacJson.Deserialize<T>(BodyJson);
}

public sealed record PipeResponse(bool Ok, string Message, string? DataJson = null);

public sealed record HelloBody(string UserName, int SessionId);
public sealed record NetRepairBody(NetRepairAction Action);
public sealed record ShutdownWarningBody(DateTimeOffset Deadline, int ExtensionsLeft, int ExtensionMinutes);
public sealed record WallpaperBody(string Path);
public sealed record LedTestResult(bool Passed, string? Note);
public sealed record PresetsBody(string Json);
public sealed record LockBody(string PeriodId, string PeriodName, int CountdownMinutes);

public sealed record AgentStatus(
    bool Enrolled,
    string? ServerHost,
    int ServerPort,
    NetDiagPolicy NetDiag,
    string? PresetPublicKeyPem,
    SignedPresetBundle? SharedPresets,
    DateTimeOffset? LastServerContact);

/// <summary>Length-prefixed (4-byte little-endian) UTF-8 JSON frames, max 256 KB.</summary>
public static class PipeFraming
{
    public const int MaxFrameBytes = 256 * 1024;

    public static async Task WriteAsync(Stream stream, PipeMessage message, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(GacJson.Serialize(message));
        if (payload.Length > MaxFrameBytes) throw new InvalidOperationException("Pipe frame too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns null when the stream ends.</summary>
    public static async Task<PipeMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > MaxFrameBytes) throw new InvalidDataException("Invalid pipe frame length.");
        var buf = new byte[len];
        if (!await ReadExactAsync(stream, buf, ct).ConfigureAwait(false)) return null;
        return GacJson.Deserialize<PipeMessage>(Encoding.UTF8.GetString(buf));
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

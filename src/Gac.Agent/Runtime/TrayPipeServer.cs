using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;
using Microsoft.Extensions.Logging;

namespace Gac.Agent.Runtime;

public delegate Task<PipeResponse> TrayRequestHandler(TrayRequest request, PipeMessage message, string user, CancellationToken ct);

/// <summary>
/// Named pipe between the SYSTEM service and tray apps (CLAUDE.md section 8 rule 3).
/// ACL: local interactive/authenticated users may connect; network logons are denied. Only messages whose type is a
/// defined <see cref="TrayRequest"/> name are accepted; everything else is rejected.
/// </summary>
public sealed class TrayPipeServer(ILogger<TrayPipeServer> log) : ITrayBridge
{
    private readonly ConcurrentDictionary<Guid, TrayConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PipeResponse>> _pending = new();
    private static readonly HashSet<string> AllowedRequests = Enum.GetNames<TrayRequest>().ToHashSet(StringComparer.Ordinal);
    public TrayRequestHandler? Handler { get; set; }

    public bool AnyUserSession => !_connections.IsEmpty || Sessions.ActiveUser() != null;
    public int ConnectedTrays => _connections.Count;

    public static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var security = CreateSecurity();
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // The first instance must be ours: if another process already created the pipe name (squatting), this throws.
                var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough | (first ? PipeOptions.FirstPipeInstance : 0);
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(PipeNames.Tray, PipeDirection.InOut, 32, PipeTransmissionMode.Byte, options, 0, 0, security);
                }
                catch (UnauthorizedAccessException) when (first)
                {
                    log.LogError("Tray pipe name is already in use by another process (possible pipe squatting). Retrying in 30 s.");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                first = false;
                await pipe.WaitForConnectionAsync(ct);
                var conn = new TrayConnection(Guid.NewGuid(), pipe);
                pipe = null;
                _connections[conn.Id] = conn;
                _ = Task.Run(() => ServeAsync(conn, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                log.LogWarning(e, "Tray pipe accept failed");
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task ServeAsync(TrayConnection conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && conn.Pipe.IsConnected)
            {
                var msg = await PipeFraming.ReadAsync(conn.Pipe, ct);
                if (msg == null) break;
                if (conn.User == null)
                {
                    try { conn.User = conn.Pipe.GetImpersonationUserName(); } catch (IOException) { conn.User = "?"; }
                }

                if (msg.Kind == PipeKind.Response && msg.ReplyTo is { } replyTo)
                {
                    if (_pending.TryRemove(replyTo, out var tcs)) tcs.TrySetResult(msg.Body<PipeResponse>() ?? new PipeResponse(false, "빈 응답"));
                    continue;
                }
                if (msg.Kind != PipeKind.Request || !AllowedRequests.Contains(msg.Type) || !Enum.TryParse<TrayRequest>(msg.Type, out var req))
                {
                    log.LogWarning("Rejected tray message type {Type} from {User}", msg.Type, conn.User);
                    await conn.SendAsync(PipeMessage.Reply(msg.Id, new PipeResponse(false, "허용되지 않은 요청입니다.")), ct);
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    PipeResponse resp;
                    try { resp = Handler == null ? new PipeResponse(false, "서비스 준비 중입니다.") : await Handler(req, msg, conn.User ?? "?", ct); }
                    catch (Exception e) { log.LogError(e, "Tray request {Request} failed", req); resp = new PipeResponse(false, "요청을 처리하지 못했습니다."); }
                    try { await conn.SendAsync(PipeMessage.Reply(msg.Id, resp), ct); } catch (IOException) { }
                }, ct);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException) { }
        finally
        {
            _connections.TryRemove(conn.Id, out _);
            conn.Pipe.Dispose();
        }
    }

    public void Broadcast(ServiceEvent type, object? body)
    {
        var msg = PipeMessage.Event(type, body);
        foreach (var c in _connections.Values) _ = c.SendAsync(msg, CancellationToken.None).ContinueWith(_ => { });
    }

    public async Task<PipeResponse?> RequestAsync(ServiceEvent type, object? body, TimeSpan timeout, CancellationToken ct)
    {
        if (_connections.IsEmpty) return null;
        var msg = PipeMessage.Event(type, body);
        var tcs = new TaskCompletionSource<PipeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[msg.Id] = tcs;
        try
        {
            foreach (var c in _connections.Values) { try { await c.SendAsync(msg, ct); } catch (IOException) { } }
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException) { return null; }
        finally { _pending.TryRemove(msg.Id, out _); }
    }

    private sealed class TrayConnection(Guid id, NamedPipeServerStream pipe)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        public Guid Id { get; } = id;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public string? User { get; set; }

        public async Task SendAsync(PipeMessage m, CancellationToken ct)
        {
            await _write.WaitAsync(ct);
            try { await PipeFraming.WriteAsync(Pipe, m, ct); }
            finally { _write.Release(); }
        }
    }
}

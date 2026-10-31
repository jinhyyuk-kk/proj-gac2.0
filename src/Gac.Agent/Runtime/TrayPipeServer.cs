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
    private const int MaxConnectionsPerUser = 4;
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
        NamedPipeServerStream? listening = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (listening == null)
                {
                    try
                    {
                        // With no live instance of ours, the next one must be the first instance of the name,
                        // otherwise another process owns it (squatting) and we refuse to serve under it.
                        listening = Create(security, first: _connections.IsEmpty);
                    }
                    catch (UnauthorizedAccessException e) when (_connections.IsEmpty || e is PipeSquattingException)
                    {
                        log.LogError("Tray pipe name is already in use by another process (possible pipe squatting). Retrying in 30 s.");
                        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // All instances busy; existing connections keep the name alive. Try again shortly.
                        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                }

                try { await listening.WaitForConnectionAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (IOException e)
                {
                    log.LogWarning(e, "Tray pipe accept failed");
                    listening.Dispose();
                    listening = null;
                    continue;
                }

                // Create the next listening instance BEFORE handing off the connected one so the name never goes away.
                // If that fails, the connected instance still holds the name; creation is retried at the top of the loop.
                NamedPipeServerStream? next = null;
                try { next = Create(security, first: false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Could not pre-create tray pipe instance: {Error}", e.Message); }
                var conn = new TrayConnection(Guid.NewGuid(), listening);
                listening = next;
                _connections[conn.Id] = conn;
                _ = Task.Run(() => ServeAsync(conn, ct), ct);
            }
        }
        finally { listening?.Dispose(); }
    }

    /// <summary>
    /// Creates an instance and verifies the pipe's security descriptor is owned by this service's own identity
    /// (SYSTEM in production) or Administrators. If another process created the pipe name first (squatting), the
    /// descriptor belongs to it and the instance is refused, whatever our own connection bookkeeping says.
    /// </summary>
    private static NamedPipeServerStream Create(PipeSecurity security, bool first)
    {
        var pipe = NamedPipeServerStreamAcl.Create(PipeNames.Tray, PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | (first ? PipeOptions.FirstPipeInstance : 0), 0, 0, security);
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            using var me = WindowsIdentity.GetCurrent();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (owner == null || me.User == null || !(owner.Equals(me.User) || owner.Equals(admins)))
                throw new PipeSquattingException(owner?.Value ?? "unknown");
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
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
                    if (_connections.Values.Count(c => c.User == conn.User) > MaxConnectionsPerUser)
                    {
                        log.LogWarning("Too many tray connections from {User}; closing", conn.User);
                        break;
                    }
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

public sealed class PipeSquattingException(string owner) : UnauthorizedAccessException($"Tray pipe is owned by {owner} (possible pipe squatting).");

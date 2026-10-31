using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using Gac.Shared;
using Gac.Shared.Pipe;

namespace Gac.Tray;

/// <summary>
/// Tray side of the named pipe. Before trusting the connection it verifies the pipe is owned by SYSTEM or
/// Administrators, so a non-admin process that squats the pipe name cannot impersonate the service.
/// Reconnects automatically when the service restarts.
/// </summary>
public sealed class AgentPipeClient : IDisposable
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PipeResponse>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;

    /// <summary>Raised on a background thread for service events. Return a response for request-style events (LED test, wallpaper).</summary>
    public Func<PipeMessage, Task<PipeResponse?>>? OnEvent { get; set; }
    public bool Connected => _pipe?.IsConnected == true;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeNames.Tray, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5000, _cts.Token);
                if (!IsTrustedServer(pipe)) { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); continue; }
                _pipe = pipe;
                await SendAsync(PipeMessage.Request(TrayRequest.Hello, new HelloBody(Environment.UserName, System.Diagnostics.Process.GetCurrentProcess().SessionId)));
                while (!_cts.IsCancellationRequested)
                {
                    var msg = await PipeFraming.ReadAsync(pipe, _cts.Token);
                    if (msg == null) break;
                    if (msg.Kind == PipeKind.Response && msg.ReplyTo is { } id)
                    {
                        if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg.Body<PipeResponse>() ?? new PipeResponse(false, ""));
                    }
                    else if (msg.Kind == PipeKind.Event && OnEvent != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            var resp = await OnEvent(msg);
                            if (resp != null) await SendAsync(PipeMessage.Reply(msg.Id, resp));
                        });
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            finally
            {
                _pipe = null;
                foreach (var p in _pending.Values) p.TrySetResult(new PipeResponse(false, "GAC 서비스와 연결이 끊겼습니다."));
                _pending.Clear();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    internal static bool IsTrustedServer(NamedPipeClientStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or IOException) { return false; }
    }

    private async Task SendAsync(PipeMessage m)
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        await _write.WaitAsync();
        try { await PipeFraming.WriteAsync(pipe, m, _cts.Token); }
        finally { _write.Release(); }
    }

    /// <summary>Sends one of the fixed tray requests and waits for the reply.</summary>
    public async Task<PipeResponse> RequestAsync(TrayRequest type, object? body, TimeSpan timeout)
    {
        if (!Connected) return new PipeResponse(false, "GAC 서비스에 연결되어 있지 않습니다. 잠시 후 다시 시도하세요.");
        var msg = PipeMessage.Request(type, body);
        var tcs = new TaskCompletionSource<PipeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[msg.Id] = tcs;
        try
        {
            await SendAsync(msg);
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (Exception e) when (e is TimeoutException or IOException) { return new PipeResponse(false, "GAC 서비스가 응답하지 않습니다."); }
        finally { _pending.TryRemove(msg.Id, out _); }
    }

    public async Task<AgentStatus?> GetStatusAsync()
    {
        var r = await RequestAsync(TrayRequest.GetStatus, null, TimeSpan.FromSeconds(5));
        return r.Ok && r.DataJson != null ? GacJson.Deserialize<AgentStatus>(r.DataJson) : null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe?.Dispose();
    }
}

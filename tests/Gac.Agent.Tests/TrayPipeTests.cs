using System.IO.Pipes;
using System.Security.Principal;
using Gac.Agent.Runtime;
using Gac.Shared;
using Gac.Shared.Pipe;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gac.Agent.Tests;

[Collection("pipe")]
public sealed class TrayPipeTests : IAsyncLifetime
{
    private readonly TrayPipeServer _server = new(NullLogger<TrayPipeServer>.Instance);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TrayRequest> _handled = [];
    private Task _run = Task.CompletedTask;

    public Task InitializeAsync()
    {
        _server.Handler = (req, msg, user, ct) =>
        {
            lock (_handled) _handled.Add(req);
            return Task.FromResult(new PipeResponse(true, "ok:" + req));
        };
        _run = _server.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _run; } catch (OperationCanceledException) { }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync()
    {
        var c = new NamedPipeClientStream(".", PipeNames.Tray, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c.ConnectAsync(5000);
        return c;
    }

    private static async Task<PipeResponse> AskAsync(Stream s, PipeMessage m)
    {
        await PipeFraming.WriteAsync(s, m, default);
        var reply = await PipeFraming.ReadAsync(s, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(m.Id, reply!.ReplyTo);
        return reply.Body<PipeResponse>()!;
    }

    [Fact]
    public async Task Fixed_requests_are_accepted()
    {
        await using var c = await ConnectAsync();
        var r = await AskAsync(c, PipeMessage.Request(TrayRequest.Hello, new HelloBody("t", 1)));
        Assert.True(r.Ok);
        r = await AskAsync(c, PipeMessage.Request(TrayRequest.NetRepair, new NetRepairBody(NetRepairAction.FlushDns)));
        Assert.Equal("ok:NetRepair", r.Message);
    }

    [Theory]
    [InlineData("RunCommand")]
    [InlineData("1")]          // numeric enum value must not be accepted
    [InlineData("hello")]      // case must match exactly
    [InlineData("Hello, ShutdownNow")]
    public async Task Unknown_request_types_are_rejected(string type)
    {
        await using var c = await ConnectAsync();
        var r = await AskAsync(c, new PipeMessage(Guid.NewGuid(), PipeKind.Request, type, null, "{\"command\":\"format c:\"}"));
        Assert.False(r.Ok);
        lock (_handled) Assert.Empty(_handled);
    }

    [Fact]
    public async Task Server_instance_is_owned_by_this_process_identity()
    {
        await using var c = await ConnectAsync();
        var owner = (SecurityIdentifier)c.GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        using var me = WindowsIdentity.GetCurrent();
        Assert.True(owner.Equals(me.User) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        // The tray trusts only SYSTEM/Administrators owners; a pipe created by a non-admin user is refused.
        var elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        Assert.Equal(elevated, Gac.Tray.AgentPipeClient.IsTrustedServer(c));
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Console.Services;

/// <summary>Real GAC Server client: HTTPS on the school LAN with the pinned self-signed certificate.</summary>
public sealed class HttpGacServerClient : IGacServerClient
{
    private readonly HttpClient _http;

    public HttpGacServerClient(ConsoleConnection connection)
    {
        Connection = connection;
        _http = new HttpClient(CertPinning.CreatePinnedHandler(connection.Thumbprint))
        {
            BaseAddress = new UriBuilder("https", connection.Host, connection.Port).Uri,
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public ConsoleConnection Connection { get; }
    public bool IsDemo => false;
    public string ServerDisplayName => $"{Connection.Host}:{Connection.Port}";

    public Task<SetupStatus> GetSetupAsync() => Get<SetupStatus>("/api/setup");
    public Task<AccountInfo> CreateOwnerAsync(CreateOwnerRequest r) => Send<AccountInfo>(HttpMethod.Post, "/api/setup/owner", r);

    public async Task<LoginResponse> LoginAsync(string userName, string password)
    {
        var r = await Send<LoginResponse>(HttpMethod.Post, "/api/auth/login", new LoginRequest(userName, password));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", r.Token);
        return r;
    }

    public async Task LogoutAsync()
    {
        try { await Send<object>(HttpMethod.Post, "/api/auth/logout", null); } catch (GacClientException) { }
        _http.DefaultRequestHeaders.Authorization = null;
    }

    public Task ChangePasswordAsync(ChangePasswordRequest r) => Send<object>(HttpMethod.Post, "/api/auth/password", r);

    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync() => GetList<AccountInfo>("/api/accounts");
    public Task<AccountInfo> CreateAccountAsync(SaveAccountRequest r) => Send<AccountInfo>(HttpMethod.Post, "/api/accounts", r);
    public Task<AccountInfo> UpdateAccountAsync(int id, SaveAccountRequest r) => Send<AccountInfo>(HttpMethod.Put, $"/api/accounts/{id}", r);
    public Task DeleteAccountAsync(int id) => Send<object>(HttpMethod.Delete, $"/api/accounts/{id}", null);
    public Task ResetPasswordAsync(int id, string pw) => Send<object>(HttpMethod.Post, $"/api/accounts/{id}/password", new ResetPasswordRequest(pw));

    public Task<IReadOnlyList<PcSummary>> GetPcsAsync() => GetList<PcSummary>("/api/pcs");
    public Task MovePcsAsync(MovePcRequest r) => Send<object>(HttpMethod.Post, "/api/pcs/move", r);
    public Task DeletePcAsync(string pcId) => Send<object>(HttpMethod.Delete, $"/api/pcs/{Uri.EscapeDataString(pcId)}", null);
    public Task<IReadOnlyList<PcGroup>> GetGroupsAsync() => GetList<PcGroup>("/api/groups");
    public Task<PcGroup> CreateGroupAsync(string name) => Send<PcGroup>(HttpMethod.Post, "/api/groups", new SaveGroupRequest(name));
    public Task RenameGroupAsync(int id, string name) => Send<object>(HttpMethod.Put, $"/api/groups/{id}", new SaveGroupRequest(name));
    public Task DeleteGroupAsync(int id) => Send<object>(HttpMethod.Delete, $"/api/groups/{id}", null);
    public Task<GroupPolicy> GetGroupPolicyAsync(int groupId) => Get<GroupPolicy>($"/api/groups/{groupId}/policy");
    public Task SaveGroupPolicyAsync(GroupPolicy p) => Send<object>(HttpMethod.Put, $"/api/groups/{p.GroupId}/policy", p);

    public Task<SendCommandResponse> SendCommandAsync(SendCommandRequest r) => Send<SendCommandResponse>(HttpMethod.Post, "/api/commands", r);
    public Task<IReadOnlyList<CommandBatchSummary>> GetBatchesAsync(int limit = 100) => GetList<CommandBatchSummary>($"/api/commands?limit={limit}");
    public Task<IReadOnlyList<CommandRow>> GetBatchAsync(Guid batchId) => GetList<CommandRow>($"/api/commands/{batchId}");

    public Task<IReadOnlyList<ReportRow>> GetReportsAsync(string? module, string? pcId, int limit = 200)
    {
        var q = $"/api/reports?limit={limit}";
        if (module != null) q += "&module=" + Uri.EscapeDataString(module);
        if (pcId != null) q += "&pcId=" + Uri.EscapeDataString(pcId);
        return GetList<ReportRow>(q);
    }

    public Task<IReadOnlyList<ReportRow>> GetLatestAsync(string module) => GetList<ReportRow>($"/api/reports/latest/{Uri.EscapeDataString(module)}");

    public Task<SharedPresetInfo> GetSharedPresetsAsync() => Get<SharedPresetInfo>("/api/presets/shared");
    public Task SaveSharedPresetsAsync(string json) => Send<object>(HttpMethod.Put, "/api/presets/shared", new SaveSharedPresetsRequest(json));

    public Task<IReadOnlyList<DeployPackage>> GetPackagesAsync() => GetList<DeployPackage>("/api/packages");

    public async Task<DeployPackage> UploadPackageAsync(string name, string filePath, string silentArgs, IProgress<double>? progress, CancellationToken ct)
    {
        var url = $"/api/packages?name={Uri.EscapeDataString(name)}&fileName={Uri.EscapeDataString(Path.GetFileName(filePath))}&silentArgs={Uri.EscapeDataString(silentArgs)}";
        await using var fs = File.OpenRead(filePath);
        using var content = new ProgressStreamContent(fs, progress);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var http = new HttpClient(CertPinning.CreatePinnedHandler(Connection.Thumbprint)) { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;
        return await Execute<DeployPackage>(() => http.PostAsync(url, content, ct));
    }

    public Task DeletePackageAsync(string id) => Send<object>(HttpMethod.Delete, $"/api/packages/{Uri.EscapeDataString(id)}", null);

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit = 2000) => GetList<AuditEntry>($"/api/audit?limit={limit}");
    public Task<ServerSettings> GetSettingsAsync() => Get<ServerSettings>("/api/settings");
    public Task SaveSettingsAsync(ServerSettings s) => Send<object>(HttpMethod.Put, "/api/settings", s);
    public Task<EnrollmentInfo> GetEnrollmentAsync() => Get<EnrollmentInfo>("/api/enrollment");
    public Task<EnrollmentInfo> RegenerateEnrollmentAsync() => Send<EnrollmentInfo>(HttpMethod.Post, "/api/enrollment/regenerate", null);

    private Task<T> Get<T>(string url) => Execute<T>(() => _http.GetAsync(url));

    private async Task<IReadOnlyList<T>> GetList<T>(string url) => await Get<List<T>>(url);

    private Task<T> Send<T>(HttpMethod method, string url, object? body) => Execute<T>(() =>
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = JsonContent.Create(body, body.GetType(), options: GacJson.Options);
        return _http.SendAsync(req);
    });

    private static async Task<T> Execute<T>(Func<Task<HttpResponseMessage>> call)
    {
        HttpResponseMessage resp;
        try { resp = await call(); }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException)
        { throw new GacClientException("서버 인증서가 등록된 서버와 다릅니다. 서버가 다시 설치되었다면 연결 설정을 새로 하세요."); }
        catch (HttpRequestException) { throw new GacClientException("GAC 서버에 연결할 수 없습니다. 관리 PC가 켜져 있는지, 같은 네트워크에 있는지 확인하세요."); }
        catch (TaskCanceledException) { throw new GacClientException("서버 응답 시간이 초과되었습니다."); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string? message = null;
                try { message = (await resp.Content.ReadFromJsonAsync<ErrorBody>(GacJson.Options))?.Message; } catch { }
                throw new GacClientException(message ?? $"서버 오류 ({(int)resp.StatusCode})", (int)resp.StatusCode);
            }
            if (typeof(T) == typeof(object)) return default!;
            return (await resp.Content.ReadFromJsonAsync<T>(GacJson.Options))!;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record ErrorBody(string Message);

    private sealed class ProgressStreamContent(Stream stream, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context)
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await stream.ReadAsync(buf)) > 0)
            {
                await target.WriteAsync(buf.AsMemory(0, n));
                done += n;
                progress?.Report(stream.Length == 0 ? 1 : (double)done / stream.Length);
            }
        }

        protected override bool TryComputeLength(out long length) { length = stream.Length; return true; }
    }
}

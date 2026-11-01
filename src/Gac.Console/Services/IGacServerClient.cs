using Gac.Shared.Auth;
using Gac.Shared.Contracts;

namespace Gac.Console.Services;

/// <summary>
/// Everything the Console needs from a GAC Server. <see cref="HttpGacServerClient"/> talks to a real server over the LAN;
/// the demo supplies an in-memory fake (DemoServerClient) so GAC-Demo.exe runs the very same Console UI.
/// </summary>
public interface IGacServerClient : IDisposable
{
    bool IsDemo { get; }
    string ServerDisplayName { get; }

    Task<SetupStatus> GetSetupAsync();
    Task<AccountInfo> CreateOwnerAsync(CreateOwnerRequest request);
    Task<LoginResponse> LoginAsync(string userName, string password);
    Task LogoutAsync();
    Task ChangePasswordAsync(ChangePasswordRequest request);

    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync();
    Task<AccountInfo> CreateAccountAsync(SaveAccountRequest request);
    Task<AccountInfo> UpdateAccountAsync(int id, SaveAccountRequest request);
    Task DeleteAccountAsync(int id);
    Task ResetPasswordAsync(int id, string newPassword);
    Task<PermissionMatrix> GetMatrixAsync();
    Task SaveMatrixAsync(PermissionMatrix matrix);
    Task TransferOwnershipAsync(int toAccountId);

    Task<IReadOnlyList<PcSummary>> GetPcsAsync();
    Task MovePcsAsync(MovePcRequest request);
    Task DeletePcAsync(string pcId);
    Task SetAliasAsync(string pcId, string? alias);
    Task RevokePcAsync(string pcId);
    Task RemoveWorkExemptionAsync(string pcId);

    Task<IReadOnlyList<BlockedDevice>> GetBlockedDevicesAsync();
    Task<IReadOnlyList<PendingDevice>> GetPendingDevicesAsync();
    Task ApproveDeviceAsync(string fingerprint);
    Task ClearConsoleFirstAsync(string fingerprint);

    Task<IReadOnlyList<PeriodPresetInfo>> GetPeriodsAsync();
    Task<PeriodPresetInfo> CreatePeriodAsync(CreatePeriodRequest request);
    Task StopPeriodAsync(string id);
    Task<IReadOnlyList<PcGroup>> GetGroupsAsync();
    Task<PcGroup> CreateGroupAsync(string name);
    Task RenameGroupAsync(int id, string name);
    Task DeleteGroupAsync(int id);
    Task<GroupPolicy> GetGroupPolicyAsync(int groupId);
    Task SaveGroupPolicyAsync(GroupPolicy policy);

    Task<SendCommandResponse> SendCommandAsync(SendCommandRequest request);
    Task<IReadOnlyList<CommandBatchSummary>> GetBatchesAsync(int limit = 100);
    Task<IReadOnlyList<CommandRow>> GetBatchAsync(Guid batchId);
    Task<IReadOnlyList<ReportRow>> GetReportsAsync(string? module, string? pcId, int limit = 200);
    Task<IReadOnlyList<ReportRow>> GetLatestAsync(string module);

    Task<SharedPresetInfo> GetSharedPresetsAsync();
    Task SaveSharedPresetsAsync(string presetsJson);

    Task<IReadOnlyList<DeployPackage>> GetPackagesAsync();
    Task<DeployPackage> UploadPackageAsync(string name, string filePath, string silentArgs, IProgress<double>? progress, CancellationToken ct);
    Task DeletePackageAsync(string id);

    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit = 2000);
    Task<ServerSettings> GetSettingsAsync();
    Task SaveSettingsAsync(ServerSettings settings);
    Task<EnrollmentInfo> GetEnrollmentAsync();
    Task<EnrollmentInfo> RegenerateEnrollmentAsync();

    // CHANGE-002 §2: calendar and lock exemption days
    Task<IReadOnlyList<ExemptionDay>> GetExemptionDaysAsync();
    Task<ExemptionDay> SaveExemptionDayAsync(string? id, SaveExemptionDayRequest request);
    Task DeleteExemptionDayAsync(string id);
    Task SkipExemptionOccurrenceAsync(string id, DateOnly date);

    // CHANGE-002 §4: overload alerts (알림) and server management (서버 관리)
    Task<IReadOnlyList<OverloadAlert>> GetAlertsAsync();
    Task MarkAlertsReadAsync(IReadOnlyList<string> ids);
    Task<ServerShutdownSettings> GetServerShutdownAsync();
    Task SaveServerShutdownAsync(ServerShutdownSettings settings);
    Task<ServerShutdownStatus> GetServerShutdownStatusAsync();
    Task SkipServerShutdownTodayAsync();
    Task ExtendServerShutdownAsync();
    Task<OverloadSettings> GetOverloadSettingsAsync();
    Task SaveOverloadSettingsAsync(OverloadSettings settings);
    Task<IReadOnlyList<OverloadBlock>> GetOverloadBlocksAsync();
    Task UnblockAsync(string key);
    Task BlockPermanentlyAsync(string key);
}

/// <summary>A failure with a Korean message suitable for showing to the operator.</summary>
public sealed class GacClientException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
    public bool SessionExpired => Status == 401;
}

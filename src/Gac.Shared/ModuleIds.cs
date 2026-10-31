namespace Gac.Shared;

/// <summary>The nine module IDs confirmed by the owner (CLAUDE.md section 2).</summary>
public static class ModuleIds
{
    public const string Clean = "clean";
    public const string Shutdown = "shutdown";
    public const string ClassMode = "classmode";
    public const string NetDiag = "netdiag";
    public const string Errors = "errors";
    public const string Update = "update";
    public const string Deploy = "deploy";
    public const string Health = "health";
    public const string Inspect = "inspect";

    public static readonly IReadOnlyList<string> All =
        [Clean, Shutdown, ClassMode, NetDiag, Errors, Update, Deploy, Health, Inspect];

    public static string KoreanName(string id) => id switch
    {
        Clean => "자동 정리",
        Shutdown => "자동 종료",
        ClassMode => "원클릭 수업 모드",
        NetDiag => "네트워크 자가 진단",
        Errors => "오류 점검",
        Update => "일괄 업데이트",
        Deploy => "일괄 배포·실행",
        Health => "PC 상태 보고",
        Inspect => "학기말 점검",
        _ => id,
    };
}

/// <summary>Actions understood by each module. Server and agent both validate against this list.</summary>
public static class ModuleActions
{
    public const string CleanRun = "run";              // payload: CleanRequest
    public const string ShutdownNow = "now";           // payload: ShutdownNowRequest
    public const string ShutdownCancel = "cancel";
    public const string ErrorsCollect = "collect";
    public const string UpdateScan = "scan";
    public const string UpdateInstall = "install";
    public const string DeployInstall = "install_package"; // payload: DeployPackageRequest
    public const string DeployRunScript = "run_script";    // payload: RunScriptRequest
    public const string HealthReport = "report";
    public const string InspectRun = "run";            // payload: InspectRequest
    public const string InspectUninstall = "uninstall"; // payload: UninstallRequest
    public const string NetDiagRun = "run";            // tray-initiated; console can request a report
    public const string ClassModeSync = "sync";         // refresh shared presets

    public static readonly IReadOnlyDictionary<string, string[]> ByModule = new Dictionary<string, string[]>
    {
        [ModuleIds.Clean] = [CleanRun],
        [ModuleIds.Shutdown] = [ShutdownNow, ShutdownCancel],
        [ModuleIds.Errors] = [ErrorsCollect],
        [ModuleIds.Update] = [UpdateScan, UpdateInstall],
        [ModuleIds.Deploy] = [DeployInstall, DeployRunScript],
        [ModuleIds.Health] = [HealthReport],
        [ModuleIds.Inspect] = [InspectRun, InspectUninstall],
        [ModuleIds.NetDiag] = [NetDiagRun],
        [ModuleIds.ClassMode] = [ClassModeSync],
    };

    public static bool IsKnown(string module, string action) =>
        ByModule.TryGetValue(module, out var actions) && actions.Contains(action);
}

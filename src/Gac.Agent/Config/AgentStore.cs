using System.Security.AccessControl;
using System.Security.Principal;
using Gac.Shared;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Agent.Config;

/// <summary>Secret agent identity. Stored in agent.json, readable only by SYSTEM and Administrators.</summary>
public sealed record AgentIdentity(string ServerHost, int ServerPort, string Thumbprint, string AgentId, string AgentKeyBase64, string PresetPublicKeyPem)
{
    public byte[] Key => Convert.FromBase64String(AgentKeyBase64);
    public Uri BaseUri => new UriBuilder("https", ServerHost, ServerPort).Uri;
}

public sealed class AgentState
{
    public DateTimeOffset? LastHealth { get; set; }
    public DateTimeOffset? LastErrors { get; set; }
    public DateOnly? LastScheduledClean { get; set; }
    public DateTimeOffset? LastServerContact { get; set; }
    public string? PolicyVersion { get; set; }
}

/// <summary>All agent files live under %ProgramData%\GAC\Agent (overridable with GAC_AGENT_DATA for tests).</summary>
public sealed class AgentStore
{
    private readonly Lock _lock = new();
    public string Root { get; }

    public AgentStore(string? root = null)
    {
        var gacDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GAC");
        Root = root ?? (Environment.GetEnvironmentVariable("GAC_AGENT_DATA") is { Length: > 0 } d ? d : Path.Combine(gacDir, "Agent"));
        if (root == null && Root.StartsWith(gacDir, StringComparison.OrdinalIgnoreCase))
        {
            // SYSTEM + Administrators only; recreated if a non-admin pre-created it (protects agent.json and the queue).
            SecureDirectory.Ensure(gacDir);
            SecureDirectory.Ensure(Root);
        }
        else Directory.CreateDirectory(Root);
    }

    public string QueueDir => Path.Combine(Root, "queue");
    public string LogsDir => Path.Combine(Root, "logs");
    public string WorkDir => Path.Combine(Root, "work");
    public string PackagesDir => Path.Combine(Root, "packages");
    public string StateDir => Path.Combine(Root, "state");
    private string IdentityPath => Path.Combine(Root, "agent.json");
    private string PolicyPath => Path.Combine(Root, "policy.json");
    private string StatePath => Path.Combine(StateDir, "state.json");
    private string ReplayPath => Path.Combine(StateDir, "replay.json");

    public AgentIdentity? LoadIdentity() => Read<AgentIdentity>(IdentityPath);

    public void SaveIdentity(AgentIdentity id)
    {
        WriteAtomic(IdentityPath, GacJson.Serialize(id));
        RestrictToSystemAndAdmins(IdentityPath);
    }

    public AgentPolicy LoadPolicy() => Read<AgentPolicy>(PolicyPath) ?? new AgentPolicy();
    public void SavePolicy(AgentPolicy p) => WriteAtomic(PolicyPath, GacJson.Serialize(p));

    private string PcPresetsPath => Path.Combine(Root, "presets.json");
    /// <summary>Per-PC teacher presets (CHANGE-001 2.1) held by the SYSTEM service; shared by everyone who uses the PC.</summary>
    public string LoadPcPresets()
    {
        try { return File.Exists(PcPresetsPath) ? File.ReadAllText(PcPresetsPath) : "{\"version\":1,\"presets\":[]}"; }
        catch (IOException) { return "{\"version\":1,\"presets\":[]}"; }
    }
    public void SavePcPresets(string json) => WriteAtomic(PcPresetsPath, json);

    public AgentState LoadState() { lock (_lock) return Read<AgentState>(StatePath) ?? new AgentState(); }

    public void UpdateState(Action<AgentState> change)
    {
        lock (_lock)
        {
            var s = Read<AgentState>(StatePath) ?? new AgentState();
            change(s);
            Directory.CreateDirectory(StateDir);
            WriteAtomic(StatePath, GacJson.Serialize(s));
        }
    }

    public Guid[] LoadReplay() => Read<Guid[]>(ReplayPath) ?? [];
    public void SaveReplay(Guid[] ids) { Directory.CreateDirectory(StateDir); WriteAtomic(ReplayPath, GacJson.Serialize(ids)); }

    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? GacJson.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return default; }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Best effort; ignored when not elevated (development and tests).</summary>
    public static void RestrictToSystemAndAdmins(string path)
    {
        try
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            using var me = WindowsIdentity.GetCurrent();
            if (!me.IsSystem && !new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator))
                sec.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(sec);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PrivilegeNotHeldException or IOException) { }
    }
}

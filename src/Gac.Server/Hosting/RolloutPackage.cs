using Gac.Shared;

namespace Gac.Server.Hosting;

/// <summary>
/// The agent rollout package (CHANGE-001 §2): a folder with gac-agent.json (server address, port, certificate
/// thumbprint, enrollment secret) that the agent installer reads from its own folder, so a classroom PC is enrolled by
/// a plain double-click / silent install. Copies GAC-Agent-Setup.exe alongside it when that installer can be found.
/// </summary>
public sealed record AgentRolloutConfig(string ServerAddress, int Port, string Thumbprint, string EnrollCode);

public static class RolloutPackage
{
    public const string ConfigFileName = "gac-agent.json";

    public static string WriteConfig(string targetDir, AgentRolloutConfig config)
    {
        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, ConfigFileName);
        File.WriteAllText(path, GacJson.Serialize(config));
        // Try to include the installer if it sits next to the server or in a known spot.
        foreach (var candidate in InstallerCandidates())
            if (File.Exists(candidate)) { File.Copy(candidate, Path.Combine(targetDir, "GAC-Agent-Setup.exe"), overwrite: true); break; }
        File.WriteAllText(Path.Combine(targetDir, "설치방법.txt"),
            "이 폴더의 GAC-Agent-Setup.exe 를 교실 PC에서 실행하면 자동으로 서버에 등록됩니다.\r\n" +
            "조용히 설치하려면: GAC-Agent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES\r\n" +
            "같은 폴더의 gac-agent.json 에서 서버 주소와 등록 정보를 읽습니다.\r\n");
        return path;
    }

    private static IEnumerable<string> InstallerCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "GAC-Agent-Setup.exe");
        yield return Path.Combine(baseDir, "..", "GAC-Agent-Setup.exe");
    }
}

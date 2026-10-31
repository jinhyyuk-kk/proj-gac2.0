using Gac.Shared;
using Gac.Shared.Contracts;

namespace Gac.Agent.Runtime;

/// <summary>
/// Durable outbox. Every module result is written to disk first and uploaded when the server is reachable,
/// so nothing is lost while the Admin PC is turned off (CLAUDE.md section 3).
/// </summary>
public sealed class ReportQueue(string directory)
{
    public const int MaxQueued = 2000;
    private readonly Lock _lock = new();

    public void Enqueue(ModuleReport report)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(directory);
            var name = $"{report.FinishedAt.ToUnixTimeMilliseconds():D15}-{report.Id:N}.json";
            var tmp = Path.Combine(directory, name + ".tmp");
            File.WriteAllText(tmp, GacJson.Serialize(report));
            File.Move(tmp, Path.Combine(directory, name), overwrite: true);
            Trim();
        }
    }

    public int Count
    {
        get { lock (_lock) return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0; }
    }

    /// <summary>Returns up to <paramref name="max"/> oldest reports with their file paths.</summary>
    public List<(string Path, ModuleReport Report)> Peek(int max)
    {
        lock (_lock)
        {
            var list = new List<(string, ModuleReport)>();
            if (!Directory.Exists(directory)) return list;
            foreach (var f in Directory.EnumerateFiles(directory, "*.json").Order().Take(max))
            {
                try
                {
                    var r = GacJson.Deserialize<ModuleReport>(File.ReadAllText(f));
                    if (r != null) list.Add((f, r)); else File.Delete(f);
                }
                catch (System.Text.Json.JsonException) { File.Delete(f); }
                catch (IOException) { }
            }
            return list;
        }
    }

    public void Remove(IEnumerable<string> paths)
    {
        lock (_lock)
            foreach (var p in paths) { try { File.Delete(p); } catch (IOException) { } }
    }

    // Drop the oldest reports if the server has been unreachable for a very long time.
    private void Trim()
    {
        var files = Directory.EnumerateFiles(directory, "*.json").Order().ToList();
        foreach (var f in files.Take(Math.Max(0, files.Count - MaxQueued))) { try { File.Delete(f); } catch (IOException) { } }
    }
}

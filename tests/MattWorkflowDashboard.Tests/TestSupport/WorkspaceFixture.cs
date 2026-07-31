using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MattWorkflowDashboard.Tests.TestSupport;

/// <summary>
/// A disposable workspace of sanitized fixtures. Tests only ever point at this tree, never at a
/// live project, so verification cannot damage the workflows the product observes.
/// </summary>
public sealed class WorkspaceFixture : IDisposable
{
    public WorkspaceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "mwd-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
        CacheFile = Path.Combine(Root, "cache", "cache.db");
    }

    public string Root { get; }

    public string CacheFile { get; }

    public string WorkspacesRoot => EnsureDirectory(Path.Combine(Root, "workspaces"));

    public string NewProject(string name, bool agentsMarker = true)
    {
        var path = EnsureDirectory(Path.Combine(WorkspacesRoot, name));
        if (agentsMarker)
        {
            File.WriteAllText(Path.Combine(path, "AGENTS.md"), "# Agents\n");
        }

        return path;
    }

    /// <summary>
    /// A tree big enough for traversal bounds to be a real question, laid out as ordinary nested
    /// directories with a project every so often. Sanitized by construction: nothing here is copied
    /// from a live workspace.
    /// </summary>
    public void NewTree(int directories, int projectsEvery, int breadth = 5)
    {
        var created = 0;
        var frontier = new Queue<string>();
        frontier.Enqueue(WorkspacesRoot);

        while (created < directories && frontier.Count > 0)
        {
            var parent = frontier.Dequeue();

            for (var i = 0; i < breadth && created < directories; i++)
            {
                var child = EnsureDirectory(Path.Combine(parent, $"d{created:D4}"));
                if (created % projectsEvery == 0)
                {
                    File.WriteAllText(Path.Combine(child, "AGENTS.md"), "# Agents\n");
                }

                frontier.Enqueue(child);
                created++;
            }
        }
    }

    public string NewEffort(string projectPath, string effortName)
    {
        var path = EnsureDirectory(Path.Combine(projectPath, ".scratch", effortName));
        EnsureDirectory(Path.Combine(path, "issues"));
        return path;
    }

    public string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteTicket(string effortPath, string fileName, string content) =>
        WriteFile(Path.Combine(effortPath, "issues", fileName), content);

    public string WriteMap(string effortPath, string content) =>
        WriteFile(Path.Combine(effortPath, "map.md"), content);

    /// <summary>A real temporary repository, so Git behaviour is exercised rather than imitated.</summary>
    public void InitGitRepository(string path, string? originUrl = null)
    {
        Git(path, "init", "--initial-branch=main");
        Git(path, "config", "user.email", "fixture@example.invalid");
        Git(path, "config", "user.name", "Fixture");
        Git(path, "config", "commit.gpgsign", "false");

        if (originUrl is not null)
        {
            Git(path, "remote", "add", "origin", originUrl);
        }
    }

    public string Commit(string repositoryPath, string message)
    {
        Git(repositoryPath, "add", "-A");
        Git(repositoryPath, "commit", "-m", message, "--allow-empty");
        return Git(repositoryPath, "rev-parse", "HEAD").Trim();
    }

    public string Git(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }

        return stdout;
    }

    /// <summary>
    /// A content fingerprint of a tree, used to prove that observing a workspace changed nothing
    /// in it.
    /// </summary>
    public static string Fingerprint(string path, string? excludingSegment = null)
    {
        var builder = new StringBuilder();
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => excludingSegment is null
                || !f.Contains(Path.DirectorySeparatorChar + excludingSegment + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            builder.Append(file).Append('|').Append(info.Length).Append('|')
                .Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }
}

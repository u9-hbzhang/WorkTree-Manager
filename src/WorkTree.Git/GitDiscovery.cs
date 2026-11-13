using System.ComponentModel;
using System.Text.RegularExpressions;

namespace WorkTree.Git;

public sealed record GitInstallation(string Executable, string Version);
public sealed record GitDetection(GitInstallation? Installation, string? Message);

public static partial class GitDiscovery
{
    private static readonly Version MinimumVersion = new(2, 43);

    public static async Task<GitInstallation> ValidateAsync(string executable, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !string.Equals(Path.GetFileName(executable), "git.exe", StringComparison.OrdinalIgnoreCase))
            throw new GitException("请选择有效的 git.exe。");

        var result = await GitProcess.RunAsync(executable, null, token, "--version").ConfigureAwait(false);
        var versionLine = result.RequiredOutput().Trim();
        var match = GitVersionPattern().Match(versionLine);
        if (!match.Success || !Version.TryParse(match.Groups["number"].Value, out var version))
            throw new GitException("无法识别 Git 版本。");
        if (version < MinimumVersion)
            throw new GitException($"当前为 {versionLine}，需要 Git 2.43 或更新版本。");

        return new GitInstallation(executable, match.Groups["display"].Value);
    }

    public static async Task<GitDetection> DetectAsync(string? preferred, CancellationToken token)
    {
        string? firstError = null;
        foreach (var executable in CandidatePaths(preferred).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(executable, preferred, StringComparison.OrdinalIgnoreCase) || File.Exists(executable))
            {
                try
                {
                    var installation = await ValidateAsync(executable, token).ConfigureAwait(false);
                    return new GitDetection(installation, firstError is null ? null : $"已使用备用 Git。{firstError}");
                }
                catch (Exception ex) when (ex is GitException or IOException or Win32Exception or UnauthorizedAccessException)
                {
                    firstError ??= ex.Message;
                }
            }
        }

        return new GitDetection(null, firstError ?? "未找到 Git，请安装 Git for Windows 或在设置中指定 git.exe。");
    }

    private static IEnumerable<string> CandidatePaths(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            yield return preferred;

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (Path.IsPathFullyQualified(directory))
                yield return Path.Combine(directory, "git.exe");
        }

        foreach (var location in new[] { Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var directory = Environment.GetFolderPath(location);
            if (!Path.IsPathFullyQualified(directory))
                continue;

            yield return location == Environment.SpecialFolder.LocalApplicationData
                ? Path.Combine(directory, "Programs", "Git", "cmd", "git.exe")
                : Path.Combine(directory, "Git", "cmd", "git.exe");
        }
    }

    [GeneratedRegex(@"^git version (?<display>(?<number>\d+\.\d+(?:\.\d+)?)[^\s]*)$")]
    private static partial Regex GitVersionPattern();
}

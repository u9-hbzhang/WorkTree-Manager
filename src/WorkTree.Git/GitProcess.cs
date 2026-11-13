using System.Diagnostics;
using System.Text;

namespace WorkTree.Git;

public sealed class GitException(string message) : Exception(message);

internal sealed record GitResult(int ExitCode, string Output, string Error)
{
    public string RequiredOutput()
    {
        if (ExitCode != 0)
            throw new GitException(string.IsNullOrWhiteSpace(Error) ? $"Git 返回错误 {ExitCode}。" : Error.Trim());
        return Output;
    }
}

internal static class GitProcess
{
    public static async Task<GitResult> RunAsync(
        string executable, string? directory, CancellationToken cancellationToken, params string[] arguments) =>
        await RunAsync(executable, directory, null, TimeSpan.FromSeconds(45), cancellationToken, arguments)
            .ConfigureAwait(false);

    public static async Task<GitResult> RunAsync(string executable, string? directory, string? input,
        TimeSpan limit, CancellationToken cancellationToken, params string[] arguments)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };
        if (input is not null)
            start.StandardInputEncoding = new UTF8Encoding(false);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["LC_ALL"] = "C";
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                     "GIT_NAMESPACE", "GIT_CONFIG", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS" })
            start.Environment.Remove(name);

        start.ArgumentList.Add("--no-optional-locks");
        if (directory is not null)
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);
        }
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new GitException("无法启动 Git。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var inputTask = input is null ? Task.CompletedTask : WriteInputAsync(process, input, timeout.Token);
            await Task.WhenAll(stdout, stderr, inputTask, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }

            cancellationToken.ThrowIfCancellationRequested();
            throw new GitException("Git 查询超时，请检查工作区所在的磁盘。");
        }
    }

    private static async Task WriteInputAsync(Process process, string input, CancellationToken token)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Git may close stdin early and report the reason on stderr.
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
        }
    }
}

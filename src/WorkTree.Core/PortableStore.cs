using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkTree.Core;

public sealed class PortableStore(string dataPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private string SettingsPath => Path.Combine(dataPath, "settings.json");

    public AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>(SettingsPath) ?? new AppSettings();
        if (settings.Version != 1)
            throw new InvalidDataException("设置文件来自不兼容的版本。");

        if (settings.Repositories is null || settings.Tools is null || settings.MergeTargets is null ||
            settings.Repositories.Any(repo => repo is null ||
                !IsAbsolute(repo.MainPath) || !IsAbsolute(repo.CommonDirectory) || !IsAbsolute(repo.AccessPath)) ||
            settings.Tools.Any(tool => tool is null || string.IsNullOrWhiteSpace(tool.Name) ||
                !IsAbsolute(tool.Executable) || tool.Arguments is null || tool.Arguments.Any(arg => arg is null)))
            throw new InvalidDataException("设置文件包含无效的仓库或开发工具配置。");

        return settings;
    }

    public Task SaveSettingsAsync(AppSettings settings) => WriteAsync(SettingsPath, settings);

    public WorktreeSnapshot? LoadSnapshot(RepositoryLocation repository)
    {
        var snapshot = Read<WorktreeSnapshot>(SnapshotPath(repository));
        return snapshot?.Entries is { Length: > 0 } entries &&
            entries.All(entry => entry is not null && IsAbsolute(entry.Path)) ? snapshot : null;
    }

    public Task SaveSnapshotAsync(RepositoryLocation repository, WorktreeSnapshot snapshot) =>
        WriteAsync(SnapshotPath(repository), snapshot);

    private static bool IsAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private string SnapshotPath(RepositoryLocation repository)
    {
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(repository.CommonDirectory.ToUpperInvariant())));
        return Path.Combine(dataPath, "cache", key + ".json");
    }

    private static T? Read<T>(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions)
                ?? throw new InvalidDataException("JSON 文件为空。");
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }

    private static async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

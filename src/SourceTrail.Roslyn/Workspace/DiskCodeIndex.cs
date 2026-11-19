using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;

namespace SourceTrail.Roslyn.Workspace;

internal sealed class DiskCodeIndex(string root, string solutionPath, AnalysisOptions options, Action<string>? log, bool requireWrites = false)
{
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private readonly string directory = Path.Combine(root, "code-" + Hash(JsonSerializer.Serialize(new
    {
        Solution = solutionPath,
        options.ProcedureCallRules,
        // Bump only when stored data/query semantics change, not on unrelated executable rebuilds.
        Version = "source-syntax-2-lazy"
    })));

    internal sealed record Manifest(SolutionState State, string[] ExtraInputs, SolutionInputTracker.FileStamp[] Files);
    private sealed record Entry<T>(string Snapshot, T Value);

    internal Task<Manifest?> ReadManifestAsync(CancellationToken token) => ReadAsync<Manifest>("manifest", token);
    internal Task SaveManifestAsync(Manifest value, CancellationToken token) => WriteAsync("manifest", value, token);
    internal async Task<T?> ReadItemAsync<T>(string snapshot, string key, CancellationToken token) where T : class
    {
        var entry = await ReadAsync<Entry<T>>(Hash(key), token);
        return entry?.Snapshot == snapshot ? entry.Value : null;
    }
    internal Task SaveItemAsync<T>(string snapshot, string key, T value, CancellationToken token) =>
        WriteAsync(Hash(key), new Entry<T>(snapshot, value), token);

    internal async Task CopyToAsync(DiskCodeIndex target, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return;
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Cache directory links are not copied during preparation.");
        Directory.CreateDirectory(target.directory);
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Cache file links are not copied during preparation.");
            await using var input = File.OpenRead(file);
            await using var output = File.Create(Path.Combine(target.directory, Path.GetFileName(file)));
            await input.CopyToAsync(output, token);
        }
    }

    private async Task<T?> ReadAsync<T>(string name, CancellationToken token) where T : class
    {
        try
        {
            await using var stream = File.OpenRead(Path.Combine(directory, name + ".json"));
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteAsync<T>(string name, T value, CancellationToken token)
    {
        string destination = Path.Combine(directory, name + ".json");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception ex) when (!requireWrites && (ex is IOException or UnauthorizedAccessException))
        {
            log?.Invoke($"C# disk index could not be written ({ex.GetType().Name}).");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

using System.Security.Cryptography;
using System.Text;

using SourceTrail.Core.Models;
namespace SourceTrail.Core.Contracts;

// 입력 변경 시 분석 상태를 다시 구성한다. Roslyn 객체 자체는 디스크에 저장하지 않는다.
public interface IRefreshableCodeAnalyzer
{
    Task EnsureFreshAsync(CancellationToken cancellationToken);
}
/// <summary>분석 입력의 목록과 내용 해시를 비교해 추가·수정·삭제 여부를 판단한다.</summary>
public sealed class SolutionInputTracker
{
    private string? fingerprint;
    public void Reset() => fingerprint = null;
    public async Task<bool> ChangedAsync(SolutionState state, IEnumerable<string> extraInputs, CancellationToken token)
    {
        if (state.SolutionPath is null) return false;
        var paths = CollectInputPaths(state, extraInputs);
        var next = await ComputeFingerprintAsync(paths, token);
        bool changed = fingerprint is not null && fingerprint != next;
        fingerprint = next;
        return changed;
    }

    // 소스 외에 프로젝트/참조/상위 설정도 포함해야 해석 결과의 변경을 놓치지 않는다.
    private static HashSet<string> CollectInputPaths(SolutionState state, IEnumerable<string> extraInputs)
    {
        var paths = new HashSet<string>(extraInputs, StringComparer.OrdinalIgnoreCase) { state.SolutionPath! };
        var roots = paths.Where(p => Path.GetExtension(p).Equals(".cs", StringComparison.OrdinalIgnoreCase)).Select(p => Path.GetDirectoryName(p)!)
            .Concat(state.Projects.Where(p => p.File is not null).Select(p => Path.GetDirectoryName(p.File!)!))
            .Append(Path.GetDirectoryName(state.SolutionPath)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var project in state.Projects) if (project.File is not null) paths.Add(project.File);
        var scanRoots = roots.Where(root => !roots.Any(other => !other.Equals(root, StringComparison.OrdinalIgnoreCase) && root.StartsWith(Path.TrimEndingDirectorySeparator(other) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var root in scanRoots)
        {
            AddDirectoryInputs(root, paths);
            AddAncestorSettings(root, paths);
        }
        return paths;
    }

    // 경로 정렬과 내용 해시를 함께 사용해 추가·삭제 및 같은 크기의 수정도 구분한다.
    private static async Task<string> ComputeFingerprintAsync(IEnumerable<string> paths, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            if (File.Exists(path)) hash.AppendData(SHA256.HashData(await File.ReadAllBytesAsync(path, token)));
            else hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }


    private static void AddDirectoryInputs(string root, HashSet<string> paths)
    {
        var pending = new Stack<string>(); if (Directory.Exists(root)) pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attr = File.GetAttributes(entry);
                if (attr.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attr.HasFlag(FileAttributes.Directory))
                { if (Path.GetFileName(entry) is not "bin" and not "obj" and not ".git" and not ".svn" and not ".vs" and not "node_modules") pending.Push(entry); }
                else if (Path.GetExtension(entry).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".config" or ".json" or ".dll") paths.Add(entry);
            }
        }
    }

    private static void AddAncestorSettings(string root, HashSet<string> paths)
    {
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
            foreach (var name in new[] { "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config" })
                paths.Add(Path.Combine(parent.FullName, name));
    }

}

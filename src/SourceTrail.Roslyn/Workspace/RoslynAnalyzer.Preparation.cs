using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;

namespace SourceTrail.Roslyn.Workspace;

public sealed partial class RoslynAnalyzer
{
    public static Task CopyPreparationCacheAsync(AnalysisOptions options, string sourcePath,
        string destination, CancellationToken token) =>
        string.IsNullOrWhiteSpace(options.CodeCacheDirectory) ? Task.CompletedTask :
            new DiskCodeIndex(options.CodeCacheDirectory, sourcePath, options, null)
                .CopyToAsync(new DiskCodeIndex(destination, sourcePath, options, null), token);

    /// <summary>선택 범위의 모든 C# 선언을 구문 분석하고 캐시에 저장한다. 컴파일은 하지 않는다.</summary>
    public async Task<CodePreparation> PrepareDeclarationsAsync(string sourcePath,
        Action<IndexProgress>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadIndexAsync(ValidateSolutionPath(sourcePath), false, token);
            int reused = _sourceFiles.Count(file => file.Hash.Length > 0);
            int parsed = 0, completed = 0;
            progress?.Invoke(new("CSharp", 0, _sourceFiles.Length, 0, reused));
            for (int index = 0; index < _sourceFiles.Length; index++)
            {
                var file = _sourceFiles[index];
                token.ThrowIfCancellationRequested();
                await EnsureParsedAsync(file.File, token, updateSymbols: false, knownIndex: index);
                if (file.Hash.Length == 0) parsed++;
                completed++;
                if (completed % 100 == 0 || completed == _sourceFiles.Length)
                    progress?.Invoke(new("CSharp", completed, _sourceFiles.Length, parsed, reused));
            }
            _indexedSymbols = _sourceFiles.SelectMany(file => file.Symbols).ToArray();
            var current = await SolutionInputTracker.CaptureAsync(_state, [], _stamps, token, hashContent: false);
            if (!SolutionInputTracker.SameContents(_stamps, current))
                throw new IOException("Source inputs changed during preparation. Retry with a stable source tree.");
            await PersistParsedAsync(token);
            return new(_sourceFiles.Length, parsed, reused, _indexedSymbols.Length, _state.Diagnostics.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task<UiPreparation> PrepareUiIndexAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var state = await EnsureUiIndexAsync(token);
            return new(state, _uiIndex!.Screens.Length);
        }
        finally { _gate.Release(); }
    }

    public async Task VerifyPreparationInputsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var current = await SolutionInputTracker.CaptureAsync(_state, [], _stamps, token, hashContent: false);
            if (!SolutionInputTracker.SameContents(_stamps, current))
                throw new IOException("Source inputs changed during preparation. The existing active index is retained.");
        }
        finally { _gate.Release(); }
    }
}

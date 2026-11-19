namespace SourceTrail.Core.Models;

public sealed record IndexProgress(string Stage, int Completed, int Total, int Parsed, int Reused);
public sealed record CodePreparation(int Files, int Parsed, int Reused, int Symbols, int Diagnostics);
public sealed record UiPreparation(string IndexState, int Screens);
public sealed record IndexStage(string Stage, long ElapsedMs);
public sealed record IndexPreparationResult(bool Published, string Mode, string? Generation,
    CodePreparation? Code, UiPreparation? Ui, SqlSourceState? Sql,
    IReadOnlyList<IndexStage> Stages, long ElapsedMs, string? Error,
    bool ReconnectRequired = true);

public sealed class IndexPreparationOptions
{
    public string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceTrail", "prepared-index");
}

using System.Text.Json;
using SourceTrail.Core.Models;

namespace SourceTrail.Mcp.Indexing;

public static class IndexPreparationCommand
{
    public static async Task<int> RunAsync(string[] args, AnalysisOptions analysis, DatabaseOptions database,
        IndexPreparationOptions options, TextWriter output, TextWriter error, CancellationToken token)
    {
        try
        {
            bool rebuild = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                var option = args[i];
                if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
                if (option == "--rebuild") { rebuild = true; continue; }
                if (option is not ("--source" or "--sql-folder" or "--index-directory"))
                    throw new ArgumentException("Unknown preparation option: " + option);
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("A path is required after " + option);
                switch (option)
                {
                    case "--source": analysis.SolutionPath = args[i]; break;
                    case "--sql-folder": database.SqlFolder = args[i]; break;
                    case "--index-directory": options.Directory = args[i]; break;
                }
            }
            var result = await new IndexPreparationService(analysis, database, options).PrepareAsync(rebuild,
                p => error.WriteLine($"{p.Stage}: {p.Completed}/{p.Total}, parsed {p.Parsed}, reused {p.Reused}"), token);
            await output.WriteLineAsync(JsonSerializer.Serialize(result));
            return result.Published ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Index preparation cancelled; the existing active index was retained.");
            return 130;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(e.Message);
            return 1;
        }
    }
}

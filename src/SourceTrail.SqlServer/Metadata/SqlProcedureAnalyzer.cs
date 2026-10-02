using System.Data;
using Microsoft.Data.SqlClient;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;

namespace SourceTrail.SqlServer.Metadata;

public sealed class SqlProcedureAnalyzer(DatabaseOptions? options = null, Action<string>? log = null) : IProcedureAnalyzer
{
    private readonly DatabaseOptions _options = options ?? new();

    public async Task<ProcedureAnalysis> AnalyzeAsync(string procedure, bool includeDefinition, CancellationToken cancellationToken)
    {
        var parts = ParseName(procedure);
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            return new("NotConfigured", procedure, parts.Schema, null, null, [],
                ["Database connection is not configured. C# analysis remains available."]);
        try
        {
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Math.Clamp(_options.CommandTimeoutSeconds, 1, 120);
            command.CommandText = """
                SELECT s.name AS SchemaName, p.name AS ProcedureName,
                       CASE WHEN @includeDefinition = 1 THEN m.definition ELSE NULL END AS Definition,
                       CASE WHEN m.definition IS NULL THEN 0 ELSE 1 END AS HasDefinition
                FROM sys.procedures p
                JOIN sys.schemas s ON s.schema_id = p.schema_id
                LEFT JOIN sys.sql_modules m ON m.object_id = p.object_id
                WHERE p.name = @name AND (@schema IS NULL OR s.name = @schema);

                SELECT s.name AS ProcedureSchema, d.referenced_server_name, d.referenced_database_name,
                       COALESCE(d.referenced_schema_name, rs.name) AS ReferencedSchema,
                       d.referenced_entity_name, o.type_desc,
                       d.referenced_id, d.is_caller_dependent, d.is_ambiguous
                FROM sys.procedures p
                JOIN sys.schemas s ON s.schema_id = p.schema_id
                JOIN sys.sql_expression_dependencies d ON d.referencing_id = p.object_id
                LEFT JOIN sys.objects o ON o.object_id = d.referenced_id
                LEFT JOIN sys.schemas rs ON rs.schema_id = o.schema_id
                WHERE p.name = @name AND (@schema IS NULL OR s.name = @schema);
                """;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = parts.Name;
            command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = (object?)parts.Schema ?? DBNull.Value;
            command.Parameters.Add("@includeDefinition", SqlDbType.Bit).Value = includeDefinition;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var objects = new List<(string Schema, string Name, string? Definition, bool HasDefinition)>();
            while (await reader.ReadAsync(cancellationToken))
                objects.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3) == 1));
            if (objects.Count == 0)
                return new("NotFoundOrNotVisible", procedure, parts.Schema, connection.Database, null, [],
                    ["Procedure not found or metadata is not visible to this account."]);
            if (objects.Count > 1)
                return new("Ambiguous", procedure, null, connection.Database, null, [],
                    ["Multiple schemas contain this procedure. Pass schema.name."]);
            var chosen = objects[0];
            var dependencies = new List<SqlObject>();
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? Read(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                dependencies.Add(new(Read(1), Read(2) ?? connection.Database, Read(3), reader.GetString(4),
                    Read(5) ?? "Unknown", "Unknown", !reader.IsDBNull(6),
                    reader.GetBoolean(7), reader.GetBoolean(8)));
            }
            var warnings = new List<string>
            {
                "Dependencies describe metadata references. READ/WRITE and runtime execution are not inferred.",
                "Dynamic SQL, temporary objects, synonyms and indirect view/function dependencies may require further analysis."
            };
            if (!chosen.HasDefinition) warnings.Add("Definition is unavailable: encryption, object type or VIEW DEFINITION permissions require verification.");
            if (dependencies.Any(d => !d.Resolved)) warnings.Add("Some objects are unresolved or reference another database/server.");
            return new(chosen.HasDefinition && dependencies.All(d => d.Resolved && !d.Ambiguous && !d.CallerDependent)
                ? "Ready" : "Partial", chosen.Schema + "." + chosen.Name, chosen.Schema, connection.Database,
                chosen.Definition, dependencies.Distinct().ToArray(), warnings);
        }
        catch (SqlException error)
        {
            // Do not return exception text: it may expose server/login information.
            log?.Invoke($"Database metadata query failed (SQL error {error.Number}).");
            return new("Unavailable", procedure, parts.Schema, null, null, [],
                [$"Database metadata query failed (SQL error {error.Number}). Check connectivity and metadata permissions."]);
        }
    }

    private static (string? Schema, string Name) ParseName(string procedure)
    {
        if (string.IsNullOrWhiteSpace(procedure)) throw new ArgumentException("procedure is required.");
        var parts = procedure.Replace("[", "").Replace("]", "").Split('.');
        if (parts.Length is < 1 or > 2 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 128))
            throw new ArgumentException("Use procedure or schema.procedure in the configured database.");
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, parts[0]);
    }
}

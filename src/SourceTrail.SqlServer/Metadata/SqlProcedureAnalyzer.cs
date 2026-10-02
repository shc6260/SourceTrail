using System.Data;
using Microsoft.Data.SqlClient;
using SourceTrail.Core.Contracts;
using SourceTrail.Core.Models;

namespace SourceTrail.SqlServer.Metadata;

/// <summary>실제 SQL Server에서 프로시저 정의와 메타데이터 참조를 읽기 전용으로 조회한다.</summary>
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
            await using var command = CreateMetadataCommand(connection, parts, includeDefinition);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var objects = await ReadProceduresAsync(reader, cancellationToken);
            if (objects.Count == 0)
                return new("NotFoundOrNotVisible", procedure, parts.Schema, connection.Database, null, [],
                    ["Procedure not found or metadata is not visible to this account."]);
            if (objects.Count > 1)
                return new("Ambiguous", procedure, null, connection.Database, null, [],
                    ["Multiple schemas contain this procedure. Pass schema.name."]);
            var chosen = objects[0];
            var dependencies = await ReadDependenciesAsync(reader, connection.Database, cancellationToken);
            return BuildAnalysis(chosen, connection.Database, dependencies);
        }
        catch (SqlException error)
        {
            // 접속정보가 노출되지 않도록 예외 본문 대신 SQL 오류 번호만 반환한다.
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

    private sealed record ProcedureMetadata(string Schema, string Name, string? Definition, bool HasDefinition);

    // 사용자 입력은 SQL 문자열에 붙이지 않고 매개변수로 전달한다. 고정 SELECT만 실행한다.
    private SqlCommand CreateMetadataCommand(SqlConnection connection,
        (string? Schema, string Name) parts, bool includeDefinition)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = Math.Clamp(_options.CommandTimeoutSeconds, 1, 120);
        command.CommandText = MetadataQuery;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = parts.Name;
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = (object?)parts.Schema ?? DBNull.Value;
        command.Parameters.Add("@includeDefinition", SqlDbType.Bit).Value = includeDefinition;
        return command;
    }

    private static async Task<List<ProcedureMetadata>> ReadProceduresAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        var objects = new List<ProcedureMetadata>();
        while (await reader.ReadAsync(cancellationToken))
            objects.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3) == 1));
        return objects;
    }

    private static async Task<List<SqlObject>> ReadDependenciesAsync(SqlDataReader reader,
        string database, CancellationToken cancellationToken)
    {
        var dependencies = new List<SqlObject>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? Read(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            dependencies.Add(new(Read(1), Read(2) ?? database, Read(3), reader.GetString(4),
                Read(5) ?? "Unknown", "Unknown", !reader.IsDBNull(6),
                reader.GetBoolean(7), reader.GetBoolean(8)));
        }
        return dependencies;
    }

    // 메타데이터 참조만으로 읽기/쓰기나 실행 성공을 추정하지 않는다.
    private static ProcedureAnalysis BuildAnalysis(ProcedureMetadata chosen, string database, List<SqlObject> dependencies)
    {
        var warnings = new List<string>
        {
            "Dependencies describe metadata references. READ/WRITE and runtime execution are not inferred.",
            "Dynamic SQL, temporary objects, synonyms and indirect view/function dependencies may require further analysis."
        };
        if (!chosen.HasDefinition) warnings.Add("Definition is unavailable: encryption, object type or VIEW DEFINITION permissions require verification.");
        if (dependencies.Any(d => !d.Resolved)) warnings.Add("Some objects are unresolved or reference another database/server.");
        return new(chosen.HasDefinition && dependencies.All(d => d.Resolved && !d.Ambiguous && !d.CallerDependent)
            ? "Ready" : "Partial", chosen.Schema + "." + chosen.Name, chosen.Schema, database,
            chosen.Definition, dependencies.Distinct().ToArray(), warnings);
    }


    private const string MetadataQuery = """
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

}

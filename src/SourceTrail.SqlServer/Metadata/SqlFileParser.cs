using Microsoft.SqlServer.TransactSql.ScriptDom;
using SourceTrail.Core.Models;
namespace SourceTrail.SqlServer.Metadata;

/// <summary>SQL 문법 트리에서 개체 선언과 참조 근거를 추출한다. SQL을 실행하지 않는다.</summary>
internal static class SqlFileParser
{
    public static SqlFileObject[] Parse(string text, string file, out string[] diagnostics)
    {
        var parser = new TSql170Parser(true);
        var script = (TSqlScript)parser.Parse(new StringReader(text), out var errors);
        var parseWarnings = errors.Select(e => $"{file}:{e.Line}:{e.Column}: {e.Message}").ToList();
        var results = new List<SqlFileObject>();
        string? database = null;
        foreach (var statement in script.Batches.SelectMany(b => b.Statements))
        {
            if (statement is UseStatement use) { database = use.DatabaseName.Value; continue; }
            if (statement is AlterTableConstraintModificationStatement) continue;
            var declaration = DescribeDeclaration(statement);
            if (declaration is null)
            {
                AddUnsupportedDiagnostic(statement, file, parseWarnings);
                continue;
            }
            results.Add(BuildObject(statement, declaration, database, text, file, errors));

        }
        diagnostics = parseWarnings.ToArray();
        return results.ToArray();
    }
    private static string Generate(TSqlFragment? fragment)
    {
        if (fragment is null) return "Unknown";
        new Sql170ScriptGenerator().GenerateScript(fragment, out var text);
        return text;
    }
    private sealed class ReferenceVisitor : TSqlFragmentVisitor
    {
        public readonly List<SqlReference> Items = [];
        public bool Dynamic;
        // 쓰기 대상을 방문한 뒤 같은 테이블을 읽기 후보로 중복 수집하지 않는다.
        private readonly HashSet<TSqlFragment> targets = [];
        private void Add(SchemaObjectName name, string access, int line) => Items.Add(new(name.ServerIdentifier?.Value,
            name.DatabaseIdentifier?.Value, name.SchemaIdentifier?.Value, name.BaseIdentifier.Value, access, line));
        private void Target(TableReference target, string access)
        {
            targets.Add(target);
            if (target is NamedTableReference n) Add(n.SchemaObject, access, n.StartLine);
        }
        public override void ExplicitVisit(InsertSpecification node)
        {
            Target(node.Target, "Insert");
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(UpdateSpecification node)
        {
            Target(node.Target, "UpdateCandidate");
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(DeleteSpecification node)
        {
            Target(node.Target, "DeleteCandidate");
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(MergeSpecification node)
        {
            Target(node.Target, "MergeCandidate");
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(NamedTableReference node)
        {
            if (!targets.Contains(node)) Add(node.SchemaObject, "ReadCandidate", node.StartLine);
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var name = node.ProcedureReference?.ProcedureReference?.Name;
            if (name is null) Dynamic = true;
            else { Add(name, "Execute", node.StartLine); if (name.BaseIdentifier.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase)) Dynamic = true; }
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(ExecutableStringList node)
        {
            Dynamic = true;
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            Add(node.SchemaObject, "ReadCandidate", node.StartLine);
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(ForeignKeyConstraintDefinition node)
        {
            Add(node.ReferenceTableName, "ForeignKey", node.StartLine);
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget target)
            {
                var parts = target.MultiPartIdentifier.Identifiers.Select(i => i.Value).Append(node.FunctionName.Value).ToArray();
                Items.Add(new(parts.Length > 3 ? parts[^4] : null, parts.Length > 2 ? parts[^3] : null,
                    parts.Length > 1 ? parts[^2] : null, parts[^1], "Execute", node.StartLine));
            }
            base.ExplicitVisit(node);
        }
    }

    private sealed record Declaration(SchemaObjectName Name, string Kind,
        IEnumerable<ProcedureParameter> Parameters, IEnumerable<ColumnDefinition> Columns, TableDefinition? Table);

    // 파일 이름 대신 실제 SQL 문법으로 개체 종류와 선언 정보를 구분한다.
    private static Declaration? DescribeDeclaration(TSqlStatement statement) => statement switch
    {
        ProcedureStatementBody p => new(p.ProcedureReference.Name, "Procedure", p.Parameters, [], null),
        FunctionStatementBody f => new(f.Name, "Function", f.Parameters, [], null),
        ViewStatementBody v => new(v.SchemaObjectName, "View", [], [], null),
        CreateTableStatement t => new(t.SchemaObjectName, "Table", [], t.Definition.ColumnDefinitions, t.Definition),
        AlterTableAddTableElementStatement add => new(add.SchemaObjectName, "TablePatch", [], add.Definition.ColumnDefinitions, add.Definition),
        _ => null
    };

    private static void AddUnsupportedDiagnostic(TSqlStatement statement, string file, List<string> warnings)
    {
        var type = statement.GetType().Name;
        if (type.StartsWith("Create", StringComparison.Ordinal) || type.StartsWith("Alter", StringComparison.Ordinal))
            warnings.Add($"{file}:{statement.StartLine}: {type} is not indexed as a supported object.");
    }

    private static SqlFileObject BuildObject(TSqlStatement statement, Declaration declaration,
        string? database, string text, string file, IList<ParseError> errors)
    {
        var references = new ReferenceVisitor();
        statement.Accept(references);
        var warnings = errors.Select(e => $"{file}:{e.Line}: {e.Message}").ToList();
        if (references.Dynamic) warnings.Add("Dynamic SQL or variable procedure execution requires verification.");
        var name = declaration.Name;
        return new(database ?? name.DatabaseIdentifier?.Value, name.SchemaIdentifier?.Value, name.BaseIdentifier.Value,
            declaration.Kind, file, statement.StartLine, text.Substring(statement.StartOffset, statement.FragmentLength),
            BuildMembers(declaration), references.Items.Distinct().ToArray(), warnings, BuildConstraints(declaration.Table));
    }

    private static SqlMember[] BuildMembers(Declaration declaration) => declaration.Parameters
        .Select(p => new SqlMember(p.VariableName.Value, Generate(p.DataType), IsOutput: p.Modifier.ToString().Contains("Output")))
        .Concat(declaration.Columns.Select(c => new SqlMember(c.ColumnIdentifier.Value, Generate(c.DataType),
            c.Constraints.OfType<NullableConstraintDefinition>().FirstOrDefault()?.Nullable))).ToArray();

    private static string[] BuildConstraints(TableDefinition? table) => table is null ? [] : table.TableConstraints
        .Concat(table.ColumnDefinitions.SelectMany(c => c.Constraints)).Select(Generate).ToArray();

}

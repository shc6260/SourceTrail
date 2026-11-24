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
        // 별칭 쓰기 대상이 가리키는 FROM 항목을 확정하지 못한 경우(별칭, 줄).
        public readonly List<(string Alias, int Line)> UnresolvedTargets = [];
        private void Target(TableReference target, string access, FromClause? from = null)
        {
            targets.Add(target);
            if (target is not NamedTableReference n) return;
            // UPDATE a SET ... FROM dbo.T a / DELETE a FROM dbo.T AS a: 한 단어 대상은 FROM의 별칭(또는 이름)을 먼저 찾는다.
            if (from is not null && n.SchemaObject.Identifiers.Count == 1)
            {
                var name = n.SchemaObject.BaseIdentifier.Value;
                var match = Flatten(from.TableReferences).FirstOrDefault(r => name.Equals(AliasOrName(r), StringComparison.OrdinalIgnoreCase));
                switch (match)
                {
                    case NamedTableReference m: // 실제 테이블, CTE, 임시 테이블 모두 FROM의 이름 그대로 쓰기 대상이 된다.
                        targets.Add(m); // 같은 문장의 SELECT 중복은 쓰기로 대체한다. 다른 별칭으로 JOIN한 같은 테이블은 읽기로 남는다.
                        Add(m.SchemaObject, access, n.StartLine);
                        return;
                    case VariableTableReference v: // 테이블 변수는 DB 개체가 아니다.
                        targets.Add(v);
                        return;
                    case null: // FROM에 없으면 대상 이름이 곧 테이블(또는 CTE)이다.
                        break;
                    default:
                        targets.Add(match);
                        UnresolvedTargets.Add((name, n.StartLine));
                        return;
                }
            }
            Add(n.SchemaObject, access, n.StartLine);
        }
        private static string? AliasOrName(TableReference r) => r switch
        {
            NamedTableReference n => n.Alias?.Value ?? n.SchemaObject.BaseIdentifier.Value,
            TableReferenceWithAlias a => a.Alias?.Value,
            _ => null
        };
        private static IEnumerable<TableReference> Flatten(IEnumerable<TableReference> references)
        {
            foreach (var r in references)
                switch (r)
                {
                    case JoinTableReference j:
                        foreach (var inner in Flatten([j.FirstTableReference, j.SecondTableReference])) yield return inner;
                        break;
                    case JoinParenthesisTableReference p:
                        foreach (var inner in Flatten([p.Join])) yield return inner;
                        break;
                    default:
                        yield return r;
                        break;
                }
        }
        public override void ExplicitVisit(InsertSpecification node)
        {
            Target(node.Target, "Insert");
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(UpdateSpecification node)
        {
            Target(node.Target, "UpdateCandidate", node.FromClause);
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(DeleteSpecification node)
        {
            Target(node.Target, "DeleteCandidate", node.FromClause);
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
        foreach (var (alias, line) in references.UnresolvedTargets)
            warnings.Add($"{file}:{line}: Write target alias '{alias}' could not be resolved to a table; verify the source.");
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

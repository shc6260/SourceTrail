using Microsoft.SqlServer.TransactSql.ScriptDom;
using SourceTrail.Core.Models;
namespace SourceTrail.SqlServer.Metadata;

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
            SchemaObjectName? name = null;
            string? kind = null;
            IEnumerable<ProcedureParameter> parameters = [];
            IEnumerable<ColumnDefinition> columns = [];
            TableDefinition? tableDefinition = null;
            if (statement is ProcedureStatementBody p) { name = p.ProcedureReference.Name; kind = "Procedure"; parameters = p.Parameters; }
            else if (statement is FunctionStatementBody f) { name = f.Name; kind = "Function"; parameters = f.Parameters; }
            else if (statement is ViewStatementBody v) { name = v.SchemaObjectName; kind = "View"; }
            else if (statement is CreateTableStatement t) { name = t.SchemaObjectName; kind = "Table"; columns = t.Definition.ColumnDefinitions; tableDefinition = t.Definition; }
            else if (statement is AlterTableAddTableElementStatement add) { name = add.SchemaObjectName; kind = "TablePatch"; columns = add.Definition.ColumnDefinitions; tableDefinition = add.Definition; }
            else if (statement is AlterTableConstraintModificationStatement) continue;
                        if (name is null || kind is null)
            {
                if (statement.GetType().Name.StartsWith("Create", StringComparison.Ordinal) || statement.GetType().Name.StartsWith("Alter", StringComparison.Ordinal))
                    parseWarnings.Add($"{file}:{statement.StartLine}: {statement.GetType().Name} is not indexed as a supported object.");
                continue;
            }
            var references = new ReferenceVisitor();
            statement.Accept(references);
            var members = parameters.Select(p => new SqlMember(p.VariableName.Value, Generate(p.DataType), IsOutput: p.Modifier.ToString().Contains("Output")))
                .Concat(columns.Select(c => new SqlMember(c.ColumnIdentifier.Value, Generate(c.DataType), c.Constraints.OfType<NullableConstraintDefinition>().FirstOrDefault()?.Nullable))).ToArray();
            var warnings = errors.Select(e => $"{file}:{e.Line}: {e.Message}").ToList();
            if (references.Dynamic) warnings.Add("Dynamic SQL or variable procedure execution requires verification.");
            results.Add(new(database ?? name.DatabaseIdentifier?.Value, name.SchemaIdentifier?.Value, name.BaseIdentifier.Value,
                kind, file, statement.StartLine, text.Substring(statement.StartOffset, statement.FragmentLength), members,
                references.Items.Distinct().ToArray(), warnings, tableDefinition is not null
                ? tableDefinition.TableConstraints.Concat(tableDefinition.ColumnDefinitions.SelectMany(c => c.Constraints)).Select(Generate).ToArray() : []));
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
        private readonly HashSet<TSqlFragment> targets = [];
        private void Add(SchemaObjectName name, string access, int line) => Items.Add(new(name.ServerIdentifier?.Value,
            name.DatabaseIdentifier?.Value, name.SchemaIdentifier?.Value, name.BaseIdentifier.Value, access, line));
        private void Target(TableReference target, string access)
        {
            targets.Add(target);
            if (target is NamedTableReference n) Add(n.SchemaObject, access, n.StartLine);
        }
        public override void ExplicitVisit(InsertSpecification node) { Target(node.Target, "Insert"); base.ExplicitVisit(node); }
        public override void ExplicitVisit(UpdateSpecification node) { Target(node.Target, "UpdateCandidate"); base.ExplicitVisit(node); }
        public override void ExplicitVisit(DeleteSpecification node) { Target(node.Target, "DeleteCandidate"); base.ExplicitVisit(node); }
        public override void ExplicitVisit(MergeSpecification node) { Target(node.Target, "MergeCandidate"); base.ExplicitVisit(node); }
        public override void ExplicitVisit(NamedTableReference node) { if (!targets.Contains(node)) Add(node.SchemaObject, "ReadCandidate", node.StartLine); base.ExplicitVisit(node); }
        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var name = node.ProcedureReference?.ProcedureReference?.Name;
            if (name is null) Dynamic = true;
            else { Add(name, "Execute", node.StartLine); if (name.BaseIdentifier.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase)) Dynamic = true; }
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(ExecutableStringList node) { Dynamic = true; base.ExplicitVisit(node); }
        public override void ExplicitVisit(SchemaObjectFunctionTableReference node) { Add(node.SchemaObject, "ReadCandidate", node.StartLine); base.ExplicitVisit(node); }
        public override void ExplicitVisit(ForeignKeyConstraintDefinition node) { Add(node.ReferenceTableName, "ForeignKey", node.StartLine); base.ExplicitVisit(node); }
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
}

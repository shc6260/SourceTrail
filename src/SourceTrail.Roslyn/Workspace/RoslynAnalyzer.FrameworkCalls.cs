using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SourceTrail.Core.Models;
using SymbolInfo = SourceTrail.Core.Models.SymbolInfo;

namespace SourceTrail.Roslyn.Workspace;

public sealed partial class RoslynAnalyzer
{
    private sealed record WcfHostOperation(IMethodSymbol Method, ProjectId Project, string HostFile);
    private Dictionary<string, List<WcfHostOperation>>? _wcfHostsByAction;

    private bool AddWcfHostCandidates(string? action, ISymbol caller, ProjectId projectId,
        Location callSite, List<CodeRelation> relations, CancellationToken token)
    {
        if (action is null) return false;
        _wcfHostsByAction ??= IndexWcfHosts(token);
        if (!_wcfHostsByAction.TryGetValue(action, out var hosts)) return false;

        var site = callSite.GetLineSpan();
        foreach (var host in hosts)
        {
            relations.Add(new(Describe(caller, projectId), Describe(host.Method, host.Project),
                "DispatchCandidate", host.HostFile, 1,
                $"WCF action {action} at {site.Path}:{site.StartLinePosition.Line + 1} matches a contract implemented by the ServiceHost type. Runtime endpoint requires verification."));
        }
        return true;
    }

    private Dictionary<string, List<WcfHostOperation>> IndexWcfHosts(CancellationToken token)
    {
        var index = new Dictionary<string, List<WcfHostOperation>>(StringComparer.Ordinal);
        foreach (var (symbol, project) in _symbols.Values)
        {
            token.ThrowIfCancellationRequested();
            if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type ||
                FindServiceHost(type) is not { } hostFile) continue;
            foreach (var contract in type.AllInterfaces)
            foreach (var operation in contract.GetMembers().OfType<IMethodSymbol>())
            {
                if (WcfAction(operation) is not { } action ||
                    type.FindImplementationForInterfaceMember(operation) is not IMethodSymbol method) continue;
                var entry = _symbols.TryGetValue(Id(method, project), out var local) &&
                    SymbolEqualityComparer.Default.Equals(local.Symbol, method.OriginalDefinition)
                    ? local : FindSourceEntry(method);
                if (entry is not { Symbol: IMethodSymbol implementation } resolved) continue;
                if (!index.TryGetValue(action, out var hosts)) index[action] = hosts = [];
                var candidate = new WcfHostOperation(implementation, resolved.Project, hostFile);
                if (!hosts.Contains(candidate)) hosts.Add(candidate);
            }
        }
        return index;
    }

    private static string? GeneratedProxyAction(IMethodSymbol target)
    {
        if (target.IsStatic || target.MethodKind != MethodKind.Ordinary ||
            !target.ContainingType.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
                attribute.ConstructorArguments.FirstOrDefault().Value as string == "System.ServiceModel"))
            return null;
        for (var type = target.ContainingType.BaseType; type is not null; type = type.BaseType)
        {
            if (type.OriginalDefinition.ToDisplayString() != "System.ServiceModel.ClientBase<TChannel>" ||
                type.TypeArguments is not [INamedTypeSymbol contract]) continue;
            var actions = contract.GetMembers(target.Name).OfType<IMethodSymbol>()
                .Select(WcfAction).OfType<string>().Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return actions.Length == 1 ? actions[0] : null;
        }
        return null;
    }

    private static string? WcfAction(IMethodSymbol method)
    {
        if (method.ContainingType.TypeKind != TypeKind.Interface) return null;
        var operation = method.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ServiceModel.OperationContractAttribute");
        var contract = method.ContainingType.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ServiceModel.ServiceContractAttribute");
        if (operation is null || contract is null ||
            operation.NamedArguments.Any(pair => pair.Key == "AsyncPattern" && pair.Value.Value is true)) return null;
        string? explicitAction = AttributeString(operation, "Action");
        if (explicitAction is not null) return explicitAction == "*" ? null : explicitAction;
        string ns = AttributeString(contract, "Namespace") ?? "http://tempuri.org/";
        string type = AttributeString(contract, "Name") ?? method.ContainingType.Name;
        string name = AttributeString(operation, "Name") ?? method.Name;
        return ns.TrimEnd('/') + "/" + type + "/" + name;
    }

    private static string? AttributeString(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string;

    private static string? FindServiceHost(INamedTypeSymbol type)
    {
        foreach (var source in type.Locations.Where(location => location.IsInSource))
        {
            string? codeFile = source.SourceTree?.FilePath;
            if (codeFile is null || !codeFile.EndsWith(".svc.cs", StringComparison.OrdinalIgnoreCase)) continue;
            string path = codeFile[..^3];
            if (!File.Exists(path)) continue;
            try
            {
                using var reader = File.OpenText(path);
                string? firstLine = reader.ReadLine();
                if (firstLine is null) continue;
                var directive = Regex.Match(firstLine, """^\s*<%@\s*ServiceHost\b[^%]*?\bService\s*=\s*["']([^"']+)["']""");
                if (directive.Success && directive.Groups[1].Value == type.ToDisplayString()) return path;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private async Task<IReadOnlyList<ProcedureUsage>> TableAdapterUsagesAsync(IMethodSymbol method,
        SymbolInfo described, CancellationToken token)
    {
        if (method.Name != "Update" || method.Parameters.Length != 1 ||
            !method.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute" &&
                attribute.ConstructorArguments.FirstOrDefault().Value as string == "System.Data.Design.TypedDataSetGenerator"))
            return [];

        var usages = new List<ProcedureUsage>();
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var document = RequireSolution().GetDocument(reference.SyntaxTree);
            var model = document is null ? null : await document.GetSemanticModelAsync(token);
            if (model is null) continue;
            var syntax = await reference.GetSyntaxAsync(token);
            foreach (var call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetOperation(call, token) is not IInvocationOperation { TargetMethod.Name: "Update" } update ||
                    (update.Instance is IConversionOperation { IsImplicit: true } receiverConversion
                        ? receiverConversion.Operand : update.Instance) is not IPropertyReferenceOperation receiver ||
                    !DerivesFromDbDataAdapter(receiver.Type) ||
                    update.Arguments.Length != 1 ||
                    (update.Arguments[0].Value is IConversionOperation { IsImplicit: true } conversion
                        ? conversion.Operand : update.Arguments[0].Value) is not IParameterReferenceOperation parameter ||
                    !SymbolEqualityComparer.Default.Equals(parameter.Parameter, method.Parameters[0]))
                    continue;
                usages.AddRange(await ReadAdapterCommandsAsync(receiver.Property, described, token));
            }
        }
        return usages.DistinctBy(usage => (usage.Procedure, usage.File, usage.Line, usage.RelationType)).ToArray();
    }

    private static bool DerivesFromDbDataAdapter(ITypeSymbol? type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Data.Common.DbDataAdapter") return true;
        return false;
    }

    private async Task<IReadOnlyList<ProcedureUsage>> ReadAdapterCommandsAsync(IPropertySymbol property,
        SymbolInfo described, CancellationToken token)
    {
        foreach (var getter in property.GetMethod?.DeclaringSyntaxReferences ?? [])
        {
            var document = RequireSolution().GetDocument(getter.SyntaxTree);
            var model = document is null ? null : await document.GetSemanticModelAsync(token);
            if (model is null) continue;
            var syntax = await getter.GetSyntaxAsync(token);
            var fields = syntax.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Select(statement => model.GetOperation(statement, token) as IReturnOperation)
                .Select(operation => operation?.ReturnedValue as IFieldReferenceOperation).OfType<IFieldReferenceOperation>()
                .Take(2).ToArray();
            var initializer = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(call => model.GetOperation(call, token) as IInvocationOperation)
                .FirstOrDefault(call => call?.TargetMethod.Name == "InitAdapter");
            if (fields.Length != 1 || initializer is null) continue;
            foreach (var reference in initializer.TargetMethod.DeclaringSyntaxReferences)
            {
                var initDocument = RequireSolution().GetDocument(reference.SyntaxTree);
                var initModel = initDocument is null ? null : await initDocument.GetSemanticModelAsync(token);
                if (initModel is null) continue;
                var body = await reference.GetSyntaxAsync(token);
                return ReadAdapterAssignments(body, initModel, fields[0].Field, described, token);
            }
        }
        return [];
    }

    private static IReadOnlyList<ProcedureUsage> ReadAdapterAssignments(SyntaxNode body, SemanticModel model,
        IFieldSymbol field, SymbolInfo described, CancellationToken token)
    {
        var assignments = new List<(IPropertySymbol Command, string Member, AssignmentExpressionSyntax Syntax)>();
        foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not MemberAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax receiver } member ||
                member.Name.Identifier.ValueText is not ("CommandText" or "CommandType") ||
                !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver.Expression, token).Symbol, field) ||
                model.GetSymbolInfo(receiver, token).Symbol is not IPropertySymbol command ||
                command.Name is not ("InsertCommand" or "UpdateCommand" or "DeleteCommand") || !IsDbCommand(command.Type))
                continue;
            assignments.Add((command, member.Name.Identifier.ValueText, assignment));
        }

        var usages = new List<ProcedureUsage>();
        foreach (var group in assignments.GroupBy(assignment => assignment.Command,
            (IEqualityComparer<IPropertySymbol>)SymbolEqualityComparer.Default))
        {
            var type = group.Where(assignment => assignment.Member == "CommandType").ToArray();
            var text = group.Where(assignment => assignment.Member == "CommandText").ToArray();
            if (type.Length != 1 || model.GetConstantValue(type[0].Syntax.Right, token) is not { HasValue: true, Value: 4 } ||
                text.Length != 1 || model.GetConstantValue(text[0].Syntax.Right, token) is not { HasValue: true, Value: string procedure })
                continue;
            var span = text[0].Syntax.GetLocation().GetLineSpan();
            string row = group.Key.Name switch
            {
                "InsertCommand" => "Added",
                "UpdateCommand" => "Modified",
                _ => "Deleted"
            };
            usages.Add(new(procedure, span.Path, span.StartLinePosition.Line + 1, described,
                "TableAdapterCommand", true,
                $"Typed TableAdapter invokes DbDataAdapter.Update; {group.Key.Name} is a stored procedure for {row} rows."));
        }
        return usages;
    }
}

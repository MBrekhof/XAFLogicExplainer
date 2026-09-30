using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Core.Analyzers;

/// <summary>
/// Finds handlers attached to Object Space events — <c>ObjectSpace.Committing += …</c> and its six
/// siblings — in every class of the module and the platform projects beside it, and names the business
/// classes each one concerns.
/// </summary>
/// <remarks>
/// Runs after the controller inventory and its inherited targeting are final, because a controller's
/// target is one of the two kinds of evidence. Syntax only, like every analyzer here: a receiver whose
/// type the source does not show is kept and marked rather than guessed.
/// </remarks>
public static class ObjectSpaceHandlerAnalyzer
{
    private static readonly Dictionary<string, ObjectSpaceEvent> Events =
        Enum.GetValues<ObjectSpaceEvent>().ToDictionary(e => e.ToString(), StringComparer.Ordinal);

    // ponytail: same-class calls only; three deep covers a lambda → Apply → ApplyRules chain.
    private const int CallDepth = 3;

    private static readonly HashSet<string> ObjectSpaceFactories = new(StringComparer.Ordinal)
    {
        "CreateObjectSpace", "CreateNonSecuredObjectSpace", "CreateNestedObjectSpace", "CreateUpdatingObjectSpace",
    };

    private enum Receiver { Confirmed, Unknown, NotObjectSpace }

    /// <summary>Fills <see cref="ExtractedProject.ObjectSpaceHandlers"/>.</summary>
    public static void Analyze(ExtractedProject project, string projectPath, ExtractionOptions options)
    {
        var directory = new EntityDirectory(project.Entities);
        var directories = new List<string> { projectPath };
        if (options.DiscoverPlatformModels)
            directories.AddRange(SourceRoster.SiblingDirectories(projectPath));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceDirectory in directories)
        {
            var sourceProject = new DirectoryInfo(sourceDirectory).Name;
            foreach (var file in EditorAnalyzer.EnumerateSource(sourceDirectory).Order(StringComparer.Ordinal))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                // Cheap gate: most files subscribe to nothing of the kind.
                if (!text.Contains("+=", StringComparison.Ordinal) || !Events.Keys.Any(text.Contains))
                    continue;

                var root = CSharpSyntaxTree.ParseText(text, path: file).GetRoot();
                foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                foreach (var handler in Read(declaration, file, sourceProject, project, directory))
                {
                    if (seen.Add(Key(handler)))
                        project.ObjectSpaceHandlers.Add(handler);
                }
            }
        }
    }

    /// <summary>
    /// A handler subscribed twice alike (unsubscribed around a save, say) is one handler; two lambdas are two,
    /// and so is one method subscribed on the controller's own Object Space and on a popup's, whose evidence differs.
    /// </summary>
    private static string Key(ExtractedObjectSpaceHandler h) =>
        $"{h.DeclaringNamespace}|{h.DeclaringClass}|{h.Event}|{h.Handler}|{h.ReceiverUnconfirmed}" +
        $"|{string.Join(",", h.ControllerTargets)}|{h.TargetOutsideApp}" +
        (h.Handler == "lambda" ? $"|{h.FilePath}|{h.Line}" : string.Empty);

    private static IEnumerable<ExtractedObjectSpaceHandler> Read(
        ClassDeclarationSyntax declaration, string file, string sourceProject,
        ExtractedProject project, EntityDirectory directory)
    {
        var className = declaration.Identifier.Text;
        var ns = ControllerAnalyzer.GetNamespace(declaration);
        var controller = project.Controllers.FirstOrDefault(c => c.ClassName == className && c.Namespace == ns);

        foreach (var subscription in declaration.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!subscription.IsKind(SyntaxKind.AddAssignmentExpression)
                || subscription.Left is not MemberAccessExpressionSyntax target
                || !Events.TryGetValue(target.Name.Identifier.Text, out var ev)
                || subscription.Ancestors().OfType<ClassDeclarationSyntax>().First() != declaration)
                continue;

            var receiver = Classify(target.Expression, subscription, declaration);
            if (receiver == Receiver.NotObjectSpace)
                continue;

            var (name, bodies) = HandlerOf(subscription.Right, declaration);
            var handler = new ExtractedObjectSpaceHandler
            {
                Event = ev,
                DeclaringClass = className,
                DeclaringNamespace = ns,
                InController = controller is not null,
                Handler = name,
                FilePath = file,
                Line = subscription.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                SourceProject = sourceProject,
                ReceiverUnconfirmed = receiver == Receiver.Unknown,
                Checks = Checks(bodies, declaration, ns, directory),
            };
            // The target speaks for the controller's own Object Space only, not for one it creates.
            if (controller is not null && IsOwnObjectSpace(target.Expression))
                AttachTarget(handler, controller, declaration, directory, project);
            yield return handler;
        }
    }

    /// <summary><c>ObjectSpace</c>, <c>this.ObjectSpace</c>, <c>View.ObjectSpace</c>, <c>Frame.View.ObjectSpace</c>.</summary>
    private static bool IsOwnObjectSpace(ExpressionSyntax receiver) => Unwrap(receiver) switch
    {
        IdentifierNameSyntax { Identifier.Text: "ObjectSpace" } => true,
        MemberAccessExpressionSyntax { Name.Identifier.Text: "ObjectSpace", Expression: var owner } =>
            owner.ToString() is "this" or "View" or "this.View" or "Frame.View" or "this.Frame.View",
        _ => false,
    };

    // ----------------------------------------------------------------- receiver

    private static Receiver Classify(ExpressionSyntax receiver, SyntaxNode use, ClassDeclarationSyntax declaration)
    {
        switch (Unwrap(receiver))
        {
            case IdentifierNameSyntax { Identifier.Text: "ObjectSpace" }:
            case MemberAccessExpressionSyntax { Name.Identifier.Text: "ObjectSpace" }:
                return Receiver.Confirmed;
            case CastExpressionSyntax cast:
                return ByType(cast.Type.ToString());
            case BinaryExpressionSyntax asExpression when asExpression.IsKind(SyntaxKind.AsExpression):
                return ByType(asExpression.Right.ToString());
            case IdentifierNameSyntax identifier:
                return ByDeclaration(identifier.Identifier.Text, use, declaration);
            default:
                return Receiver.Unknown;
        }
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) =>
        expression is ParenthesizedExpressionSyntax p ? Unwrap(p.Expression) : expression;

    /// <summary>What a name refers to: a parameter, a local declared before the use, or a field or property.</summary>
    private static Receiver ByDeclaration(string name, SyntaxNode use, ClassDeclarationSyntax declaration)
    {
        foreach (var scope in use.Ancestors())
        {
            var parameter = scope switch
            {
                BaseMethodDeclarationSyntax m => m.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == name),
                LocalFunctionStatementSyntax f => f.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == name),
                ParenthesizedLambdaExpressionSyntax l => l.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == name),
                SimpleLambdaExpressionSyntax s when s.Parameter.Identifier.Text == name => s.Parameter,
                _ => null,
            };
            if (parameter is not null)
                return parameter.Type is null ? Receiver.Unknown : ByType(parameter.Type.ToString());

            if (scope is MemberDeclarationSyntax)
            {
                var local = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Where(v => v.Identifier.Text == name && v.SpanStart < use.SpanStart
                                && v.Parent is VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax or UsingStatementSyntax })
                    .LastOrDefault();
                if (local?.Parent is VariableDeclarationSyntax localDeclaration)
                    return localDeclaration.Type.IsVar
                        ? ByInitializer(local.Initializer?.Value)
                        : ByType(localDeclaration.Type.ToString());
                break;
            }
        }

        foreach (var member in declaration.Members)
        {
            if (member is FieldDeclarationSyntax field && field.Declaration.Variables.Any(v => v.Identifier.Text == name))
                return ByType(field.Declaration.Type.ToString());
            if (member is PropertyDeclarationSyntax property && property.Identifier.Text == name)
                return ByType(property.Type.ToString());
        }

        return Receiver.Unknown;
    }

    private static Receiver ByInitializer(ExpressionSyntax? value) => value is null ? Receiver.Unknown : Unwrap(value) switch
    {
        CastExpressionSyntax cast => ByType(cast.Type.ToString()),
        BinaryExpressionSyntax asExpression when asExpression.IsKind(SyntaxKind.AsExpression) => ByType(asExpression.Right.ToString()),
        InvocationExpressionSyntax call when ObjectSpaceFactories.Contains(MethodName(call.Expression)) => Receiver.Confirmed,
        IdentifierNameSyntax { Identifier.Text: "ObjectSpace" } => Receiver.Confirmed,
        MemberAccessExpressionSyntax { Name.Identifier.Text: "ObjectSpace" } => Receiver.Confirmed,
        _ => Receiver.Unknown,
    };

    private static Receiver ByType(string written)
    {
        var name = LastSegment(written);
        return name switch
        {
            "var" or "dynamic" or "object" or "" => Receiver.Unknown,
            _ when name.EndsWith("ObjectSpace", StringComparison.Ordinal) => Receiver.Confirmed,
            _ => Receiver.NotObjectSpace,
        };
    }

    // ----------------------------------------------------------------- handler

    private static (string Name, List<SyntaxNode> Bodies) HandlerOf(ExpressionSyntax right, ClassDeclarationSyntax declaration)
    {
        switch (Unwrap(right))
        {
            case AnonymousFunctionExpressionSyntax lambda:
                return ("lambda", [lambda.Body]);
            case IdentifierNameSyntax method:
                return (method.Identifier.Text, BodiesOf(method.Identifier.Text, declaration));
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member:
                return (member.Name.Identifier.Text, BodiesOf(member.Name.Identifier.Text, declaration));
            case ObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax wrapped }] }:
                return (wrapped.Identifier.Text, BodiesOf(wrapped.Identifier.Text, declaration));
            default:
                return (right.ToString(), []);
        }
    }

    // ponytail: only this declaration's methods; a handler in another partial part of the class is not read.
    private static List<SyntaxNode> BodiesOf(string method, ClassDeclarationSyntax declaration) =>
        declaration.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == method)
            .Select(m => (SyntaxNode?)m.Body ?? m.ExpressionBody)
            .OfType<SyntaxNode>()
            .ToList();

    /// <summary>The business classes the handler tests objects for, following calls to its own class's methods.</summary>
    private static List<string> Checks(List<SyntaxNode> bodies, ClassDeclarationSyntax declaration, string ns, EntityDirectory directory)
    {
        var found = new List<string>();
        var visited = new HashSet<SyntaxNode>();
        var queue = new Queue<(SyntaxNode Body, int Depth)>(bodies.Select(b => (b, 0)));

        while (queue.Count > 0)
        {
            var (body, depth) = queue.Dequeue();
            if (!visited.Add(body))
                continue;

            foreach (var node in body.DescendantNodesAndSelf())
            {
                foreach (var type in TestedTypes(node))
                {
                    if (directory.Resolve(type.ToString(), ns) is { } entity && !found.Contains(FullName(entity)))
                        found.Add(FullName(entity));
                }

                if (depth < CallDepth && node is InvocationExpressionSyntax call)
                {
                    var callee = call.Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        GenericNameSyntax generic => generic.Identifier.Text,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.Text,
                        _ => null,
                    };
                    if (callee is not null)
                        foreach (var calleeBody in BodiesOf(callee, declaration))
                            queue.Enqueue((calleeBody, depth + 1));
                }
            }
        }

        return found;
    }

    private static IEnumerable<TypeSyntax> TestedTypes(SyntaxNode node)
    {
        switch (node)
        {
            case DeclarationPatternSyntax declaration:
                yield return declaration.Type;
                break;
            case TypePatternSyntax typePattern:
                yield return typePattern.Type;
                break;
            case RecursivePatternSyntax { Type: { } recursiveType }:
                yield return recursiveType;
                break;
            // `x is not Order` parses as a constant pattern naming the type.
            case ConstantPatternSyntax { Expression: NameSyntax constantName }:
                yield return constantName;
                break;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression):
                if (binary.Right is TypeSyntax tested)
                    yield return tested;
                break;
            case CastExpressionSyntax cast:
                yield return cast.Type;
                break;
            case GenericNameSyntax { Identifier.Text: "OfType", TypeArgumentList.Arguments: [var ofType] }:
                yield return ofType;
                break;
        }
    }

    // ----------------------------------------------------------------- controller target

    /// <summary>
    /// The class the declaring controller targets, read as written where the class declares it — the
    /// inventory keeps only the bare name, which two classes may share.
    /// </summary>
    private static void AttachTarget(
        ExtractedObjectSpaceHandler handler, ExtractedController controller, ClassDeclarationSyntax declaration,
        EntityDirectory directory, ExtractedProject project)
    {
        var written = WrittenTarget(declaration);
        if (written is null)
        {
            // Inherited, or set in a designer partial: bare, so only an unshared name is safe.
            written = controller.Targeting.TargetObjectType;
            if (written is null || directory.IsShared(LastSegment(written)))
                return;
        }

        if (directory.Resolve(written, handler.DeclaringNamespace) is { } entity)
            handler.ControllerTargets.Add(FullName(entity));
        else if (project.Entities.All(e => e.ClassName != LastSegment(written)))
            handler.TargetOutsideApp = LastSegment(written);
        // Otherwise an application class name the namespace does not settle: left unattached.
    }

    /// <remarks>
    /// The constructor's assignment first: it runs after <c>ObjectViewController&lt;TView, TObject&gt;</c>'s and
    /// replaces it. Only statements — an object initializer's <c>TargetObjectType</c> belongs to an action.
    /// </remarks>
    private static string? WrittenTarget(ClassDeclarationSyntax declaration)
    {
        var assigned = declaration.Members.OfType<ConstructorDeclarationSyntax>()
            .SelectMany(c => c.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            .Where(a => a.Parent is ExpressionStatementSyntax
                        && a.Left is IdentifierNameSyntax { Identifier.Text: "TargetObjectType" }
                            or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.Text: "TargetObjectType" })
            .Select(a => a.Right is TypeOfExpressionSyntax typeOf ? typeOf.Type.ToString() : null)
            .LastOrDefault(t => t is not null);
        if (assigned is not null)
            return assigned;

        foreach (var baseType in declaration.BaseList?.Types ?? default)
        {
            var type = baseType.Type is QualifiedNameSyntax q ? q.Right : baseType.Type;
            if (type is GenericNameSyntax { Identifier.Text: "ObjectViewController", TypeArgumentList.Arguments.Count: 2 } generic)
                return generic.TypeArgumentList.Arguments[1].ToString();
        }

        return null;
    }

    // ----------------------------------------------------------------- names

    private static string FullName(ExtractedEntity entity) =>
        string.IsNullOrEmpty(entity.Namespace) ? entity.ClassName : $"{entity.Namespace}.{entity.ClassName}";

    private static string MethodName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => string.Empty,
    };

    /// <summary><c>global::A.B.Order&lt;T&gt;?</c> → <c>Order</c>.</summary>
    private static string LastSegment(string written)
    {
        var name = written.Trim().TrimEnd('?');
        var generic = name.IndexOf('<');
        if (generic >= 0)
            name = name[..generic];
        var dot = Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':'));
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}

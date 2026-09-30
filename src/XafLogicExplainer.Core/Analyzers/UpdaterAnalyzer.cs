using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Core.Analyzers;

/// <summary>
/// Extracts seed-data creation logic from XAF updater classes.
/// </summary>
public class UpdaterAnalyzer
{
    /// <summary>
    /// Finds and analyzes updater methods that create initial data.
    /// </summary>
    /// <param name="sourceDirectory">Project source root.</param>
    /// <param name="options">Extraction options controlling output detail.</param>
    /// <param name="businessClasses">
    /// The module's business class names. When given, <c>new T(…)</c> counts as a seed only for one
    /// of them (or XAF's security classes, or an XPO <c>new T(session)</c>); without it, every
    /// <c>new</c> does, as before.
    /// </param>
    /// <returns>Extracted seed-data descriptors.</returns>
    public List<ExtractedSeedData> AnalyzeUpdater(
        string sourceDirectory,
        ExtractionOptions options,
        IReadOnlyCollection<string>? businessClasses = null)
    {
        var known = businessClasses is null ? null : new HashSet<string>(businessClasses, StringComparer.Ordinal);

        // Each updater on its own terms, one after the other: a seed method is recorded once per
        // updater, and two updaters with a method of the same name seed two different things.
        return FindUpdaterClasses(sourceDirectory)
            .SelectMany(classDecl => SeedDataOf(classDecl, options, known))
            .ToList();
    }

    /// <summary>
    /// The seed data one updater class creates.
    /// </summary>
    /// <remarks>
    /// The update methods come with the methods they call, in call order; every other method is
    /// read where it is declared. Each declaration is read once — by declaration, not by name, so
    /// two overloads of one helper are two seeds.
    /// </remarks>
    private static List<ExtractedSeedData> SeedDataOf(ClassDeclarationSyntax classDecl, ExtractionOptions options, HashSet<string>? known)
    {
        var seedData = new List<ExtractedSeedData>();
        var visited = new HashSet<MethodDeclarationSyntax>();
        var methods = classDecl.Members.OfType<MethodDeclarationSyntax>().ToList();

        void Visit(MethodDeclarationSyntax method)
        {
            if (visited.Add(method) && method.Body is not null)
                seedData.AddRange(SeedsOf(classDecl, method, options, known));
        }

        foreach (var method in methods)
        {
            Visit(method);

            if (!IsUpdateMethod(method.Identifier.Text))
                continue;

            foreach (var name in CalledNames(method))
            foreach (var target in methods.Where(m => m.Identifier.Text == name))
                Visit(target);
        }

        return seedData;
    }

    private static bool IsUpdateMethod(string name) =>
        name is "UpdateDatabaseAfterUpdateSchema" or "UpdateDatabaseBeforeUpdateSchema";

    /// <summary>The names of the methods an update method calls, in call order.</summary>
    private static IEnumerable<string> CalledNames(MethodDeclarationSyntax method) =>
        ((SyntaxNode?)method.Body ?? method.ExpressionBody)?.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                _ => null,
            })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
        ?? [];

    /// <summary>
    /// Finds the blocks that run only when an existing database is upgraded.
    /// </summary>
    /// <remarks>
    /// A version-gated block runs once, on somebody's production database, and then sits in the
    /// updater forever describing a decision nobody remembers. Reading the current code cannot
    /// recover it, which makes it some of the most valuable knowledge in an inherited application
    /// and the reason a column contains what it contains.
    /// </remarks>
    /// <param name="sourceDirectory">Project source root.</param>
    /// <param name="options">Extraction options controlling output detail.</param>
    public List<ExtractedMigration> AnalyzeMigrations(string sourceDirectory, ExtractionOptions options)
    {
        var migrations = new List<ExtractedMigration>();

        foreach (var classDecl in FindUpdaterClasses(sourceDirectory))
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (method.Body is null)
                continue;

            var phase = method.Identifier.Text switch
            {
                "UpdateDatabaseBeforeUpdateSchema" => MigrationPhase.BeforeSchemaUpdate,
                "UpdateDatabaseAfterUpdateSchema" => MigrationPhase.AfterSchemaUpdate,
                _ => MigrationPhase.Unknown,
            };

            foreach (var ifStatement in method.Body.DescendantNodes().OfType<IfStatementSyntax>())
            {
                var condition = ifStatement.Condition.ToString();

                // The gate is always a comparison against CurrentDBVersion. Anything else in an
                // updater is ordinary branching and says nothing about upgrades.
                if (!condition.Contains("CurrentDBVersion", StringComparison.Ordinal))
                    continue;

                var versions = ReadVersions(ifStatement.Condition);

                migrations.Add(new ExtractedMigration
                {
                    Condition = Collapse(condition),
                    Phase = phase,
                    // Highest is the version being upgraded to; the lower bound, when present, is
                    // the "not a brand new database" guard. MaxBy, not Max: Max returns the
                    // projected Version, and what is wanted is the string it came from.
                    TargetVersion = versions.Count > 0 ? versions.MaxBy(Compare) : null,
                    MinimumVersion = versions.Count > 1 ? versions.MinBy(Compare) : null,
                    Description = CommentAbove(ifStatement),
                    CallsMethods = CalledMethods(ifStatement),
                    Code = options.IncludeSourceCode ? ifStatement.Statement.ToString() : string.Empty,
                });
            }
        }

        return migrations;
    }

    /// <summary>Reads every <c>new Version("…")</c> out of a condition.</summary>
    private static List<string> ReadVersions(ExpressionSyntax condition) =>
        condition.DescendantNodesAndSelf()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(c => c.Type.ToString().EndsWith("Version", StringComparison.Ordinal))
            .Select(c => c.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .OfType<ExpressionSyntax>()
            .Select(SyntaxLiteral.ValueOf)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Orders version strings the way versions order, not the way strings do.
    /// </summary>
    /// <remarks>
    /// Sorted as text, "1.10.0.0" comes before "1.9.0.0" — which would report the wrong bound as
    /// soon as an application reaches its tenth minor release.
    /// </remarks>
    private static Version Compare(string value) =>
        Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    /// <summary>
    /// Reads the comment above a migration block.
    /// </summary>
    /// <remarks>
    /// The code says what the migration did. The comment is usually the only record of why, and
    /// why is the question anyone reading it actually has.
    /// </remarks>
    private static string? CommentAbove(SyntaxNode node)
    {
        var lines = node.GetLeadingTrivia()
            .ToFullString()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("//", StringComparison.Ordinal))
            .Select(line => line.TrimStart('/').Trim())
            .Where(line => line.Length > 0)
            .ToList();

        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>Names the methods a block calls, which is where the work usually lives.</summary>
    private static List<string> CalledMethods(SyntaxNode block) =>
        block.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                _ => null,
            })
            .OfType<string>()
            .Where(name => name is not ("CommitChanges" or "GetObjects" or "FirstOrDefault"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string Collapse(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        while (single.Contains("  ", StringComparison.Ordinal))
            single = single.Replace("  ", " ", StringComparison.Ordinal);

        return single;
    }

    /// <summary>One object a seed method creates, and which of its values came from a parameter.</summary>
    private sealed record Created(string EntityType, SeedRecord Record, Dictionary<string, string> FromParameter)
    {
        public Created(string entityType) : this(entityType, new SeedRecord(), new Dictionary<string, string>(StringComparer.Ordinal)) { }
    }

    /// <summary>What a caller passes for a parameter that is not a literal.</summary>
    private const string SetByTheCaller = "set by the caller";

    /// <summary>
    /// The seeds one method creates.
    /// </summary>
    /// <remarks>
    /// A named method is one seed, whatever it creates. An update method is read for what it creates
    /// itself — one seed per class, named after that class: a generated updater creates its roles
    /// inline, and five updaters each reporting "Update Database After Update Schema" said nothing.
    /// </remarks>
    private static List<ExtractedSeedData> SeedsOf(ClassDeclarationSyntax classDecl, MethodDeclarationSyntax method, ExtractionOptions options, HashSet<string>? known)
    {
        var created = WithCallerValues(classDecl, method, Creations(classDecl, method, known));
        if (created.Count == 0)
            return [];

        var name = method.Identifier.Text;
        var source = options.IncludeSourceCode ? method.Body!.ToString() : string.Empty;

        if (IsUpdateMethod(name))
        {
            return created
                .GroupBy(c => c.EntityType, StringComparer.Ordinal)
                .Select(group => new ExtractedSeedData
                {
                    EntityType = group.Key,
                    MethodName = name,
                    UpdaterClass = classDecl.Identifier.Text,
                    Description = InferDescriptionFromMethodName(group.Key),
                    RawSourceCode = source,
                    Records = group.Select(c => c.Record).ToList(),
                })
                .ToList();
        }

        return
        [
            new ExtractedSeedData
            {
                EntityType = created[0].EntityType,
                MethodName = name,
                UpdaterClass = classDecl.Identifier.Text,
                Description = InferDescriptionFromMethodName(name),
                RawSourceCode = source,
                Records = created.Select(c => c.Record).ToList(),
            },
        ];
    }

    /// <summary>
    /// The objects a method body creates, in source order: <c>new T(session)</c>,
    /// <c>ObjectSpace.CreateObject&lt;T&gt;()</c> and <c>userManager.CreateUser&lt;T&gt;(…)</c>.
    /// </summary>
    private static List<Created> Creations(ClassDeclarationSyntax classDecl, MethodDeclarationSyntax method, HashSet<string>? known)
    {
        var body = method.Body!;
        var parameters = method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var found = new List<(int Position, Created Object)>();

        // new Ward(session) { ... }
        foreach (var creation in body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!IsSeeded(creation, known))
                continue;

            var typeName = SimpleName(creation.Type);
            Created? initialized = null;

            if (creation.Initializer != null)
            {
                var record = new Created(typeName);
                foreach (var assignment in creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                    Assign(record, assignment.Left.ToString(), assignment.Right, parameters);

                if (record.Record.PropertyValues.Count > 0)
                    found.Add((creation.SpanStart, initialized = record));
            }

            // Also check for property assignments after creation: var x = new Type(); x.Prop = value;
            // Only when the creation is the value itself: a `new DateTime(…)` inside another creation's
            // initializer is a value of that object, not an object held by the enclosing variable.
            // Parentheses, casts, `??` and `?:` pass the value through: `Find(…) ?? new Ward(Session)`.
            SyntaxNode value = creation;
            while (value.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax
                   || value.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression }
                   || (value.Parent is ConditionalExpressionSyntax conditional && conditional.Condition != value))
                value = value.Parent;

            var variableName = value.Parent switch
            {
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => declarator.Identifier.Text,
                AssignmentExpressionSyntax assignExpr when assignExpr.Right == value => assignExpr.Left.ToString(),
                _ => null,
            };

            if (variableName == null)
                continue;

            // This creation's own record: the one its initializer started, else a new one. Taking
            // the last record made two sibling blocks' `var role = new Role(session)` one record.
            var own = initialized ?? new Created(typeName);
            AssignFollowing(own, creation, variableName, body, parameters);

            if (own != initialized && own.Record.PropertyValues.Count > 0)
                found.Add((creation.SpanStart, own));
        }

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var generic = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name as GenericNameSyntax,
                GenericNameSyntax plain => plain,
                _ => null,
            };

            var typeArgument = generic?.TypeArgumentList.Arguments.FirstOrDefault();
            if (typeArgument is null)
                continue;

            var record = new Created(SimpleName(typeArgument));

            // The modern style: ObjectSpace.CreateObject<Customer>(). The scan above only recognizes
            // `new Customer(session)`, and such an application was reported as having no seed data
            // at all. The result is either declared (var x = ...) or assigned to an existing local
            // (x = ...), and both forms appear in the same updater when a method checks for an
            // existing record before creating one.
            if (generic!.Identifier.Text == "CreateObject" && invocation.Expression is MemberAccessExpressionSyntax)
            {
                var variableName =
                    invocation.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.Text
                    ?? invocation.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault()?.Left.ToString();

                if (string.IsNullOrWhiteSpace(variableName))
                    continue;

                AssignFollowing(record, invocation, variableName, body, parameters);
            }

            // Users: userManager.CreateUser<ApplicationUser>(ObjectSpace, "Admin", "", user => ...),
            // the way the project template and every generator seeds them.
            else if (generic.Identifier.Text == "CreateUser" && UserNameArgument(invocation, classDecl) is { } userName)
            {
                Assign(record, "UserName", userName, parameters);
            }

            if (record.Record.PropertyValues.Count > 0)
                found.Add((invocation.SpanStart, record));
        }

        return found.OrderBy(f => f.Position).Select(f => f.Object).ToList();
    }

    /// <summary>Records <c>variable.Property = value</c> within the variable's scope.</summary>
    private static void AssignFollowing(Created record, SyntaxNode creation, string variableName, BlockSyntax body, HashSet<string> parameters)
    {
        foreach (var assignment in ScopeOf(creation, variableName, body).DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax member && member.Expression.ToString() == variableName)
                Assign(record, member.Name.ToString(), assignment.Right, parameters);
        }
    }

    /// <summary>Sets a value, remembering when it is the method's own parameter.</summary>
    private static void Assign(Created record, string property, ExpressionSyntax value, HashSet<string> parameters)
    {
        record.Record.PropertyValues[property] = SyntaxLiteral.ValueOf(value);

        if (value is IdentifierNameSyntax identifier && parameters.Contains(identifier.Identifier.Text))
            record.FromParameter[property] = identifier.Identifier.Text;
        else
            record.FromParameter.Remove(property);
    }

    /// <summary>
    /// Whether a <c>new T(…)</c> creates something the updater seeds.
    /// </summary>
    /// <remarks>
    /// Every <c>new</c> in a seed method used to count, so a role looked up with
    /// <c>FindObject&lt;PermissionPolicyRole&gt;(new BinaryOperator("Name", name))</c> made the seed a
    /// <c>BinaryOperator</c>. What counts is a business class of the module, one of XAF's security
    /// classes (never in the module's own list), or an XPO object built on a session — kept so a
    /// class the entity pass missed still reads.
    /// </remarks>
    private static bool IsSeeded(ObjectCreationExpressionSyntax creation, HashSet<string>? known)
    {
        if (known is null)
            return true;

        var name = SimpleName(creation.Type);
        if (known.Contains(name) || name.StartsWith("PermissionPolicy", StringComparison.Ordinal))
            return true;

        // ponytail: XPO's persistent constructor by argument name; a session held in a variable of another name is missed.
        return creation.ArgumentList?.Arguments is [var only]
               && only.Expression.ToString() is var text
               && (text.EndsWith("Session", StringComparison.OrdinalIgnoreCase)
                   || text.EndsWith("UnitOfWork", StringComparison.OrdinalIgnoreCase)
                   || text == "uow");
    }

    /// <summary>A type's own name: <c>global::App.Module.ApplicationUser</c> is <c>ApplicationUser</c>.</summary>
    private static string SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => SimpleName(qualified.Right),
        AliasQualifiedNameSyntax alias => SimpleName(alias.Name),
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => type.ToString(),
    };

    /// <summary>
    /// The user name a <c>UserManager.CreateUser&lt;T&gt;</c> call creates, or null for the overload
    /// that takes an <c>IPrincipal</c>.
    /// </summary>
    /// <remarks>
    /// The overloads are <c>(objectSpace, userName, password, customizeUser)</c>,
    /// <c>(objectSpace, userName, loginProviderName, providerUserKey, customizeUser, autoCommit)</c>
    /// and <c>(objectSpace, principal, customizeUser, autoCommit)</c>. Named arguments settle it; so
    /// does a third argument that is a string, since only the principal overload has a delegate or a
    /// flag there.
    /// </remarks>
    private static ExpressionSyntax? UserNameArgument(InvocationExpressionSyntax invocation, ClassDeclarationSyntax classDecl)
    {
        var arguments = invocation.ArgumentList.Arguments;

        ExpressionSyntax? Named(string name) =>
            arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == name)?.Expression;

        if (Named("principal") is not null)
            return null;

        if (Named("userName") is { } named)
            return named;

        // A positional argument sits in its own slot, also after a named one written in position:
        // `CreateUser<T>(objectSpace: os, "Admin", "", …)`.
        var positional = arguments
            .Select((argument, slot) => (argument, slot))
            .Where(a => a.argument.NameColon is null)
            .ToDictionary(a => a.slot, a => a.argument.Expression);

        if (!positional.TryGetValue(1, out var second))
            return null;

        var byUserName = Named("password") is not null || Named("loginProviderName") is not null || Named("providerUserKey") is not null
                         || (positional.TryGetValue(2, out var third) && !IsCustomization(third, classDecl));

        return byUserName ? second : null;
    }

    /// <summary>A delegate or a flag: what the principal overload takes after the principal.</summary>
    private static bool IsCustomization(ExpressionSyntax expression, ClassDeclarationSyntax classDecl) => expression switch
    {
        AnonymousFunctionExpressionSyntax => true,
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NullLiteralExpression)
                                           || literal.IsKind(SyntaxKind.TrueLiteralExpression)
                                           || literal.IsKind(SyntaxKind.FalseLiteralExpression),
        IdentifierNameSyntax method => IsMethodOf(method, classDecl),
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax method } => IsMethodOf(method, classDecl),
        _ => false,
    };

    private static bool IsMethodOf(IdentifierNameSyntax name, ClassDeclarationSyntax classDecl) =>
        classDecl.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == name.Identifier.Text);

    /// <summary>
    /// Fills in the values a helper receives from its callers: one record per call.
    /// </summary>
    /// <remarks>
    /// <c>SeedRole("Administrators", …)</c> fourteen times over one helper that does
    /// <c>role.Name = name</c> read as one role called <c>name</c>. Each call in the updater is
    /// bound to the helper's declaration the way the compiler would — by position, name and default
    /// — and a literal argument becomes the value. Anything else, a call that fits more than one
    /// overload, or a helper nobody in the class calls: "set by the caller". A record whose values
    /// are all its own is created on every call too, but is listed once.
    /// </remarks>
    private static List<Created> WithCallerValues(ClassDeclarationSyntax classDecl, MethodDeclarationSyntax method, List<Created> created)
    {
        if (created.All(c => c.FromParameter.Count == 0))
            return created;

        var name = method.Identifier.Text;
        var overloads = classDecl.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == name).ToList();

        var calls = classDecl.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text == name,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax member } => member.Identifier.Text == name,
                _ => false,
            })
            .Select(call => (Call: call, Arguments: Bind(call.ArgumentList, method.ParameterList)))
            .Where(bound => bound.Arguments is not null)
            .Select(bound => overloads.Count(o => Bind(bound.Call.ArgumentList, o.ParameterList) is not null) == 1 ? bound.Arguments : null)
            .ToList();

        var result = new List<Created>();

        foreach (var record in created)
        {
            if (record.FromParameter.Count == 0)
                result.Add(record);
            else if (calls.Count == 0)
                result.Add(Substituted(record, null));
            else
                result.AddRange(calls.Select(arguments => Substituted(record, arguments)));
        }

        return result;
    }

    private static Created Substituted(Created record, Dictionary<string, ExpressionSyntax>? arguments)
    {
        var copy = new Created(record.EntityType);

        foreach (var (property, value) in record.Record.PropertyValues)
        {
            copy.Record.PropertyValues[property] = !record.FromParameter.TryGetValue(property, out var parameter)
                ? value
                : arguments?.GetValueOrDefault(parameter) is { } argument && IsLiteral(argument)
                    ? SyntaxLiteral.ValueOf(argument)
                    : SetByTheCaller;
        }

        return copy;
    }

    private static bool IsLiteral(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } => true,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) => IsLiteral(binary.Left) && IsLiteral(binary.Right),
        _ => false,
    };

    /// <summary>
    /// Binds a call's arguments to a declaration's parameters, or null when the call cannot be to it.
    /// </summary>
    private static Dictionary<string, ExpressionSyntax>? Bind(ArgumentListSyntax arguments, ParameterListSyntax declaration)
    {
        var parameters = declaration.Parameters;
        var isParams = parameters.Count > 0 && parameters[^1].Modifiers.Any(SyntaxKind.ParamsKeyword);
        var bound = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);

        for (var i = 0; i < arguments.Arguments.Count; i++)
        {
            var argument = arguments.Arguments[i];
            var name = argument.NameColon?.Name.Identifier.Text
                       ?? (i < parameters.Count ? parameters[i].Identifier.Text : null);

            // Extra positional arguments go into a trailing params array; anywhere else they don't fit.
            if (name is null)
            {
                if (isParams)
                    continue;
                return null;
            }

            if (!parameters.Any(p => p.Identifier.Text == name) || !bound.TryAdd(name, argument.Expression))
                return null;
        }

        foreach (var parameter in parameters)
        {
            if (bound.ContainsKey(parameter.Identifier.Text))
                continue;

            if (parameter.Default is { } defaultValue)
                bound[parameter.Identifier.Text] = defaultValue.Value;
            else if (parameter != parameters[^1] || !isParams)
                return null;
        }

        return bound;
    }

    /// <summary>
    /// The block the variable holding a created object is declared in — where its assignments are.
    /// </summary>
    /// <remarks>
    /// Two blocks that each declare <c>var role</c> hold two variables of one name. Collecting
    /// <c>role.Name = …</c> across the whole method gave both records every assignment and the last
    /// one won, so an updater seeding a Guest and an Admin role was read as two Admins. A variable
    /// assigned rather than declared at the creation — <c>role = ObjectSpace.CreateObject…</c> after
    /// a lookup — is scoped where it was declared, usually the method; and anything else, the method.
    /// </remarks>
    private static BlockSyntax ScopeOf(SyntaxNode creation, string variableName, BlockSyntax body)
    {
        foreach (var block in creation.Ancestors().OfType<BlockSyntax>())
        {
            if (block.Statements.OfType<LocalDeclarationStatementSyntax>()
                .Any(statement => statement.Declaration.Variables.Any(v => v.Identifier.Text == variableName)))
                return block;

            if (block == body)
                break;
        }

        return body;
    }

    /// <summary>
    /// Converts a method identifier into a readable seed-data description.
    /// </summary>
    private static string InferDescriptionFromMethodName(string methodName)
    {
        // Convert "CrearTiposEmpleado" -> "Crear Tipos de Empleado"
        var result = System.Text.RegularExpressions.Regex.Replace(methodName, "([A-Z])", " $1").Trim();
        return $"Seed data: {result}";
    }

    /// <summary>
    /// Every updater class in the module, the template's first.
    /// </summary>
    /// <remarks>
    /// <c>GetModuleUpdaters</c> returns as many updaters as a module likes, and applications split
    /// them by concern: one for roles, one for reference data, one per generated feature. Searching
    /// for one — the template's <c>DatabaseUpdate/Updater.cs</c>, else the first class deriving from
    /// <c>ModuleUpdater</c> — reported the roles an application ships in an updater of their own as
    /// seed data it does not have.
    /// <para>
    /// An updater is a class named <c>Updater</c> or one whose base list names <c>ModuleUpdater</c>,
    /// as before. Files are read rather than trusted by name, bounded by the project directory and
    /// what its project file compiles. The template's file comes first and the rest follow by path,
    /// classes in declaration order, so an application with one updater reads exactly as it did.
    /// </para>
    /// <para>
    /// Not reached: a part of a <c>partial</c> updater that does not repeat the base list, an updater
    /// deriving from a base of the application's own, and an updater declared in a referenced project.
    /// </para>
    /// </remarks>
    private static List<ClassDeclarationSyntax> FindUpdaterClasses(string sourceDirectory)
    {
        var template = new[]
        {
            Path.Combine(sourceDirectory, "DatabaseUpdate", "Updater.cs"),
            Path.Combine(sourceDirectory, "Updater.cs"),
        };

        var removed = CompileExclusions.For(sourceDirectory);

        var files = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => BuildOutputFilter.IsAnalyzable(file, sourceDirectory) && !removed.Excludes(file))
            .OrderBy(file => Array.FindIndex(template, candidate => candidate.Equals(file, StringComparison.OrdinalIgnoreCase)) switch
            {
                -1 => template.Length,
                var index => index,
            })
            .ThenBy(file => file, StringComparer.Ordinal);

        var classes = new List<ClassDeclarationSyntax>();

        foreach (var file in files)
        {
            try
            {
                var source = File.ReadAllText(file);

                // Cheap gate before parsing: a file without the word cannot declare the class.
                if (!source.Contains("Updater", StringComparison.Ordinal))
                    continue;

                // Parsed for a class that is an updater, not searched as text: every module declares
                // `IEnumerable<ModuleUpdater> GetModuleUpdaters(...)` and has no updater in it.
                classes.AddRange(CSharpSyntaxTree.ParseText(source, path: file)
                    .GetRoot()
                    .DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(c => c.Identifier.Text == "Updater"
                                || c.BaseList?.Types.Any(t =>
                                    t.Type.ToString().Contains("ModuleUpdater", StringComparison.Ordinal)) == true));
            }
            catch (IOException)
            {
                // An unreadable file is not a reason to abandon the search.
            }
        }

        return classes;
    }
}

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
    /// <returns>Extracted seed-data descriptors.</returns>
    public List<ExtractedSeedData> AnalyzeUpdater(string sourceDirectory, ExtractionOptions options)
    {
        // Each updater on its own terms, one after the other: a seed method is recorded once per
        // updater, and two updaters with a method of the same name seed two different things.
        return FindUpdaterClasses(sourceDirectory)
            .SelectMany(classDecl => SeedDataOf(classDecl, options))
            .ToList();
    }

    /// <summary>
    /// The seed data one updater class creates.
    /// </summary>
    private static List<ExtractedSeedData> SeedDataOf(ClassDeclarationSyntax classDecl, ExtractionOptions options)
    {
        var seedData = new List<ExtractedSeedData>();

        // Find all methods that create seed data
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodName = method.Identifier.Text;

            // Skip standard XAF methods that aren't seed data
            if (methodName is "UpdateDatabaseAfterUpdateSchema" or "UpdateDatabaseBeforeUpdateSchema")
            {
                // But analyze their body for method calls to seed methods
                if (method.Body != null)
                {
                    AnalyzeUpdateMethod(classDecl, method.Body, seedData, options);
                }
                continue;
            }

            // Analyze methods that create objects
            if (method.Body != null && HasObjectCreation(method.Body))
            {
                var seed = ExtractSeedFromMethod(method, options);
                if (seed != null && seed.Records.Count > 0)
                    AddSeed(seedData, seed);
            }
        }

        return seedData;
    }

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

    /// <summary>
    /// Traverses update entry points to find delegated seed methods.
    /// </summary>
    private static void AnalyzeUpdateMethod(ClassDeclarationSyntax classDecl, BlockSyntax body, List<ExtractedSeedData> seedData, ExtractionOptions options)
    {
        // Find method invocations within UpdateDatabaseAfterUpdateSchema
        var invocations = body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString())
            .Distinct();

        foreach (var invocation in invocations)
        {
            var methodName = invocation.Contains('.') ? invocation.Split('.').Last() : invocation;

            var targetMethod = classDecl.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);

            if (targetMethod?.Body != null && HasObjectCreation(targetMethod.Body))
            {
                var seed = ExtractSeedFromMethod(targetMethod, options);
                if (seed != null && seed.Records.Count > 0)
                    AddSeed(seedData, seed);
            }
        }
    }

    /// <summary>
    /// Adds a seed method unless it has already been recorded.
    /// </summary>
    /// <remarks>
    /// A seed method is reached twice: once by following the calls out of
    /// <c>UpdateDatabaseAfterUpdateSchema</c>, and once by the sweep over every method in the
    /// class. Without this guard each one is reported twice, so documentation claims an
    /// application seeds twice as many things as it does — and the duplicate is a perfect copy,
    /// which makes it read like two genuinely separate operations.
    /// </remarks>
    private static void AddSeed(List<ExtractedSeedData> seedData, ExtractedSeedData seed)
    {
        if (seedData.Any(existing => existing.MethodName == seed.MethodName))
            return;

        seedData.Add(seed);
    }

    /// <summary>
    /// Extracts one seed-data block from a method body.
    /// </summary>
    private static ExtractedSeedData? ExtractSeedFromMethod(MethodDeclarationSyntax method, ExtractionOptions options)
    {
        if (method.Body == null) return null;

        var seed = new ExtractedSeedData
        {
            MethodName = method.Identifier.Text,
            Description = InferDescriptionFromMethodName(method.Identifier.Text),
        };

        if (options.IncludeSourceCode)
            seed.RawSourceCode = method.Body.ToString();

        // Find object creation expressions (new TipoEmpleado(session) { ... })
        var objectCreations = method.Body.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>();

        foreach (var creation in objectCreations)
        {
            var typeName = creation.Type.ToString();

            // Skip infrastructure types
            if (typeName.Contains("PermissionPolicy") || typeName.Contains("ApplicationUser"))
            {
                seed.EntityType = typeName;
                continue;
            }

            seed.EntityType = typeName;
            SeedRecord? initialized = null;

            if (creation.Initializer != null)
            {
                var record = new SeedRecord();
                foreach (var expression in creation.Initializer.Expressions)
                {
                    if (expression is AssignmentExpressionSyntax assignment)
                    {
                        var propName = assignment.Left.ToString();
                        var propValue = SyntaxLiteral.ValueOf(assignment.Right);
                        record.PropertyValues[propName] = propValue;
                    }
                }
                if (record.PropertyValues.Count > 0)
                    seed.Records.Add(initialized = record);
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

            if (variableName != null)
            {
                // This creation's own record: the one its initializer started, else a new one. Taking
                // the last record made two sibling blocks' `var role = new Role(session)` one record.
                var record = initialized ?? new SeedRecord();

                // Find subsequent property assignments
                var block = ScopeOf(creation, variableName, method.Body);
                var assignments = block.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.Left is MemberAccessExpressionSyntax mae
                                && mae.Expression.ToString() == variableName);

                foreach (var assignment in assignments)
                {
                    if (assignment.Left is MemberAccessExpressionSyntax mae)
                    {
                        record.PropertyValues[mae.Name.ToString()] = SyntaxLiteral.ValueOf(assignment.Right);
                    }
                }

                if (record != initialized && record.PropertyValues.Count > 0)
                    seed.Records.Add(record);
            }
        }

        ExtractObjectSpaceCreations(method.Body, seed);

        return seed;
    }

    /// <summary>
    /// Reads seed records created through <c>ObjectSpace.CreateObject&lt;T&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// The scan above only recognizes <c>new Customer(session)</c>. That is the older
    /// Session-based style; a modern updater works against <c>IObjectSpace</c> and writes
    /// <c>ObjectSpace.CreateObject&lt;Customer&gt;()</c>, which is an invocation rather than an
    /// object creation and so was invisible. Such an application was reported as having no seed
    /// data at all — the tool describing the absence of something plainly present in the source.
    /// </remarks>
    private static void ExtractObjectSpaceCreations(BlockSyntax body, ExtractedSeedData seed)
    {
        var creations = body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.Text: "CreateObject" }
            });

        foreach (var creation in creations)
        {
            var generic = (GenericNameSyntax)((MemberAccessExpressionSyntax)creation.Expression).Name;
            var typeName = generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();

            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            seed.EntityType = typeName;

            // The result is either declared (var x = ...) or assigned to an existing local
            // (x = ...), and both forms appear in the same updater when a method checks for an
            // existing record before creating one.
            var variableName =
                creation.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.Text
                ?? (creation.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault()?.Left.ToString());

            if (string.IsNullOrWhiteSpace(variableName))
                continue;

            var record = new SeedRecord();

            foreach (var assignment in ScopeOf(creation, variableName, body).DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is MemberAccessExpressionSyntax member
                    && member.Expression.ToString() == variableName)
                {
                    record.PropertyValues[member.Name.ToString()] = SyntaxLiteral.ValueOf(assignment.Right);
                }
            }

            if (record.PropertyValues.Count > 0)
                seed.Records.Add(record);
        }
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
    /// Checks whether a method body creates persistent objects, in either supported style.
    /// </summary>
    private static bool HasObjectCreation(BlockSyntax body)
    {
        if (body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any())
            return true;

        return body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.Text: "CreateObject" }
            });
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

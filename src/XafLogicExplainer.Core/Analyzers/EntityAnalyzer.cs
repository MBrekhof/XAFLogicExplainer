using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XafLogicExplainer.Core.Interfaces;
using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Core.Analyzers;

/// <summary>
/// Parses C# business object classes and extracts entity metadata, rules, and relationships.
/// </summary>
public class EntityAnalyzer : IEntityAnalyzer
{
    /// <summary>
    /// Analyzes entity candidates and returns extracted business entities.
    /// </summary>
    /// <param name="sourceDirectory">Project root directory.</param>
    /// <param name="options">Extraction configuration.</param>
    /// <returns>Entity extraction results.</returns>
    public List<ExtractedEntity> AnalyzeEntities(string sourceDirectory, ExtractionOptions options)
    {
        var entities = new List<ExtractedEntity>();
        var ownFiles = FindFiles(sourceDirectory, options.BusinessObjectPatterns, options.ExcludePatterns).ToList();

        // Borrowed, not owned. A base class declared in a referenced project has to be readable or
        // every class deriving from it is dropped -- and the module reports that it persists
        // nothing, which is a claim rather than a gap. These files are parsed alongside the
        // project own, so the base resolves and its properties fold down, and the entities they
        // declare are removed again before the result is returned: they belong to the project that
        // declares them.
        var borrowedFiles = ReferencedSourceFiles(sourceDirectory, options, ownFiles);
        var borrowed = borrowedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var csFiles = ownFiles.Concat(borrowedFiles).ToList();

        // Parsed once and kept, because the DbSet roster has to be known before the first class is
        // classified and re-parsing every file to build it costs more than holding the trees.
        //
        // Ordered, because the directory hands them over in whatever order the file system keeps
        // them, and that is not the same order on two machines: NTFS compares names without case,
        // ext4 by byte, so `Shipment.Generated.cs` sorts after `Shipment.cs` on one and before it
        // on the other. Anything downstream that takes the first of something then answers
        // differently on a laptop than in CI, in a document whose value is that it can be
        // regenerated and compared.
        var parsedFiles = csFiles
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => (File: file, Root: CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot()))
            .ToList();

        // Two rosters, because they answer different questions. Acceptance may read a context in
        // a referenced project: a shared `DbContext` registering a class declared here is a real
        // layout. Detection may not -- an EF Core utility beside an XPO application is also a real
        // layout, and it must not make the application EF Core.
        var roster = DbSetRoster.Read(parsedFiles.Select(parsed => parsed.Root));

        var ownRoots = parsedFiles
            .Where(parsed => !borrowed.Contains(parsed.File))
            .Select(parsed => parsed.Root)
            .ToList();

        // Resolved after the parse, because the roster is the best evidence there is and it only
        // exists once the trees do.
        var ormType = options.Orm == OrmType.Auto
            ? DetectOrm(ownRoots, parsedFiles.Select(parsed => parsed.Root).ToList(), roster)
            : options.Orm;
        options.ResolvedOrm = ormType;

        var (persistent, parents) = SelectPersistentClasses(parsedFiles.Select(parsed => parsed.Root), roster, options);

        foreach (var (file, root) in parsedFiles)
        {
            var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            foreach (var classDecl in classDeclarations)
            {
                if (persistent.Contains((GetNamespace(classDecl), classDecl.Identifier.Text)))
                {
                    var entity = ExtractEntity(classDecl, file, options);
                    entities.Add(entity);
                }
            }
        }

        // One entity per class, not per declaration -- a partial split across files is one thing.
        entities = MergePartialDeclarations(entities, borrowed, parents, options);

        foreach (var entity in entities)
        {
            var explicitlyImplemented = entity.Lifecycle.Where(hook => hook.IsExplicitImplementation)
                .Select(hook => hook.MethodName).ToHashSet(StringComparer.Ordinal);

            // A class that names the interface again maps it to its own public method, `new virtual` or not.
            if (entity.BaseTypes.Contains("IXafEntityObject"))
            {
                foreach (var hook in entity.Lifecycle.Where(hook => hook.IsNewSlot && hook.IsPublic && hook.MethodName != "AfterConstruction"
                                                                     && !explicitlyImplemented.Contains(hook.MethodName)))
                    hook.IsNewSlot = false;
            }

            // An abstract method runs nothing itself; the overrides below it are the hooks.
            entity.Lifecycle.RemoveAll(hook => !IsLifecycleHook(hook, entity, explicitlyImplemented) || (!hook.IsNewSlot && !hook.HasBody));
        }

        // Post-extraction: infer EF Core relationships from navigation properties
        if (ormType == OrmType.EfCore)
            InferEfCoreRelationships(entities);

        // After the merge, so a parent's property set is complete before it is folded into anyone;
        // after relationship inference, so an inherited navigation property is not inferred a
        // second time under the descendant that received a copy of it — the fold carries the
        // parent's relationship down itself, marked with the class that declared it.
        FoldInheritance(entities, parents, borrowed);

        // After the fold, so a hook on a base is checked against the properties the base declares
        // and a descendant against everything it inherits.
        foreach (var entity in entities)
        {
            var properties = entity.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

            entity.Lifecycle.RemoveAll(hook => hook.IsNewSlot);

            foreach (var hook in entity.Lifecycle)
                hook.AssignedProperties.RemoveAll(name => !properties.Contains(name));
        }

        // After the fold, which is the whole reason they were read: `Cliente` keeps the
        // `CreatedOn` it inherits from a base in the shared project, and the shared project own
        // classes stop being reported as this application business objects.
        if (borrowed.Count > 0)
            entities.RemoveAll(entity => borrowed.Contains(entity.FilePath));

        return entities;
    }

    /// <summary>
    /// The source files of the projects this one references, minus anything already being read.
    /// </summary>
    /// <remarks>
    /// A referenced project with no <c>BusinessObjects</c> folder is read in full, because
    /// <see cref="FindFiles"/> falls back to the whole directory -- which is the right answer for
    /// a shared library that keeps its primitives at the root, and the reason this is a switch.
    /// </remarks>
    private static List<string> ReferencedSourceFiles(
        string sourceDirectory,
        ExtractionOptions options,
        List<string> ownFiles)
    {
        if (!options.FollowProjectReferences)
            return [];

        // Seeded with what is already being read, so a project referenced twice down two paths --
        // and a reference that points back at this directory -- contributes its files once.
        var seen = ownFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        foreach (var directory in ProjectFile.ReferencedDirectories(sourceDirectory))
        {
            foreach (var file in FindFiles(directory, options.BusinessObjectPatterns, options.ExcludePatterns))
            {
                if (seen.Add(file))
                    files.Add(file);
            }
        }

        return files;
    }

    /// <summary>
    /// Extracts one business entity from a class declaration.
    /// </summary>
    private static ExtractedEntity ExtractEntity(ClassDeclarationSyntax classDecl, string filePath, ExtractionOptions options)
    {
        var entity = new ExtractedEntity
        {
            ClassName = classDecl.Identifier.Text,
            Namespace = GetNamespace(classDecl),
            FilePath = filePath,
            Line = SourceLine.Of(classDecl.Identifier),
            BaseType = GetBaseTypeName(classDecl),
            BaseTypes = GetBaseTypeNames(classDecl),
            Description = GetAttributeStringArg(classDecl, "Description"),
            NavigationGroup = GetAttributeStringArg(classDecl, "NavigationItem"),
            DefaultProperty = GetAttributeStringArg(classDecl, "XafDefaultProperty")
                              ?? GetAttributeStringArg(classDecl, "DefaultProperty"),
            IsDefaultClassOptions = HasAttribute(classDecl, "DefaultClassOptions"),
            IsPersistent = !HasAttribute(classDecl, "NonPersistent")
                           && !HasAttribute(classDecl, "DomainComponent")
                           && !HasAttribute(classDecl, "NotMapped")
                           && !GetBaseTypeNames(classDecl).Any(NonPersistentBaseTypeNames.Contains),
            IsAbstract = classDecl.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword)),
        };

        // Extract properties
        foreach (var prop in classDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (IsInfrastructureProperty(prop.Identifier.Text))
                continue;

            // `object ISecurityUserLoginInfo.User => User;` is the same property seen through an
            // interface, not a second one. Listing both puts a duplicate row in every rendering,
            // with the explicit form typed as `object` — which reads as a modelling mistake the
            // team did not make.
            if (prop.ExplicitInterfaceSpecifier is not null)
                continue;

            var extracted = ExtractProperty(prop);
            if (extracted != null)
                entity.Properties.Add(extracted);
        }

        // Extract relationships from properties (Association attribute)
        foreach (var prop in classDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            var rel = ExtractRelationship(prop);
            if (rel != null)
                entity.Relationships.Add(rel);
        }

        // Extract validation rules from class-level attributes
        entity.ValidationRules.AddRange(ExtractValidationRules(classDecl));

        // Extract appearance rules from class-level attributes
        entity.AppearanceRules.AddRange(ExtractAppearanceRules(classDecl));

        // The methods the Object Space calls on create, load and save. Which of them are hooks is
        // settled once the parts of a partial class are merged, because the interface that makes a
        // public OnSaving one may be declared on another part.
        entity.Lifecycle.AddRange(ExtractLifecycleHookCandidates(classDecl, filePath));

        // Extract comments
        if (options.IncludeComments)
        {
            entity.SourceComments.AddRange(ExtractComments(classDecl));
        }

        return entity;
    }

    /// <summary>
    /// Extracts one property definition from syntax metadata.
    /// </summary>
    private static ExtractedProperty? ExtractProperty(PropertyDeclarationSyntax prop)
    {
        var name = prop.Identifier.Text;
        var typeName = prop.Type.ToString();

        var extracted = new ExtractedProperty
        {
            Name = name,
            TypeName = typeName,
            Description = GetAttributeStringArg(prop, "Description"),
            DisplayName = GetAttributeStringArg(prop, "XafDisplayName"),
            ToolTip = GetAttributeStringArg(prop, "ToolTip"),
            EditorAlias = GetAttributeStringArg(prop, "EditorAlias"),
            PersistentAlias = GetAttributeStringArg(prop, "PersistentAlias"),
            DataSourceCriteria = GetAttributeStringArg(prop, "DataSourceCriteria"),
            IsCollection = IsCollectionType(typeName),
            IsComputed = HasAttribute(prop, "PersistentAlias")
                         || HasAttribute(prop, "NotMapped")
                         || IsGetterOnly(prop),
            IsRequired = HasAttribute(prop, "RuleRequiredField")
                         || HasAttribute(prop, "Required"),
            ImmediatePostData = HasAttribute(prop, "ImmediatePostData"),
            IsKey = HasAttribute(prop, "Key"),
            // [Indexed] alone is a performance hint; [Indexed(Unique = true)] is a rule the
            // database enforces, and the two are the same attribute.
            IsUnique = GetAttributeStringArg(prop, "Indexed", "Unique") is { } unique
                       && unique.Equals("true", StringComparison.OrdinalIgnoreCase),
        };

        // Size attribute (XPO)
        var sizeValue = GetAttributeStringArg(prop, "Size");
        if (int.TryParse(sizeValue, out int size))
            extracted.Size = size;

        // StringLength / MaxLength attributes (EF Core)
        if (!extracted.Size.HasValue)
        {
            var stringLengthValue = GetAttributeStringArg(prop, "StringLength")
                                    ?? GetAttributeStringArg(prop, "MaxLength");
            if (int.TryParse(stringLengthValue, out int stringLength))
                extracted.Size = stringLength;
        }

        // VisibleInListView / VisibleInDetailView
        var listVisible = GetAttributeStringArg(prop, "VisibleInListView");
        if (listVisible != null)
            extracted.VisibleInListView = listVisible != "false" && listVisible != "False";

        var detailVisible = GetAttributeStringArg(prop, "VisibleInDetailView");
        if (detailVisible != null)
            extracted.VisibleInDetailView = detailVisible != "false" && detailVisible != "False";

        // DisplayFormat from ModelDefault
        extracted.DisplayFormat = GetModelDefaultValue(prop, "DisplayFormat");

        // Default value from ModelDefault or initializer
        extracted.DefaultValue = GetModelDefaultValue(prop, "DefaultValue")
                                 ?? GetPropertyInitializer(prop);

        // Collect custom attributes for completeness
        foreach (var attrList in prop.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (!IsCommonAttribute(attrName))
                {
                    extracted.CustomAttributes.Add(attr.ToString());
                }
            }
        }

        // Validation rules on properties
        foreach (var attrList in prop.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (attrName.StartsWith("Rule"))
                {
                    extracted.IsRequired = extracted.IsRequired || attrName == "RuleRequiredField";
                }
            }
        }

        return extracted;
    }

    /// <summary>
    /// Extracts association metadata from a property when relationship attributes are present.
    /// </summary>
    private static ExtractedRelationship? ExtractRelationship(PropertyDeclarationSyntax prop)
    {
        var associationName = GetAttributeStringArg(prop, "Association");
        if (associationName == null) return null;

        var typeName = prop.Type.ToString();
        var isCollection = IsCollectionType(typeName);
        var isAggregated = HasAttribute(prop, "Aggregated");

        string relatedEntity;
        RelationshipType relType;

        if (isCollection)
        {
            // Extract generic type argument: XPCollection<Factura> -> Factura
            relatedEntity = ExtractGenericArgument(typeName);
            relType = RelationshipType.OneToMany;
        }
        else
        {
            relatedEntity = ReferencedClassName(typeName);
            relType = RelationshipType.ManyToOne;
        }

        return new ExtractedRelationship
        {
            PropertyName = prop.Identifier.Text,
            RelatedEntity = relatedEntity,
            AssociationName = associationName,
            Type = relType,
            IsAggregated = isAggregated
        };
    }

    /// <summary>
    /// Post-extraction pass: infer relationships from EF Core navigation properties
    /// that don't have an explicit [Association] attribute.
    /// </summary>
    private static void InferEfCoreRelationships(List<ExtractedEntity> entities)
    {
        var entityNames = new HashSet<string>(entities.Select(e => e.ClassName));

        foreach (var entity in entities)
        {
            foreach (var prop in entity.Properties)
            {
                // Skip if already has a relationship for this property
                if (entity.Relationships.Any(r => r.PropertyName == prop.Name))
                    continue;

                if (prop.IsCollection)
                {
                    // Collection nav property -> OneToMany
                    var genericArg = ExtractGenericArgument(prop.TypeName);
                    if (entityNames.Contains(genericArg))
                    {
                        entity.Relationships.Add(new ExtractedRelationship
                        {
                            PropertyName = prop.Name,
                            RelatedEntity = genericArg,
                            Type = RelationshipType.OneToMany,
                            IsAggregated = prop.CustomAttributes.Any(a => a.Contains("Aggregated"))
                        });
                    }
                }
                else if (entityNames.Contains(ReferencedClassName(prop.TypeName)))
                {
                    // Reference nav property -> ManyToOne
                    entity.Relationships.Add(new ExtractedRelationship
                    {
                        PropertyName = prop.Name,
                        RelatedEntity = ReferencedClassName(prop.TypeName),
                        Type = RelationshipType.ManyToOne,
                        IsAggregated = false
                    });
                }
            }
        }
    }

    /// <summary>
    /// Extracts class-level and property-level validation rules.
    /// </summary>
    private static List<ExtractedValidationRule> ExtractValidationRules(ClassDeclarationSyntax classDecl)
    {
        var rules = new List<ExtractedValidationRule>();

        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (!name.StartsWith("Rule")) continue;

                var rule = new ExtractedValidationRule
                {
                    RuleType = name,
                };

                ApplyRuleArguments(rule, attr);
                rules.Add(rule);
            }
        }

        // Also check property-level validation rules
        foreach (var prop in classDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            foreach (var attrList in prop.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var name = attr.Name.ToString();
                    if (!name.StartsWith("Rule")) continue;

                    var rule = new ExtractedValidationRule
                    {
                        RuleType = name,
                        TargetProperty = prop.Identifier.Text,
                    };

                    ApplyRuleArguments(rule, attr);
                    rules.Add(rule);
                }
            }
        }

        return rules;
    }

    /// <summary>
    /// Reads a validation attribute's arguments into a rule.
    /// </summary>
    /// <remarks>
    /// Shared by the class-level and property-level paths, which had drifted apart: the
    /// property-level one recorded arguments into <see cref="ExtractedValidationRule.Parameters"/>
    /// but never set <see cref="ExtractedValidationRule.MessageTemplate"/>. Property-level rules
    /// are the ordinary way to write XAF validation, and the message is the most useful part of a
    /// rule — it is what the user is told when the rule fires — so it was missing from exactly the
    /// rules people write most.
    /// <para>
    /// Positional arguments are recorded as <c>arg0</c>, <c>arg1</c>, … rather than under one
    /// shared key, which previously let each overwrite the last.
    /// </para>
    /// </remarks>
    private static void ApplyRuleArguments(ExtractedValidationRule rule, AttributeSyntax attr)
    {
        if (attr.ArgumentList is null)
            return;

        var args = attr.ArgumentList.Arguments;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            var argName = arg.NameEquals?.Name.ToString() ?? $"arg{index}";
            var argValue = SyntaxLiteral.ValueOf(arg.Expression);

            rule.Parameters[argName] = argValue;

            if (argName.Contains("Message", StringComparison.OrdinalIgnoreCase))
                rule.MessageTemplate = argValue;
            else if (argName.Equals("TargetCriteria", StringComparison.OrdinalIgnoreCase))
                rule.TargetCriteria = argValue;
            else if (argName.Equals("Criteria", StringComparison.OrdinalIgnoreCase))
                rule.Expression = argValue;
            else if (argName.Equals("TargetPropertyName", StringComparison.OrdinalIgnoreCase))
                rule.TargetProperty = argValue;
            else if (argName.Equals("Id", StringComparison.OrdinalIgnoreCase))
                rule.Id = argValue;
            else if (argName.Equals("TargetContextIDs", StringComparison.OrdinalIgnoreCase))
                rule.Contexts = argValue;
        }

        // After the named ones, which win: every assignment below is a fallback.
        ApplyPositionalArguments(rule, args);
    }

    /// <summary>
    /// Reads the arguments a rule attribute was given by position into the fields they name.
    /// </summary>
    /// <remarks>
    /// Every <c>Rule*</c> overload that takes an identifier takes it first and the validation
    /// contexts second, so those two slots can be read without knowing which attribute this is.
    /// What follows them belongs to the rule itself.
    /// <para>
    /// Taking the last positional literal as the criteria — which is what this did — is right for
    /// <c>("id", DefaultContexts.Save, "Total &gt;= 0")</c> and wrong for
    /// <c>("id", DefaultContexts.Save, "Total &gt;= 0", "A sale total cannot be negative.")</c>:
    /// the trailing literal there is the message shown to the user, so the field holding what the
    /// rule enforces held the sentence explaining it instead, and the message field stayed empty.
    /// Every fixture passed its message as <c>CustomMessageTemplate =</c>, so the whole suite
    /// agreed with the wrong answer.
    /// </para>
    /// </remarks>
    private static void ApplyPositionalArguments(
        ExtractedValidationRule rule, SeparatedSyntaxList<AttributeArgumentSyntax> args)
    {
        var positional = args.Where(arg => arg.NameEquals is null).ToList();

        // One argument leaves no room for an identifier before it, so it is the rule's own.
        if (positional.Count < 2)
        {
            if (positional.Count == 1 && IsCriteriaRule(rule))
                rule.Expression ??= StringLiteral(positional[0]);

            return;
        }

        rule.Id ??= StringLiteral(positional[0]);

        // Slot 1 is the contexts, written either as the enum or as a context name. Recorded as
        // written: `DefaultContexts.Save` is not a string in the source and resolving it would
        // mean compiling, which extraction deliberately never does.
        rule.Contexts ??= SyntaxLiteral.ValueOf(positional[1].Expression);

        var rest = positional.Skip(2).Select(StringLiteral).OfType<string>().ToList();

        if (IsCriteriaRule(rule))
        {
            if (rest.Count > 0) rule.Expression ??= rest[0];
            if (rest.Count > 1) rule.MessageTemplate ??= rest[1];
            return;
        }

        // A rule with no criteria of its own takes only a message here — but only when the tail
        // holds one literal. The attributes that put several there put values in them, the way
        // RuleRange puts its bounds, and a confidently wrong message is worse than none.
        if (rest.Count == 1)
            rule.MessageTemplate ??= rest[0];
    }

    private static bool IsCriteriaRule(ExtractedValidationRule rule)
        => rule.RuleType.Contains("Criteria", StringComparison.Ordinal);

    /// <summary>The argument's text when it was written as a string literal, else null.</summary>
    private static string? StringLiteral(AttributeArgumentSyntax arg)
        => arg.Expression is LiteralExpressionSyntax literal
           && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    /// <summary>
    /// Extracts appearance rules written on the class and on its properties.
    /// </summary>
    /// <remarks>
    /// <c>AppearanceAttribute</c> is usable on a class, a property, a method or an interface, and
    /// the documentation teaches the property form first: a rule on <c>UnitPrice</c> and a rule on
    /// the class naming <c>TargetItems = "UnitPrice"</c> are two spellings of one rule. Only the
    /// class spelling was read here, while <see cref="ExtractValidationRules"/> had walked the
    /// properties from the start.
    /// </remarks>
    private static List<ExtractedAppearanceRule> ExtractAppearanceRules(ClassDeclarationSyntax classDecl)
    {
        var rules = new List<ExtractedAppearanceRule>();

        rules.AddRange(AppearanceAttributesOf(classDecl.AttributeLists)
            .Select(attr => ReadAppearanceRule(attr))
            .OfType<ExtractedAppearanceRule>());

        foreach (var prop in classDecl.Members.OfType<PropertyDeclarationSyntax>())
        {
            rules.AddRange(AppearanceAttributesOf(prop.AttributeLists)
                .Select(attr => ReadAppearanceRule(attr, prop.Identifier.Text))
                .OfType<ExtractedAppearanceRule>());
        }

        return rules;
    }

    private static IEnumerable<AttributeSyntax> AppearanceAttributesOf(SyntaxList<AttributeListSyntax> attributeLists)
        => attributeLists
            .SelectMany(al => al.Attributes)
            .Where(a => a.Name.ToString().Contains("Appearance"));

    /// <summary>
    /// Reads one <c>[Appearance]</c> attribute.
    /// </summary>
    /// <param name="attr">The attribute to read.</param>
    /// <param name="targetProperty">
    /// The property the attribute was written on, or <see langword="null"/> for a class-level rule.
    /// A property rule that does not name its own <c>TargetItems</c> affects that property, which is
    /// what the equivalent class-level spelling states outright; filling it in keeps the two forms
    /// from documenting differently. An explicit <c>TargetItems</c> is left alone — overwriting it
    /// would silently narrow a rule that names several targets.
    /// </param>
    private static ExtractedAppearanceRule? ReadAppearanceRule(AttributeSyntax attr, string? targetProperty = null)
    {
        if (attr.ArgumentList == null) return null;

        var rule = new ExtractedAppearanceRule();
        var args = attr.ArgumentList.Arguments;

        foreach (var arg in args)
        {
            var name = arg.NameEquals?.Name.ToString();

            if (name is null) continue;

            var value = SyntaxLiteral.ValueOf(arg.Expression);

            switch (name)
            {
                case "Id": rule.Id = value; break;
                case "TargetItems": rule.TargetItems = value; break;
                case "Criteria": rule.Criteria = value; break;
                case "Context": rule.Context = value; break;
                case "Visibility": rule.Visibility = value; break;
                case "Enabled": rule.Enabled = value; break;
                case "BackColor": rule.BackColor = value; break;
                case "FontColor": rule.FontColor = value; break;
                case "AppearanceItemType": rule.AppearanceItemType = ItemType(value); break;
            }
        }

        // After the named ones, which win: everything below is a fallback.
        ApplyPositionalAppearanceArguments(rule, args);

        if (targetProperty is { Length: > 0 } && string.IsNullOrEmpty(rule.TargetItems))
            rule.TargetItems = targetProperty;

        return rule;
    }

    /// <summary>
    /// Reads the arguments an <c>[Appearance]</c> was given by position.
    /// </summary>
    /// <remarks>
    /// Two of the three constructors pass the criteria positionally —
    /// <c>(id, criteria)</c> and <c>(id, appearanceItemType, criteria)</c> — and only the named form
    /// was read, so a rule written either of the other two ways was extracted with no condition at
    /// all and documented as applying unconditionally. Every fixture wrote <c>Criteria =</c> named,
    /// which is why the whole suite agreed.
    /// <para>
    /// The criteria is the <em>last</em> positional argument in both overloads that carry one, and
    /// the item type only exists in the three-argument form, so both can be read without knowing
    /// which constructor was called.
    /// </para>
    /// </remarks>
    private static void ApplyPositionalAppearanceArguments(
        ExtractedAppearanceRule rule, SeparatedSyntaxList<AttributeArgumentSyntax> args)
    {
        var positional = args.Where(arg => arg.NameEquals is null).ToList();

        if (positional.Count == 0) return;

        if (rule.Id.Length == 0)
            rule.Id = SyntaxLiteral.ValueOf(positional[0].Expression);

        if (positional.Count >= 2)
            rule.Criteria ??= SyntaxLiteral.ValueOf(positional[^1].Expression);

        if (positional.Count >= 3)
            rule.AppearanceItemType ??= ItemType(SyntaxLiteral.ValueOf(positional[1].Expression));
    }

    /// <summary>
    /// The item type as XAF names it, however the source spelled it.
    /// </summary>
    /// <remarks>
    /// Positionally it is the enum member, so the source text reads
    /// <c>AppearanceItemType.Action</c>; by name the DevExpress examples write the plain string
    /// <c>"Action"</c>. Both mean the same rule and must not produce two different values.
    /// </remarks>
    private static string? ItemType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var dot = value.LastIndexOf('.');

        return dot >= 0 && dot < value.Length - 1 ? value[(dot + 1)..] : value;
    }

    #region Helper Methods

    /// <summary>
    /// Decides which classes are persistent, following base classes to a fixed point.
    /// </summary>
    /// <remarks>
    /// Matching a class's own base list against a list of root names stops one hop short. An
    /// application that writes a shared base — auditing, a key convention, a display name — puts
    /// every business object below it out of reach, and the loss is silent in the worst way: the
    /// abstract base is extracted in their place, so the inventory reports the one class that is
    /// not a table and omits the ones that are.
    /// <para>
    /// Repeating until a round changes nothing is what the controller side already does in
    /// <c>SelectControllers</c>, and for the same reason: a base class may be read after the class
    /// deriving from it, and a chain can be any depth.
    /// </para>
    /// <para>
    /// A base name is resolved through the deriving file's own scope rather than by simple name,
    /// because a name is not an identity — the reason the DbSet roster carries scopes. An
    /// application may keep a <c>Contracts.Order</c> beside its <c>BusinessObjects.Order</c>, each
    /// deriving from a different <c>NamedBaseObject</c>, and only one of those is a table.
    /// </para>
    /// <para>
    /// Acceptance is keyed on <c>(namespace, name)</c>, so every part of a <c>partial</c> class is
    /// selected once any part of it is — the hand-written part that carries the base list and the
    /// generated part that carries the mapping are the same class.
    /// </para>
    /// </remarks>
    private static (HashSet<(string Namespace, string Name)> Accepted,
        Dictionary<(string Namespace, string Name), (string Namespace, string Name)> Parents)
        SelectPersistentClasses(IEnumerable<SyntaxNode> roots, DbSetRoster roster, ExtractionOptions options)
    {
        var trees = roots.ToList();
        var globalUsings = GlobalUsings(trees);

        var candidates = new List<(ClassDeclarationSyntax Declaration, string Namespace, string Name, HashSet<string> Scopes)>();

        foreach (var root in trees)
        {
            var fileScopes = new HashSet<string>(globalUsings, StringComparer.Ordinal);

            foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (directive.Alias is null && directive.Name is not null)
                    fileScopes.Add(directive.Name.ToString());
            }

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var @namespace = GetNamespace(classDecl);
                var scopes = new HashSet<string>(fileScopes, StringComparer.Ordinal);

                // Its own namespace and every one enclosing it: C# resolves an unqualified name
                // outwards, so a base one level up needs no using directive.
                for (var scope = @namespace; ; )
                {
                    scopes.Add(scope);
                    var dot = scope.LastIndexOf('.');
                    if (dot < 0) break;
                    scope = scope[..dot];
                }

                candidates.Add((classDecl, @namespace, classDecl.Identifier.Text, scopes));
            }
        }

        var accepted = new HashSet<(string Namespace, string Name)>();
        var declared = candidates.Select(candidate => (candidate.Namespace, candidate.Name)).ToHashSet();

        foreach (var candidate in candidates)
        {
            if (IsXafBusinessObject(candidate.Declaration, options.BaseTypeNames)
                || roster.Registers(candidate.Namespace, candidate.Name)
                || DeclaresItselfABusinessClass(candidate.Declaration)
                || DerivesFromTheLibrary(candidate.Declaration, candidate.Scopes, declared))
                accepted.Add((candidate.Namespace, candidate.Name));
        }

        // Each round can accept a class whose base was accepted in the previous one, so it repeats
        // until a round changes nothing. Bounded by the number of classes.
        bool changed;

        do
        {
            changed = false;

            foreach (var candidate in candidates)
            {
                if (accepted.Contains((candidate.Namespace, candidate.Name)))
                    continue;

                if (ResolveBase(candidate.Declaration, candidate.Scopes, accepted) is null)
                    continue;

                accepted.Add((candidate.Namespace, candidate.Name));
                changed = true;
            }
        }
        while (changed);

        // The walk just resolved every class's ancestry; keeping the edge is what lets inherited
        // properties be folded later without resolving anything a second time. Computed after the
        // fixed point, because a parent may be accepted rounds after the class deriving from it.
        var parents = new Dictionary<(string Namespace, string Name), (string Namespace, string Name)>();

        foreach (var candidate in candidates)
        {
            if (!accepted.Contains((candidate.Namespace, candidate.Name)))
                continue;

            // Only one part of a partial class names the base class; the parts that name none, or
            // only interfaces, must not erase the edge that part resolved.
            if (ResolveBase(candidate.Declaration, candidate.Scopes, accepted) is { } parent)
                parents.TryAdd((candidate.Namespace, candidate.Name), parent);
        }

        return (accepted, parents);
    }

    /// <summary>
    /// The accepted class a name in this class's base list resolves to, if any.
    /// </summary>
    /// <remarks>
    /// Every entry is tried rather than the first, because syntax cannot tell a base class from an
    /// interface. An interface name only matches if a class of that name was itself accepted, which
    /// an interface never is.
    /// </remarks>
    private static (string Namespace, string Name)? ResolveBase(
        ClassDeclarationSyntax classDecl,
        HashSet<string> scopes,
        HashSet<(string Namespace, string Name)> accepted)
    {
        foreach (var baseType in classDecl.BaseList?.Types ?? default)
        {
            var written = baseType.Type.ToString();

            var generic = written.IndexOf('<');
            if (generic > 0) written = written[..generic];

            var dot = written.LastIndexOf('.');
            var simpleName = dot < 0 ? written : written[(dot + 1)..];

            foreach (var (@namespace, name) in accepted)
            {
                if (!string.Equals(name, simpleName, StringComparison.Ordinal)) continue;

                if (dot < 0)
                {
                    // Unqualified: it named something this file can actually see.
                    if (scopes.Contains(@namespace)) return (@namespace, name);
                    continue;
                }

                // Qualified: the tail it wrote is more specific than any using directive.
                var qualifier = written[..dot];
                if (@namespace.Equals(qualifier, StringComparison.Ordinal)
                    || @namespace.EndsWith("." + qualifier, StringComparison.Ordinal))
                    return (@namespace, name);
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>global using</c> namespaces, which reach every file however they are declared.
    /// </summary>
    private static HashSet<string> GlobalUsings(IEnumerable<SyntaxNode> trees) => trees
        .SelectMany(root => root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        .Where(directive => !directive.GlobalKeyword.IsKind(SyntaxKind.None))
        .Select(directive => directive.Name?.ToString())
        .Where(name => name is not null)
        .Select(name => name!)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Detects ORM mode by scanning file contents for EF-specific namespaces.
    /// </summary>
    /// <summary>
    /// Decides which ORM an application persists with, from what its source declares.
    /// </summary>
    /// <remarks>
    /// Read as syntax rather than as text. Scanning file contents for a namespace counts a mention
    /// of it in a comment, a string or an <c>#if</c>-disabled block as evidence — and a project
    /// that merely discusses EF Core is not one that uses it.
    /// <para>
    /// The signals are ranked by what each one costs to be wrong about. A <c>DbSet&lt;T&gt;</c>
    /// registered on a context is the application declaring a table, and it cannot be mistaken;
    /// a <c>using</c> directive is weaker but still deliberate. Where neither ORM leaves any
    /// trace the answer is <see cref="OrmType.Unknown"/>, because the alternative is to state a
    /// default in the same voice as everything that was actually read.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What this project persists with, decided first by what it declares itself.
    /// </summary>
    /// <remarks>
    /// A referenced project only ever breaks a tie. An XPO application that references an EF Core
    /// utility -- a cache, a telemetry store, an Identity database beside XAF security -- is not
    /// an EF Core application, and reading it as one is expensive: the ORM is what
    /// <c>AGENTS.md</c> and the MCP overview hand an agent as a hard rule, and the rule forbids
    /// the whole API surface of whichever ORM it did not name.
    /// <para>
    /// The fallback is for the opposite layout, which is just as real: a module whose own files
    /// name no ORM at all because every entity derives from a base in the framework project it
    /// references. Answering <see cref="OrmType.Unknown"/> there would throw away a reading that
    /// was available.
    /// </para>
    /// </remarks>
    private static OrmType DetectOrm(
        List<SyntaxNode> ownRoots,
        List<SyntaxNode> allRoots,
        DbSetRoster roster)
    {
        var own = DetectOrmType(ownRoots, DbSetRoster.Read(ownRoots));

        return own != OrmType.Unknown ? own : DetectOrmType(allRoots, roster);
    }

    private static OrmType DetectOrmType(IEnumerable<SyntaxNode> roots, DbSetRoster roster)
    {
        // The application cannot run without its registrations being right, which makes them the
        // one signal that is never incidental.
        if (roster.RegistersAnything)
            return OrmType.EfCore;

        var efCore = false;
        var xpo = false;

        foreach (var root in roots)
        {
            foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (directive.Alias is not null || directive.Name is null)
                    continue;

                var name = directive.Name.ToString();

                // Exact or namespace-prefixed, never StartsWith on its own:
                // `DevExpress.Persistent.BaseImpl` is XPO and a prefix of the EF Core one.
                if (IsOrDescends(name, "Microsoft.EntityFrameworkCore")
                    || IsOrDescends(name, "DevExpress.Persistent.BaseImpl.EF")
                    || IsOrDescends(name, "DevExpress.ExpressApp.EFCore"))
                    efCore = true;
                else if (IsOrDescends(name, "DevExpress.Xpo"))
                    xpo = true;
            }

            // A base class is as deliberate as a using directive and survives file-scoped
            // namespaces that name nothing.
            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                foreach (var baseTypeName in GetBaseTypeNames(classDecl))
                {
                    if (baseTypeName is "XPObject" or "XPCustomObject" or "XPLiteObject" or "XPBaseObject")
                        xpo = true;
                    else if (baseTypeName is "DbContext")
                        efCore = true;
                }
            }
        }

        if (efCore) return OrmType.EfCore;
        if (xpo) return OrmType.Xpo;

        return OrmType.Unknown;
    }

    private static bool IsOrDescends(string name, string ns) =>
        name.Equals(ns, StringComparison.Ordinal) || name.StartsWith($"{ns}.", StringComparison.Ordinal);

    /// <summary>
    /// The types an application registers as <c>DbSet&lt;T&gt;</c> on a <c>DbContext</c>, and the
    /// namespaces each registration could have been naming.
    /// </summary>
    /// <remarks>
    /// Under EF Core this is the application's own statement of what it persists, and it has to be
    /// right for the application to run at all — which makes it a better signal than a base class.
    /// An XAF project mapped onto an existing schema routinely has no XAF base class to match: the
    /// tables bring their own keys, so the project writes its own base, or maps a plain POCO.
    /// <para>
    /// Only classes declared in the analyzed source become entities. A DbContext also registers
    /// framework tables (<c>ModuleInfo</c>, <c>FileData</c>, <c>ModelDifference</c>) whose types
    /// are declared in DevExpress assemblies and are therefore never seen here, so they drop out
    /// without needing a list of names to exclude.
    /// </para>
    /// <para>
    /// The roster carries a namespace scope rather than a bare name because a bare name is not an
    /// identity: an application is free to have a <c>Contracts.Invoice</c> DTO beside its
    /// <c>BusinessObjects.Invoice</c> entity, and telling an agent the DTO is persistent is worse
    /// than the gap this roster exists to close. What is modelled here is ordinary C# lookup, the
    /// part of it syntax can see: the registering file's usings, its own namespace, and the
    /// namespaces enclosing it. Aliases, <c>using static</c> and extern aliases are not modelled —
    /// a registration that needs one of those finds no class and is dropped, which is the safe
    /// direction.
    /// </para>
    /// </remarks>
    private sealed class DbSetRoster
    {
        private readonly List<(string Argument, HashSet<string> Scopes)> _registrations = [];

        /// <summary>Whether any context in the source registers anything at all.</summary>
        public bool RegistersAnything => _registrations.Count > 0;

        /// <summary>
        /// Reads every <c>DbSet&lt;T&gt;</c> property declared on a context in the parsed source.
        /// </summary>
        public static DbSetRoster Read(IEnumerable<SyntaxNode> roots)
        {
            var roster = new DbSetRoster();
            var trees = roots.ToList();

            // `global using` reaches every file, so it has to be gathered before any one file is
            // read -- including from a GlobalUsings.cs that declares nothing else.
            var globalUsings = trees
                .SelectMany(root => root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                .Where(directive => !directive.GlobalKeyword.IsKind(SyntaxKind.None))
                .Select(directive => directive.Name?.ToString())
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);

            var contexts = FindContextClasses(trees);

            foreach (var context in contexts)
            {
                var root = context.SyntaxTree.GetRoot();
                var scopes = new HashSet<string>(globalUsings, StringComparer.Ordinal);

                foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                {
                    if (directive.Alias is null && directive.Name is not null)
                        scopes.Add(directive.Name.ToString());
                }

                // The context's own namespace, and every namespace enclosing it: C# resolves an
                // unqualified name outwards, so an entity one level up needs no using directive.
                var contextNamespace = GetNamespace(context);
                for (var scope = contextNamespace; ; )
                {
                    scopes.Add(scope);
                    var dot = scope.LastIndexOf('.');
                    if (dot < 0) break;
                    scope = scope[..dot];
                }

                foreach (var property in context.Members.OfType<PropertyDeclarationSyntax>())
                {
                    if (AsDbSet(property.Type) is not { } generic) continue;

                    var argument = generic.TypeArgumentList.Arguments[0].ToString();
                    if (argument.Length > 0)
                        roster._registrations.Add((argument, scopes));
                }

                // A generic context base registers its type arguments without a property saying so.
                // `IdentityDbContext<AppUser>` is the whole registration of the user table in the
                // template ASP.NET Core writes, and reading only properties leaves that table out
                // of an application that certainly has it.
                foreach (var argument in GenericContextBaseArguments(context))
                    roster._registrations.Add((argument, scopes));
            }

            return roster;
        }

        /// <summary>
        /// Whether the application registers this class -- by name, and from somewhere that could
        /// actually have been naming this one.
        /// </summary>
        public bool Registers(string @namespace, string className)
        {
            foreach (var (registered, scopes) in _registrations)
            {
                // `DbSet<global::Shop.Invoice>` names the same class as `DbSet<Shop.Invoice>`.
                // The alias qualifier survived into the namespace comparison below and made every
                // such registration match nothing.
                var argument = registered.StartsWith("global::", StringComparison.Ordinal)
                    ? registered["global::".Length..]
                    : registered;

                var dot = argument.LastIndexOf('.');
                var simpleName = dot < 0 ? argument : argument[(dot + 1)..];
                if (!string.Equals(simpleName, className, StringComparison.Ordinal)) continue;

                if (dot < 0)
                {
                    if (scopes.Contains(@namespace)) return true;
                    continue;
                }

                // A qualified registration -- DbSet<Contracts.Invoice> -- names its own namespace
                // tail, and that is more specific than anything the usings could tell us.
                var qualifier = argument[..dot];
                if (@namespace.Equals(qualifier, StringComparison.Ordinal)
                    || @namespace.EndsWith("." + qualifier, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The classes that register entities: the ones declaring <c>DbSet&lt;T&gt;</c> properties.
        /// </summary>
        /// <remarks>
        /// Read from the declaration rather than from the base class, because the base is very
        /// often not there to read. <c>IdentityDbContext&lt;TUser&gt;</c> is the base every
        /// ASP.NET Core Identity template writes, and it lives in a package -- so a walk that can
        /// only grow its set from classes declared in the analyzed source never reaches it, and an
        /// application registers nothing at all however many tables it has.
        /// <para>
        /// Declaring a <c>DbSet&lt;T&gt;</c> property is the stronger signal in any case. It is
        /// the application stating that this is one of its tables, it has to be right for the
        /// application to run, and it is the only signal that does not depend on having the base
        /// class in hand. A hand-written <c>AuditedDbContext : DbContext</c> and a context on a
        /// package base become the same case, rather than one that works and one that does not.
        /// </para>
        /// <para>
        /// It is the property that counts, never a mention of the generic: a <c>DbSet&lt;T&gt;</c>
        /// declared as a local inside a method body is a type name in a helper, not an
        /// application saying it owns a table.
        /// </para>
        /// <para>
        /// <strong>What this deliberately does not read</strong>, measured rather than guessed and
        /// written here so the gap is not rediscovered:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <c>modelBuilder.Entity&lt;T&gt;().ToTable(...)</c> in <c>OnModelCreating</c>. A real
        /// registration, and reading it means reading a method body for calls rather than a
        /// declaration for a shape -- a different kind of evidence, and one that a fluent chain
        /// built in a loop or a helper would defeat anyway.
        /// </description></item>
        /// <item><description>
        /// A context outside the business-object folder. Discovery narrows to
        /// <c>BusinessObjects/</c> when it exists, so a <c>Data/AppDbContext.cs</c> beside it is
        /// never parsed and everything it alone registers is missed. Widening the scan is the fix,
        /// and it changes what every project pays to extract, so it is its own decision.
        /// </description></item>
        /// <item><description>
        /// A class that is not a context but hands out a <c>DbSet&lt;T&gt;</c> -- a repository
        /// wrapper -- is read as one. Kept on purpose: the type it names really is mapped
        /// somewhere, and the alternative is demanding evidence of a base this rule exists
        /// precisely to stop needing.
        /// </description></item>
        /// </list>
        /// </remarks>
        private static List<ClassDeclarationSyntax> FindContextClasses(List<SyntaxNode> trees)
        {
            return trees
                .SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Where(DeclaresAnyDbSet)
                .ToList();
        }

        /// <summary>
        /// Whether the class declares at least one <c>DbSet&lt;T&gt;</c> property of its own.
        /// </summary>
        private static bool DeclaresAnyDbSet(ClassDeclarationSyntax candidate)
        {
            return candidate.Members
                       .OfType<PropertyDeclarationSyntax>()
                       .Any(property => AsDbSet(property.Type) is not null)
                   || GenericContextBaseArguments(candidate).Count > 0;
        }

        /// <summary>
        /// The <c>DbSet&lt;T&gt;</c> a type reference names, however it is written.
        /// </summary>
        /// <remarks>
        /// A property may spell its type <c>Microsoft.EntityFrameworkCore.DbSet&lt;T&gt;</c> or
        /// <c>global::Microsoft.EntityFrameworkCore.DbSet&lt;T&gt;</c>, which are the same declaration
        /// and used to be invisible: the pattern demanded a bare generic name, so a context written
        /// that way registered nothing at all.
        /// </remarks>
        private static GenericNameSyntax? AsDbSet(TypeSyntax? type)
        {
            var generic = type switch
            {
                GenericNameSyntax bare => bare,
                QualifiedNameSyntax { Right: GenericNameSyntax right } => right,
                AliasQualifiedNameSyntax { Name: GenericNameSyntax aliased } => aliased,
                _ => null,
            };

            return generic is { Identifier.Text: "DbSet", TypeArgumentList.Arguments.Count: 1 }
                ? generic
                : null;
        }

        /// <summary>
        /// The type arguments of a generic context base, which are registrations in their own right.
        /// </summary>
        /// <remarks>
        /// Matched on the base's own name ending in <c>DbContext</c> rather than on a list of known
        /// bases, so it holds for <c>IdentityDbContext</c>, for a team's
        /// <c>TenantDbContext&lt;T&gt;</c>, and for whatever the next template writes. The base itself
        /// is never needed: what it does with the argument is the framework's business, and that it
        /// maps it is the thing worth knowing.
        /// <para>
        /// Every argument is offered, not just the first. A type argument that names no class the
        /// application declares matches nothing when the roster is asked, so a <c>string</c> key costs
        /// only the lookup.
        /// </para>
        /// </remarks>
        private static List<string> GenericContextBaseArguments(ClassDeclarationSyntax candidate)
        {
            var arguments = new List<string>();

            if (candidate.BaseList is null)
                return arguments;

            foreach (var baseType in candidate.BaseList.Types)
            {
                var generic = baseType.Type switch
                {
                    GenericNameSyntax bare => bare,
                    QualifiedNameSyntax { Right: GenericNameSyntax right } => right,
                    AliasQualifiedNameSyntax { Name: GenericNameSyntax aliased } => aliased,
                    _ => null,
                };

                if (generic is null) continue;
                if (!generic.Identifier.Text.EndsWith("DbContext", StringComparison.Ordinal)) continue;

                foreach (var argument in generic.TypeArgumentList.Arguments)
                {
                    var name = argument.ToString();
                    if (name.Length > 0)
                        arguments.Add(name);
                }
            }

            return arguments;
        }
    }

    /// <summary>
    /// Folds the parts of a <c>partial</c> class into the one entity they describe.
    /// </summary>
    /// <remarks>
    /// Acceptance is keyed on the class rather than the declaration, so every part of a partial
    /// class is extracted, each holding what that part declares -- and a scaffolded legacy schema,
    /// which is exactly what the DbSet roster is for, splits its classes as a matter of routine.
    /// Left alone that reports the entity twice, each copy holding half its properties: two
    /// incomplete truths, and no way for a reader to tell they are the same class.
    /// <para>
    /// Merging also recovers the members XPO extraction has always dropped, where a hand-written
    /// part carries <c>: BaseObject</c> and a generated part carries half the columns.
    /// </para>
    /// </remarks>
    /// <param name="entities">Every entity extracted, from both pools.</param>
    /// <param name="borrowed">
    /// Files read from referenced projects. Parts are merged only within their own pool, because
    /// a class two projects both declare under one namespace is two classes, not two halves of
    /// one. C# compiles that shape -- the local type wins, with a warning -- so it is reachable
    /// by nothing worse than a file copied into a client and left in the library's namespace.
    /// Merging across the boundary let the borrowed half become primary, which handed the module
    /// the other project's properties and then deleted the module's own class along with the
    /// borrowed file it had inherited a path from.
    /// </param>
    /// <param name="parents">The base each accepted class was resolved to.</param>
    /// <param name="options">Carries the root base classes.</param>
    private static List<ExtractedEntity> MergePartialDeclarations(
        List<ExtractedEntity> entities,
        HashSet<string> borrowed,
        Dictionary<(string Namespace, string Name), (string Namespace, string Name)> parents,
        ExtractionOptions options)
    {
        // C# puts the base class first in a base list, so a part names one when its first entry is
        // a class known to be one: the base this class was resolved to, a root base, or a class the
        // business class library ships. Syntax cannot tell a class from an interface on its own, so a
        // first entry nobody can vouch for proves nothing either way.
        bool NamesTheBaseClass(ExtractedEntity part) =>
            part.BaseTypes.FirstOrDefault() is { } first
            && (options.BaseTypeNames.Contains(first, StringComparer.Ordinal)
                || LibraryBaseTypeNames.Contains(first)
                || (parents.TryGetValue((part.Namespace, part.ClassName), out var parent)
                    && parent.Name.Equals(first, StringComparison.Ordinal)));

        var parts = new Dictionary<(bool Borrowed, string Namespace, string ClassName), List<ExtractedEntity>>();
        var order = new List<(bool Borrowed, string Namespace, string ClassName)>();

        foreach (var entity in entities)
        {
            var key = (borrowed.Contains(entity.FilePath), entity.Namespace, entity.ClassName);
            if (!parts.TryGetValue(key, out var group))
            {
                parts[key] = group = [];
                order.Add(key);
            }
            group.Add(entity);
        }

        var merged = new List<ExtractedEntity>();

        foreach (var key in order)
        {
            var group = parts[key];

            // The part naming the base class is the hand-written one, and usually where the class
            // attributes live; what the other parts declare is merged into it below. Taking whichever half came first instead would make the reported
            // file, and the order of the columns, depend on the file system doing the listing. Any
            // base list is not enough: a part that only adds an interface -- a layout, an
            // `IXafEntityObject` -- declares one too, and sorts first as often as not.
            var primary = group.Find(NamesTheBaseClass)
                          ?? group.Find(part => part.BaseTypes.Count > 0)
                          ?? group[0];
            merged.Add(primary);

            foreach (var entity in group)
            {
                if (ReferenceEquals(entity, primary)) continue;

                primary.Description ??= entity.Description;
                primary.NavigationGroup ??= entity.NavigationGroup;
                primary.DefaultProperty ??= entity.DefaultProperty;
                primary.ModelCaption ??= entity.ModelCaption;
                primary.SourceProject ??= entity.SourceProject;
                primary.IsDefaultClassOptions |= entity.IsDefaultClassOptions;
                primary.IsCloneable |= entity.IsCloneable;
                // `abstract` need only be written on one part to be true of the class.
                primary.IsAbstract |= entity.IsAbstract;

                // Non-persistent anywhere means non-persistent: one part saying so is the class.
                primary.IsPersistent &= entity.IsPersistent;

                if (primary.BaseType is "object" or "" && entity.BaseType is not ("object" or ""))
                    primary.BaseType = entity.BaseType;

                foreach (var baseType in entity.BaseTypes.Where(name => !primary.BaseTypes.Contains(name)))
                    primary.BaseTypes.Add(baseType);

                var known = primary.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                primary.Properties.AddRange(entity.Properties.Where(property => known.Add(property.Name)));

                primary.Relationships.AddRange(entity.Relationships);
                primary.ValidationRules.AddRange(entity.ValidationRules);
                primary.AppearanceRules.AddRange(entity.AppearanceRules);
                primary.InferredBusinessRules.AddRange(entity.InferredBusinessRules);
                primary.Lifecycle.AddRange(entity.Lifecycle);
                primary.SourceComments.AddRange(entity.SourceComments);
            }
        }

        return merged;
    }

    /// <summary>
    /// Folds what each ancestor declares into the entities that inherit it.
    /// </summary>
    /// <remarks>
    /// An entity holding only what it declares itself is an inventory with most of its columns
    /// under other headings — or, for a shared audit base, missing from every entity at once. The
    /// inherited properties are persisted, appear in views, and are readable from any code an
    /// agent writes; a document that promises completeness has to carry them where the reader
    /// looks.
    /// <para>
    /// The same holds for everything else an ancestor declares. A <c>RuleCriteria</c> on an audit
    /// base is enforced every time any entity in the application is saved; an <c>[Appearance]</c>
    /// greys a field on every screen below it; an association gives every descendant a collection
    /// that really is populated. Carrying only the properties down fixed the inventory and left
    /// the rules one door away, which is what issue #14 is about.
    /// </para>
    /// <para>
    /// Ancestors first, so the properties read in declaration order from the root down, the way
    /// they do in the class. What the class redeclares is its own: the inherited one is not added
    /// beside it. Each fold works on a copy, because the same declaration is listed under every
    /// descendant and each listing names its own declarer.
    /// </para>
    /// </remarks>
    /// <param name="entities">Every entity extracted, from both pools.</param>
    /// <param name="parents">Each accepted class and the class it derives from.</param>
    /// <param name="borrowed">
    /// Files read from referenced projects. Since the pools stopped merging, a class two projects
    /// both declare under one namespace reaches this point twice, and the collision is real rather
    /// than a bug to route around: C# binds the local type there, warning about the other, so the
    /// local one is what a descendant inherits from and the one whose ancestry is folded.
    /// </param>
    private static void FoldInheritance(
        List<ExtractedEntity> entities,
        Dictionary<(string Namespace, string Name), (string Namespace, string Name)> parents,
        HashSet<string> borrowed)
    {
        var byClass = new Dictionary<(string Namespace, string Name), ExtractedEntity>();

        foreach (var entity in entities)
        {
            var key = (entity.Namespace, entity.ClassName);

            if (!byClass.TryGetValue(key, out var held)
                || (!borrowed.Contains(entity.FilePath) && borrowed.Contains(held.FilePath)))
            {
                byClass[key] = entity;
            }
        }
        var folded = new HashSet<(string Namespace, string Name)>();

        foreach (var entity in entities)
            Fold(entity, byClass, parents, folded);
    }

    /// <summary>
    /// Folds one entity's ancestry into it, folding the parent first so a chain of any depth
    /// arrives complete.
    /// </summary>
    private static void Fold(
        ExtractedEntity entity,
        Dictionary<(string Namespace, string Name), ExtractedEntity> byClass,
        Dictionary<(string Namespace, string Name), (string Namespace, string Name)> parents,
        HashSet<(string Namespace, string Name)> folded)
    {
        var key = (entity.Namespace, entity.ClassName);

        // Marked before recursing, which both memoizes the walk and stops it if malformed source
        // ever declares a circular base list.
        if (!folded.Add(key))
            return;

        if (!parents.TryGetValue(key, out var parentKey) || !byClass.TryGetValue(parentKey, out var parent))
            return;

        Fold(parent, byClass, parents, folded);

        // A class deriving from one that stores nothing stores nothing either, and need not say so
        // itself: only the base in the library, or an attribute on the base, does.
        if (!parent.IsPersistent)
            entity.IsPersistent = false;

        var own = entity.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        // A property redeclared with another type (`new string Student` over `Student Student`) hides the
        // navigation, and the relationship behind it goes with it. Same type (an override) keeps it,
        // however the type is spelled: `global::School.Student?` is still `Student`.
        var hidden = parent.Properties
            .Where(property => entity.Properties.Any(mine => mine.Name == property.Name
                && !string.Equals(TypeSpelling(mine.TypeName), TypeSpelling(property.TypeName), StringComparison.Ordinal)))
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var inherited = new List<ExtractedProperty>();

        foreach (var property in parent.Properties)
        {
            if (own.Contains(property.Name))
                continue;

            var copy = property.Clone();
            // The declarer, not the parent: what the parent itself inherited keeps its origin.
            copy.InheritedFrom ??= parent.ClassName;
            inherited.Add(copy);
        }

        entity.Properties.InsertRange(0, inherited);

        // Everything else the entity inherits, on the same terms. What is written on a property
        // travels with the property; what is written on the class does not, and stayed under the
        // heading of a class the reader was not reading.
        FoldInto(entity.ValidationRules, parent.ValidationRules, parent.ClassName, ValidationRuleKey,
                 rule => rule.Clone(), (rule, declarer) => rule.InheritedFrom ??= declarer);

        FoldInto(entity.AppearanceRules, parent.AppearanceRules, parent.ClassName, AppearanceRuleKey,
                 rule => rule.Clone(), (rule, declarer) => rule.InheritedFrom ??= declarer);

        FoldInto(entity.Relationships, parent.Relationships.Where(rel => !hidden.Contains(rel.PropertyName)).ToList(),
                 parent.ClassName, rel => rel.PropertyName,
                 rel => rel.Clone(), (rel, declarer) => rel.InheritedFrom ??= declarer);

        // A hook runs for a descendant only if nothing stops it: an override of the same method that
        // does not call base replaces every hook above it for that trigger, including the ones the
        // parent itself inherited.
        //
        // An override of a method a base started with `new virtual` overrides that method, not the hook,
        // so it is not one and replaces nothing. Likewise below an explicit implementation, which the Object
        // Space keeps calling. Either way, unless the class names the interface again: then its own method is it.
        var restarted = parent.Lifecycle
            .Where(hook => (hook.IsNewSlot || hook.IsExplicitImplementation) && !entity.BaseTypes.Contains("IXafEntityObject"))
            .Select(hook => hook.MethodName).ToHashSet(StringComparer.Ordinal);
        entity.Lifecycle.RemoveAll(own => own.InheritedFrom is null && own.IsOverride && restarted.Contains(own.MethodName));

        var inheritedHooks = parent.Lifecycle.Where(hook => hook.IsNewSlot || !entity.Lifecycle.Any(own =>
            own.InheritedFrom is null && !own.IsNewSlot && own.Trigger == hook.Trigger && !own.CallsBase)).ToList();
        foreach (var hook in inheritedHooks)
        {
            var copy = hook.Clone();
            if (copy.InheritedFrom is null)
            {
                copy.InheritedFrom = parent.ClassName;
                copy.InheritedFromNamespace = parent.Namespace;
            }
            entity.Lifecycle.Add(copy);
        }
    }

    /// <summary>
    /// Methods of one declaration that may be lifecycle hooks, with what they assign.
    /// </summary>
    /// <remarks>
    /// A name alone is not enough — any class may have a method called <c>OnSaving</c> — so only
    /// three shapes are kept: an override (of <c>BaseObject</c>'s, or XPO's), an explicit
    /// <c>IXafEntityObject</c> implementation, and a public method, which is a hook only when the class
    /// names <c>IXafEntityObject</c> (<see cref="IsLifecycleHook"/>, after the parts are merged).
    /// </remarks>
    private static IEnumerable<ExtractedLifecycleHook> ExtractLifecycleHookCandidates(ClassDeclarationSyntax classDecl, string filePath)
    {
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;
            LifecycleTrigger? trigger = name switch
            {
                "OnCreated" or "AfterConstruction" => LifecycleTrigger.Created,
                "OnLoaded" => LifecycleTrigger.Loaded,
                "OnSaving" => LifecycleTrigger.Saving,
                _ => null,
            };

            if (trigger is null || method.ParameterList.Parameters.Count > 0 || method.TypeParameterList is not null
                || method.ReturnType is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }
                || method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)))
                continue;

            var isOverride = method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OverrideKeyword));
            var isExplicit = method.ExplicitInterfaceSpecifier?.Name.ToString().EndsWith("IXafEntityObject", StringComparison.Ordinal) == true;
            var isPublic = method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
            var hasBody = method.Body is not null || method.ExpressionBody is not null;

            // A `new virtual` namesake starts a method of its own: not a hook unless the class names the
            // interface again (IsLifecycleHook), but it decides what an override below it is.
            var isNewSlot = method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.NewKeyword))
                && method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.VirtualKeyword) || modifier.IsKind(SyntaxKind.AbstractKeyword));

            if (!isNewSlot && (!hasBody || (!isOverride && !isExplicit && !isPublic)))
                continue;

            yield return new ExtractedLifecycleHook
            {
                Trigger = trigger.Value,
                MethodName = name,
                FilePath = filePath,
                Line = SourceLine.Of(method.Identifier),
                AssignedProperties = AssignedNames(method),
                // A call in a lambda or a local function may never run, so it does not keep the base hook,
                // and neither does a call with arguments, which is an overload of the same name.
                CallsBase = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                    invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax, Name: IdentifierNameSyntax } access
                    && access.Name.Identifier.Text == name
                    && invocation.ArgumentList.Arguments.Count == 0
                    && !IsDeferred(invocation, method)),
                IsOverride = isOverride,
                IsExplicitImplementation = isExplicit,
                IsNewSlot = isNewSlot,
                IsPublic = isPublic,
                HasBody = hasBody,
            };
        }
    }

    /// <summary>Whether a candidate read from a declaration is a hook the Object Space calls.</summary>
    /// <remarks>
    /// A class that implements an interface method explicitly is called there, so an override or a public
    /// method of that name beside it is not. A public method counts only as one of the interface's three;
    /// <c>AfterConstruction</c> is XPO's, and only as an override. Markers of a <c>new virtual</c> namesake
    /// stay until the fold has used them.
    /// </remarks>
    private static bool IsLifecycleHook(ExtractedLifecycleHook hook, ExtractedEntity entity, HashSet<string> explicitlyImplemented) =>
        hook.IsNewSlot || hook.IsExplicitImplementation
        || (!explicitlyImplemented.Contains(hook.MethodName)
            && (hook.IsOverride || (entity.BaseTypes.Contains("IXafEntityObject") && hook.MethodName != "AfterConstruction")));

    /// <summary>
    /// The names a method assigns to members of its own object, before they are checked against the
    /// class's properties.
    /// </summary>
    /// <remarks>
    /// <c>P = …</c>, <c>this.P = …</c>, <c>base.P = …</c>, and <c>SetPropertyValue(nameof(P), …)</c> or its
    /// security-bypassing sibling with a <c>nameof</c> or a literal, and <c>P++</c> or <c>--P</c>. Left out: a
    /// bare name the method body declares (a local, a pattern or loop variable) — <c>this.P</c> is the property
    /// whatever the locals are called — a property set in an object initializer, which
    /// belongs to the object being built, and anything inside a lambda, an anonymous method or a local
    /// function, which may never run.
    /// </remarks>
    private static List<string> AssignedNames(MethodDeclarationSyntax method)
    {
        // Each name with the scope it is declared in, which is the only place it hides a property. Only
        // declarations in code that runs: nothing inside a lambda or a local function is read anyway.
        var declared = method.DescendantNodes()
            .Where(node => !IsDeferred(node, method))
            .Select(node => node switch
            {
                VariableDeclaratorSyntax variable => variable.Identifier.Text,
                SingleVariableDesignationSyntax designation => designation.Identifier.Text,
                ForEachStatementSyntax loop => loop.Identifier.Text,
                CatchDeclarationSyntax @catch => @catch.Identifier.Text,
                _ => null,
            } is { } name
                ? (Name: name, Scope: node.AncestorsAndSelf().TakeWhile(ancestor => ancestor != method).FirstOrDefault(ancestor =>
                      ancestor is BlockSyntax or ForStatementSyntax or ForEachStatementSyntax or CatchClauseSyntax
                          or UsingStatementSyntax or SwitchStatementSyntax
                      // A switch's sections share one scope; only a case label's pattern belongs to its section.
                      || (ancestor is SwitchSectionSyntax section && section.Labels.Any(label => label.Span.Contains(node.Span)))) ?? method)
                : default)
            .Where(declaration => declaration.Name is not null)
            .ToList();

        var names = new List<string>();

        void Add(string? name)
        {
            if (name is { Length: > 0 } && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }

        // A bare name a local shadows is the local; this.P and base.P never are.
        string? Target(ExpressionSyntax target) => target switch
        {
            IdentifierNameSyntax identifier when !declared.Any(declaration => declaration.Name == identifier.Identifier.Text
                                                                              && identifier.Ancestors().Contains(declaration.Scope))
                => identifier.Identifier.Text,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } access => access.Name.Identifier.Text,
            _ => null,
        };

        foreach (var node in method.DescendantNodes())
        {
            if (IsDeferred(node, method))
                continue;

            switch (node)
            {
                case AssignmentExpressionSyntax assignment when assignment.Parent is not InitializerExpressionSyntax:
                    Add(Target(assignment.Left));
                    break;

                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    Add(Target(postfix.Operand));
                    break;

                case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    Add(Target(prefix.Operand));
                    break;

                case InvocationExpressionSyntax invocation
                    when InvokedOnThis(invocation) is "SetPropertyValue" or "SetPropertyValueWithSecurityBypass"
                         && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } first:
                    Add(first switch
                    {
                        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
                        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } => SyntaxLiteral.ValueOf(first),
                        _ => null,
                    });
                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// The name of a method called on the object itself — with no receiver, or on <c>this</c> or
    /// <c>base</c> — and null for a call on anything else, which sets another object's property.
    /// </summary>
    private static string? InvokedOnThis(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } access => access.Name.Identifier.Text,
        _ => null,
    };

    /// <summary>Whether a node sits in a lambda, an anonymous method or a local function inside the method.</summary>
    private static bool IsDeferred(SyntaxNode node, SyntaxNode method) => node.Ancestors()
        .TakeWhile(ancestor => ancestor != method)
        .Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

    /// <summary>
    /// Adds what the parent declared to the descendant, keeping the descendant's own where the two
    /// name the same thing.
    /// </summary>
    /// <remarks>
    /// Appended rather than inserted first, unlike properties: a class's properties read in
    /// declaration order from the root down, but a rule or an association has no such order to
    /// preserve, and what the entity declares itself is what a reader came for.
    /// </remarks>
    private static void FoldInto<T>(
        List<T> target,
        List<T> fromParent,
        string parentClassName,
        Func<T, string> keyOf,
        Func<T, T> clone,
        Action<T, string> markDeclarer)
    {
        var own = target.Select(keyOf).ToHashSet(StringComparer.Ordinal);

        foreach (var item in fromParent)
        {
            // Redeclaring wins: a descendant that reuses an identifier is replacing the rule, and
            // listing both would show a reader two rules that contradict each other.
            if (!own.Add(keyOf(item)))
                continue;

            var copy = clone(item);
            markDeclarer(copy, parentClassName);
            target.Add(copy);
        }
    }

    /// <summary>
    /// What makes two validation rules the same rule.
    /// </summary>
    /// <remarks>
    /// Its identifier when it was given one, because that is what XAF itself keys on. Without one,
    /// the attribute and the property it targets: a descendant that writes its own
    /// <c>[RuleRequiredField]</c> over an inherited column has replaced the inherited one, and two
    /// unnamed rules of the same kind on the same property cannot be told apart anyway.
    /// </remarks>
    private static string ValidationRuleKey(ExtractedValidationRule rule)
        => rule.Id is { Length: > 0 } id ? id : $"{rule.RuleType}\0{rule.TargetProperty}";

    /// <summary>
    /// What makes two appearance rules the same rule.
    /// </summary>
    /// <remarks>
    /// Its identifier when it was given one, on the same terms as <see cref="ValidationRuleKey"/>.
    /// An empty id is ordinary rather than an omission — a rule written on a property already says
    /// what it governs, and the DevExpress non-persistent-objects demo writes
    /// <c>[Appearance("", Enabled = false, TargetItems = "*")]</c> — so the targets stand in for a
    /// name, and two unnamed rules over different properties stay two rules through the fold.
    /// </remarks>
    private static string AppearanceRuleKey(ExtractedAppearanceRule rule)
        => rule.Id is { Length: > 0 } id ? id : $"Appearance {rule.TargetItems}";

    /// <summary>
    /// The class attributes with which an application puts a class into its XAF model, whatever the
    /// class derives from.
    /// </summary>
    /// <remarks>
    /// A base list cannot answer that on its own. A <c>[DomainComponent]</c> may declare no base at all,
    /// and a class may derive from anything DevExpress ships; the attribute is the application saying
    /// the class is a business class. On six real applications (#82) these were exactly the classes a
    /// base-list test missed, the one a staffing module is built on among them.
    /// </remarks>
    private static readonly string[] BusinessClassAttributes =
        ["DefaultClassOptions", "DomainComponent", "NavigationItem", "CreatableItem", "MapInheritance"];

    /// <summary>
    /// The classes of DevExpress's business class library that applications derive their own business
    /// classes from.
    /// </summary>
    /// <remarks>
    /// Names only, taken from the documentation (Built-in Business Classes and Interfaces, and the 25.2
    /// note removing the demo classes), because Core references no DevExpress assembly.
    /// <c>Person</c>, <c>Party</c>, <c>Organization</c>, <c>Address</c>, <c>Note</c> and <c>Task</c>
    /// left the library in 25.2: an application on an earlier version still derives from them, and one
    /// that upgraded copied them into its own source, where they are read like any other class.
    /// <para>
    /// A name is not an identity, so it loses to a class of the same name that the deriving file can
    /// see. C# binds that declaration, and so does this.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> LibraryBaseTypeNames = new(StringComparer.Ordinal)
    {
        // XPO and EF Core.
        "BaseObjectWithNotifyPropertyChanged", "DashboardData", "Event", "FileAttachment", "FileAttachmentBase",
        "FileData", "HCategory", "MediaDataObject", "MediaResourceObject", "ModelDifference",
        "ModelDifferenceAspect", "PermissionPolicyRole", "PermissionPolicyRoleBase", "ReportData",
        "ReportDataV2", "Resource",
        // The demo classes, removed in 25.2.
        "Address", "Analysis", "Note", "Organization", "Party", "Person", "PhoneNumber", "Task",
        // Non-persistent, from DevExpress.ExpressApp.
        "NonPersistentBaseObject", "NonPersistentEntityObject", "NonPersistentLiteObject", "NonPersistentObjectImpl",
    };

    /// <summary>The library's non-persistent bases. A class deriving from one stores nothing.</summary>
    private static readonly HashSet<string> NonPersistentBaseTypeNames = new(StringComparer.Ordinal)
    {
        "NonPersistentBaseObject", "NonPersistentEntityObject", "NonPersistentLiteObject", "NonPersistentObjectImpl",
    };

    private const string ReportParametersBase = "ReportParametersObjectBase";

    /// <summary>
    /// Whether the class carries an attribute that puts it in the application model.
    /// </summary>
    /// <remarks>
    /// A report's parameters dialog is a <c>[DomainComponent]</c> too, and is left out. The report
    /// extraction reads it as that report's parameters, criteria included; listed beside the business
    /// classes, it would say the application stores a class of that name.
    /// </remarks>
    private static bool DeclaresItselfABusinessClass(ClassDeclarationSyntax classDecl) =>
        BusinessClassAttributes.Any(attribute => HasAttribute(classDecl, attribute))
        && !GetBaseTypeNames(classDecl).Contains(ReportParametersBase);

    /// <summary>
    /// Whether the class derives from a class of the business class library, rather than from a class
    /// of the application's own that happens to share its name.
    /// </summary>
    private static bool DerivesFromTheLibrary(
        ClassDeclarationSyntax classDecl,
        HashSet<string> scopes,
        HashSet<(string Namespace, string Name)> declared)
    {
        foreach (var baseType in classDecl.BaseList?.Types ?? default)
        {
            var written = baseType.Type.ToString();

            var generic = written.IndexOf('<');
            if (generic > 0) written = written[..generic];

            var dot = written.LastIndexOf('.');
            var simpleName = dot < 0 ? written : written[(dot + 1)..];

            if (!LibraryBaseTypeNames.Contains(simpleName))
                continue;

            // The same resolution ResolveBase applies: unqualified, the name reaches a declaration in a
            // namespace the file can see; qualified, one whose namespace ends with the qualifier.
            var qualifier = dot < 0 ? null : written[..dot];
            var ownDeclaration = declared.Any(declaration =>
                declaration.Name.Equals(simpleName, StringComparison.Ordinal)
                && (qualifier is null
                    ? scopes.Contains(declaration.Namespace)
                    : declaration.Namespace.Equals(qualifier, StringComparison.Ordinal)
                      || declaration.Namespace.EndsWith("." + qualifier, StringComparison.Ordinal)));

            if (!ownDeclaration)
                return true;
        }

        return false;
    }

    private static bool IsXafBusinessObject(ClassDeclarationSyntax classDecl, string[] baseTypeNames)
    {
        if (classDecl.BaseList == null) return false;

        foreach (var baseType in classDecl.BaseList.Types)
        {
            var typeName = baseType.Type.ToString();
            // Check direct match or generic base (e.g., ViewController<T>)
            var simpleTypeName = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;

            if (baseTypeNames.Any(bt => simpleTypeName.Equals(bt, StringComparison.Ordinal)
                                        || simpleTypeName.EndsWith($".{bt}")))
                return true;
        }

        return false;
    }

    private static bool IsCollectionType(string typeName)
    {
        return typeName.StartsWith("XPCollection")
               || typeName.Contains("IList")
               || typeName.Contains("ICollection")
               || typeName.StartsWith("ObservableCollection")
               || typeName.StartsWith("List<");
    }

    private static string GetNamespace(ClassDeclarationSyntax classDecl)
    {
        var nsDecl = classDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        return nsDecl?.Name.ToString() ?? string.Empty;
    }

    private static string GetBaseTypeName(ClassDeclarationSyntax classDecl)
    {
        return classDecl.BaseList?.Types.FirstOrDefault()?.Type.ToString() ?? "object";
    }

    /// <summary>
    /// Every name in the class's base list, with namespaces and generic arguments removed.
    /// </summary>
    private static List<string> GetBaseTypeNames(ClassDeclarationSyntax classDecl)
    {
        var names = new List<string>();

        foreach (var baseType in classDecl.BaseList?.Types ?? default)
        {
            var name = baseType.Type.ToString();
            var generic = name.IndexOf('<');

            if (generic > 0)
                name = name[..generic];

            var lastDot = name.LastIndexOf('.');

            if (lastDot >= 0)
                name = name[(lastDot + 1)..];

            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }

        return names;
    }

    private static bool HasAttribute(MemberDeclarationSyntax member, string attributeName)
    {
        return member.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => MatchesAttributeName(a.Name.ToString(), attributeName));
    }

    private static string? GetAttributeStringArg(MemberDeclarationSyntax member, string attributeName)
    {
        var attr = member.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => MatchesAttributeName(a.Name.ToString(), attributeName));

        if (attr?.ArgumentList == null || attr.ArgumentList.Arguments.Count == 0)
            return null;

        return SyntaxLiteral.ValueOf(attr.ArgumentList.Arguments[0].Expression);
    }

    /// <summary>
    /// Reads one named argument of an attribute, e.g. the <c>Unique</c> of <c>[Indexed]</c>.
    /// </summary>
    private static string? GetAttributeStringArg(
        MemberDeclarationSyntax member,
        string attributeName,
        string argumentName)
    {
        var attr = member.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => MatchesAttributeName(a.Name.ToString(), attributeName));

        var argument = attr?.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.ToString() == argumentName);

        return argument is null ? null : SyntaxLiteral.ValueOf(argument.Expression);
    }

    private static string? GetModelDefaultValue(PropertyDeclarationSyntax prop, string propertyName)
    {
        var attrs = prop.AttributeLists
            .SelectMany(al => al.Attributes)
            .Where(a => MatchesAttributeName(a.Name.ToString(), "ModelDefault"));

        foreach (var attr in attrs)
        {
            if (attr.ArgumentList == null || attr.ArgumentList.Arguments.Count < 2) continue;

            var firstArg = SyntaxLiteral.ValueOf(attr.ArgumentList.Arguments[0].Expression);
            if (firstArg.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return SyntaxLiteral.ValueOf(attr.ArgumentList.Arguments[1].Expression);
            }
        }

        return null;
    }

    private static string? GetPropertyInitializer(PropertyDeclarationSyntax prop)
    {
        return prop.Initializer?.Value.ToString();
    }

    private static bool IsGetterOnly(PropertyDeclarationSyntax prop)
    {
        if (prop.AccessorList == null) return prop.ExpressionBody != null;
        return !prop.AccessorList.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration);
    }

    private static bool MatchesAttributeName(string fullName, string shortName)
    {
        var normalized = fullName.Replace("Attribute", "");
        return normalized.Equals(shortName, StringComparison.Ordinal)
               || normalized.EndsWith($".{shortName}");
    }

    /// <summary>
    /// The class a reference is to, without the nullable annotation: <c>Student?</c> is a
    /// <c>Student</c> that may be empty, not a different type. Trimmed again after, because
    /// <c>Student ?</c> is valid C# and the type is kept as written.
    /// </summary>
    private static string ReferencedClassName(string typeName) => typeName.Trim().TrimEnd('?').TrimEnd();

    /// <summary>A type as written, without namespace qualifiers, nullable annotations or spaces.</summary>
    private static string TypeSpelling(string? typeName) =>
        System.Text.RegularExpressions.Regex.Replace(typeName ?? string.Empty, @"(?:\w+(?:::|\.))+|\?|\s", string.Empty);

    private static string ExtractGenericArgument(string typeName)
    {
        var start = typeName.IndexOf('<');
        var end = typeName.LastIndexOf('>');
        if (start >= 0 && end > start)
            return typeName[(start + 1)..end].Trim();
        return typeName;
    }

    private static bool IsInfrastructureProperty(string propertyName)
    {
        return propertyName is
            // XPO infrastructure
            "Session" or "ClassInfo" or "This" or "Loading"
            or "IsLoading" or "IsDeleted" or "IsSaving" or "Oid"
            or "GCRecord" or "OptimisticLockField"
            // EF Core infrastructure
            or "ObjectSpace" or "ID";
    }

    private static bool IsCommonAttribute(string attrName)
    {
        return attrName is "Description" or "NavigationItem" or "XafDefaultProperty"
            or "DefaultProperty" or "DefaultClassOptions" or "Association"
            or "Aggregated" or "Size" or "VisibleInListView" or "VisibleInDetailView"
            or "XafDisplayName" or "ToolTip" or "EditorAlias" or "PersistentAlias"
            or "DataSourceCriteria" or "ImmediatePostData" or "Key" or "ModelDefault"
            or "NonPersistent" or "DomainComponent" or "RuleRequiredField"
            // EF Core / DataAnnotations attributes
            or "Required" or "StringLength" or "MaxLength" or "NotMapped"
            or "ForeignKey" or "Column" or "Table" or "InverseProperty";
    }

    private static List<string> ExtractComments(ClassDeclarationSyntax classDecl)
    {
        var comments = new List<string>();

        var trivia = classDecl.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                        || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                        || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

        foreach (var t in trivia)
        {
            comments.Add(t.ToString().Trim());
        }

        return comments;
    }

    /// <summary>
    /// Locates C# files from configured patterns, applying directory exclusions.
    /// </summary>
    private static IEnumerable<string> FindFiles(string sourceDirectory, string[] patterns, string[] excludePatterns)
    {
        var allFiles = new HashSet<string>();

        // What the project file keeps out of the build is not a class of the application, however
        // many attributes it carries. Read from this directory's own project file, so a referenced
        // project answers with its own.
        var removed = CompileExclusions.For(sourceDirectory);

        foreach (var pattern in patterns)
        {
            // Convert glob pattern to directory search
            var dir = sourceDirectory;
            var searchPattern = "*.cs";

            if (pattern.Contains("BusinessObjects"))
            {
                var boDir = Path.Combine(sourceDirectory, "BusinessObjects");
                if (Directory.Exists(boDir))
                    dir = boDir;
            }
            else if (pattern.Contains("Controllers"))
            {
                var ctrlDir = Path.Combine(sourceDirectory, "Controllers");
                if (Directory.Exists(ctrlDir))
                    dir = ctrlDir;
            }

            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories))
                {
                    // NOTE: the configured glob-like exclude patterns are still not evaluated;
                    // only build output is filtered. Applying them properly is tracked separately.
                    if (BuildOutputFilter.IsAnalyzable(file, dir) && !removed.Excludes(file))
                        allFiles.Add(file);
                }
            }
        }

        return allFiles;
    }

    #endregion
}

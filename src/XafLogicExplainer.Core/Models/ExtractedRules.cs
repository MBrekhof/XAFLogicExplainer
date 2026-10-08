namespace XafLogicExplainer.Core.Models;

/// <summary>
/// Represents one validation rule declared on an entity or property.
/// </summary>
public class ExtractedValidationRule
{
    /// <summary>
    /// Rule attribute type (for example RuleRequiredField).
    /// </summary>
    public string RuleType { get; set; } = string.Empty;

    /// <summary>
    /// The rule's identifier, when the attribute was given one.
    /// </summary>
    /// <remarks>
    /// It is how a rule is referred to everywhere outside the source — in the Model Editor, in a
    /// validation error a user reports, in the sentence a developer writes about it. It was read,
    /// but only into <see cref="Parameters"/> under the key <c>arg0</c>, which is where the
    /// renderers found it and printed it that way.
    /// </remarks>
    public string? Id { get; set; }

    /// <summary>
    /// The validation contexts the rule belongs to, as written.
    /// </summary>
    /// <remarks>
    /// Almost always <c>DefaultContexts.Save</c>. The interesting case is the other one: a rule
    /// declared in a context of the application's own fires only where that context is validated,
    /// so a reader who assumes every rule runs on save is wrong about it in the direction that
    /// matters — believing something is enforced when it is not.
    /// </remarks>
    public string? Contexts { get; set; }

    /// <summary>
    /// Target property name when rule is property-scoped.
    /// </summary>
    public string? TargetProperty { get; set; }

    /// <summary>
    /// User-facing validation message template when available.
    /// </summary>
    public string? MessageTemplate { get; set; }

    /// <summary>
    /// The criteria the rule enforces, for <c>RuleCriteria</c>.
    /// </summary>
    /// <remarks>
    /// Not the same thing as <see cref="TargetCriteria"/>, and conflating them loses the rule
    /// itself. <c>[RuleCriteria("Prescription_NotExpired", DefaultContexts.Save,
    /// "ExpiresOn &gt; IssuedOn")]</c> says what must be true; a target criteria says when the rule
    /// applies at all. Only the second was being read, so the expression a user actually hits was
    /// missing from an index that claimed to hold every one.
    /// </remarks>
    public string? Expression { get; set; }

    /// <summary>
    /// Rule criteria/context expression when present.
    /// </summary>
    public string? TargetCriteria { get; set; }

    /// <summary>
    /// Additional raw rule arguments captured as key/value pairs.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>
    /// The class that declared this rule, when it is not the entity listing it.
    /// </summary>
    /// <remarks>
    /// <c>null</c> for a rule the entity declares itself. A rule declared on a shared base is
    /// enforced when any descendant is saved, so it belongs under each of them; where it was
    /// written is what tells a reader that changing it changes the whole application.
    /// </remarks>
    public string? InheritedFrom { get; set; }

    /// <summary>A copy this rule's declarer does not share.</summary>
    public ExtractedValidationRule Clone()
    {
        var copy = (ExtractedValidationRule)MemberwiseClone();
        copy.Parameters = new Dictionary<string, string>(Parameters);
        return copy;
    }
}

/// <summary>
/// Represents one appearance customization rule declared on an entity.
/// </summary>
public class ExtractedAppearanceRule
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Target items expression.
    /// </summary>
    public string? TargetItems { get; set; }

    /// <summary>
    /// Which kind of UI element <see cref="TargetItems"/> names: <c>ViewItem</c>, <c>Action</c> or
    /// <c>LayoutItem</c>. Absent means the XAF default, which is <c>ViewItem</c>.
    /// </summary>
    /// <remarks>
    /// Without it every target reads as a field, so a rule disabling the <c>Delete</c> action was
    /// documented as governing a column called Delete — a confident sentence about a thing that does
    /// not exist. It is written two ways in real code: positionally as the enum, and by name as a
    /// string, which is the form the DevExpress examples use.
    /// </remarks>
    public string? AppearanceItemType { get; set; }

    /// <summary>
    /// Criteria expression controlling when the rule is active.
    /// </summary>
    public string? Criteria { get; set; }

    /// <summary>
    /// UI context where rule applies.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Visibility behavior value.
    /// </summary>
    public string? Visibility { get; set; }

    /// <summary>
    /// Enablement behavior value.
    /// </summary>
    public string? Enabled { get; set; }

    /// <summary>
    /// Background color override value.
    /// </summary>
    public string? BackColor { get; set; }

    /// <summary>
    /// Font color override value.
    /// </summary>
    public string? FontColor { get; set; }

    /// <summary>
    /// The class that declared this rule, when it is not the entity listing it.
    /// </summary>
    /// <remarks>
    /// <c>null</c> for a rule the entity declares itself. An appearance rule on a base greys the
    /// same field on every screen below it, which is a thing a reader of one screen has no other
    /// way to learn.
    /// </remarks>
    public string? InheritedFrom { get; set; }

    /// <summary>A copy this rule's declarer does not share.</summary>
    public ExtractedAppearanceRule Clone() => (ExtractedAppearanceRule)MemberwiseClone();
}

/// <summary>
/// Represents an inferred business rule synthesized from analyzed artifacts.
/// </summary>
public class ExtractedBusinessRule
{
    /// <summary>
    /// Human-readable rule text.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Rule category classification.
    /// </summary>
    public BusinessRuleCategory Category { get; set; }

    /// <summary>
    /// Source reference where the rule was inferred from.
    /// </summary>
    public string? SourceLocation { get; set; }

    /// <summary>
    /// Related entity name when applicable.
    /// </summary>
    public string? RelatedEntity { get; set; }

    /// <summary>
    /// Optional code excerpt associated with the inferred rule.
    /// </summary>
    public string? CodeSnippet { get; set; }
}

/// <summary>
/// Taxonomy for inferred business rule grouping.
/// </summary>
public enum BusinessRuleCategory
{
    /// <summary>
    /// Numeric or expression-based calculations.
    /// </summary>
    Calculation,

    /// <summary>
    /// Data validation and consistency checks.
    /// </summary>
    Validation,

    /// <summary>
    /// Explicit exclusion logic.
    /// </summary>
    Exclusion,

    /// <summary>
    /// Module or runtime configuration rules.
    /// </summary>
    Configuration,

    /// <summary>
    /// Workflow/state transition logic.
    /// </summary>
    Workflow,

    /// <summary>
    /// Mapping, normalization, and transformation logic.
    /// </summary>
    DataTransformation,

    /// <summary>
    /// Access and authorization constraints.
    /// </summary>
    AccessControl
}

/// <summary>
/// When the Object Space calls a business object's own code.
/// </summary>
public enum LifecycleTrigger
{
    /// <summary>The object is created: <c>OnCreated</c>, or <c>AfterConstruction</c> in XPO.</summary>
    Created,

    /// <summary>The object is loaded from the database: <c>OnLoaded</c>.</summary>
    Loaded,

    /// <summary>The object is saved or deleted: <c>OnSaving</c>.</summary>
    Saving,
}

/// <summary>
/// A method on a business class that the Object Space calls when an object is created, loaded or
/// saved — the place XAF documents for logic that belongs to the object rather than to a screen.
/// </summary>
/// <remarks>
/// Only what the class declares. Logic attached from outside it — a controller handling
/// <c>ObjectSpace.Committing</c>, a job — is not read here, so a class with no hook is not a class
/// nothing happens to on save.
/// </remarks>
public class ExtractedLifecycleHook
{
    /// <summary>When the method runs.</summary>
    public LifecycleTrigger Trigger { get; set; }

    /// <summary>The method as declared: <c>OnSaving</c>, <c>AfterConstruction</c>.</summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>Source file the method is declared in.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>One-based line of the method name, or zero when unknown.</summary>
    public int Line { get; set; }

    /// <summary>
    /// Properties of the class the method assigns, in the order written.
    /// </summary>
    /// <remarks>
    /// Assigned somewhere in the method, not necessarily every time it runs: a condition around an
    /// assignment is not read. An assignment inside a lambda or a local function is left out, because
    /// nothing proves it runs at all, and so is a name the method declares itself.
    /// </remarks>
    public List<string> AssignedProperties { get; set; } = [];

    /// <summary>Whether the method calls the same method on its base class.</summary>
    /// <remarks>
    /// It decides what a descendant inherits: an override that does not call <c>base</c> replaces
    /// the hook above it, which then never runs for that class.
    /// </remarks>
    public bool CallsBase { get; set; }

    /// <summary>
    /// The class that declared this hook, when it is not the entity listing it.
    /// </summary>
    public string? InheritedFrom { get; set; }

    /// <summary>The namespace of <see cref="InheritedFrom"/>, which tells it apart from a class of the same name.</summary>
    public string? InheritedFromNamespace { get; set; }

    internal bool IsOverride { get; set; }

    internal bool IsExplicitImplementation { get; set; }

    /// <summary>A <c>new virtual</c> namesake: not a hook, only evidence for the fold.</summary>
    internal bool IsNewSlot { get; set; }

    internal bool IsPublic { get; set; }

    internal bool HasBody { get; set; }

    /// <summary>A copy this hook's declarer does not share.</summary>
    public ExtractedLifecycleHook Clone()
    {
        var copy = (ExtractedLifecycleHook)MemberwiseClone();
        copy.AssignedProperties = [.. AssignedProperties];
        return copy;
    }
}

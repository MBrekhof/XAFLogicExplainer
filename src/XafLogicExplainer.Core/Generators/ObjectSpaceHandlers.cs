using XafLogicExplainer.Core.Analyzers;
using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Core.Generators;

/// <summary>
/// What every output says about Object Space handlers, so that they say the same thing.
/// </summary>
public static class ObjectSpaceHandlers
{
    /// <summary>
    /// The handlers that concern a class: those naming it or a class it derives from, and those of a
    /// controller targeting a type outside the application that the class is built on.
    /// </summary>
    /// <remarks>
    /// XAF matches a controller's target type by assignability, and an <c>is</c> test on a base class is
    /// true of its descendants, so a handler for <c>Vehicle</c> runs for a <c>Car</c> too.
    /// </remarks>
    public static IEnumerable<ExtractedObjectSpaceHandler> For(ExtractedProject project, ExtractedEntity entity)
    {
        var lineage = Lineage(entity, new EntityDirectory(project.Entities));
        var names = lineage.Select(FullName).ToHashSet(StringComparer.Ordinal);
        var builtOn = lineage.SelectMany(e => e.BaseTypes).ToHashSet(StringComparer.Ordinal);

        return project.ObjectSpaceHandlers.Where(h =>
            h.ControllerTargets.Concat(h.Checks).Any(names.Contains)
            || h.TargetOutsideApp is { } outside && builtOn.Contains(outside));
    }

    /// <summary>The handlers that name the class itself — once per class in a document that lists every class.</summary>
    public static IEnumerable<ExtractedObjectSpaceHandler> Naming(ExtractedProject project, ExtractedEntity entity)
    {
        var name = FullName(entity);
        return project.ObjectSpaceHandlers.Where(h => h.ControllerTargets.Contains(name) || h.Checks.Contains(name));
    }

    /// <summary>The types outside the application that controllers with handlers target, each with its handlers.</summary>
    public static IEnumerable<IGrouping<string, ExtractedObjectSpaceHandler>> ByOutsideTarget(ExtractedProject project) =>
        project.ObjectSpaceHandlers.Where(h => h.TargetOutsideApp is not null)
            .GroupBy(h => h.TargetOutsideApp!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

    /// <summary>Handlers that name no class at all: nothing in the source ties them to one.</summary>
    public static IEnumerable<ExtractedObjectSpaceHandler> Unattached(ExtractedProject project) =>
        project.ObjectSpaceHandlers.Where(h =>
            h.ControllerTargets.Count == 0 && h.Checks.Count == 0 && h.TargetOutsideApp is null);

    /// <summary>When a handler runs. <paramref name="l"/> picks the Spanish or the English words.</summary>
    public static string When(ObjectSpaceEvent ev, Func<string, string, string> l) => ev switch
    {
        ObjectSpaceEvent.Committing => l("antes de guardar", "before a save"),
        ObjectSpaceEvent.Committed => l("despues de guardar", "after a save"),
        ObjectSpaceEvent.ObjectChanged => l("cuando cambia un valor", "when a value changes"),
        ObjectSpaceEvent.ObjectSaving => l("antes de guardar un objeto", "before an object is saved"),
        ObjectSpaceEvent.ObjectSaved => l("despues de guardar un objeto", "after an object is saved"),
        ObjectSpaceEvent.ObjectDeleting => l("antes de borrar", "before objects are deleted"),
        _ => l("despues de borrar", "after objects are deleted"),
    };

    /// <summary>When a handler runs, in the words the English outputs use.</summary>
    public static string When(ObjectSpaceEvent ev) => When(ev, English);

    /// <summary>
    /// The handler and its evidence, without the when and the citation:
    /// <c>`OrderController.ObjectSpace_Committing`, controller for `Order`, checks `Order`</c>.
    /// </summary>
    public static string Describe(ExtractedProject project, ExtractedObjectSpaceHandler handler, Func<string, string, string> l)
    {
        var directory = new EntityDirectory(project.Entities);
        var parts = new List<string>
        {
            handler.Handler == "lambda"
                ? $"`{handler.DeclaringClass}` ({l("lambda", "lambda")})"
                : $"`{handler.DeclaringClass}.{handler.Handler}`",
        };

        parts.AddRange(handler.ControllerTargets.Select(t => $"{l("controlador de", "controller for")} `{Label(project, directory, t)}`"));
        if (handler.TargetOutsideApp is { } outside)
            parts.Add($"{l("controlador de toda clase basada en", "controller for every class built on")} `{outside}`");
        if (handler.Checks.Count > 0)
            parts.Add($"{l("comprueba", "checks")} {string.Join(", ", handler.Checks.Select(c => $"`{Label(project, directory, c)}`"))}");
        if (handler.ReceiverUnconfirmed)
            parts.Add(l("Object Space no confirmado en el codigo", "object space not confirmed from the source"));

        return string.Join(", ", parts);
    }

    /// <summary>English, for the outputs that have one language.</summary>
    public static string English(string spanish, string english) => english;

    /// <summary><c>Namespace.Class</c>, the form handlers name classes in.</summary>
    public static string FullName(ExtractedEntity entity) =>
        string.IsNullOrEmpty(entity.Namespace) ? entity.ClassName : $"{entity.Namespace}.{entity.ClassName}";

    private static string Label(ExtractedProject project, EntityDirectory directory, string fullName) =>
        project.Entities.FirstOrDefault(e => FullName(e) == fullName) is { } entity
            ? directory.Label(entity)
            : fullName;

    /// <summary>The class and every class of the application it derives from, nearest first.</summary>
    private static List<ExtractedEntity> Lineage(ExtractedEntity entity, EntityDirectory directory)
    {
        var lineage = new List<ExtractedEntity>();
        for (var current = entity; current is not null && !lineage.Contains(current);
             current = directory.Resolve(current.BaseType, current.Namespace))
            lineage.Add(current);
        return lineage;
    }
}

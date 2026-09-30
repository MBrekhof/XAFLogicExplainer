using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Core.Generators;

/// <summary>
/// What every output says about lifecycle hooks, so that they say the same thing.
/// </summary>
public static class LifecycleHooks
{
    /// <summary>
    /// Each hook once, under the class that declared it.
    /// </summary>
    /// <remarks>
    /// A class's own hooks, and the hooks of a base the application borrows from a referenced project.
    /// That base is not in the entity list, so its hooks reach the extraction only as copies inherited by
    /// the classes deriving from it, and an index of declared hooks would otherwise list them nowhere.
    /// A class is named by its bare name, or by its full name when another class, of the application or
    /// borrowed, has the same one.
    /// </remarks>
    /// <param name="project">The extracted project.</param>
    public static IEnumerable<(string ClassName, ExtractedLifecycleHook Hook)> DeclaredIn(ExtractedProject project)
    {
        // By where the hook is declared, not by the declarer's name, which an application class may share.
        var own = project.Entities.SelectMany(entity => entity.Lifecycle)
            .Where(hook => hook.InheritedFrom is null)
            .Select(hook => (hook.FilePath, hook.Line))
            .ToHashSet();
        var borrowed = new HashSet<(string, LifecycleTrigger, string, string, int)>();
        var declared = new List<(string Namespace, string ClassName, ExtractedLifecycleHook Hook)>();

        foreach (var entity in project.Entities)
        {
            foreach (var hook in entity.Lifecycle)
            {
                if (hook.InheritedFrom is null)
                    declared.Add((entity.Namespace, entity.ClassName, hook));
                else if (!own.Contains((hook.FilePath, hook.Line))
                         && borrowed.Add((hook.InheritedFrom, hook.Trigger, hook.MethodName, hook.FilePath, hook.Line)))
                    declared.Add((hook.InheritedFromNamespace ?? string.Empty, hook.InheritedFrom, hook));
            }
        }

        var shared = project.Entities.Select(entity => (entity.Namespace, entity.ClassName))
            .Concat(declared.Select(entry => (entry.Namespace, entry.ClassName)))
            .Distinct()
            .GroupBy(type => type.ClassName, StringComparer.Ordinal)
            .Where(sameName => sameName.Count() > 1)
            .Select(sameName => sameName.Key)
            .ToHashSet(StringComparer.Ordinal);

        return declared.Select(entry => (
            shared.Contains(entry.ClassName) && entry.Namespace.Length > 0 ? $"{entry.Namespace}.{entry.ClassName}" : entry.ClassName,
            entry.Hook));
    }

    /// <summary>When a hook runs, in the words the English outputs use.</summary>
    public static string When(LifecycleTrigger trigger) => trigger switch
    {
        LifecycleTrigger.Created => "when created",
        LifecycleTrigger.Loaded => "when loaded",
        _ => "when saved",
    };
}

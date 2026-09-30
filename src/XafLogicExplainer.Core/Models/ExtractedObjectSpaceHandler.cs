namespace XafLogicExplainer.Core.Models;

/// <summary>
/// The Object Space events that concern saving, changing and deleting objects — the ones
/// <c>IObjectSpace</c> declares for logic that runs around a save.
/// </summary>
public enum ObjectSpaceEvent
{
    /// <summary>Before the Object Space saves its changes.</summary>
    Committing,

    /// <summary>After the changes are stored.</summary>
    Committed,

    /// <summary>When an object is created, changed or deleted in the Object Space.</summary>
    ObjectChanged,

    /// <summary>Before one object is saved.</summary>
    ObjectSaving,

    /// <summary>After one object is saved.</summary>
    ObjectSaved,

    /// <summary>Before objects are deleted.</summary>
    ObjectDeleting,

    /// <summary>After objects are deleted.</summary>
    ObjectDeleted,
}

/// <summary>
/// A handler a controller or another class attaches to an Object Space event — the second place XAF
/// documents for save-time logic, beside the business class's own <c>OnSaving</c>.
/// </summary>
/// <remarks>
/// A business class is named on two kinds of evidence only: the declaring controller targets it, or
/// the handler tests an object for it (<c>is</c>, <c>as</c>, a cast, <c>OfType</c>). Being tested for
/// does not mean being changed. A name two classes share is not guessed.
/// </remarks>
public class ExtractedObjectSpaceHandler
{
    /// <summary>The event subscribed to.</summary>
    public ObjectSpaceEvent Event { get; set; }

    /// <summary>The class whose code subscribes.</summary>
    public string DeclaringClass { get; set; } = string.Empty;

    /// <summary>Namespace of <see cref="DeclaringClass"/>, as the controller inventory records it.</summary>
    public string DeclaringNamespace { get; set; } = string.Empty;

    /// <summary>Whether the declaring class is one of the application's controllers.</summary>
    public bool InController { get; set; }

    /// <summary>The handler method's name, or <c>lambda</c> for an inline handler.</summary>
    public string Handler { get; set; } = string.Empty;

    /// <summary>Source file of the subscription.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>One-based line of the <c>+=</c>.</summary>
    public int Line { get; set; }

    /// <summary>The project folder the file belongs to: the module or a platform project beside it.</summary>
    public string? SourceProject { get; set; }

    /// <summary>
    /// Whether the source does not show the subscribed object is an Object Space — a value returned by a
    /// method, say. Kept, because the event names are the Object Space's; marked, because they need not be.
    /// </summary>
    public bool ReceiverUnconfirmed { get; set; }

    /// <summary>Full names (<c>Namespace.Class</c>) of the business classes the declaring controller targets.</summary>
    public List<string> ControllerTargets { get; set; } = [];

    /// <summary>
    /// The type the declaring controller targets when it is none of the application's classes — an
    /// interface or a DevExpress base. XAF runs the controller for every class built on it.
    /// </summary>
    public string? TargetOutsideApp { get; set; }

    /// <summary>Full names of the business classes the handler, or a method of its class it calls, tests for.</summary>
    public List<string> Checks { get; set; } = [];
}

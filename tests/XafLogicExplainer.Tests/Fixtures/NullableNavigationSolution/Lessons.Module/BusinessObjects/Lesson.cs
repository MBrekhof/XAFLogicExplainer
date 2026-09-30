using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Lessons.Module.BusinessObjects;

[DefaultClassOptions]
public class Lesson : BaseObject
{
    public virtual DateTime StartsAt { get; set; }

    // May be empty until a student books the slot.
    public virtual Student? Student { get; set; }

    // Always set: the same kind of navigation, written without the annotation.
    public virtual Instructor Instructor { get; set; } = null!;
}

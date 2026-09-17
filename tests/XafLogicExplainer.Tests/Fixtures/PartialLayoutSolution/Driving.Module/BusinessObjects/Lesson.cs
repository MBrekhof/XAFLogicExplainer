using DevExpress.Persistent.Base;

namespace Driving.Module.BusinessObjects;

[DefaultClassOptions]
public partial class Lesson : AuditedObject
{
    public virtual DateTime StartsAt { get; set; }
}

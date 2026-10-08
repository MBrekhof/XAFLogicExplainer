using DevExpress.Persistent.BaseImpl.EF;

namespace Driving.Module.BusinessObjects;

// The application's own base.
public abstract class AuditedObject : BaseObject
{
    public virtual DateTime CreatedOn { get; set; }
}

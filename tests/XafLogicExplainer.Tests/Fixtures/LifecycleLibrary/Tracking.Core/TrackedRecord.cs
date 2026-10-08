using DevExpress.Persistent.BaseImpl.EF;

namespace Tracking.Core;

public abstract class TrackedRecord : BaseObject
{
    public virtual DateTime TrackedOn { get; set; }

    public override void OnSaving()
    {
        base.OnSaving();
        TrackedOn = DateTime.UtcNow;
    }
}

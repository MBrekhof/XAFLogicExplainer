using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Overrides BaseObject's method; the Object Space still calls the explicit implementation Part declares.
[DefaultClassOptions]
public class PartChild : Part
{
    public virtual int Extra { get; set; }

    public override void OnSaving()
    {
        Extra = 1;
    }
}

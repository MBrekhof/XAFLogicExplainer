using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Garage.Module.BusinessObjects.Archive;

// Shares its name with the library base Customer derives from, and is not that base.
[DefaultClassOptions]
public class TrackedRecord : BaseObject
{
    public virtual string Shelf { get; set; }

    public override void OnSaving()
    {
        base.OnSaving();
        Shelf = "A1";
    }
}

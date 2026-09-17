using DevExpress.ExpressApp;
using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Names the interface again, so its override of the base's new method is what the Object Space calls.
[DefaultClassOptions]
public class Diary : LedgerBase, IXafEntityObject
{
    public virtual int Pages { get; set; }

    public override void OnSaving()
    {
        Pages = 1;
    }
}

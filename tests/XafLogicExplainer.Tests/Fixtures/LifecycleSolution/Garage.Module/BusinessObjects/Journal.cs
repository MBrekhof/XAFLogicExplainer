using DevExpress.ExpressApp;
using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Names the interface again, so its new method is what the Object Space calls, and the override below is that method.
public abstract class JournalBase : AuditedObject, IXafEntityObject
{
    public virtual int Entries { get; set; }

    public new virtual void OnSaving()
    {
        Entries = 0;
    }
}

[DefaultClassOptions]
public class Journal : JournalBase
{
    public virtual int Lines { get; set; }

    public override void OnSaving()
    {
        Lines = 1;
    }
}

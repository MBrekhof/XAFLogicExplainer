using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Starts a method of its own that shares the hook's name; the Object Space still calls the audit stamp.
public abstract class LedgerBase : AuditedObject
{
    public virtual int Entries { get; set; }

    public new virtual void OnSaving()
    {
        Entries = 0;
    }
}

// Overrides the base's new method, not the hook.
[DefaultClassOptions]
public class Ledger : LedgerBase
{
    public virtual int Pages { get; set; }

    public override void OnSaving()
    {
        Pages = 1;
    }
}

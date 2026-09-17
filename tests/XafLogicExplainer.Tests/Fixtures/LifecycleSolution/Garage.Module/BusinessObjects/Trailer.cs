using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Calls only a helper that shares the hook's name: the audit stamp does not run for a trailer.
[DefaultClassOptions]
public class Trailer : AuditedObject
{
    public virtual int Hitches { get; set; }

    public override void OnSaving()
    {
        base.OnSaving(false);
        Hitches = 1;
    }
}

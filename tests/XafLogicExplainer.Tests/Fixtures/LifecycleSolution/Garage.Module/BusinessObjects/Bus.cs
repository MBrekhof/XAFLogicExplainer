using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Calls base only from a lambda that is never shown to run: the audit stamp is not proven to run for a bus.
[DefaultClassOptions]
public class Bus : AuditedObject
{
    public virtual int Doors { get; set; }

    public override void OnSaving()
    {
        Action later = () => base.OnSaving();
        Doors = 2;
    }
}

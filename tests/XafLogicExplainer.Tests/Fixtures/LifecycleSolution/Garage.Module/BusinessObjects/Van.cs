using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Overrides the save hook and calls base: both run.
[DefaultClassOptions]
public class Van : AuditedObject
{
    private DateTime lastSaved;

    public virtual int Seats { get; set; }

    public override void OnSaving()
    {
        base.OnSaving();
        Seats = 3;
        lastSaved = DateTime.Now;
    }
}

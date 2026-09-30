using DevExpress.Persistent.BaseImpl.EF;

namespace Garage.Module.BusinessObjects;

// The application's audit base: every save stamps who and when.
public abstract class AuditedObject : BaseObject
{
    public virtual DateTime ChangedOn { get; set; }

    public virtual string ChangedBy { get; set; }

    public override void OnSaving()
    {
        base.OnSaving();
        ChangedOn = DateTime.Now;
        SetPropertyValueWithSecurityBypass(nameof(ChangedBy), "system");
    }

    // A helper that shares the hook's name; calling it does not run the hook.
    protected void OnSaving(bool quiet) { }
}

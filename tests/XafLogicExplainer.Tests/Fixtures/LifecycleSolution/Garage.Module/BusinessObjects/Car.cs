using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Declares no hook of its own: the audit base's runs.
[DefaultClassOptions]
public class Car : AuditedObject
{
    public virtual string Plate { get; set; }
}

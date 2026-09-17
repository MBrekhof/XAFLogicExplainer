using DevExpress.Persistent.Base;

namespace Driving.Module.BusinessObjects;

[DefaultClassOptions]
public partial class Vehicle : DevExpress.Persistent.BaseImpl.EF.BaseObject
{
    public virtual string Plate { get; set; } = string.Empty;
}

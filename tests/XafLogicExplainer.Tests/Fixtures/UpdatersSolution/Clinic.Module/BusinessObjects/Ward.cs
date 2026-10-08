using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Clinic.Module.BusinessObjects;

[DefaultClassOptions]
public class Ward : BaseObject
{
    public virtual string Name { get; set; }
}

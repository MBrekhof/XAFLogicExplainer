using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Bookings.Module.BusinessObjects;

[DefaultClassOptions]
public class Student : BaseObject
{
    public virtual string Name { get; set; } = string.Empty;
}

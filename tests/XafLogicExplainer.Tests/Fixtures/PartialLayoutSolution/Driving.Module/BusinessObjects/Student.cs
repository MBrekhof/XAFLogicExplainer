using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Driving.Module.BusinessObjects;

[DefaultClassOptions]
[NavigationItem("Students")]
public partial class Student : BaseObject
{
    public virtual string Name { get; set; } = string.Empty;
}

using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Driving.Module.BusinessObjects;

[DefaultClassOptions]
public partial class Instructor : BaseObject
{
    public virtual string Name { get; set; } = string.Empty;
}

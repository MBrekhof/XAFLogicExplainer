using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Garage.Module.BusinessObjects;

[DefaultClassOptions]
public partial class Invoice : BaseObject
{
    public virtual decimal Total { get; set; }
}

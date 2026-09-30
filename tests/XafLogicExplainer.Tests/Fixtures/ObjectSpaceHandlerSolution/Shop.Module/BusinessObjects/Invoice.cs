using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Shop.Module.BusinessObjects;

[DefaultClassOptions]
public class Invoice : BaseObject
{
    public virtual decimal Total { get; set; }
}

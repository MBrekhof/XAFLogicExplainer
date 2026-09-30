using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Shop.Module.BusinessObjects;

public interface IAudited
{
    DateTime ChangedOn { get; set; }
}

[DefaultClassOptions]
public class Order : BaseObject, IAudited
{
    public virtual string Number { get; set; }

    public virtual DateTime ChangedOn { get; set; }

    public void Stamp() => ChangedOn = DateTime.Now;
}

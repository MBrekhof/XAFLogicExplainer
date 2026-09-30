using DevExpress.Persistent.BaseImpl.EF;

namespace Shop.Module.BusinessObjects.A;

public class Note : BaseObject
{
    public virtual string Text { get; set; }
}

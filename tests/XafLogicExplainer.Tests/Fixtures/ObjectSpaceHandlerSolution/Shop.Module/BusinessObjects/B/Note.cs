using DevExpress.Persistent.BaseImpl.EF;

namespace Shop.Module.BusinessObjects.B;

public class Note : BaseObject
{
    public virtual string Body { get; set; }
}

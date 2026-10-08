using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Lessons.Module.BusinessObjects;

[DefaultClassOptions]
public class Instructor : BaseObject
{
    public virtual string Name { get; set; } = string.Empty;
}

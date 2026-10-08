using System.Collections.ObjectModel;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Lessons.Module.BusinessObjects;

[DefaultClassOptions]
public class Student : BaseObject
{
    public virtual string Name { get; set; } = string.Empty;

    public virtual IList<Lesson> Lessons { get; set; } = new ObservableCollection<Lesson>();
}

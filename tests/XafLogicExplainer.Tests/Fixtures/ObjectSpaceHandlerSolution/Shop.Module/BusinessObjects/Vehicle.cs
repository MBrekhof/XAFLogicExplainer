using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Shop.Module.BusinessObjects;

[DefaultClassOptions]
public class Vehicle : BaseObject
{
    public virtual string Plate { get; set; }
}

[DefaultClassOptions]
public class Car : Vehicle
{
    public virtual int Seats { get; set; }
}

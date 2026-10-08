using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Garage.Module.BusinessObjects;

[DefaultClassOptions]
public class Vehicle : BaseObject
{
    public virtual string Status { get; set; }

    public virtual int Mileage { get; set; }

    public virtual string Color { get; set; }

    public virtual string Nickname { get; set; }

    public override void OnCreated()
    {
        base.OnCreated();
        Status = "New";

        // A local that shares a property's name is the local.
        int Mileage = 0;
        Mileage = 5;

        // Set only if the lambda is ever invoked.
        Action paint = () => Color = "Red";

        // The initializer sets the new object's property, not this one's, and so does a setter called on it.
        var copy = new Vehicle { Nickname = "Spare" };
        copy.SetPropertyValueWithSecurityBypass(nameof(Nickname), "Spare");
    }

    public override void OnLoaded()
    {
        base.OnLoaded();
    }
}

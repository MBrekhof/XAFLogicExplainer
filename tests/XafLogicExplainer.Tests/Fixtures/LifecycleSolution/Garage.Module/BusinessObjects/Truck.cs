using DevExpress.Persistent.Base;

namespace Garage.Module.BusinessObjects;

// Overrides the save hook without calling base: the audit stamp never runs for a truck.
[DefaultClassOptions]
public class Truck : AuditedObject
{
    public virtual int Axles { get; set; }

    public virtual string Cab { get; set; }

    public virtual int Trips { get; set; }

    public virtual int Gear { get; set; }

    public override void OnSaving()
    {
        // A local that shares a property's name does not hide the property named through this.
        var Axles = 2;
        this.Axles = Axles;

        // A lambda's parameter is the lambda's; outside it the name is the property.
        Action<string> paint = Cab => { };
        Cab = "Day";

        // Inside the block the name is the local; after it, the property again.
        {
            int Trips = 0;
            Trips++;
        }
        Trips++;

        // A local declared in one switch section is in scope in the others.
        switch (Axles)
        {
            case 0:
                int Gear = 0;
                break;
            default:
                Gear = 1;
                break;
        }
    }
}

using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Garage.Module.BusinessObjects;

// A public method that only shares the name: it hides BaseObject's, and the Object Space never calls it.
[DefaultClassOptions]
public class Wheel : BaseObject
{
    public virtual int Spokes { get; set; }

    public new void OnSaving()
    {
        Spokes = 32;
    }
}

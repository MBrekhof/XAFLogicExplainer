using DevExpress.ExpressApp;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Garage.Module.BusinessObjects;

[DefaultClassOptions]
public class Part : BaseObject, IXafEntityObject
{
    public virtual int Stock { get; set; }

    void IXafEntityObject.OnSaving()
    {
        Stock = 0;
    }

    // Overrides BaseObject's method, which the Object Space no longer calls: it calls the explicit implementation.
    public override void OnSaving()
    {
        Stock = 5;
    }

    // Returns a value: not the interface's method.
    public new int OnLoaded() => Stock;
}

using System.ComponentModel.DataAnnotations;
using DevExpress.ExpressApp;

namespace Garage.Module.BusinessObjects;

// Not built on BaseObject: registered by the context, and implementing the interface with public methods.
public class Tyre : IXafEntityObject
{
    [Key]
    public virtual Guid ID { get; set; }

    public virtual int Size { get; set; }

    public virtual void OnCreated() { }

    public virtual void OnLoaded() { }

    public virtual void OnSaving()
    {
        Size = 16;
    }

    // Only share the name: an overload, a generic method and XPO's create hook are not the interface's methods.
    public void OnSaving(int reason) { Size = 1; }

    public void OnSaving<T>() { Size = 2; }

    public void AfterConstruction() { Size = 3; }
}

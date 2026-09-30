using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using Shop.Module.BusinessObjects;

namespace Shop.Module.Controllers;

// An action's own target is not the controller's.
public class ActionTargetController : ViewController
{
    public ActionTargetController()
    {
        var approve = new SimpleAction(this, "Approve", "View") { TargetObjectType = typeof(Invoice) };
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        ObjectSpace.Committing += Validate;
    }

    private void Validate(object sender, EventArgs e) { }
}

// The constructor narrows what the generic argument says.
public class CarOnlyController : ObjectViewController<DetailView, Vehicle>
{
    public CarOnlyController()
    {
        TargetObjectType = typeof(Car);
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        ObjectSpace.ObjectChanged += Changed;
    }

    private void Changed(object sender, ObjectChangedEventArgs e) { }
}

// One handler on a popup's Object Space first, then on the controller's own.
public class ReusedHandlerController : ObjectViewController<DetailView, Order>
{
    protected override void OnActivated()
    {
        base.OnActivated();
        var popupSpace = Application.CreateObjectSpace(typeof(Invoice));
        popupSpace.Committing += Validate;
        ObjectSpace.Committing += Validate;
    }

    private void Validate(object sender, EventArgs e) { }
}

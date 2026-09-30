using System.ComponentModel;
using DevExpress.ExpressApp;
using Shop.Module.BusinessObjects;

namespace Shop.Module.Controllers;

public class OrderController : ObjectViewController<DetailView, Order>
{
    protected override void OnActivated()
    {
        base.OnActivated();
        ObjectSpace.Committing += ObjectSpace_Committing;
        ObjectSpace.ObjectChanged += (s, e) =>
        {
            if (e.Object is not Invoice inv) return;
            inv.Total = 0;
        };
    }

    protected override void OnDeactivated()
    {
        ObjectSpace.Committing -= ObjectSpace_Committing;
        base.OnDeactivated();
    }

    // Unsubscribes while it saves, then subscribes again: still one handler.
    private void Resubscribe()
    {
        ObjectSpace.Committing -= ObjectSpace_Committing;
        try { ObjectSpace.CommitChanges(); }
        finally { ObjectSpace.Committing += ObjectSpace_Committing; }
    }

    private void ObjectSpace_Committing(object sender, CancelEventArgs e)
    {
        foreach (var order in ObjectSpace.ModifiedObjects.OfType<Order>())
            order.Stamp();
    }

    // A popup's own Object Space: what this controller targets says nothing about it.
    private void ShowPopup()
    {
        var popupSpace = Application.CreateObjectSpace(typeof(Invoice));
        popupSpace.ObjectDeleted += (s, e) => { };
    }
}

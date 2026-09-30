using DevExpress.ExpressApp;
using Shop.Module.BusinessObjects;

namespace Shop.Module.Controllers;

// Targets an interface: runs for every class that implements it.
public class AuditController : ObjectViewController<DetailView, IAudited>
{
    protected override void OnActivated()
    {
        base.OnActivated();
        ObjectSpace.ObjectSaved += ObjectSpace_ObjectSaved;
    }

    private void ObjectSpace_ObjectSaved(object sender, ObjectManipulatingEventArgs e) { }
}

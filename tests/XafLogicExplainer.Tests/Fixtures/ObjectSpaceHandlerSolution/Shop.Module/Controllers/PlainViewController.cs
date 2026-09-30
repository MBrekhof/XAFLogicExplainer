using DevExpress.ExpressApp;

namespace Shop.Module.Controllers;

// Targets nothing and checks nothing: the handler is reported without a business class.
public class PlainViewController : ViewController
{
    protected override void OnActivated()
    {
        base.OnActivated();
        ObjectSpace.ObjectDeleting += OnDeleting;
    }

    private void OnDeleting(object sender, ObjectsManipulatingEventArgs e)
    {
        Tracing.Tracer.LogText("deleting");
    }
}

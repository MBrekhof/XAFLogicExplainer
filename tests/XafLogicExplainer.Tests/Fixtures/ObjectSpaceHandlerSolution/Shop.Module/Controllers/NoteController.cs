using DevExpress.ExpressApp;
using Shop.Module.BusinessObjects.A;

namespace Shop.Module.Controllers;

// `Note` is two classes (A and B); this namespace encloses neither, so the check names no class.
public class NoteController : ViewController
{
    protected override void OnActivated()
    {
        base.OnActivated();
        View.ObjectSpace.Committed += (s, e) =>
        {
            foreach (var changed in ((IObjectSpace)s).ModifiedObjects)
                if (changed is Note note) { }
        };
    }
}

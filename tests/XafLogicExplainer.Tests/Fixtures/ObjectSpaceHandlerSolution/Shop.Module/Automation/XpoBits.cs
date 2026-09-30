using DevExpress.ExpressApp;
using DevExpress.Xpo;
using Shop.Module.BusinessObjects;

namespace Shop.Module.Automation;

// Receivers of every kind: an XPO session, one the source does not type, an unsubscription, a cast object space.
public class XpoBits
{
    // An XPO Session's ObjectSaving is not the Object Space's event.
    public void A(Session session)
    {
        session.ObjectSaving += (s, e) => { };
    }

    // The source does not show what GetThing returns: reported, but not confirmed as an Object Space.
    public void B()
    {
        GetThing().Committing += (s, e) => { if (e is Order) { } };
    }

    // An unsubscription alone attaches nothing.
    public void C(IObjectSpace os)
    {
        os.Committing -= Nothing;
    }

    public void D(XafApplication application)
    {
        var os = (NonPersistentObjectSpace)application.CreateObjectSpace(typeof(Invoice));
        os.ObjectChanged += Changed;
    }

    private void Changed(object sender, ObjectChangedEventArgs e)
    {
        if (e.Object is Invoice) { }
    }

    private void Nothing(object sender, EventArgs e) { }

    private dynamic GetThing() => null;
}

using DevExpress.ExpressApp;

// Controllers in the namespace of one Note that target the other Note by its full name.
namespace Shop.Module.BusinessObjects.A
{
    public class NoteByGenericController : ObjectViewController<DetailView, global::Shop.Module.BusinessObjects.B.Note>
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            ObjectSpace.Committed += (s, e) => { };
        }
    }

    public class NoteByTypeofController : ViewController
    {
        public NoteByTypeofController()
        {
            TargetObjectType = typeof(global::Shop.Module.BusinessObjects.B.Note);
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            ObjectSpace.Committed += (s, e) => { };
        }
    }

    // Inherits its target, which the inventory keeps only as the bare name `Note`: shared, so not guessed.
    public class InheritedNoteController : global::Shop.Module.BusinessObjects.B.NoteBaseController
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            ObjectSpace.Committing += (s, e) => { };
        }
    }
}

namespace Shop.Module.BusinessObjects.B
{
    public class NoteBaseController : ObjectViewController<DetailView, global::Shop.Module.BusinessObjects.B.Note>
    {
    }
}

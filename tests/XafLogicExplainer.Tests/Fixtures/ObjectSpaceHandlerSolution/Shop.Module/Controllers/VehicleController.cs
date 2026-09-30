using DevExpress.ExpressApp;
using Shop.Module.BusinessObjects;

namespace Shop.Module
{
    namespace Controllers
    {
        // Targets Vehicle, so it also runs for a Car. Declared in a nested namespace block.
        public class VehicleController : ObjectViewController<ListView, Vehicle>
        {
            protected override void OnActivated()
            {
                base.OnActivated();
                ObjectSpace.ObjectSaving += (s, e) => { };
            }
        }
    }
}

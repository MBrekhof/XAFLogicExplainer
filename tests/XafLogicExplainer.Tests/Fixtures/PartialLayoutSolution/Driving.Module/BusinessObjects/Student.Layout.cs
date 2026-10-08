using DevExpress.ExpressApp.Model;

namespace Driving.Module.BusinessObjects;

// The layout half: no base class, one interface.
public partial class Student : ISupportViewLayoutCustomization
{
    public void CustomizeLayout(IModelView view) { }
}

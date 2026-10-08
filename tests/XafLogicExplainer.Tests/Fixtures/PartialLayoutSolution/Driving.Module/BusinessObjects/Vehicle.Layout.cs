using DevExpress.ExpressApp.Model;
using DevExpress.Persistent.Base;

namespace Driving.Module.BusinessObjects;

// The layout half, here also carrying what the other half does not.
[NavigationItem("Fleet")]
public partial class Vehicle : ISupportViewLayoutCustomization
{
    public virtual string? Nickname { get; set; }

    public void CustomizeLayout(IModelView view) { }
}

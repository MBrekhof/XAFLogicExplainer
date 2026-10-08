using DevExpress.Persistent.Base;
using DevExpress.Xpo;

namespace Depot.Module.BusinessObjects;

[DefaultClassOptions]
public class Crate : XPObject
{
    public Crate(Session session) : base(session) { }

    private string _label;

    public string Label
    {
        get => _label;
        set => SetPropertyValue(nameof(Label), ref _label, value);
    }

    public override void AfterConstruction()
    {
        base.AfterConstruction();
        Label = "new";
    }
}

using DevExpress.Persistent.Base;
using DevExpress.Xpo;

namespace Rentals.Module.BusinessObjects;

[DefaultClassOptions]
public class Customer : XPObject
{
    public Customer(Session session) : base(session) { }

    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set => SetPropertyValue(nameof(Name), ref _name, value);
    }

    [Association("Customer-Rentals")]
    public XPCollection<Rental> Rentals => GetCollection<Rental>(nameof(Rentals));
}

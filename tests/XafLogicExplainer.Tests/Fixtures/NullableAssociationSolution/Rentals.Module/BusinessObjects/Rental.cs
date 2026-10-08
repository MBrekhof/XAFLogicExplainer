using DevExpress.Persistent.Base;
using DevExpress.Xpo;

namespace Rentals.Module.BusinessObjects;

[DefaultClassOptions]
public class Rental : XPObject
{
    public Rental(Session session) : base(session) { }

    private Customer? _customer;

    [Association("Customer-Rentals")]
    public Customer? Customer
    {
        get => _customer;
        set => SetPropertyValue(nameof(Customer), ref _customer, value);
    }
}

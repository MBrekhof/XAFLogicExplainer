namespace Garage.Module.BusinessObjects;

public partial class Invoice
{
    public override void OnSaving()
    {
        base.OnSaving();
        Total = 0;
    }
}

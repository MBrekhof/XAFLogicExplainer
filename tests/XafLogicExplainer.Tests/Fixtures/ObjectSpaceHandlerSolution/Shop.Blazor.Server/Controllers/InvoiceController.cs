using DevExpress.ExpressApp;
using Shop.Module.BusinessObjects;

namespace Shop.Blazor.Server.Controllers;

public class InvoiceController : ObjectViewController<DetailView, Invoice>
{
    protected override void OnActivated()
    {
        base.OnActivated();
        View.ObjectSpace.Committed += Committed;
    }

    private void Committed(object sender, EventArgs e) { }
}

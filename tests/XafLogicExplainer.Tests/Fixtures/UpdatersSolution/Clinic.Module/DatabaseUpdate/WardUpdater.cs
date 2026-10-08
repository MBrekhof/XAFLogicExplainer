using Clinic.Module.BusinessObjects;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Updating;

namespace Clinic.Module.DatabaseUpdate;

public class WardUpdater : ModuleUpdater
{
    public WardUpdater(IObjectSpace objectSpace, Version currentDBVersion) : base(objectSpace, currentDBVersion) { }

    public override void UpdateDatabaseAfterUpdateSchema()
    {
        base.UpdateDatabaseAfterUpdateSchema();
        CreateDefaultRole();
        ObjectSpace.CommitChanges();
    }

    // Named like the template's method, and about something else entirely.
    private void CreateDefaultRole()
    {
        var ward = ObjectSpace.CreateObject<Ward>();
        ward.Name = "Emergency";
    }
}

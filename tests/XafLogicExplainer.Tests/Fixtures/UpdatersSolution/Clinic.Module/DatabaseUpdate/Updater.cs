using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;

namespace Clinic.Module.DatabaseUpdate;

// The one the project template creates.
public class Updater : ModuleUpdater
{
    public Updater(IObjectSpace objectSpace, Version currentDBVersion) : base(objectSpace, currentDBVersion) { }

    public override void UpdateDatabaseAfterUpdateSchema()
    {
        base.UpdateDatabaseAfterUpdateSchema();
        CreateDefaultRole();
        ObjectSpace.CommitChanges();
    }

    private void CreateDefaultRole()
    {
        var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
        role.Name = "Default";
    }
}

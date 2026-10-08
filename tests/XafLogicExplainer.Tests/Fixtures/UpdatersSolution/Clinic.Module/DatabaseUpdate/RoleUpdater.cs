using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;

namespace Clinic.Module.DatabaseUpdate;

// The roles the clinic works with, kept apart from the template's.
public class RoleUpdater : ModuleUpdater
{
    public RoleUpdater(IObjectSpace objectSpace, Version currentDBVersion) : base(objectSpace, currentDBVersion) { }

    public override void UpdateDatabaseAfterUpdateSchema()
    {
        base.UpdateDatabaseAfterUpdateSchema();

        // Nurses were administrators until 2.0.
        if (CurrentDBVersion < new Version("2.0.0.0"))
            DemoteNurses();

        SeedNurseRole();
        ObjectSpace.CommitChanges();
    }

    private void SeedNurseRole()
    {
        var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
        role.Name = "Nurse";
    }

    private void DemoteNurses() { }
}

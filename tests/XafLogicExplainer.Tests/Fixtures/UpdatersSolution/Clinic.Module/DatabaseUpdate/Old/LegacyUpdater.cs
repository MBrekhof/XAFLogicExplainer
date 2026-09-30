using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;

namespace Clinic.Module.DatabaseUpdate;

// Removed from the build; kept for reference.
public class LegacyUpdater : ModuleUpdater
{
    public LegacyUpdater(IObjectSpace objectSpace, Version currentDBVersion) : base(objectSpace, currentDBVersion) { }

    public override void UpdateDatabaseAfterUpdateSchema() => SeedSurgeonRole();

    private void SeedSurgeonRole()
    {
        var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
        role.Name = "Surgeon";
    }
}

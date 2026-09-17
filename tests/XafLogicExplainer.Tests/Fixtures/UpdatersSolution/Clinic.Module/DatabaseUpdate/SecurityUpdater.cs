using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;

namespace Clinic.Module.DatabaseUpdate;

// Roles created where they are checked for, the way a code generator writes them: one block each,
// the same variable name in every block.
public class SecurityUpdater : ModuleUpdater
{
    public SecurityUpdater(IObjectSpace objectSpace, Version currentDBVersion) : base(objectSpace, currentDBVersion) { }

    public override void UpdateDatabaseAfterUpdateSchema()
    {
        base.UpdateDatabaseAfterUpdateSchema();

        if (ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Guest") == null)
        {
            var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
            role.Name = "Guest";
        }

        if (ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Admin") == null)
        {
            var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
            role.Name = "Admin";
            role.IsAdministrative = true;
        }

        SeedPharmacistRole();
        ObjectSpace.CommitChanges();
    }

    // Looked up first, created only when missing, named after the block.
    private void SeedPharmacistRole()
    {
        var role = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Pharmacist");
        if (role == null)
        {
            role = ObjectSpace.CreateObject<PermissionPolicyRole>();
        }

        role.Name = "Pharmacist";
    }
}

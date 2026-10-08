using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Updating;

namespace Clinic.Module;

public sealed class ClinicModule : ModuleBase
{
    public override IEnumerable<ModuleUpdater> GetModuleUpdaters(IObjectSpace objectSpace, Version versionFromDB) =>
    [
        new DatabaseUpdate.Updater(objectSpace, versionFromDB),
        new DatabaseUpdate.RoleUpdater(objectSpace, versionFromDB),
        new DatabaseUpdate.WardUpdater(objectSpace, versionFromDB),
        new DatabaseUpdate.RoomUpdater(objectSpace, versionFromDB),
        new DatabaseUpdate.ShiftUpdater(objectSpace, versionFromDB),
        new DatabaseUpdate.SecurityUpdater(objectSpace, versionFromDB),
    ];
}

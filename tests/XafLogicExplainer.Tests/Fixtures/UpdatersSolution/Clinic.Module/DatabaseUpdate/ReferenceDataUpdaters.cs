using Clinic.Module.BusinessObjects;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Updating;

namespace Clinic.Module.DatabaseUpdate;

// Two updaters in one file, the first with its base written in full.
public class RoomUpdater : DevExpress.ExpressApp.Updating.ModuleUpdater
{
    public RoomUpdater(IObjectSpace objectSpace, Version currentDBVersion) : base(objectSpace, currentDBVersion) { }

    public override void UpdateDatabaseAfterUpdateSchema() => SeedRooms();

    private void SeedRooms()
    {
        var room = ObjectSpace.CreateObject<Ward>();
        room.Name = "Room 101";
    }
}

public class ShiftUpdater : ModuleUpdater
{
    public ShiftUpdater(IObjectSpace objectSpace, Version currentDBVersion) : base(objectSpace, currentDBVersion) { }

    public override void UpdateDatabaseAfterUpdateSchema() => SeedShifts();

    private void SeedShifts()
    {
        var ward = ObjectSpace.CreateObject<Ward>();
        ward.Name = "Night";
    }
}

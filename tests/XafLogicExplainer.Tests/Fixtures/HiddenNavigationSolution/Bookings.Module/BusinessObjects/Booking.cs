using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace Bookings.Module.BusinessObjects;

[DefaultClassOptions]
public class Booking : BaseObject
{
    public virtual Student Student { get; set; } = null!;
}

// Keeps only the student's name once the booking is archived: the navigation is hidden.
public class ArchivedBooking : Booking
{
    public new string Student { get; set; } = string.Empty;
}

// Still a booking of a student: the navigation is overridden, not replaced — the type spelled in full.
public class TrialBooking : Booking
{
    public override global::Bookings.Module.BusinessObjects.Student Student { get; set; } = null!;
}

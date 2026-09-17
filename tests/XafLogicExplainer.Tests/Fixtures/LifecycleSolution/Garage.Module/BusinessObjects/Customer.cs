using DevExpress.Persistent.Base;
using Tracking.Core;

namespace Garage.Module.BusinessObjects;

// Its save hook is declared in a library this project references.
[DefaultClassOptions]
public class Customer : TrackedRecord
{
    public virtual string Name { get; set; }
}

using Microsoft.EntityFrameworkCore;

namespace Garage.Module.BusinessObjects;

public class GarageDbContext : DbContext
{
    public DbSet<Tyre> Tyres { get; set; }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LocalMessaging.Infrastructure;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MessagingDbContext>()
        .UseSqlite("Data Source=design-time.db;Foreign Keys=True;Cache=Private")
        .Options);
}

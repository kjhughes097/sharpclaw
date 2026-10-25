using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SharpClaw.Gateway.Data;

/// <summary>Used by the EF tools (migrations) at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SharpClawDbContext>
{
    public SharpClawDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseSqlite("Data Source=sharpclaw-design.db")
            .Options;
        return new SharpClawDbContext(options);
    }
}

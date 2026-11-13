using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace backend.tests;

internal static class TestSchema
{
    public static async Task CreateAsync(DatabaseFacade database)
    {
        await database.EnsureCreatedAsync();
        await database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "042_NewestOdds.sql")));
    }
}

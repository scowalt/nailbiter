namespace backend.tests;

internal sealed class TestDatabase(string connectionString, Func<ValueTask> release) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public static async Task<TestDatabase> StartContainerAsync()
    {
        var container = IsolatedPostgreSql.CreateContainer();
        try
        {
            await container.StartAsync();
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
        return new TestDatabase(container.GetConnectionString(), container.DisposeAsync);
    }

    public ValueTask DisposeAsync() => release();
}

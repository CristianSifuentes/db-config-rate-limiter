namespace Spark.ConfigCatalog.Domain;

public interface IConfigProvider
{
    Task<T?> GetAsync<T>(
        string conceptKey,
        string entryKey,
        ConfigScope scope,
        CancellationToken ct = default);
}

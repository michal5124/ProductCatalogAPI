namespace ProductCatalogAPI.Options;

public class CacheOptions
{
    public const string SectionName = "Cache";

    public int AbsoluteExpirationSeconds { get; set; } = 300;
}

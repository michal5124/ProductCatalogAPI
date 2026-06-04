using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductCatalogAPI.Interfaces;
using ProductCatalogAPI.Models;
using ProductCatalogAPI.Options;

namespace ProductCatalogAPI.Repositories;

public class ProductMemoryCache : IProductCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProductMemoryCache> _logger;
    private readonly IOptionsMonitor<CacheOptions> _options;
    private readonly TimeProvider _timeProvider;

    private static string CacheKey(int id) => $"product:{id}";

    public ProductMemoryCache(
        IMemoryCache cache,
        ILogger<ProductMemoryCache> logger,
        IOptionsMonitor<CacheOptions> options,
        TimeProvider? timeProvider = null)
    {
        _cache = cache;
        _logger = logger;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Product? Get(int id)
    {
        try
        {
            return _cache.TryGetValue(CacheKey(id), out Product? product) ? product : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache GET failed for product id {Id}", id);
            return null;
        }
    }

    public void Set(int id, Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        try
        {
            var expirationSeconds = _options.CurrentValue.AbsoluteExpirationSeconds;
            var absoluteExpiration = _timeProvider.GetUtcNow().Add(TimeSpan.FromSeconds(expirationSeconds));
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = absoluteExpiration
            };
            _cache.Set(CacheKey(id), product, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache SET failed for product id {Id}", id);
        }
    }

    public void Remove(int id)
    {
        try
        {
            _cache.Remove(CacheKey(id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache REMOVE failed for product id {Id}", id);
        }
    }
}

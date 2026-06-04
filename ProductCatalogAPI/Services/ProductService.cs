using Microsoft.Extensions.Logging;
using ProductCatalogAPI.Interfaces;
using ProductCatalogAPI.Models;
using System.Collections.Concurrent;

namespace ProductCatalogAPI.Services;

public class ProductService
{
    private readonly IProductRepository _repository;
    private readonly IProductCache _cache;
    private readonly ILogger<ProductService> _logger;

    // One semaphore per product id — threads for different ids never block each other
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _keyLocks = new();

    private SemaphoreSlim GetLock(int id) =>
        _keyLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public ProductService(IProductRepository repository, IProductCache cache, ILogger<ProductService> logger)
    {
        _repository = repository;
        _cache = cache;
        _logger = logger;
    }

    public async Task<Product?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        _logger.LogDebug("GetById called for product id {Id}", id);

        var cached = _cache.Get(id);
        if (cached is not null)
        {
            _logger.LogDebug("Cache HIT for product id {Id}", id);
            return cached;
        }

        _logger.LogDebug("Cache MISS for product id {Id} — waiting for gate", id);
        var gate = GetLock(id);
        await gate.WaitAsync(ct);
        try
        {
            cached = _cache.Get(id);
            if (cached is not null)
            {
                _logger.LogDebug("Cache HIT on re-check for product id {Id} — stampede prevented", id);
                return cached;
            }

            _logger.LogDebug("Loading product id {Id} from repository", id);
            var product = await _repository.GetByIdAsync(id, ct);

            if (product is not null)
            {
                _cache.Set(id, product);
                _logger.LogDebug("Product id {Id} loaded from repository and written to cache", id);
            }
            else
            {
                _logger.LogWarning("Product id {Id} not found in repository", id);
            }

            return product;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get product with id {Id}", id);
            throw;
        }
        finally
        {
            gate.Release();
            _logger.LogDebug("Gate released for product id {Id}", id);
        }
    }

    public async Task<Product> AddAsync(Product product, CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Adding new product with name '{Name}'", product.Name);

            var created = await _repository.AddAsync(product, ct);
            _logger.LogInformation("Product created with id {Id} and name '{Name}'", created.Id, created.Name);

            var gate = GetLock(created.Id);
            await gate.WaitAsync(ct);
            try
            {
                _cache.Set(created.Id, created);
                _logger.LogDebug("Product id {Id} written to cache after Add", created.Id);
            }
            finally
            {
                gate.Release();
            }

            return created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add product");
            throw;
        }
    }

    public async Task<Product?> UpdateAsync(int id, Product product, CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Updating product id {Id}", id);

            var updated = await _repository.UpdateAsync(id, product, ct);

            if (updated is not null)
            {
                _logger.LogInformation("Product id {Id} updated to name '{Name}' and price {Price}", updated.Id, updated.Name, updated.Price);

                var gate = GetLock(id);
                await gate.WaitAsync(ct);
                try
                {
                    _cache.Remove(id);
                    _logger.LogDebug("Stale cache entry removed for product id {Id}", id);

                    _cache.Set(id, updated);
                    _logger.LogDebug("Fresh cache entry written for product id {Id}", id);
                }
                finally
                {
                    gate.Release();
                }
            }
            else
            {
                _logger.LogWarning("Update skipped — product id {Id} not found in repository", id);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update product with id {Id}", id);
            throw;
        }
    }
}


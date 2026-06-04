using Microsoft.Extensions.Logging;
using ProductCatalogAPI.Interfaces;
using ProductCatalogAPI.Models;

namespace ProductCatalogAPI.Repositories;

public class InMemoryProductRepository : IProductRepository
{
    private readonly Dictionary<int, Product> _store = new();
    private readonly ILogger<InMemoryProductRepository> _logger;
    private int _nextId = 1;

    public InMemoryProductRepository(ILogger<InMemoryProductRepository> logger)
    {
        _logger = logger;
    }

    public Task<Product?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_store.TryGetValue(id, out var product) ? product : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get product with id {Id}", id);
            throw;
        }
    }

    public Task<IEnumerable<Product>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IEnumerable<Product>>(_store.Values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get all products");
            throw;
        }
    }

    public Task<Product> AddAsync(Product product, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(product);
            product.Id = _nextId++;
            _store[product.Id] = product;
            return Task.FromResult(product);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to add product");
            throw;
        }
    }

    public Task<Product?> UpdateAsync(int id, Product product, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(product);
            if (!_store.ContainsKey(id))
                return Task.FromResult<Product?>(null);

            product.Id = id;
            _store[id] = product;
            return Task.FromResult<Product?>(product);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to update product with id {Id}", id);
            throw;
        }
    }
}

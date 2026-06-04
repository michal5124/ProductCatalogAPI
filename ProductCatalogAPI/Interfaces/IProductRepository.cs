using ProductCatalogAPI.Models;

namespace ProductCatalogAPI.Interfaces;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IEnumerable<Product>> GetAllAsync(CancellationToken ct = default);
    Task<Product> AddAsync(Product product, CancellationToken ct = default);
    Task<Product?> UpdateAsync(int id, Product product, CancellationToken ct = default);
}

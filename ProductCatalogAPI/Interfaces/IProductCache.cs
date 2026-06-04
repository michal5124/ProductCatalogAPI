using ProductCatalogAPI.Models;

namespace ProductCatalogAPI.Interfaces;

public interface IProductCache
{
    Product? Get(int id);
    void Set(int id, Product product);
    void Remove(int id);
}

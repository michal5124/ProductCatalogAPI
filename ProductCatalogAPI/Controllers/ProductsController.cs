using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProductCatalogAPI.Models;
using ProductCatalogAPI.Services;

namespace ProductCatalogAPI.Controllers;

[ApiController]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    private readonly ProductService _service;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(ProductService service, ILogger<ProductsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Product>> Get(int id, CancellationToken ct)
    {
        try
        {
            var product = await _service.GetByIdAsync(id, ct);
            return product is null ? NotFound() : Ok(product);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled for GET product id {Id}", id);
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting product with id {Id}", id);
            return StatusCode(500, "An error occurred while retrieving the product.");
        }
    }

    [HttpPost]
    public async Task<ActionResult<Product>> Post([FromBody] Product product, CancellationToken ct)
    {
        try
        {
            var created = await _service.AddAsync(product, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled for POST product");
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding product");
            return StatusCode(500, "An error occurred while adding the product.");
        }
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Product>> Put(int id, [FromBody] Product product, CancellationToken ct)
    {
        try
        {
            var updated = await _service.UpdateAsync(id, product, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request cancelled for PUT product id {Id}", id);
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating product with id {Id}", id);
            return StatusCode(500, "An error occurred while updating the product.");
        }
    }
}

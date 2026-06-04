using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProductCatalogAPI.Interfaces;
using ProductCatalogAPI.Models;
using ProductCatalogAPI.Options;
using ProductCatalogAPI.Repositories;
using ProductCatalogAPI.Services;

namespace ProductCatalogAPI.Tests;

// Minimal IOptionsMonitor<T> implementation for tests
file sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public class CacheInvalidationTests
{
    private ProductService CreateService(out InMemoryProductRepository repo, out ProductMemoryCache cache)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new TestOptionsMonitor<CacheOptions>(new CacheOptions { AbsoluteExpirationSeconds = 300 });
        cache = new ProductMemoryCache(memoryCache, NullLogger<ProductMemoryCache>.Instance, options);
        repo = new InMemoryProductRepository(NullLogger<InMemoryProductRepository>.Instance);
        return new ProductService(repo, cache, NullLogger<ProductService>.Instance);
    }

    // Scenario 1: POST ? cache should immediately hold the new product
    [Fact]
    public async Task Add_ShouldCacheProductImmediately()
    {
        var service = CreateService(out _, out var cache);

        var created = await service.AddAsync(new Product { Name = "Laptop", Price = 999 });

        var cached = cache.Get(created.Id);
        Assert.NotNull(cached);
        Assert.Equal("Laptop", cached.Name);
    }

    // Scenario 2: POST ? PUT ? GET should return updated data, not stale data
    [Fact]
    public async Task Update_ShouldRefreshCache_NotReturnStaleData()
    {
        var service = CreateService(out _, out var cache);

        var created = await service.AddAsync(new Product { Name = "Laptop", Price = 999 });
        await service.UpdateAsync(created.Id, new Product { Name = "Gaming Laptop", Price = 1499 });

        var cached = cache.Get(created.Id);
        Assert.NotNull(cached);
        Assert.Equal("Gaming Laptop", cached.Name);
        Assert.Equal(1499, cached.Price);
    }

    // Scenario 3: GET on missing id should NOT store null in cache
    [Fact]
    public async Task GetById_ShouldNotCacheNull_WhenProductNotFound()
    {
        var service = CreateService(out _, out var cache);

        var result = await service.GetByIdAsync(999);

        Assert.Null(result);
        Assert.Null(cache.Get(999));
    }

    // Scenario 4: GET after POST should be served from cache, not repository
    [Fact]
    public async Task GetById_ShouldReturnFromCache_AfterAdd()
    {
        var service = CreateService(out _, out _);

        var created = await service.AddAsync(new Product { Name = "Phone", Price = 499 });
        var result = await service.GetByIdAsync(created.Id);

        Assert.NotNull(result);
        Assert.Equal("Phone", result.Name);
    }

    // Scenario 5: GET (warms cache from repo) ? PUT ? GET must NOT return the stale cached value.
    [Fact]
    public async Task GetById_AfterUpdate_ShouldNotReturnStaleData()
    {
        var service = CreateService(out _, out var cache);

        var created = await service.AddAsync(new Product { Name = "Keyboard", Price = 79 });

        var beforeUpdate = await service.GetByIdAsync(created.Id);
        Assert.Equal("Keyboard", beforeUpdate!.Name);

        await service.UpdateAsync(created.Id, new Product { Name = "Mechanical Keyboard", Price = 129 });

        var cachedAfterUpdate = cache.Get(created.Id);
        Assert.NotNull(cachedAfterUpdate);
        Assert.Equal("Mechanical Keyboard", cachedAfterUpdate.Name);
        Assert.Equal(129, cachedAfterUpdate.Price);

        var serviceAfterUpdate = await service.GetByIdAsync(created.Id);
        Assert.NotNull(serviceAfterUpdate);
        Assert.Equal("Mechanical Keyboard", serviceAfterUpdate.Name);
        Assert.Equal(129, serviceAfterUpdate.Price);
    }

    // Scenario 7: pre-cancelled token must throw immediately — gate is never acquired
    [Fact]
    public async Task GetById_WithPreCancelledToken_ThrowsOperationCanceledException()
    {
        var service = CreateService(out _, out _); // cold cache, empty repo
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled before the call

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetByIdAsync(1, cts.Token));
    }

    // Scenario 8: Task B waits for the gate (Task A holds it) and gets cancelled.
    // Asserts: Task B throws, Task A completes normally, gate is properly released,
    // Task C gets a cache HIT — no deadlock, no corrupted semaphore.
    [Fact]
    public async Task GetById_CancellationWhileWaitingForGate_ThrowsAndGateRemainsUsable()
    {
        var entered = new SemaphoreSlim(0, 1);
        var proceed = new SemaphoreSlim(0, 1);
        var blockingRepo = new BlockingProductRepository(entered, proceed);
        blockingRepo.Seed(new Product { Id = 1, Name = "Monitor", Price = 299 });

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new TestOptionsMonitor<CacheOptions>(new CacheOptions { AbsoluteExpirationSeconds = 300 });
        var cache = new ProductMemoryCache(memoryCache, NullLogger<ProductMemoryCache>.Instance, options);
        var service = new ProductService(blockingRepo, cache, NullLogger<ProductService>.Instance);

        // Task A: acquires the per-key gate and blocks inside the repository
        var taskA = service.GetByIdAsync(1);
        await entered.WaitAsync(); // wait until Task A is inside the repo (holds the gate)

        // Task B: waits for the gate — Task A holds it — cancelled after 50 ms
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var taskB = service.GetByIdAsync(1, cts.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await taskB);

        // Unblock Task A — must complete normally and populate the cache
        proceed.Release();
        var resultA = await taskA;
        Assert.Equal("Monitor", resultA!.Name);

        // Gate released by Task A — Task C must get a cache HIT, repo not called again
        var resultC = await service.GetByIdAsync(1);
        Assert.Equal("Monitor", resultC!.Name);
    }

    // Scenario 9: absolute expiration — advance a fake clock instead of Thread.Sleep.
    // Proves the entry expires exactly at TTL without any real waiting.
    [Fact]
    public async Task GetById_AfterTtlExpires_ShouldMissCache_WithoutRealSleep()
    {
        var fakeTime = new FakeTimeProvider();
        var clock = new FakeMemoryCacheClock(fakeTime);
        var memoryCache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var options = new TestOptionsMonitor<CacheOptions>(new CacheOptions { AbsoluteExpirationSeconds = 60 });
        var cache = new ProductMemoryCache(memoryCache, NullLogger<ProductMemoryCache>.Instance, options, fakeTime);
        var spy = new CountingProductRepository();
        spy.Seed(new Product { Id = 1, Name = "Monitor", Price = 299 });
        var service = new ProductService(spy, cache, NullLogger<ProductService>.Instance);

        // Warm the cache — repo call #1
        await service.GetByIdAsync(1);
        Assert.Equal(1, spy.GetByIdCallCount);

        // Before expiry — cache HIT, repo not called again
        await service.GetByIdAsync(1);
        Assert.Equal(1, spy.GetByIdCallCount);

        // Advance past TTL (60 s) without sleeping
        fakeTime.Advance(TimeSpan.FromSeconds(61));

        // After expiry — cache MISS, repo call #2
        await service.GetByIdAsync(1);
        Assert.Equal(2, spy.GetByIdCallCount);

        // Cache is warm again — repo not called a third time
        await service.GetByIdAsync(1);
        Assert.Equal(2, spy.GetByIdCallCount);
    }

    // Scenario 10: 50 tasks all released at the exact same instant via a start gate.
    // Stronger than Scenario 6 — maximum concurrency is guaranteed by the start gate,
    // not left to OS scheduling. Repository must still be called exactly once.
    [Fact]
    public async Task GetById_HighConcurrencyStampede_RepositoryCalledExactlyOnce()
    {
        const int concurrency = 50;
        var startGate = new SemaphoreSlim(0); // all tasks wait here until released together
        var spy = new CountingProductRepository();
        spy.Seed(new Product { Id = 1, Name = "Monitor", Price = 299 });

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new TestOptionsMonitor<CacheOptions>(new CacheOptions { AbsoluteExpirationSeconds = 300 });
        var cache = new ProductMemoryCache(memoryCache, NullLogger<ProductMemoryCache>.Instance, options);
        var service = new ProductService(spy, cache, NullLogger<ProductService>.Instance);

        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {
                await startGate.WaitAsync(); // hold until all tasks are ready
                return await service.GetByIdAsync(1);
            }))
            .ToList();

        startGate.Release(concurrency); // release all 50 tasks at the same instant
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal("Monitor", r.Name);
        });

        Assert.Equal(1, spy.GetByIdCallCount);
    }
}

// Spy repository that counts GetByIdAsync calls and lets the test seed data
file sealed class CountingProductRepository : IProductRepository
{
    private readonly Dictionary<int, Product> _store = new();
    private int _getByIdCallCount;

    public int GetByIdCallCount => _getByIdCallCount;

    public void Seed(Product product) => _store[product.Id] = product;

    public async Task<Product?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _getByIdCallCount);
        await Task.Delay(5, ct); // simulate async latency so tasks truly overlap
        return _store.TryGetValue(id, out var p) ? p : null;
    }

    public Task<IEnumerable<Product>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<Product>>(_store.Values);

    public Task<Product> AddAsync(Product product, CancellationToken ct = default) =>
        Task.FromResult(product);

    public Task<Product?> UpdateAsync(int id, Product product, CancellationToken ct = default) =>
        Task.FromResult<Product?>(product);
}

// Repository that signals when it has been entered then blocks until told to proceed.
// Used to hold the per-key semaphore open while a second task tries to acquire it.
file sealed class BlockingProductRepository : IProductRepository
{
    private readonly Dictionary<int, Product> _store = new();
    private readonly SemaphoreSlim _entered;
    private readonly SemaphoreSlim _proceed;

    public BlockingProductRepository(SemaphoreSlim entered, SemaphoreSlim proceed)
    {
        _entered = entered;
        _proceed = proceed;
    }

    public void Seed(Product product) => _store[product.Id] = product;

    public async Task<Product?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        _entered.Release();          // signal: "I am inside the repo"
        await _proceed.WaitAsync(ct); // block until the test says to continue
        return _store.TryGetValue(id, out var p) ? p : null;
    }

    public Task<IEnumerable<Product>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<Product>>(_store.Values);
    public Task<Product> AddAsync(Product product, CancellationToken ct = default) =>
        Task.FromResult(product);
    public Task<Product?> UpdateAsync(int id, Product product, CancellationToken ct = default) =>
        Task.FromResult<Product?>(product);
}

// Bridges FakeTimeProvider into MemoryCache's ISystemClock so TryGetValue
// uses the same fake time as AbsoluteExpiration computed in ProductMemoryCache.
file sealed class FakeMemoryCacheClock(FakeTimeProvider fakeTime) : ISystemClock
{
    public DateTimeOffset UtcNow => fakeTime.GetUtcNow();
}

// Controllable time provider — advances the clock manually so expiry tests
// need no Thread.Sleep / Task.Delay.
file sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public void Advance(TimeSpan delta) => _now += delta;
    public override DateTimeOffset GetUtcNow() => _now;
}


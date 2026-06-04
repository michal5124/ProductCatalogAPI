# ProductCatalogAPI

A .NET 8 Web API demonstrating in-memory caching with:

- **Cache-aside pattern** — read from cache, fall back to repository on miss
- **Cache invalidation** — immediate eviction and refresh on update or create, no stale reads
- **Configurable TTL** — absolute expiration driven by `appsettings.json`, no restart needed
- **Cache stampede prevention** — per-key `SemaphoreSlim` ensures only one thread loads from the repository on a cold cache; all concurrent requests for the same key wait and get a cache hit on re-check

---

## How to Run

**Requirements:** .NET 8 SDK

```powershell
cd C:\Projects\ProductCatalogAPI
dotnet run --project ProductCatalogAPI
```

- API: `https://localhost:7074`
- Swagger: `https://localhost:7074/swagger`

Debug logs (cache hit/miss/gate) are enabled by default in `appsettings.json`:
```json
"ProductCatalogAPI.Services.ProductService": "Debug"
```

---

## Request Flow

### Create a product
```powershell
Invoke-WebRequest -Uri "https://localhost:7074/api/products" `
    -Method POST -ContentType "application/json" `
    -Body '{"name":"Monitor","price":299}'
```
Response `201 Created`:
```json
{ "id": 1, "name": "Monitor", "price": 299 }
```

### Read the product
```powershell
Invoke-WebRequest -Uri "https://localhost:7074/api/products/1"
```
Response `200 OK`:
```json
{ "id": 1, "name": "Monitor", "price": 299 }
```

### Update the product
```powershell
Invoke-WebRequest -Uri "https://localhost:7074/api/products/1" `
    -Method PUT -ContentType "application/json" `
    -Body '{"name":"4K Monitor","price":499}'
```
Response `200 OK`:
```json
{ "id": 1, "name": "4K Monitor", "price": 499 }
```

### Read after update — must return fresh value, not stale
```powershell
Invoke-WebRequest -Uri "https://localhost:7074/api/products/1"
```
Response `200 OK`:
```json
{ "id": 1, "name": "4K Monitor", "price": 499 }
```

---

## Cache Hit / Miss Demo

TTL is **10 seconds** (`Cache:AbsoluteExpirationSeconds` in `appsettings.json`).

```powershell
# 1. Create
Invoke-WebRequest -Uri "https://localhost:7074/api/products" `
    -Method POST -ContentType "application/json" `
    -Body '{"name":"Monitor","price":299}'

# 2. GET -> cache HIT (written on create)
Invoke-WebRequest -Uri "https://localhost:7074/api/products/1"
# dbug: Cache HIT for product id 1

# 3. Wait for TTL
Start-Sleep -Seconds 11

# 4. GET -> cache MISS, loads from repository
Invoke-WebRequest -Uri "https://localhost:7074/api/products/1"
# dbug: Cache MISS for product id 1 - waiting for gate
# dbug: Loading product id 1 from repository
# dbug: Product id 1 loaded from repository and written to cache

# 5. GET -> cache HIT again
Invoke-WebRequest -Uri "https://localhost:7074/api/products/1"
# dbug: Cache HIT for product id 1
```

---

## Stampede Prevention Demo

20 concurrent requests on a cold cache — repository called exactly once.

```powershell
# Create a product then let the cache expire
Invoke-WebRequest -Uri "https://localhost:7074/api/products" `
    -Method POST -ContentType "application/json" `
    -Body '{"name":"Monitor","price":299}'
Start-Sleep -Seconds 11

# Fire 20 truly parallel requests
$tasks = 1..20 | ForEach-Object {
    [System.Threading.Tasks.Task]::Run({
        Invoke-WebRequest "https://localhost:7074/api/products/1" | Out-Null
    })
}
[System.Threading.Tasks.Task]::WaitAll($tasks)
```

Expected logs:
```
dbug: Cache MISS for product id 1 - waiting for gate          <- many threads
dbug: Loading product id 1 from repository                     <- exactly once
dbug: Gate released for product id 1
dbug: Cache HIT on re-check for product id 1 - stampede prevented  <- all others
```

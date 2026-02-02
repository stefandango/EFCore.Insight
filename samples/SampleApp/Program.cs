using Microsoft.EntityFrameworkCore;
using EFCore.Insight;
using SampleApp;

var builder = WebApplication.CreateBuilder(args);

// Add EF Core with SQLite database
builder.Services.AddDbContext<SampleDbContext>(options =>
    options.UseSqlite("Data Source=sample.db"));

// Add EFCore.Insight
builder.Services.AddEFCoreInsight(options =>
{
    options.MaxStoredQueries = 100;
});

var app = builder.Build();

// Enable EFCore.Insight dashboard
app.UseEFCoreInsight();

// Create and seed the database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
    db.Database.EnsureCreated();
    SeedData(db);
}

// API Endpoints
app.MapGet("/", () => "EFCore.Insight Sample App - Visit /_ef-insight to see the dashboard");

app.MapGet("/products", async (SampleDbContext db) =>
    await db.Products.ToListAsync());

app.MapGet("/products/{id:int}", async (int id, SampleDbContext db) =>
    await db.Products.FindAsync(id) is Product p ? Results.Ok(p) : Results.NotFound());

app.MapGet("/products/search", async (string? name, decimal? minPrice, SampleDbContext db) =>
{
    var query = db.Products.AsQueryable();

    if (!string.IsNullOrEmpty(name))
        query = query.Where(p => p.Name.Contains(name));

    if (minPrice.HasValue)
        query = query.Where(p => p.Price >= minPrice.Value);

    return await query.ToListAsync();
});

app.MapGet("/orders", async (SampleDbContext db) =>
    await db.Orders
        .Include(o => o.Items)
        .ThenInclude(i => i.Product)
        .ToListAsync());

app.MapGet("/orders/{id:int}", async (int id, SampleDbContext db) =>
    await db.Orders
        .Include(o => o.Items)
        .ThenInclude(i => i.Product)
        .FirstOrDefaultAsync(o => o.Id == id) is Order o ? Results.Ok(o) : Results.NotFound());

app.MapPost("/orders", async (CreateOrderRequest request, SampleDbContext db) =>
{
    var order = new Order
    {
        CustomerName = request.CustomerName,
        OrderDate = DateTime.UtcNow
    };

    foreach (var item in request.Items)
    {
        var product = await db.Products.FindAsync(item.ProductId);
        if (product is null)
            return Results.BadRequest($"Product {item.ProductId} not found");

        order.Items.Add(new OrderItem
        {
            Product = product,
            Quantity = item.Quantity,
            UnitPrice = product.Price
        });
    }

    db.Orders.Add(order);
    await db.SaveChangesAsync();

    return Results.Created($"/orders/{order.Id}", order);
});

app.MapGet("/stats", async (SampleDbContext db) => new
{
    TotalProducts = await db.Products.CountAsync(),
    TotalOrders = await db.Orders.CountAsync(),
    AverageOrderValue = await db.Orders
        .Select(o => o.Items.Sum(i => i.Quantity * i.UnitPrice))
        .DefaultIfEmpty()
        .AverageAsync(),
    TopProducts = await db.Products
        .OrderByDescending(p => p.Price)
        .Take(3)
        .Select(p => new { p.Name, p.Price })
        .ToListAsync()
});

// ============================================================================
// N+1 DEMO ENDPOINTS
// These endpoints demonstrate N+1 query patterns for EFCore.Insight testing
// ============================================================================

// BAD: N+1 pattern - loads orders, then issues separate query for each order's items
app.MapGet("/demo/n1-bad", async (SampleDbContext db) =>
{
    var orders = await db.Orders.ToListAsync();
    var result = new List<object>();

    foreach (var order in orders)
    {
        // N+1: This issues a separate query for each order!
        var items = await db.OrderItems
            .Where(i => i.OrderId == order.Id)
            .ToListAsync();

        result.Add(new
        {
            order.Id,
            order.CustomerName,
            ItemCount = items.Count
        });
    }

    return result;
});

// GOOD: Single query with Include - no N+1
app.MapGet("/demo/n1-good", async (SampleDbContext db) =>
{
    var orders = await db.Orders
        .Include(o => o.Items)
        .ToListAsync();

    return orders.Select(o => new
    {
        o.Id,
        o.CustomerName,
        ItemCount = o.Items.Count
    });
});

// BAD: N+1 when looking up products by ID in a loop
app.MapGet("/demo/n1-products", async (SampleDbContext db) =>
{
    var productIds = await db.Products.Select(p => p.Id).ToListAsync();
    var result = new List<object>();

    foreach (var id in productIds)
    {
        // N+1: Separate query for each product!
        var product = await db.Products.FindAsync(id);
        if (product != null)
        {
            result.Add(new { product.Name, product.Price });
        }
    }

    return result;
});

// SPLIT QUERY: Uses AsSplitQuery() to execute multiple SELECTs instead of JOINs
app.MapGet("/demo/split", async (SampleDbContext db) =>
{
    // AsSplitQuery splits this into separate queries for Orders and OrderItems
    var orders = await db.Orders
        .Include(o => o.Items)
        .ThenInclude(i => i.Product)
        .AsSplitQuery()
        .ToListAsync();

    return orders.Select(o => new
    {
        o.Id,
        o.CustomerName,
        Items = o.Items.Select(i => new
        {
            i.Product.Name,
            i.Quantity,
            i.UnitPrice
        })
    });
});

// SINGLE QUERY: Same query without AsSplitQuery (uses JOINs)
app.MapGet("/demo/single", async (SampleDbContext db) =>
{
    // This uses JOINs - single query but may have "cartesian explosion"
    var orders = await db.Orders
        .Include(o => o.Items)
        .ThenInclude(i => i.Product)
        .ToListAsync();

    return orders.Select(o => new
    {
        o.Id,
        o.CustomerName,
        Items = o.Items.Select(i => new
        {
            i.Product.Name,
            i.Quantity,
            i.UnitPrice
        })
    });
});

// ============================================================================
// CARTESIAN EXPLOSION DEMO
// This demonstrates the cartesian explosion problem with multiple Includes
// ============================================================================

// BAD: Cartesian Explosion - multiple collection includes without AsSplitQuery
// With 10 orders, each having 5 items = 50+ rows returned from a single query
// If we had another collection (like Payments), it would multiply further!
app.MapGet("/demo/cartesian", async (SampleDbContext db) =>
{
    // This query loads all orders with their items
    // Even though we have 10 orders with ~5 items each, the JOIN returns ~50 rows
    // This can be detected as a potential cartesian explosion
    var orders = await db.Orders
        .Include(o => o.Items)
        .ThenInclude(i => i.Product)
        .ToListAsync();

    return new
    {
        OrderCount = orders.Count,
        TotalItems = orders.Sum(o => o.Items.Count),
        Message = "Check the dashboard - you may see a Cartesian Explosion warning",
        Orders = orders.Select(o => new
        {
            o.Id,
            o.CustomerName,
            Items = o.Items.Select(i => new
            {
                i.Product.Name,
                i.Quantity
            })
        })
    };
});

// GOOD: Same query with AsSplitQuery - avoids cartesian explosion
app.MapGet("/demo/cartesian-fixed", async (SampleDbContext db) =>
{
    // Using AsSplitQuery() splits this into separate queries
    // This avoids row multiplication from JOINs
    var orders = await db.Orders
        .Include(o => o.Items)
        .ThenInclude(i => i.Product)
        .AsSplitQuery()
        .ToListAsync();

    return new
    {
        OrderCount = orders.Count,
        TotalItems = orders.Sum(o => o.Items.Count),
        Message = "Using AsSplitQuery - check dashboard to see split queries instead of joins",
        Orders = orders.Select(o => new
        {
            o.Id,
            o.CustomerName,
            Items = o.Items.Select(i => new
            {
                i.Product.Name,
                i.Quantity
            })
        })
    };
});

app.Run();

// Seed data helper
static void SeedData(SampleDbContext db)
{
    if (db.Products.Any()) return;

    var products = new[]
    {
        new Product { Name = "Laptop", Price = 999.99m, Stock = 50 },
        new Product { Name = "Mouse", Price = 29.99m, Stock = 200 },
        new Product { Name = "Keyboard", Price = 79.99m, Stock = 150 },
        new Product { Name = "Monitor", Price = 349.99m, Stock = 75 },
        new Product { Name = "Headphones", Price = 149.99m, Stock = 100 },
        new Product { Name = "USB Cable", Price = 9.99m, Stock = 500 },
        new Product { Name = "Webcam", Price = 89.99m, Stock = 80 },
        new Product { Name = "Desk Lamp", Price = 45.99m, Stock = 120 }
    };
    db.Products.AddRange(products);

    // Create multiple orders to make N+1 and cartesian patterns more visible
    var customers = new[] { "John Doe", "Jane Smith", "Bob Wilson", "Alice Brown", "Charlie Davis",
                            "Diana Prince", "Eve Adams", "Frank Miller", "Grace Lee", "Henry Ford" };
    var random = new Random(42);

    for (var i = 0; i < customers.Length; i++)
    {
        var order = new Order
        {
            CustomerName = customers[i],
            OrderDate = DateTime.UtcNow.AddDays(-i)
        };

        // Add 3-6 random items per order (more items to demonstrate cartesian explosion)
        var itemCount = random.Next(3, 7);
        for (var j = 0; j < itemCount; j++)
        {
            var product = products[random.Next(products.Length)];
            order.Items.Add(new OrderItem
            {
                Product = product,
                Quantity = random.Next(1, 5),
                UnitPrice = product.Price
            });
        }

        db.Orders.Add(order);
    }

    db.SaveChanges();
}


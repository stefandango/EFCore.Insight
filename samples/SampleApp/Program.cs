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

    var order = new Order
    {
        CustomerName = "John Doe",
        OrderDate = DateTime.UtcNow.AddDays(-1)
    };
    order.Items.Add(new OrderItem { Product = products[0], Quantity = 1, UnitPrice = products[0].Price });
    order.Items.Add(new OrderItem { Product = products[1], Quantity = 2, UnitPrice = products[1].Price });
    db.Orders.Add(order);

    db.SaveChanges();
}


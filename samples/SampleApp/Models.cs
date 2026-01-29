using Microsoft.EntityFrameworkCore;

namespace SampleApp;

public class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public required string CustomerName { get; set; }
    public DateTime OrderDate { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public required Product Product { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public record CreateOrderRequest(string CustomerName, List<OrderItemRequest> Items);
public record OrderItemRequest(int ProductId, int Quantity);

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
}

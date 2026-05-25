namespace LunchMenu.Models;

public class Restaurant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? WebsiteUrl { get; set; }
    public string? PhoneNumber { get; set; }
    public double Rating { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class DailyMenu
{
    public int RestaurantId { get; set; }
    public DateOnly Date { get; set; }
    public string? SoupOfTheDay { get; set; }
    public List<MenuItem> Items { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.Now;
}

public class MenuItem
{
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public bool IsVegetarian { get; set; }
    public List<int> Allergens { get; set; } = new();
    public string Category { get; set; } = "Main";
}

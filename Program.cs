using LunchMenu.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<MenuScraperService>();
builder.Services.AddSingleton<LunchMenuService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

app.MapGet("/api/menus/today", async (LunchMenuService svc, string? search, int? dayOffset) =>
{
    var overview = await svc.GetTodaysOverviewAsync(search, dayOffset ?? 0);
    return Results.Ok(overview.Select(o => new
    {
        restaurant = new { o.Restaurant.Id, o.Restaurant.Name, o.Restaurant.Address, o.Restaurant.Rating, o.Restaurant.Tags },
        menu = o.Menu == null ? null : new
        {
            o.Menu.SoupOfTheDay,
            o.Menu.LastUpdated,
            items = o.Menu.Items.Select(i => new { i.Name, i.Price, i.IsVegetarian, i.Allergens, i.Category })
        }
    }));
});

app.MapGet("/api/menus/search", async (LunchMenuService svc, string q) =>
{
    var results = await svc.SearchMenusAsync(q);
    return Results.Ok(results.Select(r => new
    {
        restaurant = r.Restaurant.Name,
        item = r.Item.Name,
        price = r.Item.Price
    }));
});

app.Run();

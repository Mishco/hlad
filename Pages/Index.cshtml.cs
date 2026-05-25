using LunchMenu.Models;
using LunchMenu.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LunchMenu.Pages;

public class IndexModel : PageModel
{
    private readonly LunchMenuService _menuService;

    public List<(Restaurant Restaurant, DailyMenu? Menu)> TodaysMenus { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int DayOffset { get; set; } = 0;

    public DateTime SelectedDate => DateTime.Today.AddDays(DayOffset);

    public IndexModel(LunchMenuService menuService)
    {
        _menuService = menuService;
    }

    public async Task OnGetAsync()
    {
        TodaysMenus = await _menuService.GetTodaysOverviewAsync(Search, dayOffset: DayOffset);
    }
}

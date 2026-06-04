# Hlad - Denné menu

Web app showing daily lunch menus from restaurants.

**Live:** [https://hlad.soon.it/](https://hlad.soon.it/)

## Restaurants

| # | Restaurant | Source |
|---|-----------|--------|
| 1 | Popradská Plzeňka | restauracie.sme.sk |
| 2 | Aquacity Poprad | aquacity.sk |
| 3 | Rock'n'Roll Steak Pub (Forum Poprad) | forumpoprad.sk |
| 4 | Barn Club | menucka.sk |
| 5 | Angry Chef | angrychef.sk |
| 6 | Mamut Pub & Restaurant | mamutpoprad.sk |
| 7 | Pho King | phoking.sk |
| 8 | SAVOURY Asian Restaurant & Sushi Bar | wolt.com |

## Features

- Daily menu overview with day navigation (prev/next/today)
- Search filtering across all restaurants
- "Dám si!" button to track your lunch picks (localStorage)
- Personal statistics: top meals, top restaurants, spending
- Live web scraping with static data fallback
- Responsive design with dark mode support
- Slovak language UI

## Tech Stack

- ASP.NET Core (.NET 10), Razor Pages, Minimal API
- HtmlAgilityPack for web scraping
- In-memory caching (1hr per restaurant/day)
- Docker deployment on Render (free tier)
- DNS: FreeDNS (afraid.org)

## Run locally

```bash
dotnet run
```

Open http://localhost:5000

## API

- `GET /api/menus/today` - all menus for today (JSON)
- `GET /api/menus/search?q=rezeň` - search menu items

## Deploy

Auto-deploys from `master` branch via Render (Docker runtime).

```dockerfile
docker build -t hlad .
docker run -p 10000:10000 hlad
```

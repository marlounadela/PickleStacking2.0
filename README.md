# 🥒 PickleStacking

Automatic pickleball matchmaking & court queue system built with Blazor WebAssembly.

## What is PickleStacking?

PickleStacking is a client-side web application that manages pickleball sessions with automatic matchmaking. It handles:

- **Player management** — add, edit, and remove players
- **Court configuration** — 1–10 courts, singles or doubles mode
- **Automatic stacking** — fair matchmaking based on games played, WIN/LOSS grouping, partner rotation, and opponent avoidance
- **First-round FIFO** — unplayed players get priority in the first round
- **Next Up queue** — pre-computed upcoming matches
- **Court repair** — drag-and-drop player swapping within a court
- **Game history** — complete record of all completed games
- **Session management** — start, pause, resume, and reset sessions

## Technology

- **Blazor WebAssembly** (.NET 10)
- **C#** — all application logic
- **localStorage** — client-side state persistence (no database)
- **Vercel** — static hosting

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Run Locally

```bash
# Restore packages
dotnet restore PickleStacking/PickleStacking.csproj

# Run in development
dotnet run --project PickleStacking/PickleStacking.csproj
```

Open `http://localhost:5001` in your browser.

## Build

```bash
dotnet build PickleStacking/PickleStacking.csproj -c Release
```

## Publish

```bash
dotnet publish PickleStacking/PickleStacking.csproj -c Release -o dist
```

The static output is in `dist/wwwroot/`.

## Run Tests

```bash
dotnet run --project PickleStacking.Tests/PickleStacking.Tests.csproj -c Release
```

## Deploy to Vercel

1. Push this repository to GitHub.
2. Import the repository in [Vercel](https://vercel.com).
3. Vercel will automatically detect the `vercel.json` configuration.
4. Deploy.

The `vercel.json` handles:
- Build: `bash build.sh` (installs .NET SDK if needed, then publishes)
- Output: `dist/wwwroot`
- Client-side routing: all routes fall back to `index.html`

## Environment Variables

**None required.** This application is fully client-side and uses browser `localStorage` for state persistence.

## Important: State Persistence

State is stored in the browser's `localStorage`. This means:

- Data is **per-browser** — different browsers/devices have separate state.
- Clearing browser data resets the application.
- There is **no server-side database**.
- State persists across page refreshes within the same browser.

This design is intentional — the app is a lightweight, self-contained tool for managing pickleball sessions without requiring backend infrastructure.

## Project Structure

```
PickleStacking/
├── Components/
│   ├── Layout/          # Main layout & navigation
│   ├── Pages/           # Dashboard, Players, Courts, History
│   └── Shared/          # CourtCard, StatusBadge
├── Models/              # Player, Court, Game, Team, Session
├── Services/            # Stacking, Player, Court, Queue, Session services
└── wwwroot/             # Static assets (CSS, icons, index.html)
```

## License

MIT
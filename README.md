# Uni Schedule

A phone-first class timetable for GUtech students, with a dashboard for the RSA office and owners. The product plan is in [docs/plan.md](docs/plan.md).

- `server/` — ASP.NET Core (.NET 10) API, EF Core + SQLite. Also serves the built frontend.
- `web/` — React + TypeScript + Vite + Tailwind.
- `sample-data/fall-2026.csv` — sample semester in the upload format.
- `deploy/` — Dockerfile, compose file, deploy script and Caddy block ([deploy/README.md](deploy/README.md)).

## Run locally

```bash
# API on http://localhost:5085 (Development: seeds Fall 2026 from sample-data, test-mode codes)
cd server/UniSchedule.Api
dotnet run --launch-profile http

# Frontend on http://localhost:5173 (proxies /api to the API)
cd web
npm install
npm run dev
```

- Students: sign in with any ID like `24-0161`; in Development the code is shown on screen.
- Dashboard: `/admin/login` with `dev-owner` / `dev-password` (Development only).
- To make an ID an owner locally, set `Owners__Ids=24-0161` before `dotnet run`.
- Delete `server/UniSchedule.Api/app-data/` to start over.

## Tests

```bash
cd server && dotnet test        # xUnit: time parsing, files, import diff, clashes, auth flow
cd web && npx tsc -b && npx eslint .
```

## Upload format

One row per section meeting time; the header row names the columns (any order, common variants accepted):

| Column | Example | Notes |
|---|---|---|
| Course Code | `CS201` | required |
| Course Name | `Data Structures` | |
| Major | `CS` or `CS;LOG` | required for Core; empty Elective = open to all majors; ignored for LS |
| Type | `Core` / `LS` / `Elective` | empty = Core |
| Year | `2` | the study year a core course is pre-ticked for |
| Credits | `3` | |
| Section | `1`, `01`, `Sec 1` | required |
| Instructor | `Dr. Laura Becker` | |
| Days | `Sun/Tue`, `MW`, `UTR` | required |
| Time | `08:00-09:30`, `1:30 PM - 3:00 PM` | required (or Start + End columns) |
| Room | `B105` | |

`.xlsx`, `.csv`, or rows pasted straight from Excel.

## Configuration

| Setting (env var) | Default | |
|---|---|---|
| `Data__Path` | `app-data/schedule.db` (`/app/data/schedule.db` in Docker) | SQLite file; keys go in `keys/` next to it |
| `Owners__Ids` | | student IDs that are always owners |
| `Auth__TestMode` | `false` (`true` in Development) | show sign-in codes in the app, never for owners |
| `Email__Provider` | `log` | `log` or `resend` |
| `Email__ResendApiKey`, `Email__From` | | for Resend |
| `RateLimits__RequestCode`, `RateLimits__Verify` | 10, 20 | per IP per 5 minutes |

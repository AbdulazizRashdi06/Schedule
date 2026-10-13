# Handoff: Uni Schedule

For the next session (local Claude Code on the laptop). Read this first, then [README.md](../README.md), [deploy/README.md](../deploy/README.md) and [plan.md](plan.md).

## Where things are

- Repo: <https://github.com/AbdulazizRashdi06/Schedule>
- **Status:** the plan (`docs/plan.md`, Parts A–F) is fully built. Backend tests pass, and the app was walked through in a browser (phone size, light and dark). Not deployed yet.

## What's built

| Area | Where |
|---|---|
| API (.NET 10, EF Core, SQLite) | `server/UniSchedule.Api` |
| Data model + migration | `Data/Entities.cs`, `Data/AppDb.cs`, `Data/Migrations/` |
| Startup, auth cookie, policies, rate limits, SPA fallback, `/api/health` | `Program.cs` |
| Student/owner sign-in (ID + OTP), admin username/password, lockout | `Auth/` |
| Upload: CSV/TSV/xlsx parsing, preview diff, apply only changed rows | `Schedule/` |
| Student endpoints (setup steps, picks, timetable, "Changed" badge) | `Students/StudentEndpoints.cs` |
| Admin: semesters, upload, publish, courses/sections, majors, electives, stats | `Admin/AdminEndpoints.cs` |
| Owner-only students + accounts (RSA can manage RSA only) | `Admin/OwnerEndpoints.cs` |
| `create-admin <username> <owner\|rsa>` CLI | `Cli.cs` |
| Dev seed (Fall 2026 from sample data, login `dev-owner` / `dev-password`) | `DevSeed.cs` |
| Frontend (React + TS + Vite + Tailwind) | `web/src` (`student/`, `admin/`, `components/`, `lib/`) |
| Tests: 64 xUnit (time parsing, files, import diff, clashes, auth flow, sample files) | `server/UniSchedule.Tests` |
| Docker, compose, deploy script, Caddy block | `deploy/` |
| Mock data | `sample-data/fall-2026.csv`, `sample-data/spring-2027.csv` and `.xlsx` |

## Next steps

1. **Deploy from the laptop.** Port 22 is blocked on campus Wi-Fi, so use a hotspot. Full steps are in [deploy/README.md](../deploy/README.md):
   1. `ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 docker network ls` to find Zarqa's network (likely `zarqa_default`).
   2. `./deploy/deploy.sh` (ships committed HEAD only). The first run creates `/opt/uni-schedule/deploy/.env` on the server.
   3. Edit that `.env`: set `OWNER_IDS` and `ZARQA_NETWORK`, and keep `AUTH_TEST_MODE=true` and `EMAIL_PROVIDER=log` for now. Run `./deploy/deploy.sh` again.
   4. Append `deploy/Caddyfile.snippet` to `C:\Projects\LostraAi\deploy\Caddyfile` and reload Zarqa's Caddy.
   5. Create the first RSA account with `create-admin` (the command is in `deploy/README.md`).
   6. Open <https://schedule.173-249-40-122.sslip.io>.
2. **Try it with mock data:** in the dashboard, Upload → New semester → Spring 2027 → upload `sample-data/spring-2027.xlsx` → Preview → Confirm → Publish.
3. **Real data:** the user will send real schedules. They use the same upload format (see the table in `README.md`).
4. **Real email:** set `EMAIL_PROVIDER=resend`, `RESEND_API_KEY` and `EMAIL_FROM` (domain verified in Resend), then turn `AUTH_TEST_MODE` off.

## Things to know

- Owners' sign-in codes are never shown on screen, even in test mode. With `EMAIL_PROVIDER=log`, read them from the container logs (`docker compose ... logs -f`).
- `Data/` was missing from the first commit: the old `.gitignore` line `data/` matched `Data/` on Windows, where git ignores case. It's fixed with anchored rules. If the laptop still has old `Data/Entities.cs` / `AppDb.cs` files, compare them before overwriting; the committed ones were rebuilt from how the code uses them.
- Publishing a different semester sends every student back to setup (from Major). Their last picks show as "Picked last semester" hints.
- Edits to a live semester (upload or single-section edit) show a "Changed" badge to students who picked that section. Edits before publishing don't.
- Removed sections clear the pick and flag the student to re-pick. A banner on their timetable links to Edit my courses.
- Allowed electives are stored by course code (`MajorElective`), so they carry over between semesters. Electives with no major in the upload are open to everyone.
- Rate limits are per IP per 5 minutes: 10 code requests and 20 verifies. Change them with `RateLimits__RequestCode` / `RateLimits__Verify`.
- Data lives in the `uni-schedule_data` Docker volume (SQLite + data-protection keys). Sessions survive restarts. Migrations run at startup. A backup command is in `deploy/README.md`.

## Run and check locally

```bash
cd server/UniSchedule.Api && dotnet run --launch-profile http   # API :5085, seeds Fall 2026
cd web && npm install && npm run dev                            # app :5173
cd server && dotnet test                                        # 64 tests
cd web && npx tsc -b && npx eslint .
```

Delete `server/UniSchedule.Api/app-data/` to reset the local database.

## Open questions for the user

- The real major codes and names. Mock data uses CS, PE, LOG and UPAD, and a major from an upload gets its code as its name until it is renamed under Majors.

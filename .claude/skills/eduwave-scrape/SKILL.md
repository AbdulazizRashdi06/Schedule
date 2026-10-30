---
name: eduwave-scrape
description: Fetch (scrape) GUtech's real timetable from EduWave's Advisor Plan page and get it into Classy, then report what changed since the last run. Use when the user asks to rescrape, refetch, sync or update the timetable from EduWave, check what changed in EduWave, or rebuild the upload CSV.
---

# Fetch the timetable from EduWave

Classy's timetable comes from EduWave, where every class has its own date, time and room (GUtech's timetable changes week to week). Background: `docs/scraping.md`. Scripts: `tools/eduwave/`.

## Rules (always)
- **Never ask for, type or store the user's EduWave or university password.** The user signs in themselves. Never try to get past Cloudflare, and never fetch EduWave from the VPS (it gets a 403).
- **Read only.** The only request sent is the one EduWave's own "Sections" button sends (`GetCourseSections`). Never submit anything else on EduWave.
- **One course a second.** About 4–5 minutes for ~220 courses.
- `imports/` holds real university data and stays out of git (it's in `.gitignore`).
- Nothing reaches students until an admin checks and applies it in the dashboard (Upload).

## Way 1: the Classy Sync extension (preferred)
If the user has the extension (`extension/`, setup in `extension/README.md`), they press **Sync now** in its popup, or it runs every morning. The result waits in the dashboard under **Upload → From EduWave**, where they check the changes and press **Apply**. Point them there; there's nothing to run in this session.

## Way 2: in the built-in browser (when the extension isn't available)
Load the `anthropic-skills:built-in-browser` skill before the first browser step.

1. **The user signs in.** Ask them to sign in to EduWave in the browser pane (https://eduwave.gutech.edu.om) and open **Advisor Plan through EduWave's menu**. Opening the page's URL directly gives NotAuthorized. Wait until they say it's open.
   - Warn them: navigating that tab away stops the fetch. They should leave it alone for ~5 minutes.
2. **The course codes to fetch.** Take the Code column of `imports/offered-codes.md` (EduWave spelling, like `CS 1 107`), plus any lab codes such as `CS 1 102-L`. If the user names other courses, add them.
3. **Inject the script.** Read `tools/eduwave/inpage-scrape.js` and run its whole text in the EduWave tab with the browser JavaScript tool. It returns `EduWave scrape ready`. Then run `ewStart([...codes])` with the code list as a JSON array.
4. **Wait and check.** Every ~60–90 seconds run `ewStatus()`, and tell the user the progress in one short line (`120 of 217`).
   - `status: 'paused'`, with a sign-out message: ask the user to sign in again and reopen Advisor Plan through the menu. Then inject the script again (it keeps its progress in `window.__ew`) and run `ewResume()`.
   - `failed` lists codes EduWave didn't answer twice. Mention them, and re-run just those if there are many.
5. **What changed.** When `status` is `'done'`, read `imports/ew-old-hashes.txt` (the last run's fingerprints) and run `await ewDiff("<its contents>")`.
   - `added` lines are new or changed.
   - `gone` counts lines from last time that no longer match.
   - Report the changes in plain words, per course: moved room, new time, added or removed class dates, new teacher, new or removed section. To see what a changed line replaced, compare it with the same course's lines in the previous `imports/ew-dated-*.txt`.
6. **Copy the lines out.** `ewLines().length` gives the count. Read them in chunks of ~300 with `ewChunk(0, 300)`, `ewChunk(300, 600)` and so on, and save them in order to `imports/ew-dated-YYYYMMDD.txt` (today's date), one line each. Check the saved line count matches.
   - The browser pane blocks pages from sending to localhost, so copying out through the tool is the way.
7. **Build the upload file.**
   ```
   python tools/eduwave/build_csv.py imports/ew-dated-YYYYMMDD.txt --out imports/upload-<semester>-dated.csv
   ```
   It prints courses, sections and classes, and lists any code without a name. Ask the user for missing names and add them to `imports/offered-codes.md`. It also writes `imports/ew-dated-YYYYMMDD-hashes.txt`. Copy that over `imports/ew-old-hashes.txt`, so the next run diffs against this one.
8. **Upload.** The user uploads the CSV in the dashboard: **Upload → pick the semester → upload the CSV → check the preview → Apply**. Tell them the file path, and the changes from step 5, so they know what the preview should show.

## Data format
`CODE|section|teacher|HH:MM-HH:MM|room|days`. Days count from 1 Aug 2026 (day 0). `59w3` means day 59 and every 7 days after it, 3 days in all. Parts are joined with `.`, as in `52.53.59w3.85.92`. Fingerprint = the first 10 hex characters of the line's SHA-1.

If the semester starts in another year, change `EPOCH` in both `tools/eduwave/inpage-scrape.js` and `tools/eduwave/build_csv.py`, and check the type and elective lists at the top of `build_csv.py`.

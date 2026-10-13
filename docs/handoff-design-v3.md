# Handoff: design v3 build

## 1. Branch

- Branch: `design-v3` (the same commits are also on `claude/design-handoff-v3-p9po7w`, the branch this cloud session was told to use).
- Last code commit: `c668295`. This file is committed on top of it; `git log -1 design-v3` shows the final hash.
- Not merged, not deployed.

## 2. What was built

Student (mobile, 390 wide, no sideways scroll, checked in a headless browser):

| Artboard | Screen | Notes |
|---|---|---|
| `SignIn` | Sign in: ID | "Ahlan.", blocks drop in, dash added as you type, check when valid, invalid-format error with shake, rate-limit countdown ring (from the server's 429 "Wait N seconds"). Staff link goes to `/admin/login`. |
| `Code` | Sign in: code | Six boxes on one hidden input, Checking…, wrong code (shake, "N tries left", boxes clear), expired / too many tries (Send a new code), resend countdown, "New code sent" toast, boxes fill navy on success, "You're in." for new students. Test-mode code still shown. |
| `Major`, `Year`, `Core`, `LS`, `Electives` | Setup | 5-bar progress (current bar grows), Year pre-picks the ID guess with a From ID badge and one pulse, never auto-advances. Course cards with section chips, live clash notice, Continue blocked on clashes or missing sections. "Picked last semester, still need it?" tag. |
| `Done` | You're set. | Picked courses list, Show me today, Change my picks. |
| `Waiting` | Not out yet. | Shimmer week, Notify toggle (visual only, saved locally), log out / other ID, change major or year. |
| `Main`, `TodayNext`, `TodayDone`, `TodayFree`, `TodayEmpty` | Today | MENU pill, live card with progress, Next in X min (number slides in on change, hint line), That's it + next day's first class, Free day (sky) + Back on {day}, No classes yet + Pick my courses. |
| `Menu` | Menu sheet | ID, major/year, email, links, Admin (STAFF) for owners, Light/Dark/Auto switch, Log out in red. |
| `Week` | Schedule | Kept the v2 build; restyled header (sky back ring, View group, sky next button) and a skeleton. |
| `Course` | Course | When & where tiles (sky for the live one), lecturer, Remind me button still says reminders are coming soon. |
| `EditCourses` | Edit my courses | Rows with CORE/LS/ELECTIVE/NEW/SECTION CHANGED/DROPPED, inline chips with clash check, Drop/Undo without rows jumping, Add sheet with search, save bar with change count, Saving…, Saved toast. |
| `Loading`, `Offline`, `NotFound` | Shared states | Today skeleton (also shown while the session loads), offline banner with Retry → Trying… → Still offline over the saved day, Not in your timetable (and a generic Nothing here page for unknown routes). |

Dashboard (fluid, checked at 1280 and 390):

| Artboard | Screen |
|---|---|
| shell | Sidebar: Uni Schedule · Staff, semester picker, Home/Upload/Courses/Majors, OWNERS: Students, Accounts; name, role, Change password, Log out. Wraps above the content on narrow screens. |
| `StaffSignIn` | Staff. panel, Show/Hide, empty/wrong/locked errors with shake. |
| `ChangePassword` | New password. with live checklist (12+, number, not username) and match check. |
| `DashHome` | Semester + published date, 4 hairline stats, Needs attention, Recent uploads. |
| `Upload` | Semester → File (drop or choose, or paste) → Check (5 tabs) → Publish / Published. |
| `Courses` | Search (code, title, instructor), All / Needs fix / Changed, table with status. |
| `CourseAdmin` | Section table, Edit side panel (day + slot per class, room, instructor), SAVED tag, toast. |
| `Majors` | List + add inline, selected major with Rename/Remove, elective ticks save at once. |
| `Students` | Search by ID, All / Stuck in setup / Done, table with courses and last seen. |
| `StudentDetail` | Mono ID heading, picks, read-only 6-slot week grid (lectures navy), reset/owner/remove actions kept. |
| `Accounts` | Rows with role tag and last sign-in, Reset password shows the temporary password inline, Add account modal with role cards. |

## 3. Backend and API changes

One migration: `20261013141344_UploadLog` (new `UploadLogs` table). Nothing removed; existing responses only gained fields.

- Applying an upload writes an `UploadLog` row (file name, who, added/changed/removed).
- `GET /admin/stats` adds `sectionEditsThisWeek`, `attention { removed[], noRoom[], stuckInSetup }`, `recentUploads[]`.
- `GET /admin/semesters/{id}/courses` adds `noRoom`, `removedPicks`, `changed`; `q` also matches instructors.
- `GET /admin/students` adds `courses`; `GET /admin/students/{id}` adds `setupCompletedAt`.
- `POST /auth/change-password` now needs 12+ characters, a number, and no username in it (was 10+ characters). The existing auth test now uses a password with a number.

## 4. Results

- `dotnet test`: 72 passed, 0 failed (64 before + 8 new in `DashboardTests.cs`).
- `npx tsc -b`: no errors. `npx eslint .`: no errors or warnings. `npm run build`: OK (JS 406 kB, 122 kB gzip).
- Clicked through every student and dashboard screen with Playwright against the dev API, with pinned clocks for the Today states; no console errors, no horizontal scroll at 390.

## 5. Skipped or approximated

- Dark palette: the Theme switch saves the choice and says dark colours are coming; the app stays light.
- Arabic / RTL: not done.
- Reminders and notifications: Course button and Waiting "Notify me" are visual only.
- Upload "Confirm and skip bad rows": the server still refuses files with error rows, so the button reads "Fix N bad rows first".
- "ID not found" on sign-in: never happens, anyone with a valid ID can register.
- Lecturer office/email on Course: no data, not shown.
- "Students signed in, of 413": there's no roster, so it's just the count of students.
- Offline only covers the API failing after the app has loaded; there's no service worker, so a cold start with no network won't load the app.
- Banners (13): dropped per the spec.

## 6. Decisions

- Setup step label shows only the step name ("CORE"), following the spec's "never Step 1" rule over the artboard's "STEP 3 OF 5 · CORE".
- Clashes now block Continue in setup and Save in Edit my courses (spec), where v2 only warned.
- Core step keeps the old "add a core course from another year" view, as a link under the list.
- Electives "Finish setup" saves and finishes in one go, then shows You're set.
- Upload: for a draft semester the last step offers Save as draft or Publish; for the live semester Publish applies the file. A draft with courses can be published without a new file.
- Section edit: day + slot dropdowns write the slot's start/end; a meeting at a non-slot time keeps its time until a slot is picked. Rooms stay per meeting.
- Majors need a code (used in uploads), so "add a major" has code + name fields.
- Accounts: Reset password runs at once (as designed) and shows the new temporary password inline; Remove still asks first. Change password moved to the sidebar.
- After a forced first sign-in the temporary password is carried to the New password screen, so it isn't asked twice.
- Countdown hint: under 20 min "Head over now", under an hour "Time for a coffee first", midday "Time for lunch first", otherwise "Plenty of time".

## 7. Known issues and follow-ups

- Dark theme, Arabic, reminders, notify-on-publish.
- The upload step has no real upload progress (the bar runs while the preview request is in flight).
- Section times that aren't on a slot show as a disabled "custom" option in the editor.
- Spec open items still open: end of day 18:15 vs 18:45, placeholders, em-dash cleanup in copy that comes from the server.

## 8. Deploy (from the laptop)

1. `git fetch origin && git checkout main && git merge origin/design-v3 && git push`
2. From the repo root: `./deploy/deploy.sh` (needs SSH, which campus Wi-Fi blocks). The new migration runs at startup.
3. Check https://schedule.173-249-40-122.sslip.io: student sign-in, Today, and `/admin` (Home should show Needs attention and Recent uploads after the next upload).

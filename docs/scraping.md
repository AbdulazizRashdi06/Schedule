# Getting the timetable from EduWave

How Classy's real timetable is pulled out of GUtech's EduWave and loaded into the app. It's done by hand, from a signed-in browser, about once a week or whenever the timetable changes.

## The easy way: Classy Sync
The **Classy Sync** browser extension (`extension/`, see its README) does steps 1–6 below by itself: it fetches every course in the background, sends the result to Classy, and staff apply it from **Upload → From EduWave**. It can sync every morning. The steps below are the manual way, and what the extension does under the hood.

## What you need
- A student account that can open **Advisor Plan** in EduWave.
- The list of course codes to fetch, which is in `imports/offered-codes.md` together with their names and majors.
- The browser pane in Claude (or any browser where you can run a script on the EduWave page).

## Why it runs in your browser
- EduWave sits behind **Cloudflare**. Requests from a server or a script outside a real browser get a **403 "Attention Required"** page, so the VPS can't fetch it, and we don't try to get around that.
- The script runs **inside your signed-in EduWave tab**, so it uses your normal session. No password is stored or typed by a script.
- The browser pane also blocks sites from sending data to programs on the laptop, so the results are copied out of the tab in chunks rather than posted to a local receiver.

## The process

### 1. Sign in and open Advisor Plan
Sign in to EduWave and open **Advisor Plan** (`/EduWaveHE/EduWaveHEPortal/AdvisorStudentPlan.aspx`). The script needs this page because it owns the form that loads course sections.

### 2. Fetch every course
Each "Sections" button on that page does an ASP.NET **postback**: it sends the page's form with `__EVENTTARGET = "<code>,<name>"` and `__EVENTARGUMENT = "GetCourseSections"`. The server only reads the code, so `"CS 2 114,"` (with the comma) is enough.

The script repeats that postback for each code with `fetch()`, **one per second**, and reads the sections table `ctl00_cphPageBody_gvCourseSections` from each reply:

```js
const form = document.forms[0]
const fd = new FormData(form)                    // includes __VIEWSTATE
fd.set('__EVENTTARGET', code + ',')
fd.set('__EVENTARGUMENT', 'GetCourseSections')
const html = await (await fetch(location.href, { method: 'POST', body: new URLSearchParams(fd), credentials: 'include' })).text()
const table = new DOMParser().parseFromString(html, 'text/html').getElementById('ctl00_cphPageBody_gvCourseSections')
```

For each section row it reads the **section number**, the **teacher** (first and last name cells), and three lists that line up one-to-one: **dates** (`Mon(21/09/2026)`), **times** (`11:30 AM-01:00 PM`) and **rooms**. Every class has its own date, time and room, because GUtech's timetable changes week to week.

If a reply comes back as the login page, the session has expired. The script stops, and you sign in again.

### 3. Shrink the result
5,000+ dated classes are too much to copy out as they are, so the script:
- **Groups** classes that share course, section, teacher, time and room.
- **Stores dates as day numbers** counted from 1 Aug 2026.
- **Writes weekly runs** as `50w14`, meaning day 50 and then every 7 days, 14 times.

Each group becomes one line:
```
CS 2 114|1|Nabil Sahli|11:30-13:00|GU1 102|51w3.79w3.95
```
For the Winter 2026 semester that's about 970 lines and 57 KB, instead of 5,753 rows.

### 4. Copy it out of the tab
Read the text from the tab in three chunks of about 330 lines, and save them as `imports/ew-dated-1.txt`, `-2.txt` and `-3.txt`. Check that the line count matches what the script reported.

### 5. Build the upload file
A short Python step turns the lines back into one row per class:
- Expands `50w14` and the other day numbers back into real dates.
- Adds each course's **name**, **major**, **type** and **year** from `imports/offered-codes.md`:
  - Type: `LS` for Life Skills; `Elective` for IBSM-FE, IBSM-TM, LOG-E and the listed electives; otherwise `Core`.
  - Major: the code's prefix, for core courses.
  - Year: the middle digit of the code.
- Writes `imports/upload-winter-2026-dated.csv` with the columns `Course Code, Course Name, Major, Type, Year, Credits, Section, Instructor, Date, Time, Room`.

Check the totals against the script's numbers (courses, sections, classes), and check that every course has a name.

### 6. Upload
In the dashboard: **Upload** → pick the semester → upload the CSV → check the preview (added, changed, removed) → apply. Students with picks in a changed section get the **Changed** badge.

## Comparing with the last run
To see what changed without copying everything again:
1. Fingerprint each line of the previous run (the first 10 characters of its SHA-1) and save them as `imports/ew-old-hashes.txt`.
2. In the tab, fingerprint the new lines the same way and compare.
3. Read back only the lines that are new or gone. Those are the changes: moved times, new rooms, new or cancelled sections, teacher changes.

## Rules
- **One request per second.** A full run of 217 courses takes about 4 minutes and looks like normal browsing.
- **Your own account, your own session.** Never store an EduWave password or try to get past Cloudflare.
- The files in `imports/` hold real university data and stay out of git (`imports/` is in `.gitignore`).
- Placeholder sessions come through as they are: internships, theses, field trips, and teachers like `CS4 CS4` or `LOG4 LOG4`.

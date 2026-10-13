# Uni Schedule: Design Handoff (v3)

A mobile timetable app for university students, plus a desktop dashboard for staff (RSA and owners).

- **Design canvas:** Uni Schedule (Design artifact), 31 artboards.
- **Click-through:** every artboard marked interactive works in Play.
- **Data:** all courses, rooms, IDs, numbers and account names are sample data. Lecturer names, the email domain and credits are `[placeholders]`.

---

## 1. Screen map

| # | Screen | Artboard | Platform |
|---|---|---|---|
| — | Today (live class) | `Main.dc.html` | Mobile |
| — | Schedule (Day / Week) | `Week.dc.html` | Mobile |
| — | Course detail | `Course.dc.html` | Mobile |
| 1 | Sign in: university ID | `SignIn.dc.html` | Mobile |
| 2 | Sign in: 6-digit code | `Code.dc.html` | Mobile |
| 3 | Setup: Major | `Major.dc.html` | Mobile |
| 4 | Setup: Year | `Year.dc.html` | Mobile |
| 5 | Setup: Core courses | `Core.dc.html` | Mobile |
| 6 | Setup: LS courses | `LS.dc.html` | Mobile |
| 7 | Setup: Electives | `Electives.dc.html` | Mobile |
| 8 | Setup: Done | `Done.dc.html` | Mobile |
| 9 | Waiting (no semester published) | `Waiting.dc.html` | Mobile |
| 10 | Edit my courses | `EditCourses.dc.html` | Mobile |
| 11 | Menu / profile sheet | `Menu.dc.html` | Mobile |
| 12a | Today: Next in X min | `TodayNext.dc.html` | Mobile |
| 12b | Today: That's it | `TodayDone.dc.html` | Mobile |
| 12c | Today: Free day | `TodayFree.dc.html` | Mobile |
| 12d | Today: No classes yet | `TodayEmpty.dc.html` | Mobile |
| 13 | Banners (section removed, changed) | — | **Dropped**, not designed |
| 14 | Staff sign-in | `StaffSignIn.dc.html` | Desktop |
| 15 | Change password (forced at first sign-in) | `ChangePassword.dc.html` | Desktop |
| 16 | Dashboard home | `DashHome.dc.html` | Desktop |
| 17 | Upload flow | `Upload.dc.html` | Desktop |
| 18 | Courses | `Courses.dc.html` | Desktop |
| 19 | Course detail and section edit | `CourseAdmin.dc.html` | Desktop |
| 20 | Majors | `Majors.dc.html` | Desktop |
| 21 | Students (owners only) | `Students.dc.html` | Desktop |
| 22 | Student detail (owners only) | `StudentDetail.dc.html` | Desktop |
| 23 | Accounts (owners only) | `Accounts.dc.html` | Desktop |
| 24a | Loading | `Loading.dc.html` | Mobile |
| 24b | Error / offline | `Offline.dc.html` | Mobile |
| 24c | Not found | `NotFound.dc.html` | Mobile |

- **Mobile frames:** 390 × 844. No status bar is drawn, so respect the phone's safe area.
- **Desktop frames:** fluid pages, designed at 1280 wide. The sidebar wraps above the content on narrow screens.

---

## 2. Time model

Classes are stored by **slot**, not by clock time.

- Every class is **1h30**, with **15-minute breaks** between classes.
- The week runs **Sunday to Thursday**.

| Slot | Start | End |
|---|---|---|
| 0 | 08:00 | 09:30 |
| 1 | 09:45 | 11:15 |
| 2 | 11:30 | 13:00 |
| 3 | 13:15 | 14:45 |
| 4 | 15:00 | 16:30 |
| 5 | 16:45 | 18:15 |

- The week grid's side labels are slots, never hours.
- Two sessions **clash** when they share the same `day + slot`.
- **Open question:** the day was described as ending at 18:45, but slot 5 ends at 18:15. Is there a 7th slot?

---

## 3. Design tokens

### Colour

| Token | Hex | Use |
|---|---|---|
| `cream` (beige) | `#F8F3EA` | **Default background for the whole app and dashboard**, text on navy, **lecture** blocks |
| `navy` | `#0B1957` | Text, primary buttons, hero cards, active nav, the Week screen background |
| `sky` | `#9ECCFA` | Accent: hero numbers on navy, **tutorial** blocks, Course and Free-day backgrounds |
| `ice` | `#D1E8FF` | Secondary text on navy, **lab** blocks, chips, info notices |
| `paper` | `#FFFDF8` | Input fields and raised cards on cream |
| `muted` | `#3A4675` | Secondary text on cream (passes AA) |
| `navy-800` | `#13226A` | Unselected tiles on navy |
| `navy-700` | `#1F2F74` | Tracks on navy |
| `line` | `rgba(11,25,87,0.10–0.20)` | Hairlines and input outlines on cream |
| `alert` | `#B42318` | Error text on cream |
| `alert-bg` / `alert-ink` | `#FBE3DF` / `#8F1D12` | Clash and error notices |

**Rules**

- **Class types:** Lecture is cream, Lab is ice, Tutorial is sky. Text on blocks is always navy.
- **Lectures on cream:** on a cream page (the student-detail grid), lecture blocks are navy so they stay visible.
- **Contrast:** never put sky or ice text on cream.
- **Shadows:** always tinted navy (`rgba(11,25,87,…)`), never black.

### Type

| Role | Font | Spec |
|---|---|---|
| Display ("Today.", "You're set.") | Bricolage Grotesque | 52–84px / 800 / −0.05 to −0.06em, line-height 0.82–0.9 |
| Hero number | Bricolage Grotesque | 96px / 800 / −0.06em |
| Titles | Bricolage Grotesque | 18–40px / 800 |
| Body | Bricolage Grotesque | 14–19px / 600 |
| Labels, times, IDs, codes | JetBrains Mono | 10–13px / 700 / +0.08 to 0.14em, UPPERCASE |
| Code entry and IDs in inputs | JetBrains Mono | 28–30px / 700 |

### Shape and spacing (one rule, used everywhere)

- **Buttons, pills and chips:** fully rounded (999px).
- **Cards:**
  - Hero cards: 32px corners.
  - Content cards and tiles: 20–24px.
  - Inputs: 14–22px.
  - Section chips: 12–14px.
- **Padding:** 24px for mobile content, 12px for cards that run edge to edge, 36–40px for desktop pages.
- **Touch targets:** 44px minimum. Primary buttons are 56–60px tall.
- **Dividers:** 2.5px navy rule above a list or section, 1.5px hairlines between rows.

---

## 4. Motion spec

The spec follows two skills: Emil Kowalski's design-eng and animate skills, and the anti-slop "taste" skill.

| Token | Value | Use |
|---|---|---|
| `--ease-out` | `cubic-bezier(0.23, 1, 0.32, 1)` | All entrances and taps |
| `--ease-in-out` | `cubic-bezier(0.77, 0, 0.175, 1)` | Things moving on screen: progress fills, the theme switch indicator |
| `--ease-drawer` | `cubic-bezier(0.32, 0.72, 0, 1)` | Bottom sheets and side panels |

| Pattern | Spec |
|---|---|
| Button press | `transform: scale(0.97)` on `:active`, 160ms ease-out |
| Entrance (first-time screens only) | opacity 0 → 1, translateY 14px → 0, blur 4px → 0, 520–560ms, staggered 50–60ms per item |
| Sign-in decor | Coloured blocks drop in from translateY(−28px) and a slight rotate, 700ms, staggered |
| Error | Shake on the field: 340–380ms, alternating A/B keyframes so it can replay |
| Code digit | Pops in with scale 0.9 → 1 in 220ms. On success the boxes fill navy one after another (45ms apart) |
| Bottom sheet | translateY(100%) → 0, 320–340ms drawer curve. Scrim fades in 240ms |
| Side panel (dashboard) | translateX(105%) → 0, 300ms drawer curve |
| Modal (Accounts) | scale 0.96 → 1 plus fade, 200ms, centred |
| Toast | Slides from the bottom and leaves the same way. 240–260ms with transitions, not keyframes, so it can be interrupted |
| Skeleton loader | Linear shimmer sweep, 1400ms. Never a spinner for page loads |
| Dashboard | Press feedback and hover tints only. It's used all day, so almost nothing animates |

- **Never:** start from `scale(0)`, use `ease-in` on UI, use `transition: all`, or go over 300ms for frequent UI.
- **Hover effects:** wrapped in `@media (hover: hover) and (pointer: fine)`.
- **Reduced motion:** `prefers-reduced-motion` turns movement into plain fades and removes shakes. It's on every screen.

---

## 5. Screens

### Sign in and setup (1–9)

1. **University ID**
   - **Input:** "Ahlan." headline, label above the field. The dash is added as you type (`24-0161`), and a check appears when the ID is valid.
   - **Errors:**
     - **Invalid format:** "IDs look like 24-0161…"
     - **ID not found:** "We can't find…"
     - **Too many tries:** "Wait 34 seconds", with a countdown ring and the button disabled until it ends.
   - **Next:** "Send me a code" goes to screen 2.
2. **Code**
   - **Input:** six boxes backed by one hidden input (`inputmode=numeric`, `autocomplete=one-time-code`). Shows "Checking…" while it verifies.
   - **Wrong code:** the boxes shake and it says "2 tries left", then the boxes clear.
   - **Expired code:** "Send a new code" becomes the main button.
   - **Resend:** a 30-second countdown, then a "New code sent" message.
   - **Success:** "You're in." with a check that draws itself, then "Let's set you up".
3. **Major:** a single-choice list. Continue appears once one is picked.
4. **Year:** a 2×2 grid. The year guessed from the ID prefix is pre-selected with a "From ID" badge and one pulse. **It never moves on by itself.**
5. **Core courses**
   - **Defaults:** all ticked, with section chips `[1][2][3]` and the meeting times under them.
   - **Clashes:** a red notice names the other course and the time. Continue is blocked ("Fix 1 clash to continue").
6. **LS courses:** optional. Section chips open when you tick a course, and clashes are checked against the core picks. The button reads "Skip for now" when nothing is picked.
7. **Electives**
   - **List:** only electives allowed for the major. A "Picked last semester" tag shows where it applies.
   - **Finish:** "Finish setup", or "Skip and finish" when nothing is picked.
8. **Done:** "You're set." with a list of the picked courses (code, name, type and section), then "Show me today". **No weekly grid here, because timetables change.**
9. **Waiting:** an empty week grid with a shimmer and "Not out yet." The "Notify me" button switches to "We'll ping you".

**Progress:** setup shows 5 bars at the top. The current bar fills in with the ease-in-out curve. Labels name the step ("Major", "Core"), never "Step 1".

### Student, after setup (10–12)

10. **Edit my courses**
    - **Course rows:** each shows the course code, name and type tag (CORE, LS, ELECTIVE, NEW, SECTION CHANGED or DROPPED).
    - **Change section:** inline chips, with live clash checking.
    - **Drop:** crosses the row out and shows Undo. The row doesn't disappear, so nothing jumps.
    - **Add course:** opens a bottom sheet with search. Only courses allowed for the major are listed.
    - **Save:** a bar slides up showing how many changes there are. It's blocked while there's a clash. Saving shows "Saving…", then a "Saved" message.
11. **Menu / profile**
    - **Profile:** a bottom sheet with the ID, major and year, and email.
    - **Links:** Today, Full week, Edit my courses, and Admin (staff only, marked STAFF).
    - **Theme:** a Light / Dark / Auto switch with a sliding indicator.
    - **Log out:** shown in the alert colour.
12. **Today states**
    - **Next in X min:** the countdown number slides in each time it changes. Under it is a hint ("Time for lunch first"), then "Later today".
    - **That's it:** shows tomorrow's first class.
    - **Free day:** a sky card, then "Back on Thursday".
    - **No classes yet:** "Pick my courses".

**Today screens:** use a MENU pill top-right in place of the old notifications bell and bottom nav (both were removed from `Main.dc.html`).

### Shared states (24)

- **Loading:** grey placeholder shapes in the exact layout of Today, with a shimmer.
- **Offline:** a navy banner, "You're offline. Showing what we saved at 08:12". The Retry button shows "Trying…", then "Still offline". The saved Today content stays visible below.
- **Not found:** "Not in your timetable." Main action: back to Today. Second action: add the course.

### Dashboard (14–23)

**Shell** (left sidebar on every page):

- **Top:** "Uni Schedule · Staff".
- **Links:** Home, Upload, Courses, Majors. Under an OWNERS label: Students and Accounts.
- **Bottom:** the user's name and role, and Log out.
- **Active link:** filled navy.

14. **Staff sign-in**
    - **Layout:** navy "Staff." panel on one side, the form on the other.
    - **Password:** has a Show / Hide toggle.
    - **Errors:** wrong password, locked for 15 minutes, and empty fields. The form shakes.
15. **Change password**
    - **Rules:** a live checklist (12+ characters, has a number, isn't your username) plus a "type it again" match check.
    - **Save:** stays disabled until every rule passes.
16. **Home**
    - **Header:** semester and publish date, and an "Upload a new file" button.
    - **Stats:** 4 numbers separated by hairlines rather than cards.
    - **Needs attention:** removed sections, sections without a room, students stuck in setup.
    - **Recent uploads.**
17. **Upload:** four steps. Each step slides in.
    - **Semester:** pick an existing one or create a new one.
    - **File:** drop or choose a file, then a progress bar, then the file name and row count.
    - **Check:** five tabs (Added, Changed, Removed, Errors, New majors) with a table of rows for each. Errors are shown in red.
    - **Publish:** shows who will be affected, then "Publishing…", then "Published."
18. **Courses**
    - **Find:** search by code, title or instructor. Filters: All, Needs fix, Changed.
    - **Table:** code, title, majors, sections, students, status.
19. **Course detail**
    - **Table:** one row per section, with an Edit button.
    - **Edit panel:** opens from the right with two meetings (day and slot dropdowns), room and instructor. It shows how many students will see the change.
    - **Save:** shows "Section N saved. X students notified." and a SAVED tag on the row.
20. **Majors**
    - **List:** majors with student counts. Add a major inline.
    - **Selected major:** Rename, and ticks for allowed electives. Changes save straight away.
21. **Students**
    - **Find:** search by ID. Filters: All, Stuck in setup, Done.
    - **Table:** ID, major, year, number of courses, setup status, last seen.
22. **Student detail:** the ID heading, major and year, setup date. Shows their picks and a read-only grid of 6 slots × 5 days.
23. **Accounts**
    - **List:** username, role (Owner or RSA), last sign-in.
    - **Reset password:** shows a temporary password inline.
    - **Add account:** a pop-up with username and role cards. Creating the account also shows its temporary password.

---

## 6. Data model

```ts
type Day = 'sun' | 'mon' | 'tue' | 'wed' | 'thu';
type Slot = 0 | 1 | 2 | 3 | 4 | 5;
type ClassType = 'lecture' | 'lab' | 'tutorial';
type CourseKind = 'core' | 'ls' | 'elective';

interface Semester { id: string; name: string; status: 'draft' | 'published'; publishedAt?: string; }
interface Major { id: string; name: string; allowedElectives: string[]; }
interface Course { code: string; title: string; type: ClassType; credits: number; majors: string[]; kind: CourseKind; }
interface Section {
  courseCode: string; n: number;
  meetings: { day: Day; slot: Slot }[];
  room: string | null; instructor: string;
}
interface Student {
  id: string;              // "24-0161" (the first two digits are the intake year, used to guess Year)
  email: string;           // uni email, OTP target
  majorId: string; year: 1 | 2 | 3 | 4;
  picks: { courseCode: string; section: number }[];
  setupDoneAt?: string; lastSeenAt?: string;
}
interface StaffAccount { username: string; role: 'owner' | 'rsa'; mustChangePassword: boolean; lastSignInAt?: string; }

interface UploadPreview {
  added: Section[]; changed: { before: Section; after: Section }[];
  removed: (Section & { affectedStudents: number })[];
  errors: { row: number; message: string }[];
  newMajors: string[];
}
```

The app works out the following itself; none of it is stored:

- **Clashes:** two picks share the same `day + slot`.
- **Today's state:** live, next-in, that's-it, free day, or empty.
- **Minutes left and progress.**
- **Year guess:** from the ID prefix and the current semester.
- **"Needs attention":** the dashboard list on Home.

---

## 7. Accessibility

- **Controls:** real `<button>`, `<a>` and `<input>` everywhere. Labels sit above inputs, and no input relies on its placeholder as a label.
- **Choices:**
  - Single-choice lists use `role=radio` with `aria-checked`.
  - Courses use `role=checkbox`.
  - Tabs use `role=tab` with `aria-selected`.
- **Feedback:** `aria-live` on error messages, countdowns, "Saved" messages and offline status. `aria-invalid` on fields with errors.
- **Pop-ups:** sheets, the side panel and the modal use `role=dialog` with `aria-modal`. Tapping the scrim closes them.
- **Contrast:** all text passes WCAG AA. Grey-ish text on cream uses `#3A4675`.
- **Motion:** respects `prefers-reduced-motion`. Hover effects only run on devices with a mouse.

---

## 8. Open items

- [ ] **End of day:** confirm whether it's 18:15 or 18:45 (see §2).
- [ ] **Banners (13):** dropped. Decide how students learn about a removed or changed section, for example through a push notification only.
- [ ] **Dark theme:** the switch is in the Menu, but no dark palette is designed yet.
- [ ] **Dashes:** remove the em-dashes and extra "·" separators left in the early screens (sign-in and setup). The taste skill bans them.
- [ ] **Placeholders:** replace the email domain, lecturer names and credits, and swap in real course, major and section data.
- [ ] **Arabic / RTL:** pick an Arabic display font to pair with Bricolage Grotesque.
- [ ] **Staff sign-in links:** the "Staff? Sign in here" link on screen 1 and the Admin entry in the Menu both need routing to the dashboard (desktop or web).

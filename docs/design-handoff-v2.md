# Uni Schedule — Design Handoff (v2)

This is a mobile app that shows students their class schedule. The design canvas is **Uni Schedule** and has 3 artboards: Today, Schedule (day and week views) and Course.

Frame size: **390 × 844**. The artboards don't draw a status bar, so leave room for the phone's safe area.

---

## 1. What's in this version

| Screen | Artboard | Status |
|---|---|---|
| Today | `Main.dc.html` | Done |
| Schedule (Day / Week toggle) | `Week.dc.html` | Done and interactive |
| Course detail | `Course.dc.html` | Done |
| Login (uni ID + OTP sent to uni email) | — | Not designed yet |
| Onboarding (major → courses → sections) | — | Not designed yet |
| Profile / settings | — | Not designed yet |
| Admin dashboard (users, course and schedule uploads) | — | Not designed yet |

**Changes since v1:**

- The week grid now goes by class slot instead of by hour.
- The Day / Week toggle and the buttons to move between weeks were added.
- Lectures are now beige.
- Deadlines and attendance were taken off the Course page.
- The live pill, the deadline chip and the bottom nav were taken off the Today screen.

---

## 2. Time model (important)

The timetable is built from **slots**, not hours. Every class is **1h30**, with **15-minute breaks** in between. The week is **Sunday to Thursday**.

| Slot | Start | End |
|---|---|---|
| 1 | 08:00 | 09:30 |
| 2 | 09:45 | 11:15 |
| 3 | 11:30 | 13:00 |
| 4 | 13:15 | 14:45 |
| 5 | 15:00 | 16:30 |
| 6 | 16:45 | 18:15 |

- Store a class by its **day and slot index**. Work out the clock times from the slot table above, and don't store free-form times.
- The left side of the week grid shows the slot labels (start and end time), not hour marks.
- **Open question:** the day was described as ending at 18:45, but the last slot ends at 18:15. Confirm whether there's a 7th slot or a longer last class.

---

## 3. Design tokens

### Colour

| Token | Hex | Use |
|---|---|---|
| `navy` | `#0B1957` | Main text, dark backgrounds (Schedule screen, cards) |
| `cream` / beige | `#F8F3EA` | Today background, text on navy, **lecture** blocks, selected tiles |
| `sky` | `#9ECCFA` | Accent colour: live progress, hero numbers, **tutorial** blocks, Course background |
| `ice` | `#D1E8FF` | Secondary text on navy, **lab** blocks |
| `navy-700` | `#1F2F74` | Progress track |
| `navy-800` | `#13226A` | Day tiles that aren't selected, secondary buttons |
| `navy-line` | `#2C3D85` / `#3A4E9C` | Dividers, slot lines in the grid |
| column tint | `rgba(158,204,250,0.06)` (today: `0.14`) | Day columns in the week grid |

**Class type colours:** Lecture = cream, Lab = ice, Tutorial = sky. Text on every class block is navy.

Never put sky or ice text on a cream background.

### Type

| Role | Font | Spec |
|---|---|---|
| Display ("Today.", "Wednesday.") | Bricolage Grotesque | 60–76px / 800 / −0.05 to −0.06em |
| Hero time (Today card) | Bricolage Grotesque | 96px / 800 / −0.06em |
| Screen title (course name) | Bricolage Grotesque | 52px / 800 / −0.04em |
| Week title ("Week 7") | Bricolage Grotesque | 30px / 800 |
| Card title | Bricolage Grotesque | 18–30px / 800 |
| Body | Bricolage Grotesque | 13–16px / 600 |
| Labels, times, metadata | JetBrains Mono | 9–12px / 700 / +0.08 to 0.14em, UPPERCASE |

Fonts come from Google Fonts: Bricolage Grotesque (opsz 12–96, weights 400/600/800) and JetBrains Mono (weights 500/700).

### Shape and spacing

- **Corner radius:** big cards 32px, day tiles 20px, list cards 18–20px, week blocks 12px, day columns 14px. Pills and round buttons are fully rounded (999px).
- **Padding:** 24px for content, 12px for cards that run edge to edge.
- **Touch targets:** at least 44px. Icon buttons are 44–48px and the main button is 56px tall.
- **Icons:** Lucide-style stroke icons with a 2.2–2.4px stroke and round caps. No emoji.

---

## 4. Screens

### 4.1 Today (`Main.dc.html`)
The background is cream.

- **Header:** a mono line with the date and week (`SUN · 11 OCT · WEEK 7`), the word "Today." at 76px, and a round outline button for notifications.
- **Live card (navy):**
  - the start time in sky at 96px
  - the course title and "Lecture · Lecturer"
  - a progress bar with the start time, `XX MIN LEFT` and the end time under it
- **Up next:** a list of rows. Each row has the start time (34px), the course name, `TYPE · ROOM` in mono, and an arrow icon. Tapping a row opens Course. `FULL WEEK →` opens Schedule.
- **States that still need designing:**
  - between classes (the card should become "Next in X min")
  - no classes left today
  - a day with no classes

The `accent` tweak changes the live colour; the default is sky.

### 4.2 Schedule (`Week.dc.html`), interactive
The background is navy. It opens in **Week** view.

**Header:**

- a back button and a **DAY | WEEK** toggle (the active side is cream)
- a week switcher: a ‹ button, then "Week N" with the date range under it (plus `· THIS WEEK` on the current week), then a › button
- the arrows work in both views and update the dates

**Week view:**

- Columns: a 38px column for slot labels, then 5 equal day columns with 5px gaps.
- Day headers are buttons. Today's is a cream tile. Tapping a day header opens Day view for that day.
- **6 slot rows, each 86px tall with 8px gaps.** A thin line marks each break, and the lines run across the whole week.
- Each class block fills its slot and shows the course code (16px/800), the start time (mono 9px) and the room (11px).
  - Tapping a block opens Day view for that day.
- The class happening now has a cream ring around it.
- A cream line marks the current time across the week. It sits behind the class blocks.

**Day view:**

- The title is the day's name ("Wednesday.") with a summary under it: `N CLASSES · total time · first start–last end`.
- Below that is a row of 5 day tiles (76px tall), each showing the day, the date and how many classes it has (`N CLS`). The selected tile is cream.
- The class list shows the start and end time in a left column. Next to it is a card in the class-type colour with `TYPE · CODE`, the course name and the room.
  - The class happening now gets a `● NOW` tag and a ring.
- When there's a gap of at least 30 minutes between classes, a dashed **FREE · duration** row shows it. The 15-minute breaks don't get one.
- Tapping a card opens Course.

**Legend** at the bottom of the screen: Lecture, Lab, Tutorial.

### 4.3 Course detail (`Course.dc.html`)
The background is sky.

- **Header:** a back button (navy) and an outline pill with `[CODE] · SECTION [X]`.
- **Title:** the course name at 52px over two lines, then `Type · N× a week · credits`.
- **Navy card:**
  - **When & where:** one row per weekly session with the day, time and room. The session happening now is shown in sky with `● NOW`, and the next one is marked `NEXT`.
  - **Lecturer:** an avatar with initials, the name, the office and office hours in mono, and a round email button.
  - **Main button:** "Remind me 15 min before" (cream, 56px).

---

## 5. Navigation

```
Today ──(Up next row)──────────────▶ Course ──(back)──▶ Today
  └──(FULL WEEK →)──▶ Schedule[Week] ⇄ Schedule[Day] ──(class card)──▶ Course
```

---

## 6. Data model

```ts
type ClassType = 'lecture' | 'lab' | 'tutorial';
type Day = 'sun' | 'mon' | 'tue' | 'wed' | 'thu';

const SLOTS = [
  ['08:00','09:30'], ['09:45','11:15'], ['11:30','13:00'],
  ['13:15','14:45'], ['15:00','16:30'], ['16:45','18:15'],
] as const;

interface Course {
  code: string;            // "ML"
  title: string;           // "Machine Learning"
  section: string;
  credits?: number;
  lecturer: { name: string; email: string; office?: string; officeHours?: string };
}

interface Session {        // one weekly meeting of a section
  courseCode: string;
  type: ClassType;
  day: Day;
  slot: 0 | 1 | 2 | 3 | 4 | 5;   // index into SLOTS
  room: string;
}
```

The app works out the following itself; none of it is stored:

- The live class, minutes left and progress, from the current time compared with the slot's start and end.
- The week number and date range, from the week shown. The timetable repeats every week.
- The counts for each day and week, the total time, and the FREE gaps (gaps of 30 minutes or more).
- The "Up next" list: the rest of today's sessions.

---

## 7. Accessibility

- Use real `<button>` and `<a>` elements everywhere.
- Icon-only buttons need an `aria-label`. This covers Back, Notifications, Previous/next week and Email lecturer.
- The DAY/WEEK toggle uses `aria-pressed`. The day tiles are tabs with `aria-selected`.
- Each week block needs a full `aria-label`. For example: "Machine Learning, lecture, Sunday 09:45 to 11:15, room B-204".
- The Today progress bar should have `role="progressbar"` with values.
- Class type isn't shown by colour alone: day cards show the type as text, and the week view has a legend.

---

## 8. Open items

- [ ] Confirm when the day ends (18:15 or 18:45), as noted in §2.
- [ ] Replace the placeholders: lecturer name, course code, section, credits, office and office hours, email.
- [ ] All courses, rooms and sessions in the mockups are **sample data**.
- [ ] Design the Today states from §4.1 that aren't designed yet.
- [ ] Design the screens that aren't done yet: login, onboarding, profile and the admin dashboard.
- [ ] Decide how the user reaches profile and settings now that the bottom nav is gone from Today.
- [ ] Arabic / RTL version: pick an Arabic display font to pair with Bricolage Grotesque.

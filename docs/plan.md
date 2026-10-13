# Uni Schedule App: product walkthrough (no tech yet)

## Part A: Student first-time setup

Flow: **Register → Major → Year → Core (auto) → Edit core? → Life Skills → Electives → Done**

### 1. Register / Log in
- Anyone with a valid ID (`YY-NNNN`) can register. No admin approval is needed.
- The student enters their ID, and a code is sent to `yy-nnnn@student.gutech.edu.om`. They enter the 6-digit code.
- A new student goes to Major. A returning student who has finished setup goes straight to the timetable.

### 2. Major
- Majors are shown as cards. The student taps one and presses Next.

### 3. Year
- Chips: Year 1 / 2 / 3 / 4.
- The predicted year (from the ID's intake year) is **already highlighted as picked**. The app does **not** move on by itself; the student confirms by pressing Next or taps another year.

### 4. Core courses (auto-selected)
- All core courses for major + year + current semester are ticked.
- Each course row has its sections inline as chips:
  ```
  ☑ CS201 Data Structures    [1] [2] [3]
  ☑ CS210 Databases          [1] [2]
  ```
- Tapping a chip picks that section, and only one per course can be picked. Each chip shows days/time (and room/instructor on tap).
- If a section clashes with one already picked, its chip shows a ⚠ warning. The student can still pick it.
- Buttons: **Looks right** goes to LS. **Edit core courses** goes to step 5.

### 5. Edit core courses (optional)
- The student can untick courses, or add a core course from another year (for students who are behind or ahead).

### 6. Life Skills
- The LS list is shown with nothing ticked. Ticking a course reveals its section chips, the same as for core courses.
- **Skip** / **Next**.

### 7. Electives
- Only electives the major allows are shown. They work the same as LS: tick, then pick a section chip.
- **Skip** / **Next**.

### 8. Done
- A summary grouped as Core / LS / Electives, showing the picked sections and any clash warnings.
- **Edit** links next to each group, and a **Finish** button that opens the timetable.

### Throughout
- A progress bar and a back arrow on every step. Picks are kept when going back.
- Progress is saved as the student goes, so they resume where they left off.
- The same flow is available later as "Edit my courses".

---

## Part B: Admin dashboard flow (draft)

**Who:** Owners (Abdulaziz + partners) log in with student ID + OTP. RSA logs in with a username + password. See Part B2 for what each can access.

### Home
- A semester switcher at the top (e.g. "Fall 2026").
- Quick numbers: students registered, students who finished setup, number of courses and sections.
- Shortcuts: Upload schedule · Manage students · Edit a section.

### Upload a semester schedule
1. Choose or create the semester (e.g. Spring 2027).
2. Upload one spreadsheet/CSV, one row per section: course code, name, major, type (Core / LS / Elective), year, section number, instructor, days, time, room.
3. **Preview screen:** shows what will be added, with problems flagged, such as a missing room, a bad time or a duplicate section.
4. Confirm, then publish to students.

### Edit a single section
- Search for a course and open it to see its sections.
- Edit the instructor, days, time or room, then save.
- Students enrolled in that section see the change on their timetable right away.

### Manage majors
- A list of majors.
- For each major: which electives it allows. Core courses come from the upload's "major" + "type" columns.

### Manage students
- A searchable list of students showing ID, major, year and setup status.
- Open a student to see their picked courses and sections.
- Actions on a student:
  - **Reset major / reset picks:** they redo setup at their next login.
  - **Remove student.**
  - **Make admin.**

---

## Decided
- **Semester rollover:** publishing a new semester makes every student redo the **full setup**, starting from Major. Their last picks are pre-filled as suggestions.
- **Section changes:** students are told with an **in-app badge only**. The changed slot shows "Changed" on the timetable until the student opens it. No email.
- **Re-upload mid-semester:**
  - Only the rows that changed are updated, and student picks are kept.
  - Before confirming, the admin sees a preview of what changed (added, edited, removed).
  - Students whose section was removed are flagged so they can re-pick.
- **Admin roles:** two levels, Owner and RSA (see Part B2).

## Part B2: Who can do what

| Area | Owners (Abdulaziz + partners) | RSA |
|---|---|---|
| Upload, preview and publish the semester schedule | ✅ | ✅ main job |
| Edit a single section (time, room, instructor) | ✅ | ✅ main job |
| Manage majors and allowed electives | ✅ | ✅ secondary |
| Course stats (e.g. "42 students in CS201-1") | ✅ | ✅ counts only |
| Student list, student picks, reset or remove a student | ✅ | ❌ no student access |
| Add or remove admins | ✅ owners and RSA | ✅ RSA accounts only |

- **RSA dashboard:** the home screen is built around courses: Semester · Upload schedule · Courses & sections · Majors.
  - There is no Students tab.
  - Course pages show enrollment counts only, never names or IDs.
- **Owner dashboard:** everything in RSA's dashboard, plus Students and Admins.
- **RSA login:** username + password on a separate `/admin/login` page.
  - An owner or an RSA user creates the account with a temporary password.
  - The new user must change it at first login.
- **Adding admins:** RSA can create and remove only RSA accounts. Only owners can create owners.
- **Owners** are students too. They log in with their student ID + OTP, and an "Admin" link appears in their menu.

---

## Part C: Timetable (student home)
- **Opens on Today:**
  - Today's classes as cards in time order, with the **next class highlighted**.
  - On a day with no classes: "No classes today", plus a preview of the next class day.
- **Day / Week toggle** at the top:
  - **Day view:** day tabs Sun · Mon · Tue · Wed · Thu, with Sat shown only if one of the student's sections meets on Saturday.
  - **Week view:** a days × times grid. Each block shows the course code and room. On a phone it scrolls sideways.
- **Each class card or block** shows the course code, course name, time, room and instructor.
- **Tapping a class** opens a details sheet with room, instructor and time.
- **"Changed" badge:** shown on a class whose time or room an admin edited, until the student opens it.
- **Clashing classes** are drawn side by side in the week grid with a ⚠ mark.
- **Menu:** Edit my courses · Log out.

## Part D: Edit my courses (mid-semester)
- **One page with all picks:**
  - Core, Life Skills and Electives sections.
  - Each course has its section chips `[1] [2] [3]` with ⚠ on clashes.
  - "+ Add" buttons in each section and a ✕ on each course.
- **Save once** at the bottom. The timetable updates right away.

## Part E: Before a semester is published
- After login, the student can still pick their major and year.
- They then see: "Courses for Fall 2026 aren't published yet. We'll take you to course picking as soon as they are."
- Once an admin publishes, the student's next login continues from the Core courses step.

---

## Part F: Technical plan (short)
- **Backend:** ASP.NET Core (.NET 10) Web API, EF Core, SQLite in development (movable to Postgres or SQL Server later).
  - Login uses ID + OTP and then a JWT cookie.
  - The OTP email address is derived from the ID: `yy-nnnn@student.gutech.edu.om`.
  - Codes are only logged to the console until real SMTP credentials are added.
- **Frontend:** React + TypeScript + Vite + Tailwind + shadcn/ui. One app with student routes and `/admin` routes.
- **Data:** Semester, Student (with an admin flag), Major, Course (type, year, major), MajorElective, Section + meetings, Enrollment, SectionChange (drives the "Changed" badge), OtpCode.
- **Build order:**
  1. Login and OTP.
  2. Student setup flow (Parts A and E).
  3. Timetable (Part C).
  4. Edit my courses (Part D).
  5. Admin dashboard (Part B).
- **Test data:** the user will provide it, and it is loaded through the same CSV format the admin upload uses.

## Verification
- Run the backend and the frontend, then walk through Parts A–E in the browser pane:
  - A new student.
  - A returning student.
  - A clash warning.
  - An admin edits a section, and the student sees the "Changed" badge.
  - A re-upload preview.
  - Publishing a new semester forces the full setup again.

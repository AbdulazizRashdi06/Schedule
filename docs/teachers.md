# Classy for teachers

The requirement is [teachers/plan.md](teachers/plan.md); the technical design, with every decision and its reason, is [teachers/design.md](teachers/design.md). This page is how it works.

## Access and rollout

Teachers open `/teach`, or follow **Staff? Sign in here** on the student sign-in screen, and sign in with their `@gutech.edu.om` address and a 6-digit emailed code. The code is never shown in the app. There are three ways onto the teacher side, and all three see the same screens:

- **A teacher**, signed in with a staff email.
- **A teaching assistant**: a student account an owner gave teaching rights (**Students → a student → Teaching rights**). They keep their student sign-in and get a **Teaching** item in their menu.
- **An owner using "view as"**: **Teachers → Open teacher view** on an account, or **View as** on any timetable name, with or without an account.

A plain `firstname.lastname@` address that matches exactly one teacher name in the timetable is linked to it at once. Anyone else picks their name from the list and waits for staff; meanwhile they can add their classes by hand and change them. One account holds a name at a time. An account with no linked name is shown to students by the name in its email.

Everything ships together and is live once deployed. The teacher tables are new; no student table changed, and web apps students already have open keep showing a correct timetable (a cancelled class is simply absent, a moved one sits at its new time).

## What a teacher sees

The teacher home is the student home showing the classes they teach: today, any day, the week, each session. Classes that meet together under several codes are one block. From the menu: **Edit my classes** (add any section, or hide one of their own), **Office hours & details**, **My changes**, **Free rooms**, **Calendar** (a private link for a phone calendar), **Notifications**, colours.

Office hours, the office room and a short note show on every class of that teacher and on their teacher page, which students open by tapping a teacher's name. A teacher who never signed in still has a page with this week's classes. The teacher's email shows only if they switch it on themselves.

## Class changes

On a session a teacher can **cancel**, **move**, **change the room**, or **add an extra class**. Each takes an optional note. Before saving, the form warns (and never blocks): how many Classy users have another class then, a room that is taken, short notice (under 2 hours), a day off, a weekend, a time outside 08:00–18:15 or outside the teaching weeks. When a section meets twice that day, a tick box applies the change to the other session too.

- Students of the section with notifications on are told within about 30 seconds, and again if the change is edited or undone. Reminders, the absence counter, clash checks, free rooms and calendar feeds all follow the change. In phone calendars a cancelled class stays with "Cancelled:" before its title.
- A change can be undone until the class starts; for a moved class, until the earlier of the old and the new time.
- A teacher can change any class they added, not only their own. The teacher the class is listed under is told and can undo it, and the log flags it.
- A teacher's change wins over a batch rep's change to the same class.
- Changing a class needs an emailed code entered in that browser in the last 7 days. Dashboard password accounts are exempt.

A teacher's change sits on top of the university timetable and is stored apart from it. When an upload, a sync or a hand edit by staff then changes that class: a new room alone keeps the change (the teacher is told the university lists another room); another day or time, or a removed section, drops it and the teacher is told; a timetable that now says what the teacher did merges it quietly. Before applying an upload or a sync, staff see a **Teacher changes** tab listing what will be dropped; the course editor shows the same warning on a section.

## Dashboard

**Teachers** and **Teacher changes** are in the sidebar for owners and RSA. The number on **Teachers** is names waiting for approval plus teachers with more than 5 changes today; the dashboard home lists the same, and says when first-time teacher sign-ins are paused after many wrong codes.

On **Teachers**:

- **Waiting for approval:** approve or reject a picked name. When someone else holds the name, reject or detach that one first.
- **Each account:** its names (**Reverse** undoes an automatic link, **Detach** removes an approved one, a "same person?" prompt follows a re-spelled name), attach another name, the classes it added, changes today and upcoming, **Freeze** (no class changes, added sections or office-hours edits; staff undo for them), **Sign out everywhere** (the next sign-in needs a new code), **Remove**.
- **Remove** deletes the account and blocks the email until it is unblocked under **Blocked emails**. The dialog offers to undo their upcoming changes too; students are then told those classes are back on.
- **Timetable names:** every teacher name this semester and the account linked to it.
- **Teacher class changes: On / Off** (owners only): off stops every new change at once. Changes already made stay and undo still works.

**Teacher changes** is the log, newest first: what changed, who made it, how many Classy users it reaches, whether it is still live or how it ended, and **Undo** until the class starts. Changes by someone who is not the class's named teacher are flagged. Nobody is stopped at 5 changes a day; staff are told.

In **"view as"** an owner does everything about classes. What they change is stored under their own name, students see "Classy staff" as the maker, and the teacher is told about each edit to their sections or office hours. The teacher's own slots, notification settings and calendar link are not shown, their notices are listed without being cleared, and an owner can never switch on a teacher's email. Owners in "view as" pass the off switch, so the flow can be tried while it is off.

RSA see teacher names, staff emails, the log and counts. They see nothing about students: a teaching assistant is a name to them, never a university ID, and only owners can give teaching rights, use "view as" or flip the switch.

## Configuration

| Setting | Default | |
| --- | --- | --- |
| `RateLimits__TeacherRequestCode` | 60 | teacher code requests per IP per 5 minutes |
| `RateLimits__TeacherWrongCodesPerHour` | 30 | wrong teacher codes per IP per hour, from browsers that address hasn't signed in on |
| `RateLimits__TeacherWrongCodesNewPerHour` | 40 | wrong teacher codes per hour, site-wide, for addresses with no account yet; reaching it pauses first-time teacher sign-ins |

The on/off switch and each freeze are stored in the database, not in configuration. "Delete all data" deletes teacher accounts and their class changes; blocked emails stay blocked.

## Verification

Backend: `cd server && dotnet test`. Frontend: `npx eslint . && npm run build` in `web`. The first deploy's checks (the migration created tables only, "view as", one change made and undone, the switch) are in [deploy/README.md](../deploy/README.md#first-deploy-with-teachers).

Not checked before launch: delivery of the sign-in email to staff mailboxes. If GUtech's mail system filters it, no teacher can sign in until that is fixed; "view as" still works.

# Classy for teachers: technical design

This is the one design for the teachers update. It merges three drafts (lowest risk to the live site, data correctness over time, web reuse) and was checked against the real source on `teachers-update`. The requirement is `docs/teachers/plan.md`; nothing here weakens it.

How to read it: sections 1 to 11 say what is built. Section 12 splits the work into eight stages; one stage can be built from this document alone. Where the drafts differed there is a one-line **Chosen because** note. Three reviews (security, data and sync, web compatibility) were then worked into the sections they concern; the last section, "Review decisions", lists each issue and what was done.

Words used below:

- **Oman now**: `clock.GetUtcNow().ToOffset(TimeSpan.FromHours(4))`. Every "today", "started" and "future" test uses it. It is for reading the date and the clock time only; it is never stored or put in a query (section 1, "Times are UTC").
- **Live change**: a `ClassChange` row with `EndedAt == null`.
- **A section's students**: distinct `Pick.StudentId` with that `SectionId`.
- **Staff**: owners and RSA (the dashboard). **Teacher**: a `Teachers` row.

---

## 0. The shape in one page

1. **Teachers live in new tables only.** One migration creates ten tables. No existing table gets a column, an index or a rebuild. Cookie kind `teacher` holds `Teachers.Id`; `/api/me/*` stays `kind == student`.
2. **One teaching identity, three ways in, one cookie.** A teacher (kind `teacher`); a student with teaching rights (a `Teachers` row whose `StudentId` points at them, found from their normal student cookie); an owner using "view as" (the request header `X-Teach-As`, honoured only for role `owner`). All three are resolved in one place, `TeachActor`.
3. **Teacher changes are an overlay table (`ClassChanges`)** keyed by `(SectionId, OrigDate, OrigStart)`. Never by `Meeting.Id` (reissued whenever a section changes, `ScheduleImport.cs:417-418`) and never by the planner fingerprint (it contains the room). It is never behind the planner switch.
4. **One reader of live changes, `Schedule/ClassChanges.cs` (`ChangeSet`)**, feeds `StudentSchedule.ForAsync` (reminders, feeds, groups, absences), the shared timetable builder (`GET /me/timetable` and `GET /teach/timetable`) and free rooms.
5. **Old cached copies of the web app stay correct without knowing about changes.** The server uses the two things old clients already understand: `excludedDates` (the original session is not on) and extra dated meetings in the section (the class at its new time or room, an extra class). New clients read an additive `changes` array to draw the marks.
6. **Telling people goes through one outbox table, `TeachNotices`.** `Section.ChangedAt`, `ChangeSummary` and the `push.changes.since` mark are not used for teacher changes.
7. **The web home is not forked.** `/teach` renders the same `Lab` → `Today` / `DayHome` / `WeekPage` / `SessionPage`. A `Viewer` context picks the data hook, the API base and a handful of branches.

**Must stay as it is (behaviour and JSON):** tables `Students`, `Picks`, `PushSubscriptions`, `CustomSlots`, `Absences`, `LoginCodes`, `Sections`, `Meetings`; `StudentPolicy`; the student branch of `Session.ValidateAsync`; the request and response of `/api/auth/request-code`, `/api/auth/verify`, every `/api/me/*` route and `/api/rooms/free`; `/api/public/timetable`; the CSV export; `ScheduleImport.DescribeChange`; `Notifications.Changes` and `DueReminders`; `PushSender.SendAsync`; `web/public/sw.js` and `manifest.json`.

---

## 1. Data model

New file `server/UniSchedule.Api/Teachers/TeacherModels.cs`, namespace `UniSchedule.Api.Teachers` (the pattern of `Planner/PlannerModels.cs`). DbSets and relationships go in `Data/AppDb.cs`. Strings that act as enums are plain strings with `HasMaxLength`. `DateTimeOffset` is stored through the existing converter; `DateOnly` and `TimeOnly` are text.

**Times are UTC.** The converter stores the local ticks with the offset, so a `+04:00` value sorts four hours late in SQL (`Notifications.cs:238-240`). Every `DateTimeOffset` written to a column or used in a query is therefore UTC:

- the start of a session: `ChangeRules.StartUtc(date, start)` = `new DateTimeOffset(date.ToDateTime(start), TimeSpan.FromHours(4)).ToUniversalTime()`. Check 9 of 5.2, the undo limits of 5.4 and every `ExpiresAt` use it;
- Oman midnight: `new DateTimeOffset(omanNow.Date, omanNow.Offset).ToUniversalTime()`, as `AdminEndpoints.cs:479` does. The daily count (5.5, 9) uses it;
- "now" in a column or a filter is `clock.GetUtcNow()`.

"Newest" notice or change always means the highest `Id`, never `CreatedAt`.

### 1.1 Entities

**`Teacher`**: a staff account, or the teaching profile of a student who was given teaching rights.

| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| Email | string? (100) | Lower-case staff address. Null for a student's teaching profile. Unique index (SQLite allows many nulls). |
| StudentId | int? | Set only for a student teaching assistant. Unique index. FK `Students`, cascade. |
| DisplayName | string (80), default "" | Typed by the owner for a student. Empty for staff (their shown name is worked out, section 3.5). |
| CreatedAt, LastSeenAt | DateTimeOffset, DateTimeOffset? | |
| CreatedBy | string? (64) | Who granted teaching rights (student profiles). |
| NameSkippedAt | DateTimeOffset? | Pressed "My name isn't here". |
| FrozenAt, FrozenBy | DateTimeOffset?, string? (64) | Per-teacher freeze. |
| SessionVersion | int, default 0 | Raised to end this account's sign-ins everywhere (section 2.3). |
| CalendarToken | string? | Unique index. |
| RemindMinutes, MuteChanges | int?, bool | As on `Student`. Mute covers university-change alerts only. |
| OfficeRoom, ProfileNote | string (60), string (200), default "" | |
| ShowEmail | bool | Off by default. |
| BusyAlertOn | DateOnly? | The Oman day the "more than 5 changes" alert was raised. |

**`TeacherName`**: a link between an account and a timetable name. The link is the name's text, so it survives re-imports and new semesters.

| Column | Type | Notes |
|---|---|---|
| Id, TeacherId | int | FK `Teachers`, cascade. |
| Name | string (120) | As written on the timetable. |
| NameKey | string (120) | `TeacherNames.NameKey(Name)`. |
| State | string (16) | `auto`, `approved`, `pending`, `rejected`. Only `auto` and `approved` give classes. |
| How | string (16) | `email` (matched at sign-in), `picked` (chosen by the teacher), `staff` (attached in the dashboard). |
| Conflict | bool | Another account holds or wants this name. |
| SuggestedName | string? (120) | A new spelling staff can accept with one tap (section 7.6). |
| CreatedAt, DecidedAt, DecidedBy | DateTimeOffset, DateTimeOffset?, string? (64) | |

Indexes: unique `(TeacherId, NameKey)`; `(State)`; unique `(NameKey)` with `HasFilter("\"State\" IN ('auto','approved')")` (one account per timetable name).

**`TeacherSection`**: a section a teacher added, or hid. One semester only.

| Column | Type | Notes |
|---|---|---|
| Id, TeacherId | int | FK cascade. |
| SemesterId | int | FK `Semesters`, cascade. |
| SectionId | int? | FK `Sections`, SET NULL. Null = the university removed it; it re-attaches by text if it returns. |
| CourseCode, SectionNumber | string (32), string (16) | Text copy: for re-attaching and next semester's suggestions. |
| Mode | string (8) | `added` or `hidden`. |
| By | string? (64) | Null when the teacher did it. The owner (`OwnerEndpoints.ActorName`) when it was done through "view as". |
| CreatedAt | DateTimeOffset | |

Indexes: unique `(TeacherId, SemesterId, CourseCode, SectionNumber)`; `(SectionId)`.

**`TeacherSeen`**: key `(TeacherId, SectionId)`, `SeenAt`. Both FKs cascade. Clears the teacher's "Changed" badge for that class.

**`TeacherSlot`**: the columns of `CustomSlot` with `TeacherId` in place of `StudentId` (`Id, TeacherId, SemesterId, Title(60), Day, Start, End, Date?, Place(60), Note?(500), CreatedAt`). FKs to `Teachers` and `Semesters` cascade. Index `(TeacherId, SemesterId)`.

**`TeacherPushSubscription`**: `Id, TeacherId (FK cascade, index), Endpoint (unique), P256dh, Auth, CreatedAt`.

**`OfficeHour`**: `Id, TeacherId (FK cascade, index), Day (0–6), Start, End (TimeOnly), Room (60)`. Weekly, not tied to a semester.

**`BlockedEmail`**: `Email` PK (100), `BlockedAt`, `BlockedBy` (64). No FKs, so it survives "Delete all data".

**`ClassChange`**: one row per section per changed session. The rows are also the change log.

| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| SemesterId | int | FK cascade. |
| SectionId | int? | FK `Sections`, SET NULL. The row is always ended before its section is removed. |
| CourseCode, CourseName, SectionNumber | string (32), (200), (16) | Text copies, so the log survives a dropped section. |
| GroupId | string (32) | `Guid.NewGuid().ToString("N")`. One teacher action on one real class (several course codes) shares it. |
| Kind | string (8) | `cancel`, `move`, `room`, `extra`. |
| SessionType | string (10) | `lecture`, `lab`, `tutorial`. Read from the original room for cancel, move and room (the existing "lab / tut" rule); chosen by the teacher for an extra. Stored, so a lab moved to a typed room stays a lab. |
| OrigDate, OrigStart | DateOnly?, TimeOnly? | **The session key**, with `SectionId`. Null for `extra`. |
| OrigEnd, OrigRoom | TimeOnly?, string? (100) | Snapshot of the university session, refreshed on re-import. Never part of the key. |
| NewDate, NewStart, NewEnd | DateOnly?, TimeOnly?, TimeOnly? | Null for `cancel`. For `room` equal to the original when it was made; readers take a room change's time from its base (6.1), so a later end-time change by the university shows. |
| NewRoom | string? (100) | Null for `cancel`. `""` = to be announced. |
| Note | string? (200) | |
| Tag | string (48) | The notification tag for this session (section 5.6). A replacement copies it. |
| TeacherId | int? | The teaching account it was made as. FK `Teachers`, SET NULL. Null when an owner viewed a name nobody has claimed, or the account was removed. |
| ByKind, ById | string (8), int | Who was signed in: `teacher`, `student` or `admin`, and that table's row id. |
| ByLabel | string (100) | For the log: staff email, university ID or dashboard username. |
| ByName | string (80) | What students see: "Raed Bolbol", or "Classy staff" for an owner's "view as". |
| OnBehalfOf | string? (120) | The name an owner was viewing as. |
| NamedTeacher | string (120) | `Section.Instructor` at the time. |
| Flagged | bool | The maker was not the class's named teacher (or it was made through "view as"). |
| CreatedAt | DateTimeOffset | |
| EndedAt, EndedBy | DateTimeOffset?, string? (120) | `EndedBy` is for the log only, never for a notice: `teacher:{email}`, `assistant:{university ID}` (a student acting as their own teaching profile), `staff:{ActorName}` (an owner or RSA: dashboard undo, "view as", removal) or `system` (an import, a day off, a weeks edit). |
| EndReason | string? (20) | `undone`, `undone-by-staff`, `undone-on-remove`, `replaced`, `dropped-moved`, `dropped-removed`, `dropped-day-off`, `merged`. |

Indexes: unique `(SectionId, OrigDate, OrigStart)` with `HasFilter("\"EndedAt\" IS NULL AND \"Kind\" <> 'extra' AND \"SectionId\" IS NOT NULL")` (one live change per session); `(SemesterId, EndedAt)`; `(TeacherId, CreatedAt)`; `(GroupId)`.

- Chosen because: typed columns and a filtered unique index give "one live change per session" without a hand-built key string; changes are ended before a section goes, so the key never needs to outlive the section.
- Chosen because: an edit ends the old rows as `replaced` and makes new ones, so the log keeps every state students ever saw without a second history table.

**`TeachNotice`**: the outbox for notifications about teacher changes, and the teacher's in-app inbox.

| Column | Type | Notes |
|---|---|---|
| Id, CreatedAt | | |
| Audience | string (10) | `students` (everyone who picked `SectionId`), `teacher` (`TeacherId`), `staff` (student owners). |
| SectionId | int? | FK SET NULL. |
| TeacherId | int? | FK cascade. |
| GroupId | string? (32) | The change it is about. |
| Kind | string (20) | Students: `change`, `back-on`, `dropped`. Teacher: `other-teacher`, `undone`, `dropped`, `room-differs`, `merged`, `reassigned`, `uni-changed`, `section-removed`, `name-approved`, `name-rejected`, `staff-edit`. Staff: `many-changes`, `sign-in-guessing`. |
| Title, Body, Url, Tag | string (120), (400), (200), (64) | The final text, written when the event happens. |
| ByLabel | string? (64) | Set on `staff-edit` rows: the owner who edited the account through "view as" (`OwnerEndpoints.ActorName`). Never shown to the teacher or to RSA as a student ID (section 9). |
| ExpiresAt | DateTimeOffset? | Not pushed after this (the class has started). |
| ClaimedAt, SentAt | DateTimeOffset? | Set by the worker (section 5.7). |
| SeenAt | DateTimeOffset? | Teacher inbox only. |

Indexes: `(SentAt, Id)`; `(TeacherId, SeenAt)`; `(SectionId, Tag)`.

- Chosen because: an outbox row written in the same transaction as the change cannot be lost or doubled, and one table serves students, teachers and staff. A claim before sending covers a crash or an undo that lands mid-send.

**Stored elsewhere, no migration:**

- The all-changes-off switch: `AppSettings` key `teachers.changes.enabled`, `"true"` or `"false"`. Missing = on. Read by `ClassChanges.EnabledAsync`.
- Student accounts with teaching rights: a `Teacher` row with `StudentId` set and `Email` null. No column on `Students`.
- Sign-in codes: the existing `LoginCodes` table, with the lower-cased email in the `UniversityId` column. It cannot collide with `dd-dddd` or `admin:username`.
- The time of the last sign-in code: cookie claim `code-at` (section 2.6).

### 1.2 Delete rules

- "Delete all data" (`OwnerEndpoints.cs:209-228`) adds, before the semesters are deleted: `db.TeachNotices.ExecuteDeleteAsync`, `db.ClassChanges.ExecuteDeleteAsync`, `db.Teachers.ExecuteDeleteAsync` (names, sections, seen, slots, devices, office hours cascade). `BlockedEmails` is left alone. The answer adds `teachers` (additive).
- Removing a student cascades their teaching profile; their changes stay with `TeacherId` null, `ByName` and `ByLabel` kept.
- Deleting a non-current semester cascades its `ClassChanges` and `TeacherSections`.
- Every new FK has an explicit rule above, so the raw `ExecuteDelete` calls cannot fail on a foreign key.

### 1.3 The one migration

`cd server && dotnet tool restore && dotnet ef migrations add Teachers --project UniSchedule.Api --output-dir Data/Migrations`

It must contain only `CreateTable` and `CreateIndex` for: `Teachers, TeacherNames, TeacherSections, TeacherSeens, TeacherSlots, TeacherPushSubscriptions, OfficeHours, BlockedEmails, ClassChanges, TeachNotices`. If the generated file has any `AddColumn`, `AlterColumn`, `Drop…` or a table rebuild, the model is wrong. All ten entities are defined in stage 1. If a later stage needs a model change, run `dotnet ef migrations remove` and add `Teachers` again: nothing ships until the final merge, so there is never a second migration.

---

## 2. Auth

### 2.1 Files

| File | What |
|---|---|
| `Auth/TeacherEmails.cs` (new) | `Normalize(raw)`, `DisplayName(email)`. Sibling of `StudentIds.cs`. |
| `Auth/CodeSignIn.cs` (new) | The bodies of today's `request-code` (`AuthEndpoints.cs:105-156`) and `verify` (`171-218`), moved into two helpers keyed by a string. |
| `Auth/TeacherCodeLimiter.cs` (new) | The guess limits only teacher sign-in has (below). A singleton, in memory, the shape of `WrongCodeLimiter`. |
| `Auth/EmailSender.cs` | `IEmailSender.SendCannotSignInAsync(email, ct)`, declared with a default body that does nothing so existing test senders compile unchanged; the log and Resend senders implement it. |
| `Auth/AuthEndpoints.cs` | Student endpoints call the helpers; teacher endpoints; `/logout` and `/session` learn the teacher kind. |
| `Auth/Session.cs` | `SignInTeacherAsync`, the `code-at` claim, the teacher branch in `ValidateAsync`, helpers. |
| `Auth/Errors.cs` | One new helper: `CodeNeeded(string message)` → 403 `{ error, needsCode: true }`. |
| `Teachers/TeachActor.cs` (new) | Who a `/api/teach` request is acting as. |
| `Program.cs` | Rate-limit policy, endpoint maps. |

`TeacherEmails.Normalize(string? raw)`: trim, lower-case (invariant), total length ≤ 100, and match `^[a-z0-9]+(?:[._-][a-z0-9]+)*@gutech\.edu\.om$`. Anything else returns null. So `@student.gutech.edu.om`, look-alike hosts, commas, spaces and double dots are refused (the address is a key in the comma-joined device cookie).

`CodeSignIn`:

```csharp
/// <summary>Asks for a code for a key (a university ID or a staff email). Returns the code to send, or the error to answer with.</summary>
public static Task<(LoginCode? Login, string? Code, IResult? Error)> RequestAsync(AppDb db, HttpContext http, TimeProvider clock, string key, string noun, bool trusted, CancellationToken ct);
/// <summary>After the email went out: this browser's earlier codes stop working.</summary>
public static Task RetireOthersAsync(AppDb db, LoginCode login, string key, DateTimeOffset now);
/// <summary>Checks a code. Error is null when it was right (and is now used). WrongCode is true when a real try was used on a wrong code.</summary>
public static Task<(IResult? Error, bool WrongCode)> VerifyAsync(AppDb db, HttpContext http, TimeProvider clock, string key, string? code, string noun, int maxAttempts, bool trusted, CancellationToken ct);
```

`noun` is `"ID"` for students, so every existing message stays byte for byte ("Too many codes for this ID…"), and `"email"` for teachers. `trusted` is what `SignInCookies.IsTrustedDevice` says today (it skips the per-key daily caps); `maxAttempts` is `LoginCodes.MaxAttempts` (5) for students and **3** for email keys. Sending the email, deleting the row on a failed send, the answer, and the cross-key limiter (the "is this address blocked" check before and the `Record` after a wrong code) stay in each endpoint: students use `WrongCodeLimiter` exactly as today, teachers use `TeacherCodeLimiter`. The guard: `AuthFlowTests` and `SignInSafetyTests` must pass with no edit. If the extraction cannot be made to pass them unmodified, stop and copy the two bodies instead.

- Chosen because: a second copy of the sign-in limits would drift; the unmodified existing tests are the safety net for the move.

**`TeacherCodeLimiter`.** Every existing guess limit is per key (5 tries a code, 20 codes and 30 wrong codes a day per ID), and the only limit across keys is 300 wrong codes per IP per hour. That is enough for student IDs, where a made-up account has no power. A made-up `@gutech.edu.om` address is a new key every time and a hit gives an account that can change any class, so teacher verify has three more counts, all in memory like `WrongCodeLimiter`:

| Count | Limit | When reached |
|---|---|---|
| Wrong teacher codes per IP, from browsers the address has not signed in on | `RateLimits:TeacherWrongCodesPerHour`, default 30 per rolling hour | 429 "Too many wrong codes from this network. Try again in an hour, or sign in on a device you've used before." |
| Wrong codes, site-wide, for addresses that have **no `Teachers` row** | `RateLimits:TeacherWrongCodesNewPerHour`, default 40 per rolling hour | Verify for an address with no account answers 429 "New teacher sign-ins are paused for an hour. Try again later." until the count falls. The first time it trips, one `staff` notice (`sign-in-guessing`, "Many wrong teacher sign-in codes. New teacher sign-ins are paused for an hour.") is written, and the dashboard home shows the pause (section 9). Addresses that have an account are not affected. |
| Wrong codes for one address from browsers it **has** signed in on | 30 in 24 hours (`LoginCodes.MaxWrongPerDay`), counted per address | Those browsers stop counting as trusted for that address until the count falls: they are held to the per-key daily caps and the per-IP count like any other browser. So a lecture-room PC that once signed a teacher in cannot guess without limit, and clearing the sign-in cookie does not reset the count. |

The teacher endpoint works out `trusted` as `SignInCookies.IsTrustedDevice(http, email) && !limiter.TrustSpent(email)` and passes it to both helpers. With the defaults a made-up address can be tried about 960 times a day in total, from any number of addresses, instead of 7,200 a day per IP.

### 2.2 Endpoints (inside the existing `/api/auth` group: 4096-byte limit, cookie path)

**`POST /api/auth/teacher/request-code`** `{ email }` → `{ email, resendAfterSeconds }`

- The answer has **no `testCode` property**, in any mode. There is no `Auth:TestMode` branch on this endpoint.
- Bad address → 400. When the raw value ends with `@student.gutech.edu.om`: "That's a student address. Students sign in with their university ID." Otherwise: "Enter your GUtech staff email, like firstname.lastname@gutech.edu.om."
- Then `CodeSignIn.RequestAsync(key: email, noun: "email", trusted)` and `IEmailSender.SendSignInCodeAsync(email, code)`.
- Known, unknown and blocked addresses get the same answer (accounts are made at verify), so nobody can test which staff addresses were removed from Classy. For an address in `BlockedEmails` the same limits run and a `LoginCodes` row is stored as usual (so the resend wait and the daily caps behave the same), but its `CodeHash` is a random value no code matches, and the email sent is `SendCannotSignInAsync`: "This email can't sign in to Classy. Ask the Classy team." The blocked person is told, in their own mailbox only.
- `.RequireRateLimiting("auth-teacher-request-code")`: per IP, 5 minutes, default 60 (`RateLimits:TeacherRequestCode`). Lower than the student cap because each request sends mail to a guessable staff mailbox.

**`POST /api/auth/teacher/verify`** `{ email, code, endpoint? }` → `{ isNew, status }`

- Normalize. Then the `TeacherCodeLimiter` checks (2.1): the per-IP count when the browser is not trusted, and the new-address pause when the address has no `Teachers` row.
- `CodeSignIn.VerifyAsync(email, …, "email", maxAttempts: 3, trusted)`. On `WrongCode`, the limiter records it (per IP when not trusted; site-wide when the address has no account; per address when trusted).
- A blocked address has no special answer: its stored hash matches nothing, so it gets the usual wrong-code errors. As a backstop, a right code for an address in `BlockedEmails` (a code asked for just before the block) answers 400 "That code has expired. Send a new one." and signs nobody in; removing a teacher also deletes that address's `LoginCodes` rows.
- Find or create the `Teacher` by `Email` (`StudentId` null); set `LastSeenAt`.
- Run the auto-link (section 3.4) when the account has no `TeacherName` rows.
- When `endpoint` is sent: delete that endpoint from `PushSubscriptions` and from other teachers' `TeacherPushSubscriptions`. The device belongs to whoever just signed in.
- `Session.SignInTeacherAsync(http, teacher, now)`; `SignInCookies.RememberDevice(http, email)`.
- `status`: `linked` (has an `auto` or `approved` name), else `pending` (has a `pending` one), else `pick-name` (`NameSkippedAt` is null), else `none`.
- `.RequireRateLimiting(AuthEndpoints.VerifyLimit)`.

**Student endpoints, additive only:**

- `request-code`: `showCode` is also false when the ID has a teaching profile (`db.Teachers.Any(t => t.StudentId == s.Id)` for that university ID). A student who can change classes never gets an on-screen code, like owners and reps.
- `verify`: accepts an optional `endpoint`. When present, that endpoint is deleted from `TeacherPushSubscriptions`. `Session.SignInStudentAsync(http, student, now)` gains the `now` parameter (its only caller).

**`POST /api/auth/logout`**: for kind `teacher`, the named endpoint is deleted from `TeacherPushSubscriptions` where `TeacherId == Id()`. The student branch is unchanged (a teaching assistant's devices are their student devices).

**`GET /api/auth/session`**, explicit branches with no fall-through:

```jsonc
// student: today's fields, plus two additive ones
{ "kind": "student", "name": "24-0161", "role": "owner" | "batch-rep" | null, "mustChangePassword": false, "batchRep": …,
  "teaching": false, "codeFresh": true }
// teacher
{ "kind": "teacher", "name": "raed.bolbol@gutech.edu.om", "role": "teacher", "mustChangePassword": false,
  "teaching": true, "codeFresh": true, "teacher": { "id": 3, "displayName": "Raed Bolbol" } }
// admin: exactly as today, but only when Kind() == "admin". Any other kind: { "kind": null }
```

`teaching` = the student has a `Teacher` row. `codeFresh` = the `code-at` claim is at most 7 days old and, for a student with a teaching profile, not older than that profile's `CreatedAt` (2.5).

### 2.3 Session kind, claims, validation (`Auth/Session.cs`)

- `SignInTeacherAsync(http, Teacher t, DateTimeOffset codeAt)` → kind `"teacher"`, id = `Teachers.Id`, plus the existing `VersionClaim` holding `t.SessionVersion`.
- New claim `CodeAtClaim = "code-at"` (unix seconds), added by the private `SignIn` for kinds `student` and `teacher`. Old cookies lack it and count as stale.
- Helpers: `IsTeacher()`, `IsAdmin()`, `CodeAt()`, `CodeIsFresh(user, now)`.
- `ValidateAsync` gets a third branch before the final `else`: load `Teachers` where `Id == id && StudentId == null` (`LastSeenAt` and `SessionVersion`). Missing → `Reject` (a removed teacher is signed out on their next request; a teacher cookie can never reach a student's teaching profile). A cookie whose version claim differs from `SessionVersion` → `Reject`, as for dashboard accounts. Add role claim `"teacher"` (a new string, never `owner`, `rsa` or `batch-rep`). Bump `LastSeenAt` at most once per `SeenEvery`.
- The student branch is not edited.
- Cookie lifetime stays 60 days sliding for every kind.

**Ending a teacher's sign-ins without removing them** (a lost phone, a copied cookie). Both raise `Teacher.SessionVersion` by one, so every cookie of that account is rejected on its next request; nothing else about the account changes:

- `POST /api/teach/sign-out-everywhere` (a staff account's own cookie only; 409 in "view as" and for a teaching assistant, whose sign-in is the student one). It then re-issues this browser's cookie with the new version and the cookie's **existing** `code-at`, so it gives no fresh code.
- `POST /api/admin/teachers/{id}/sign-out` (owners and RSA, section 9). Not offered for a teaching assistant.

Checked: ASP.NET's cookie handler captures the ticket for a sliding renewal **before** `OnValidatePrincipal` runs. Claims set at sign-in (`kind`, `code-at`) survive renewals; claims added in `ValidateAsync` (roles) are per request and never written back. So `code-at` only changes when a code is entered, and no extra claim clean-up is needed.

**Policies.** No new policy. A teacher cookie fails `StudentPolicy`, `StaffPolicy`, `OwnerPolicy` and `RepPolicy`, so every `/api/me/*`, `/api/admin/*` and `/api/rep/*` route answers 403. `SignedInPolicy` admits teachers: `/api/rooms/free` (wanted) and `/api/majors` (harmless).

### 2.4 Who is acting: `Teachers/TeachActor.cs`

The `/api/teach` group is `api.MapGroup("/teach").RequireAuthorization(Session.SignedInPolicy).AddEndpointFilter(TeachActor.Filter)`. The filter resolves the actor once, stores it in `http.Items` and answers 403 when there is none. Handlers read it with `http.Teach()` and never call `http.User.Id()` themselves, so the kind always travels with the id.

```csharp
/// <summary>Who a /api/teach request is acting as, and who is really signed in.</summary>
public sealed record TeachActor(
    Teacher? Profile,                  // the account acted for; null only when an owner views a name nobody claimed
    IReadOnlyList<string> Names,       // timetable names whose sections are theirs (auto or approved)
    IReadOnlyList<string> NameKeys,
    string ByKind, int ById,           // "teacher" | "student" | "admin": who is signed in
    string ByLabel,                    // staff email, university ID or dashboard username (for the log)
    string ByName,                     // what students see
    string? OnBehalfOf)                // set for an owner's "view as": the name being viewed
{
    public bool ViewAs => OnBehalfOf is not null;
    /// <summary>Own slots, devices, calendar link and the name step belong to the teacher, not to an owner looking in.</summary>
    public bool Personal => !ViewAs;
}
```

Resolution, in order:

1. **Header `X-Teach-As` present and not empty.** The caller must have role `owner` (a student owner or a dashboard owner), else 403. The value is a teacher key (section 8.3): `t-<teacherId>` or `n-<base64url of the UTF-8 name>`.
   - `t-…`: `Profile` = that teacher (404 when gone); `Names` = its `auto` and `approved` names; `OnBehalfOf` = its shown name.
   - `n-…`: the name must equal, by `NameKey`, an instructor of the current semester (placeholders allowed: owners manage those sections this way) or a held `TeacherName`; else 404 "That name isn't on the timetable." `Profile` = the account holding it, or null; `Names` = that account's names, or just this name.
   - `ByName = "Classy staff"`; `ByKind`, `ById`, `ByLabel` = the owner (`OwnerEndpoints.ActorName`).
2. **Kind `teacher`**: `Profile` = `Teachers` row by id. `ByLabel` = email. `ByName` = the shown name (section 3.5).
3. **Kind `student`**: `Profile` = `Teachers` row where `StudentId == Id()`; none → 403 "Teaching isn't turned on for this account." `ByLabel` = university ID. `ByName` = the profile's shown name.
4. **Kind `admin` without the header** → 403 "Open the teacher view from the dashboard."

No second cookie is involved. "View as" is a per-request header the web sends only on `/teach` calls from one browser tab; a teaching assistant is recognised from the database by their own student cookie.

- Chosen because: resolving in a filter on the teach group leaves the student branch of cookie validation untouched and adds no query to student requests.
- Chosen because: the header takes an account or a name, so an owner can also open the view of a teacher who has no linked name yet.

**What "view as" may do:**

| Thing | In "view as" |
|---|---|
| Timetable, sections list, class changes, change list | Works. Changes are stored with `By*` = the owner and `OnBehalfOf` = the name. |
| Added and hidden sections, office hours, profile | Works when the target has an account. 409 "This name has no Classy account yet." for a bare name. Every such write is logged under the owner and the teacher is told: a `TeacherSection` row made this way stores `By`; each successful `PUT /teach/sections/{id}` and `PUT /teach/office` writes one `teacher` notice to the viewed account (kind `staff-edit`, "Classy staff changed your office hours" / "Classy staff added BE1101 section 2 to your classes", `ByLabel` = the owner, tag `staff-edit-{teacherId}` so a run of edits is one notification). |
| Showing the teacher's email | Never. `PUT /teach/office` in "view as" leaves `ShowEmail` as it is, and answers 409 "Only the teacher can show their email." when the body would turn it on. An owner may turn it off. |
| Own slots, devices, notification settings, calendar link, the name step, marking notices as seen, sign-out-everywhere | 409 "Not available while viewing as a teacher.", reads and writes alike. The viewed teacher's own slots are private and are not shown to the owner (`slots` is `[]`). The teacher's notices are shown in the banner without "Got it". |

The web hides the controls that would only answer 409 (10.5). An owner who wants to try the personal extras gives their own student account teaching rights.

### 2.5 The fresh-code rule

`TeachActor.CodeNeeded(HttpContext http, TimeProvider clock)` returns `Errors.CodeNeeded("Confirm it's you with a new code before changing a class.")` when `Kind() != "admin"` and `code-at` is missing or older than 7 days.

For a student acting as their own teaching profile (`ByKind == "student"`, not "view as") the code must also be at least as new as the grant: `code-at >= Profile.CreatedAt` (unix seconds). A browser where the student signed in before an owner gave them teaching rights (a shared PC, or an on-screen test-mode code from before the profile existed) cannot change classes until a code is entered after the grant. Revoking and granting again makes a new row, so the rule applies again.

- Called at the top of the three change writes on `/api/teach`: create, replace, undo. Not on preview, sections, profile, office hours or slots.
- It covers every sign-in that came from an emailed code: teachers, teaching assistants, and student owners in "view as". Dashboard password accounts are exempt (they have no code; they have session versions and lockouts).
- It is per browser, because the claim lives in the cookie. That is the point on a lecture-room PC.

How the web asks: `/auth/session` carries `codeFresh`, so the change sheets show a code step before submitting when it is false. The step uses the normal endpoints of the signed-in kind (teacher: `/auth/teacher/request-code` and `/auth/teacher/verify` with the session email; student: `/auth/request-code` and `/auth/verify` with their ID). Verifying re-issues the cookie with a new `code-at`. A 403 with `needsCode` (the 7 days ran out mid-form) opens the same step and retries once.

- Chosen because: reusing the sign-in endpoints adds no new way to mint a session, and undo needs a fresh code too since it also notifies a whole class.

### 2.6 Device memory

`SignInCookies.RememberDevice(http, email)` and `IsTrustedDevice(http, email)`: unchanged code. A validated email holds no comma. Logout keeps the device cookie, as today.

### 2.7 Every place that reads "not student" as "dashboard account"

| Place | Fix |
|---|---|
| `AuthEndpoints.cs:339-340`, `/session` fall-through | Explicit branches (2.2). |
| `AuthEndpoints.cs:319`, logout push removal | Teacher branch (2.2). |
| `OwnerEndpoints.cs:37-43`, `ActorName` | Switch on `Kind()`: student → university ID, admin → username, teacher → email, else throw. |
| `OwnerEndpoints.cs:67` | `IsAdmin() ? Id() : -1`. |
| `OwnerEndpoints.cs:266` | `IsAdmin() && Id() == id`. |
| `PlannerEndpoints.cs:265, 272, 277` (`OwnerAccount = !IsStudent()`) | `IsAdmin()`. |
| `PlannerEndpoints.cs:91, 101` | Already student-only reads; no change. |
| `UsageAnalytics.cs:91` | `IsStudent() ? "student" : IsTeacher() ? "teacher" : "staff"`. `studentSeen` stays student-only. |
| `AuthEndpoints.cs:296` | Already explicit. |
| `Session.cs:84` | Teacher branch before the reject. |
| Web: `main.tsx` `StudentOnly` and `StaffOnly`, `Login.tsx:41-42`, `AdminLogin.tsx:35`, `Join.tsx:39-43`, `Planner.tsx:18`, `NotFound.tsx`, logout targets in `StudentShell.tsx:137` and `AdminLayout.tsx:142` | Section 10.1. |

All server rows except `/session`, logout and analytics sit behind Staff, Owner or Rep policies a teacher cannot pass; the edits make that explicit and change nothing for students or admins.

---

## 3. Email-to-name matching

New `Teachers/TeacherNames.cs`. Pure static functions, plus the auto-link.

### 3.1 Keys

- **`Fold(s)`**: Unicode `FormKD`, drop combining marks, lower-case (invariant).
- **`NameKey(name)`**: `Fold`, replace every run of characters outside `[a-z0-9]` with one space, trim. "Dr. Fatma Al-Rawahi" → `dr fatma al rawahi`. This is the only way a `Section.Instructor` is compared with a link, so case, spacing, punctuation and accents never break one.
- **`Tokens(name)`**: `NameKey` split on spaces; while more than two tokens remain and the first is one of `dr, prof, professor, mr, mrs, ms, eng`, drop it.

### 3.2 Placeholder rule

`IsPlaceholder(name)` is true when, on the name split by whitespace **as written**, any of these holds:

1. fewer than two words (a blank instructor is not a name);
2. all words equal, ignoring case;
3. a digit anywhere;
4. no word starts with an upper-case letter.

Placeholders are never offered in the picker, never linked, and not tappable on class pages.

### 3.3 The match

`Matches(string emailLocalPart, string name)`:

1. A local part with `_` in it never matches. `e` = the local part split on `.`, with any `-` inside a word dropped (`al-humairi` → `alhumairi`; `raed-bolbol` is then one word). It needs at least two words, each `^[a-z]+$`. A digit anywhere means no match.
2. `t = Tokens(name)`. It needs at least two tokens, all letters only.
3. `e[0] == t[0]`, and for some `k` in `1 … t.Count-1` the name words `t[k..]` can be cut into `e.Count − 1` runs of whole words whose glued text equals `e[1]`, `e[2]`, … in order. That is: the exact first name, then a trailing run of the remaining name words, each email word being one or more **whole** name words glued together (`alhumairi` = `al` + `humairi`). `raed.bol.bol` does not match "Raed Bolbol": `bol` is not a word of the name.

No fuzzy matching, no initials, no reversed order. `Match(email, names)` returns the names that match, placeholders removed, de-duplicated by `NameKey`.

The rule is this narrow on purpose: a link made here needs no approval and the account is then shown to students under the timetable name, so the forms that link by themselves are the ones GUtech's own address format produces (`firstname.lastname`). A two-word name has exactly one such address. `raed_bolbol@` and `raed-bolbol@` go to the picker and staff approval.

### 3.4 Auto-link

`TeacherNames.LinkOnSignInAsync(db, teacher, now, ct)` runs at teacher verify, and in `GET /teach/me` (outside "view as"), only while **all** hold: it is a staff account (`StudentId` null), it has no `TeacherName` rows in any state, and a semester is published. Candidates are the distinct `Section.Instructor` values of the current semester.

| Result of `Match` | What happens |
|---|---|
| Exactly one name, held by nobody | Add `State = auto, How = email`. |
| Exactly one name, held (`auto`/`approved`) by a link with `How = picked` | That link becomes `pending` with `Conflict = true`; this account gets `auto, email` with `Conflict = true`. The matching email wins; staff see both sides. |
| Exactly one name, held by a link with `How = email` or `staff` | This account gets `pending, email, Conflict = true`. Staff decide. |
| No name, or more than one | Nothing. The web shows the picker. |

Reversing an automatic link in the dashboard sets it to `rejected`, so the account still has a row and is never auto-linked again.

- Chosen because: keeping how a link was made (`How`) is what lets "a matching email wins over someone who picked the name by hand" stop short of taking a name staff attached on purpose.

### 3.5 Shown name

`TeacherNames.Shown(teacher, names, taken)`: the `Name` of the oldest `auto` or `approved` link as written on the timetable; else `DisplayName` when not empty; else `TeacherEmails.DisplayName(email)` (the local part split on `._-`, each word capitalised, digits kept: "raed.bolbol" → "Raed Bolbol", "raed.bolbol.2" → "Raed Bolbol 2").

An account with no `auto` or `approved` link is never shown under a real teacher's exact name. `taken` (`TeacherNames.TakenKeysAsync`: the `NameKey`s of the current semester's instructors and of every `auto` or `approved` link) is checked for the last two cases: when `NameKey` of the name that would be shown is in it, the email's local part is shown as typed instead ("raed_bolbol"), or "Teaching assistant" for a student profile whose typed name is a teacher's. So "Cancelled by Raed Bolbol" always means the account linked to that name.

### 3.6 Picking a name (`/api/teach`, staff accounts only, not in "view as")

- `GET /teach/names` → `{ names: [{ name, sections }] }`: non-placeholder names of the current semester.
- `POST /teach/names` `{ name }`: the name must be in that list. Upserts this account's link for that name as `pending, picked` (any other `pending` link of this account is deleted); `Conflict = true` when another account holds it. `{ none: true }` sets `NameSkippedAt` ("My name isn't here").

A pending name adds nothing to the timetable. Rights never depend on it: the teacher can add those sections by hand at once.

### 3.7 Checked against the real names

The rule above was run (throwaway script, not in the repo) against the 83 real names and 8 placeholders in the code map's data area:

- All 8 placeholders are caught (`CS1 CS1`, `CS3 CS3`, `CS4 CS4`, `engineering1 engineering1`, `IBSM1 IBSM1`, `LS1 LS1`, `LS2 LS2`, `LOG4 LOG4`); none of the 83 real names is.
- For every real name, `first.last`, `first.gluedsurname`, every `first.trailing-run`, `all.words.dotted`, `first_last` and `first-gluedsurname` resolve to exactly that name: 140 distinct keys, 0 collisions. That run used an earlier, wider rule (split on `._-`, any gluing). The rule in 3.3 only removes matches from it (underscore forms, a hyphen between the two halves, surnames cut inside a word), and removing matches cannot create a collision. The release check in stage 8 runs the final rule.

| Email local part | Result |
|---|---|
| `raed.bolbol` | Raed Bolbol |
| `ali.alhumairi`, `ali.al.humairi`, `ali.al-humairi`, `ali.humairi` | Ali Al Humairi |
| `mohammed.albalushi`, `mohammed.balushi` / `aliya.albalushi` | Mohammed Al Balushi / Aliya Al Balushi |
| `hajir.alsaqri` / `jokha.alsaqri`, `raja.anwar` / `shams.anwar` | each their own |
| `nesrine.abdallah`, `nesrine.ouledabdallah` | Nesrine Ouled Abdallah |
| `abdelaziz.korba` | Abdelaziz Amara Korba |
| `tamer.mahmoud`, `tamer.mohamedmahmoud`, `tamer.adel.mohamed.mahmoud` | Tamer Adel Mohamed Mahmoud |
| `nesrine.ouled`, `abdelaziz.amara`, `tamer.adel` (middle name only) | no match → picker |
| `mohamed.elbeheiry`, `yousuf.bulale`, `ahmed.adeel`, `saskia.spaeth`, `gosta.hoffmann`, `mohammed.beheiry` (spelling differs) | no match → picker |
| `r.bolbol`, `bolbol.raed`, `raed`, `bolbol`, `raed.bolbol1`, `mohammed.albalushi2`, `cs1.cs1`, `ahmed.al` | no match → picker |
| `raed_bolbol`, `raed-bolbol`, `raed.bol.bol`, `ali_al_humairi`, `ali.alhum.airi` | no match → picker (and shown to students as typed until staff approve) |
| `fatma.alrawahi`, `fatma.rawahi` → "Dr. Fatma Al-Rawahi"; `jens.muller` → "Prof. Jens Müller" (dev seed names) | linked |
| `…@student.gutech.edu.om`, `…@gutech.edu.om.evil.com`, `raed..bolbol@…`, `raed,bolbol@…` | refused by `Normalize` |

Tests use invented names of the same shapes: `imports/` is gitignored and the real staff list must not be committed. Re-run the collision check against the live export before release.

---

## 4. Teaching timetable

### 4.1 Which sections (`Teachers/TeacherSchedule.cs`)

```csharp
public sealed record Taught(Course Course, Section Section, bool Own, bool Added, bool Hidden);
/// <summary>The sections a teacher has to do with this semester: under their names, plus those they added. Hidden ones are marked, not removed.</summary>
public static Task<List<Taught>> SectionsAsync(AppDb db, int semesterId, TeachActor actor, CancellationToken ct);
/// <summary>The teaching accounts that have each section (named by an auto or approved link, or added it).</summary>
public static Task<ILookup<int, Teacher>> TeachersOfAsync(AppDb db, int semesterId, IReadOnlyCollection<int> sectionIds, CancellationToken ct);
/// <summary>Classy users per section: how many students picked it.</summary>
public static Task<Dictionary<int, int>> SizesAsync(AppDb db, IReadOnlyCollection<int> sectionIds, CancellationToken ct);
```

`SectionsAsync`:

1. Load the semester's sections with course and meetings (about 300 rows) and mark `Own` where `TeacherNames.NameKey(Instructor)` is in `actor.NameKeys`.
2. Mark `Added` from `actor.Profile`'s `TeacherSection` rows with `Mode = added` and a non-null `SectionId`.
3. Mark `Hidden` from rows with `Mode = hidden`.
4. Return those that are `Own` or `Added`.

Two uses, one function:

- **On the timetable** = `!Hidden`. This is what `/teach/timetable`, reminders and the feed show.
- **May change** = every row returned, hidden or not. So the named teacher can always undo a change to a class under their name.

A teacher with a pending name gets only what they added, with full rights on it, as the plan decides.

### 4.2 Merged classes

One real class listed under several course codes is one block.

- `ClassChanges.RoomKey(room)`: trim, lower-case, remove all whitespace ("A 101" = "a101"). Used for every room comparison in this feature.
- `TeacherSchedule.MergeKey(DateOnly? date, int day, TimeOnly start, TimeOnly end, string room)`: `"{date:yyyy-MM-dd or 'w'+day}|{HH:mm}|{HH:mm}|{RoomKey}"`, or null when the room is empty or not a real room (`FreeRooms.NotARoom`, made `internal`: online, Coursera, field trips…).
- **A session a change put on** (a `ChangeSet` `Added`: the class at its new time or room, an extra class) always carries `merge = "g:" + GroupId`, whatever its room. One teacher action is one real class, so a merged class moved to "to be announced" or to a typed room stays one block, with one reminder and no clash mark. `/teach/timetable`, `ItemsAsync`, reminders and the teacher page all use this key.
- **Siblings**: `TeacherSchedule.Siblings(taught, changes, sectionId, date, start)` is the server's only definition of a merged class. Preview and every write call it; the page never decides which sections a change is aimed at.
  - For a session as the university lists it: the sections on the same teaching timetable, with a **different course**, that have a session on that date with the same merge key **and no live change on that (date, start)**. A code that already has its own change is reached through that change's group, so after "cancel two of three codes" the third block's default ticks are just itself and creating never trips over the other two.
  - For a session a change put on: the sections of that change's group.
- On `/teach/timetable` every meeting carries its merge key (`merge`). The web collapses sessions that share a key into one block (university keys: only between different courses; `g:` keys: every section of the group); the code shows as "BE1101 · IBSM1101 · LOG1101".
- **Class size** of a block is the distinct students over its member sections, the same number the preview gives. `/teach/timetable` sends it as top-level `mergeSizes: { "<merge key>": 23 }` for every key with more than one section; each class still carries its own `students` for the per-code line. On a date when some codes of a weekly block are off (10.4), the block shows the sum of the codes still on.
- Two sections of the same course at the same time are not merged; they show as a clash.

- Chosen because: the server sends the key and recomputes siblings on every write, so a display disagreement can never aim a change at the wrong sections, and the web holds no second copy of the rule.

### 4.3 The shared builder (`Schedule/TimetableView.cs`)

The body of `GET /me/timetable` (`StudentEndpoints.cs:327-367`) moves into:

```csharp
public sealed record ClassRow(Course Course, Section Section, bool Changed, object? Notes, int? Students, bool? Own, bool? Added);
/// <summary>The timetable JSON the home draws: for a student's picks, or for a teacher's sections.</summary>
public static Task<Dictionary<string, object?>> BuildAsync(AppDb db, Semester semester, IReadOnlyList<ClassRow> classes,
    IEnumerable<object> slots, (List<PlannerOccurrence> Events, Dictionary<string, List<string>> Excluded) planner,
    IEnumerable<object> needsSection, TeachActor? teacher, CancellationToken ct);
```

It emits today's keys with today's values (`semester, span, daysOff, plannerEvents, slots, classes, needsSection, clashes`), and adds `changes` (section 6.2) and `teachers` on each class (section 8.2). Each class is written as a dictionary so a teacher's class has `students`, `own`, `added` and **no `notes` key** (the teacher path never calls `VisibleNotes`), while a student's class has `notes` and none of the three.

`/me/timetable` loads the student's picks, planner view and note counts as today and calls the builder. A contract test pins its old property set and types.

- Chosen because: the web home reads both timetables with the same code; two hand-written copies of the shape would drift.

### 4.4 Teacher endpoints (group `/api/teach`)

| Endpoint | Purpose |
|---|---|
| `GET /teach/me` | `TeachMe` (below). Also runs the auto-link while the account has no name rows. |
| `GET /teach/timetable` | The timetable shape, for the sections on the teacher's timetable. |
| `POST /teach/seen` `{ sectionId }` | Upserts `TeacherSeen`. |
| `GET /teach/names`, `POST /teach/names` | Section 3.6. |
| `GET /teach/sections?q=` | Edit my classes (below). |
| `PUT /teach/sections/{sectionId}` `{ on }` | Under my name: `false` adds a hidden row, `true` removes it. Otherwise: `true` adds an added row, `false` removes it. The section must be in the current semester. At most 60 added per semester. Takes effect at once. A frozen account cannot add a section (403 with the freeze message, 5.9); hiding and removing still work. |
| `GET/POST /teach/slots`, `PUT/DELETE /teach/slots/{id}` | Own slots; same body, validation and answer as `/me/slots` (`PersonalEndpoints.Fill` becomes `internal` and generic over the seven fields). Personal. `{id}` is looked up with `TeacherId == actor.Profile.Id`; another account's slot is a 404. |
| `GET/PUT /teach/office` | Section 8.1. |
| `POST /teach/notices/seen` `{ ids }` | Personal (409 in "view as"). At most 100 ids. The update is `db.TeachNotices.Where(n => ids.Contains(n.Id) && n.Audience == "teacher" && n.TeacherId == actor.Profile.Id)`, so nobody can clear another teacher's inbox by counting ids. |
| `GET/PUT /teach/notifications`, `POST /teach/push`, `/teach/push/remove`, `/teach/push/test` | Section 5.8. Personal. |
| `GET /teach/calendar`, `POST /teach/calendar/reset` | Section 6.6. Personal. |
| `POST /teach/sign-out-everywhere` | Section 2.3. A staff account's own cookie only. |

Every route that takes a row id acts only on the caller's own rows, by the same kind of filter as above.

```ts
interface TeachMe {
  id: number | null                 // null: an owner viewing a name nobody has claimed
  email: string | null
  name: string                      // the shown name
  kind: 'staff' | 'student' | 'view'
  as: string | null                 // the name being viewed, in "view as"
  personal: boolean
  semester: { id: number; name: string; weeks: Weeks } | null
  plannerEnabled: boolean
  status: 'linked' | 'pending' | 'pick-name' | 'none'
  names: { name: string; state: 'auto' | 'approved' | 'pending' | 'rejected' }[]
  frozen: boolean
  changesEnabled: boolean
  codeFresh: boolean
  notices: { id: number; kind: string; title: string; body: string; url: string; at: string }[]   // unseen, last 14 days
}
```

`status` for a staff account is the rule of 2.2. For a student's teaching profile (`kind: 'student'`) it is `linked` when the profile has an `auto` or `approved` name and `none` otherwise, never `pick-name` or `pending`: the name step is for staff accounts only (3.6), and an owner attaches names to a teaching assistant in the dashboard. So a new teaching assistant lands on an empty home with "Add the classes you teach".

`GET /teach/timetable`, differences from the student one:

```jsonc
{
  "semester": { "id": 4, "name": "Fall 2026" },
  "span": { "first": "2026-09-20", "last": "2026-12-26" },
  "daysOff": [ { "date": "2026-11-18", "name": "National Day" } ],
  "plannerEvents": [],            // reps' published events for these sections; only when the planner is on
  "slots": [ /* TeacherSlot rows, same shape as /me/slots; [] in "view as" */ ],
  "officeHours": [ { "id": 1, "day": 1, "start": "10:00", "end": "11:00", "room": "B-204", "excludedDates": ["2026-11-18"] } ],
  "classes": [ {
      "courseId": 31, "code": "BE1101", "name": "Business Economics", "type": "Core", "credits": 3,
      "section": { "id": 120, "number": "2", "instructor": "Raed Bolbol", "changedAt": null, "changeSummary": null,
                   "meetings": [ { "day": 0, "start": "08:00", "end": "09:30", "room": "A101", "date": "2026-11-29",
                                   "excludedDates": null, "merge": "2026-11-29|08:00|09:30|a101" } ] },
      "changed": false, "students": 12, "own": true, "added": false, "teachers": [ /* section 8.2 */ ] } ],
  "needsSection": [], "clashes": [],
  "changes": [ /* section 6.2, each with canUndo, canEdit, mine, flagged */ ],
  "mergeSizes": { "2026-11-29|08:00|09:30|a101": 23 },   // distinct Classy users of each merged block (4.2)
  "removed": [ { "code": "CS1102-L", "number": "3" } ]    // sections they added that the university has since removed
}
```

- `classes` can hold several entries with the same `courseId` (two sections of one course).
- `section.meetings` has the overlay applied exactly as for students (section 6).
- `changed`: `Section.ChangedAt` is newer than the `TeacherSeen` row, or than `Teacher.CreatedAt` when there is none. False in "view as".
- `officeHours[].excludedDates`: the days off on that weekday. The web also limits them to the teaching weeks through `span`.
- `plannerEvents`: only when `PlannerEngine.Enabled`. `PublishedPlan.For` is fed unsaved `Pick`-shaped objects (`new Pick { CourseId, SectionId, Course = course }`, never added to the context) for the taught sections.
- **Class size** is always the exact pick count. Numbers only; no teacher endpoint returns a student ID or name.

`GET /teach/sections?q=`:

```ts
{ semester: { id: number; name: string },
  mine: SectionRow[],                                   // own and added, hidden ones included
  previous: { code: string; number: string }[],         // last semester's added sections that exist now and aren't mine yet
  courses: { id: number; code: string; name: string; sections: SectionRow[] }[] }   // only when q is given; up to 50 courses
interface SectionRow { id: number; code: string; number: string; instructor: string; when: string /* "Sun 08:00 · Tue 11:30" */;
  state: 'own' | 'added' | 'hidden' | null }
```

The search matches code (spaces ignored), course name or instructor, as `AdminEndpoints.cs:281-287` does.

**New semester.** `TeacherSection` rows belong to one semester, so a new one starts with none; `previous` offers last semester's to tick again. Name links carry over by text.

### 4.5 Teacher items for reminders and feeds

```csharp
/// <summary>What each teacher has on: their classes (one item per real class), own slots, office hours and reps' events. The same items the student paths use.</summary>
public static Task<Dictionary<int, List<StudentSchedule.Item>>> ItemsAsync(AppDb db, Semester semester, IReadOnlyCollection<Teacher> teachers,
    bool slots, bool planner, CancellationToken ct, List<(int TeacherId, StudentSchedule.Item Item)>? cancelled = null);
```

- The dictionary key is the teacher id. Every item has `StudentId = 0`, which matches no student, and the lists are never mixed with student lists.
- Only sections on the timetable (`!Hidden`). Merged sessions (one merge key, 4.2) become one item with `Code = "BE1101 · IBSM1101 · LOG1101"` and the lead section's ids. The lead is the member whose course code sorts first (ordinal), the same rule the web uses (10.4).
- A weekly merged class whose codes skip different dates (one code was unticked from a change): one weekly item with the lead of all members and `Skip` = the union of the members' skipped dates, plus one dated item for each date in (union minus intersection), made from the members still on that date (their codes, their lead). So a reminder for such a date names only the codes that still meet.
- The overlay is applied through the same `ChangeSet` as for students.
- Office hours are `Source.Slot` items named "Office hours", with `Skip` = the days off on that weekday. Weekly slots already stop outside the teaching weeks.

---

## 5. Class changes

### 5.1 Endpoints (`Teachers/ClassChangeEndpoints.cs`, group `/api/teach`; the rules live in `Teachers/ChangeRules.cs` so the dashboard can call them)

| Endpoint | Purpose |
|---|---|
| `POST /teach/changes/preview` | Numbers and warnings. Saves nothing. No fresh-code check. It refuses exactly what a write would for the things that guard numbers: checks 3 to 8 of 5.2 run as the same hard refusals, so a frozen account gets 403 with the freeze message and no numbers, and a section the actor may not change is a 403. Sizes and clash counts are only ever given for sections on the actor's timetable. |
| `POST /teach/changes` | Create. |
| `PUT /teach/changes/{groupId}` | Replace: edit a change without undoing it first. |
| `POST /teach/changes/{groupId}/undo` | Undo. |
| `GET /teach/changes` | The acting teacher's list: changes they made, or on sections they may change; upcoming plus the last 14 days. |
| `GET /teach/rooms?date=&start=&end=&sectionId=&origDate=&origStart=` | Rooms free for the whole interval, ignoring the class being changed. Same answer shape as `/rooms/free`. |

Body for preview, create and replace:

```jsonc
{ "kind": "move",
  "sectionId": 41, "date": "2026-12-01", "start": "10:00",   // the session; not sent for "extra" or on replace
  "sectionIds": [41, 57, 63],                                 // the course codes ticked
  "alsoOther": false,                                         // also the section's other sessions that day
  "newDate": "2026-12-02", "newStart": "11:30", "newEnd": "13:00",   // move and extra
  "room": "B202",                                             // move, room, extra. "" or null = to be announced
  "type": null,                                               // extra only: lecture | lab | tutorial
  "note": "Guest lecture clash",
  "groupId": null }                                           // preview only: the change being replaced
```

- cancel, move, room: `sectionId, date, start` name the session. `sectionIds` must be that session's section plus any of its siblings (4.2); leaving it out means all of them. Siblings never include a code that already has its own live change on that session.
- extra: `sectionIds` are any sections the actor may change (at least one). Preview for an extra takes the same `sectionIds` and returns `codes` for exactly those.
- replace: the session is the group's own; `sectionIds` may untick codes.

- Chosen because: the body names one session and the server works out its siblings, so the server, not the page, decides which sections are one real class.

Preview answers:

```jsonc
{ "codes": [ { "sectionId": 41, "code": "BE1101", "number": "2", "students": 9, "named": "Raed Bolbol", "yours": true, "changedBy": null } ],
  "size": 23,                       // distinct Classy users over the ticked codes
  "others": [ { "start": "13:15", "end": "14:45", "room": "A101" } ],   // every other not-started, unchanged session of that section that day
  "needsCode": false,
  "blocked": null,                  // "started" | "switched-off" | null
  "warnings": [ { "code": "clash", "text": "4 of 23 Classy users have another class then." } ] }
```

`codes` lists the session's section and its siblings (tickable). A code of the same university block that already has its own live change is listed too, with `changedBy` = that change's group id, for information only: it cannot be ticked and is never part of "all of them".

Create, replace and undo answer `{ changes: [ /* section 6.2 items */ ] }`.

### 5.2 Validation (create and replace)

Each write opens its transaction **first** (Microsoft.Data.Sqlite begins `BEGIN IMMEDIATE`, as `ScheduleImport.ApplyAsync` relies on), then reads `now`, then validates and writes. A change and a timetable apply therefore never interleave.

Refused, in this order. Everything else only warns.

| # | Check | Answer |
|---|---|---|
| 1 | Fresh code (2.5) | 403 with `needsCode` |
| 2 | The switch is off and the actor is not in "view as" | 409 "Class changes are switched off for now." |
| 3 | `actor.Profile?.FrozenAt` is set | 403 "Your class changes are paused. Ask the Classy team." |
| 4 | No current semester | 409 |
| 5 | `kind` invalid; `note` over 200; `room` over 60 (both trimmed, control characters stripped); `type` invalid for an extra | 400 |
| 6 | A section in `sectionIds` is not one the actor may change (4.1) | 403 "That class isn't on your timetable. Add it under Edit my classes first." |
| 7 | cancel, move, room: the session does not exist, meaning no meeting of `sectionId` with that start that happens on that date (dated on it, or weekly on that weekday inside the teaching weeks and not a day off) | 400 "This class has no session then." |
| 8 | `sectionIds` holds a section that is not the session's own or a sibling | 400 |
| 9 | The session has started: `ChangeRules.StartUtc(date, start) <= now` (UTC, section 1). On a create this is the session as the university lists it. On a replace it is the limit of 5.4: for a live move, the class as it now stands (its current `NewDate` and `NewStart`), not the original | 400 "This class has already started." |
| 10 | Create only: a ticked section already has a live change on that session | 409 "This session already has a change. Edit or undo it." with `{ groupId }`. A `DbUpdateException` from the unique index is answered the same way. |
| 11 | move, extra: bad date or time; `end <= start`; shorter than 15 or longer than 600 minutes; new start not in the future; date more than 190 days ahead | 400 |
| 12 | move: same date and start as the original | 400 "Pick a different time, or use Change room." |
| 13 | room: the session has exactly one base meeting (6.1) and `RoomKey(room)` equals its room's | 400 "That's the room it's already in." |
| 14 | extra: `sectionIds` empty; or a ticked section already has a live extra at that date and start | 400 / 409 |
| 15 | Replace or undo: the group has no live rows | 409 "Someone already changed this. Reload." |

Owners in "view as" pass check 2, as owners pass the planner switch, so the flow can be tried while the switch is off.

`alsoOther: true` applies the action to **every** other not-yet-started, unchanged session of the section that day (the preview's `others`; a few sections have three or four sessions on one day), one more group for each: cancel cancels it, room gives it the same room, move shifts it by the same number of days and minutes. Each sibling section moves its own other sessions the same way. The answer lists every group made, and the Undo toast undoes all of them.

### 5.3 Warnings (never block)

| Code | When | Text |
|---|---|---|
| `clash` | move or extra, and the count is above 0 | "4 of 23 Classy users have another class then." |
| `room-taken` | `FreeRooms.OccupiedAsync` (6.5) has an overlapping entry with the same `RoomKey`, ignoring the sessions being changed | "B202 is taken then by CS2101 (until 11:30)." |
| `outside-hours` | new start before 08:00 or new end after 18:15 (`Busy.DayStart`, `Busy.DayEnd`) | "That's outside 08:00–18:15." |
| `weekend` | Friday or Saturday | "That's a Friday." |
| `day-off` | `span.DaysOff` has the new date | "18 Nov is National Day, a day off." |
| `outside-weeks` | the new date is before `span.First` or after `span.Last` | "That's outside the teaching weeks." |
| `short-notice` | the earliest affected start (old or new) is under 120 minutes away | "This starts in 10 minutes; students may not see it in time." |
| `same-class` | move or extra onto a date and start where a ticked section already has a session that is still on (a university session no live change skips, or one a change put there) | "This class already has a session then." |
| `replaces` | preview of a session that has a live change | "This replaces: Cancelled by Raed Bolbol." |
| `not-yours` | none of the actor's name keys equals `NameKey(section.Instructor)` | "Listed under Mariam Ebeid. They'll be told." |

**Clash count** (`ChangeRules.NumbersAsync`): the students = distinct `StudentId` of picks on the ticked sections. Load `StudentSchedule.ForAsync(db, semester.Id, thoseStudents, slots: false, planner, ct)` and count the students with an item that `Happens` on the new date and overlaps the new time. Left out: the session being changed (an item of a ticked section starting at the original start on the original date) and, on a replace, items whose `ChangeId` belongs to the group. `slots: false` is the plan's "classes only, not their private own slots". Only integers leave the function.

### 5.4 Who may do what

| Action | Who |
|---|---|
| Create | Any acting teacher, on any section they may change (named or added, name approved or not). As decided. |
| Replace, undo | The maker (`ByKind`, `ById`); anyone who may change the section (so the named teacher, a second teacher who added it, the new teacher after a reassignment); an owner in "view as". Staff undo from the dashboard (section 9). |
| Undo, until when (`ChangeRules.CanUndo(change, now)`) | cancel, room: before the original start. move: before the earlier of the old and the new start. extra: before its start. The same limit applies to staff. |
| Replace, until when (`ChangeRules.CanEdit(change, now)`) | cancel, room: before the original start. move, extra: before the start of the class **as it now stands** (the change's current `NewDate` and `NewStart`). So a class moved from Sunday to Thursday can still be moved again, re-roomed or cancelled on Monday, though it can no longer be undone. |
| Replace of a move after its original start has passed | The new change may only stay a `move` (another time, another room, or both: "Change room" on a moved class is a move that keeps the time) or become a `cancel`. It can never become a `room` change, which would put the class back at a time that has gone. The new time must be in the future (check 11). A cancel made this way is final: its original start has passed, so it can be neither undone nor edited. |
| Frozen teacher | No create, replace or undo. Staff undo for them. A frozen account also cannot add a section or save office hours and details (5.9). |
| Switch off | No create or replace. Undo still works (it only restores the university timetable). |

A teacher's change wins over a batch rep's change to the same session (section 6.7).

`Flagged` is set when the actor is in "view as", or none of the actor's name keys equals `NameKey(section.Instructor)`.

### 5.5 What a write does (one transaction)

**Create.** One `ClassChange` row per ticked section, sharing a new `GroupId`. Each row copies the course code, name and section number; `Orig*` from the matched meeting; `SessionType` from `SessionKinds.Of(origRoom)` (the lab/tut rule moved out of `CalendarFeed.Kind` into `Schedule/SessionKinds.cs`) or the chosen type; `NamedTeacher`; `Flagged`; `By*`, `OnBehalfOf`, `TeacherId = actor.Profile?.Id`; `Tag` (5.6). Then the notices (5.6) and the daily count (below).

**Replace.** For each live row of the group: a section still ticked is ended as `replaced` and gets a new row (new `GroupId`, same `Tag`, same `Orig*`); an unticked one is ended as `undone` and its students are told the class is back on (unticking is refused with 400 once that row can no longer be undone, 5.4). Notices as for a create, after deleting the session's unclaimed ones (5.6), so students get one notification with the new details. When a moved class is replaced after its original start, the student notice is about the moved session: "{Code} cancelled" / "Thu 4 Dec 11:30 (moved from Sun 30 Nov 10:00) · Cancelled by {ByName}".

**Undo.** Ends every live row of the group as `undone` through `EndAsync`. Undo on a group with no live rows answers 409 and writes nothing, so a double tap sends one notice.

**`ChangeRules.EndAsync(db, rows, reason, by, now)`** sets `EndedAt`, `EndedBy`, `EndReason`. It is the only way a change ends, used by teachers, staff, teacher removal and reconciliation. It always first deletes the `students` notices of each row's `(SectionId, Tag)` whose `ClaimedAt` is null, so a change that ends before its notice went out is never announced afterwards. What it writes then depends on the reason:

| Reason | Students | Teachers |
|---|---|---|
| `undone`, `undone-by-staff`, `undone-on-remove` | `back-on`, when the session was announced (5.6) | `undone` (5.6) |
| `replaced` | nothing (the replace writes its own `change`) | nothing (the replace writes its own) |
| `merged` | nothing: the university timetable now shows it | nothing; the caller writes `merged` when 7.4 says so |
| `dropped-moved`, `dropped-removed`, `dropped-day-off` | nothing from here: "back on" would be false. The caller writes the `dropped` notice of 7.3 where one is due | nothing from here; the caller writes the 7.4 notice |

**Daily alert.** After a create or replace outside "view as": count the distinct `GroupId`s this `TeacherId` created since Oman midnight (as a UTC instant, section 1). When it is more than 5 and `BusyAlertOn` is not today: set it and add one `staff` notice "{name} has made 6 class changes today" (URL `/admin/teacher-changes`, tag `busy-{teacherId}-{yyyyMMdd}`). The dashboard home lists such teachers too (section 9). Nobody is stopped.

### 5.6 Notices written with each action

All rows are written by `Teachers/TeachNotices.cs` in the same transaction as the change.

**The tag** identifies a session on a phone, so a later state replaces the earlier one: `tchange-{sectionId}-{yyyyMMdd}-{HHmm}` from the original date and start; for an extra, `tchange-{sectionId}-x{yyyyMMdd}-{HHmm}` from its first new time. It is stored on the change and copied by a replacement.

| Event | To | Title / body |
|---|---|---|
| cancel | students of each section | "{Code} cancelled" / "{Name} · Tue 2 Dec 10:00 · Cancelled by {ByName}: {note}" |
| move | students | "{Code} moved" / "Tue 2 Dec 10:00 → Wed 3 Dec 11:30, room B202 · Moved by {ByName}: {note}" |
| room | students | "{Code} room changed" / "Tue 2 Dec 10:00 · now in B202 (was A101) · {ByName}: {note}" |
| extra | students | "{Code} extra class" / "Thu 4 Dec 15:00–16:30, room A101 · Lecture · Added by {ByName}: {note}" |
| any of the above | every other teaching account that may change one of the group's sections (`TeachersOfAsync` over all of them, minus the acting profile; in "view as" the viewed account is told too) | "{ByName} changed your class" + the same body. Kind `other-teacher`. Covers the named teacher and "the other teacher". |
| undo | the same students, **only if they were told** | "{Code} is back on" / "Tue 2 Dec 10:00, room A101, as on the timetable." (extra: "{Code} extra class removed") |
| undo | the other teaching accounts; and the maker when someone else undid it | "{Code}: the change on Tue 2 Dec was undone by {name}". Kind `undone`. |

- **Student notices are one per section** (each section has its own students). **Teacher notices are one per `(TeacherId, Kind, GroupId)`**: the recipients of one action are gathered over all its sections into a distinct set, and each gets one row whose `{Code}` lists the codes ("BE1101 · IBSM1101 · LOG1101"). One cancel of a three-code class under one name is one inbox row and one push for that teacher, not three.
- `{name}` and `{ByName}` in every student and teacher notice are the actor's `ByName` (a shown name, or "Classy staff" for staff and "view as"). `ByLabel` and `EndedBy` hold emails, usernames and university IDs for the log and are never put in a notice.
- With "view as", `ByName` is "Classy staff" and the body ends "for {OnBehalfOf}".
- Student URL: `/?session={courseId}.{date}.{HH:mm}` of the session as it now stands (the new time for a move or an extra; the original for a cancel, a room change and "back on"). Teacher URL: `/teach?session=s{sectionId}.{date}.{HH:mm}` with the group's lead section (4.5).
- `ExpiresAt` (UTC, section 1): the start of that session. For a move, and for the "back on" of a move, the **later** of the original start and the new start: a class moved to an earlier time must still be announced after the new time has passed, because the old slot is still ahead and students must know it is off.

**"Only if they were told", exactly.** Before writing student notices for a session, the writer deletes the `students` rows with the same `(SectionId, Tag)` whose `ClaimedAt` is null (`EndAsync` does the same for every ending, 5.5). The session counts as **announced** when the newest (highest `Id`) remaining `students` row for that `(SectionId, Tag)` is a `change`. Then:

- create or replace → add a `change` row;
- undo → add a `back-on` row when announced, else nothing.

So a change made and undone before it was sent sends nothing; an edit before it was sent sends one notice; an undo after sends "back on".

### 5.7 Sending (`TeachNotices.RoundAsync`)

`NotificationWorker.RoundAsync` gets one more independent try-block that calls `TeachNotices.RoundAsync(db, push, now, ct)`:

1. Read rows with `SentAt == null`, by `Id`, 200 at most.
2. A row past `ExpiresAt` is marked `SentAt = now` and not pushed (a teacher's row still shows in their inbox). So is a `students` row whose `SectionId` is null (its section was removed; there is nobody to resolve).
3. Claim each row: `ExecuteUpdate` sets `ClaimedAt = now` where `Id == id && SentAt == null && (ClaimedAt == null || ClaimedAt < now − 2 minutes)`. Send only when one row changed.
4. Recipients:
   - `students`: picks on `SectionId` with `!Student.MuteChanges` and a device (the query of `Notifications.Changes`), in one `PushSender.SendAsync` call.
   - `teacher`: load the `Teacher` row and call `PushSender.SendToTeacherAsync(db, teacher, note, ct)` (5.8). Nothing else.
   - `staff`: students with `IsOwner` and a device. RSA and dashboard owners have no devices; their alert is the dashboard (section 9).
5. Set `SentAt = now`. TTL = `ExpiresAt − now`, at least 60 seconds, at most 3 days.

A crash between claim and send re-sends after 2 minutes (the tag replaces the notification on the phone). Without a crash each notice is pushed once. "At once" means within the worker's 30 seconds, as for every notification today.

### 5.8 The teacher's own devices and reminders (`Teachers/TeachDevices.cs`)

- `GET/PUT /teach/notifications`: `{ remindMinutes, changes, devices }`, the shape of `/me/notifications`, stored on the `Teacher` row.
- `POST /teach/push`: for a teacher cookie the endpoint is saved in `TeacherPushSubscriptions` and deleted from `PushSubscriptions` in one transaction. `POST /me/push` deletes it from `TeacherPushSubscriptions`. A teaching assistant's device stays a student device (their own reminders keep working); `/teach/push` stores it under the student.
- `POST /teach/push/remove` deletes only the caller's own row for that endpoint: `TeacherId == actor.Profile.Id` for a staff account, `StudentId == actor.Profile.StudentId` in `PushSubscriptions` for a teaching assistant.
- **One way to reach a teacher's devices.** A teacher id must never be passed where a student id is expected (`PushSender.SendAsync` looks ids up in `PushSubscriptions.StudentId`, so teacher 7's reminder would go to student 7). `PushSender` gets one method:

  ```csharp
  /// <summary>Sends to a teaching account's devices: its own for a staff account, the student's for a teaching assistant.</summary>
  public Task<int> SendToTeacherAsync(AppDb db, Teacher t, Note note, CancellationToken ct);
  ```

  When `t.StudentId` is null it sends to `TeacherPushSubscriptions` where `TeacherId == t.Id` (the device loop of `SendAsync`, shared as a private method); otherwise it calls `SendAsync(db, [t.StudentId.Value], note, ct)`. Reminders, `TeachNotices.RoundAsync` (5.7) and `/teach/push/test` call only this method. No teacher code calls `SendAsync` with an id of its own.
- **"Has a device"** for a teacher means: a `TeacherPushSubscriptions` row with their `TeacherId` when `StudentId` is null; otherwise a `PushSubscriptions` row with their `StudentId`.
- `TeachReminders.DueAsync(db, from, to, ct, planner)` returns `List<(Teacher Teacher, Note Note)>`: the window loop of `Notifications.DueReminders` over `TeacherSchedule.ItemsAsync`, for teachers with `RemindMinutes` and a device. Classes and own slots only (not office hours). Class URL `/teach?session=s{sectionId}.{date}.{time}`, tag `tclass-{sectionId}-{date}-{HHmm}`; slot URL `/teach`, tag `tslot-{id}-{date}`. One reminder per merged block (one merge key, 4.2). The worker sends them in a try-block of their own, over the same `(last, now]` window as the student reminders, each through `SendToTeacherAsync`. The student block (`Notifications.cs:345-363`) is not edited and never sees a teacher row, and a failure here cannot make student reminders go out twice.

### 5.9 Freeze and the switch

- `PUT /api/admin/teachers/{id}/frozen` (staff) and `PUT /api/admin/teachers/switch` (owners), section 9.
- Both stop new writes; live changes stay as they are.
- A freeze also closes the other things a frozen account could put in front of students: while `Profile.FrozenAt` is set, `PUT /teach/sections/{id}` with `on = true` for a section not under the account's names, and `PUT /teach/office`, answer 403 with the freeze message. Hiding a section and removing an added one still work. Preview gives a frozen account no numbers (5.1).

---

## 6. One shared place for changes

### 6.1 `Schedule/ClassChanges.cs`

```csharp
public static class ClassChanges
{
    public const string EnabledSetting = "teachers.changes.enabled";
    public static string RoomKey(string? room);
    public static Task<bool> EnabledAsync(AppDb db, CancellationToken ct);
    /// <summary>The live changes of these sections (meetings loaded), ready to apply. One query; normally finds nothing.</summary>
    public static Task<ChangeSet> LoadAsync(AppDb db, int semesterId, IReadOnlyCollection<Section> sections, CancellationToken ct);
    // Stage 5: Decide, ReconcileAsync, EndSectionAsync, AfterApplyAsync, SameForStudents (section 7)
}

public sealed class ChangeSet
{
    public static readonly ChangeSet None;
    public bool Empty { get; }
    /// <summary>Dates this meeting doesn't happen as the timetable has it: cancelled, moved away, or put in another room.</summary>
    public IReadOnlySet<DateOnly> Skip(Section s, Meeting m);
    /// <summary>Sessions a change puts on: the class at its new time or room, and extra classes.</summary>
    public IReadOnlyList<Added> AddedTo(Section s);
    /// <summary>Sessions that stay visible though they aren't on: cancelled ones and the old slot of a moved one.</summary>
    public IReadOnlyList<Off> OffIn(Section s);
    /// <summary>Every live change of these sections, for the marks people see.</summary>
    public IReadOnlyList<ClassChange> Shown(IEnumerable<Section> sections);
    /// <summary>Whether a rep's event on this session gives way to a teacher's change.</summary>
    public bool Shadows(int sectionId, DateOnly date, TimeOnly start);
}
public sealed record Added(ClassChange Change, DateOnly Date, TimeOnly Start, TimeOnly End, string Room, Meeting? Base);
public sealed record Off(ClassChange Change, DateOnly Date, TimeOnly Start, TimeOnly End, string Room, Meeting Base);
```

- `LoadAsync` reads `db.ClassChanges.Where(c => c.SemesterId == semesterId && c.EndedAt == null)` (indexed), keeps the given sections' rows, and returns `None` when there are none. The semester and its `SemesterSpan` are loaded only when there are changes. `Empty` lets every caller skip the rest.
- **The base of a change** is **every** meeting `m` of its section with `m.Start == c.OrigStart && (m.Date == c.OrigDate || (m.Date is null && span.Weekly(m.Day, c.OrigDate, true)))`. Room and end time are never part of the match. Normally that is one meeting. The importer can keep two (a class listed in two rooms at one time, `ScheduleImport.cs:140-143`; or a weekly and a dated meeting on the same day and start): then `Skip` applies to all of them, a move or room change still adds **one** session, and the `Orig*` snapshot, `SessionType` and `Added.Base` come from the first by room (ordinal).
- **Read-time guard.** A cancel, move or room change whose section has no base is left out of every member. Stage 5 ends such rows when the timetable changes; the guard means a missed case can never draw a change on top of a session that is gone, and can never make one come back.
- **A room change keeps the university's time.** An `Added` session of kind `room` takes `Start` and `End` from its base, not from the row, so when the university later changes only the end time the re-roomed session follows it.
- **No doubles.** An `Added` session is left out only when the section has a real meeting on that date at that start **that is still on**, meaning no live change skips it. The change's own base never counts (a room change's base is always at that date and start, and is skipped by the change itself). So a room change shows once; two sessions of one day swapped with each other both show; an extra class put on a cancelled slot shows. The case the rule is for is the university adopting the new time while the old one is still listed (7.1).

### 6.2 `GET /me/timetable` and `GET /teach/timetable` (through `TimetableView`)

Three things, nothing else:

1. `Skipped(section, m)` also unions `changes.Skip(section, m)`. So `excludedDates` on the base meeting carries the original date of every live cancel, move and room change.
2. The section's `meetings` get one more entry per `changes.AddedTo(section)`: `day` = weekday of the new date, `start`, `end`, `room`, `date` = the new date, plus `changeId` and `type`. The list is sorted as `Dto.Of(Section)` sorts it.
3. A new top-level `changes` array = `changes.Shown(sections)`.

`MeetingDto` gets three trailing members, each omitted from JSON when null, so the admin course pages, `/me/options` and the public timetable API emit exactly today's JSON:

```csharp
public record MeetingDto(int Day, string Start, string End, string Room, string? Date, List<string>? ExcludedDates = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ChangeId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Merge = null);
```

`SectionDto` is not changed.

A `changes` item:

```ts
export interface ClassChange {
  id: number
  group: string
  kind: 'cancel' | 'move' | 'room' | 'extra'
  courseId: number
  sectionId: number
  code: string
  /** Sent, not guessed from the room: a lab moved to a typed room stays a lab. */
  type: 'lecture' | 'lab' | 'tutorial'
  /** The session as the university lists it; null for an extra class. */
  from: { date: string; start: string; end: string; room: string } | null
  /** Where it is now; null for a cancelled class. room '' = to be announced. */
  to: { date: string; start: string; end: string; room: string } | null
  note: string | null
  /** "Raed Bolbol", or "Classy staff". */
  by: string
  at: string
  // Sent only by /teach/timetable and /teach/changes:
  canUndo?: boolean     // ChangeRules.CanUndo, and this teacher may undo it
  canEdit?: boolean     // ChangeRules.CanEdit (5.4): a moved class can still be edited after it can no longer be undone
  mine?: boolean
  flagged?: boolean
}
```

Students get the live changes of their picked sections for the whole semester (past ones too, so last week still shows Cancelled). Teachers get those of the sections on their timetable.

**What each client sees:**

| Change | An old cached copy of the app | The new app |
|---|---|---|
| cancel | The class is gone from that date. | A greyed block in place, "Cancelled by …". |
| move | Gone from the old slot; a normal class at the new time. | Greyed "Moved to Wed 11:30" in the old slot; a "Moved" mark at the new time. |
| room | Same time, the new room. | The same, with "Room changed". |
| extra | A normal dated class. | An "Extra class" mark. |

An old cached copy only pages through the teaching weeks, so a class moved to a date outside them has no day for it to show on there; the notification link still opens that session's page. The new app adds such weeks (10.5).

`clashes` in the answer stays `Clashes.Find` on raw sections, as today (and `[]` for teachers). The web computes clashes from the meetings it receives, so a cancelled session drops out and a moved one counts at its new time.

- Chosen because: the hard rule is that old cached copies keep working; with the class at its new time sent as an ordinary meeting, an old copy shows the right time and room instead of an empty slot. It also means every existing web reader of sessions (clashes, attendance totals, the share picture) needs no edit.

### 6.3 `StudentSchedule.ForAsync`

`Item` gets trailing optional fields, so existing constructor calls compile unchanged:

```csharp
int? ChangeId = null, string? ChangeKind = null, string? ChangeBy = null, string? Uid = null, string? Kind = null
```

`ForAsync` gets one trailing optional parameter: `List<Item>? off = null`.

Inside, whatever the `planner` argument is:

- `var changes = await ClassChanges.LoadAsync(db, semesterId, sectionsOfThePicks, ct);`
- each class item's `Skip` = the planner's dates ∪ `changes.Skip(section, m)`;
- for each `changes.AddedTo(section)`: one more `Source.Class` item at the new date, time and room, with `SectionId`, `CourseId`, `Note`, `ChangeId`, `ChangeKind`, `ChangeBy`, `Kind = SessionType`, and `MeetingId` null;
- when `off` is given, each `changes.OffIn(section)` is added **to that list only**, dated on the original date. Cancelled and moved-away sessions are never in the returned list.
- `plan.For(picks, changes.Shadows)` (section 6.7).

`Uid` is the calendar id to reuse: for a moved or re-roomed class and for an off item, the original class's UID (`class-{SectionId}-{MeetingId}@classy`, with `-yyyyMMdd` of the original date for a weekly meeting, exactly as `CalendarFeed.Event` builds it); for an extra, `change-{ChangeId}@classy`.

- Chosen because: a separate list for sessions that are not on means no existing reader can mistake one for a class, while the calendar still gets them from the same single load.

Because every consumer goes through `ForAsync` and `Happens`, these follow with no further edit:

| Surface | Result |
|---|---|
| Reminders (`DueReminders`) | None for a cancelled or moved-away session; one at the new time (the existing URL and tag, which resolve to the class at its new time); one for an extra class. |
| Groups' busy times and rides (`Busy.ForAsync`) | The cancelled time is free; the new time is busy. |
| Absence marking (`PUT /me/absences`) | A cancelled session can't be marked; a moved or extra one can, at its new date and start. |

### 6.4 Smaller edits so the rest agrees

- **Absence counter.** `GET /me/absences` leaves out rows whose `(course, date, start)` is the original of a live cancel or move on the student's picked section, **unless** `changes.AddedTo(section)` has a session on that date at that start (another change put a class there, as in a swap of two sessions: an absence marked on it counts). With no changes the answer is identical. The web's "of Y" already follows the sessions it draws.
- **Notes.** `PersonalEndpoints` note date check (`150-162`): `classDay` is also true when `changes.AddedTo(pick.Section)` has that date. Checked before, and whatever, `PlannerSession` says.

### 6.5 Free rooms

Lines 49–68 of `FreeRooms.cs` move into:

```csharp
public sealed record Occupied(string Room, TimeOnly Start, TimeOnly End, string Label, int? SectionId);
/// <summary>What is in each room on a date: classes in the teaching weeks, reps' changes when the planner is on, and teachers' changes always.</summary>
public static Task<(List<string> Rooms, List<Occupied> Occupied, string? DayOff)> OccupiedAsync(AppDb db, Semester? semester, DateOnly day, IConfiguration config, CancellationToken ct);
```

Teacher changes are applied through `ChangeSet`, the one reader (section 0, point 4). `FreeRooms` never reads the `ClassChanges` table itself, so it cannot disagree with the timetable about the read-time guard or the no-doubles rule:

- The meetings query loads the semester's sections with their meetings (today's query selects meetings without their section; `Occupied.SectionId` needs it), then `var changes = await ClassChanges.LoadAsync(db, semester.Id, sections, ct)`.
- A base entry is dropped when `changes.Skip(section, m)` has the day.
- Each `changes.AddedTo(section)` session on that day with a non-empty room adds `(Room, Start, End)`.
- With the planner on, the list still comes from `PlannerEngine.Calendar` (with `changes.Shadows`, 6.7); the same two steps are then applied to its class entries, matched by the entry's section (its `Targets`) and start. The planner is off in production, so this branch ships with tests only.

A typed room occupies by `RoomKey` but is never added to the room list. `GET /rooms/free` calls this function and returns exactly today's shape; `FreeRoomsTests` must pass unmodified. `GET /teach/rooms` uses the same function for "free for the whole interval" and ignores the entries of the session named by `sectionId, origDate, origStart` and its siblings.

- Chosen because: a teacher-only rooms endpoint leaves the student one's contract alone and can ignore the class being moved, which a shared `?until=` parameter could not.

### 6.6 Calendar feeds

- `Build` and `Text` keep their signatures and call new `BuildFrom(items, off, span, now, calendarName)` and `TextFrom(items, off, span, now, from, days, intro)`, which the teacher feed reuses. `CalendarTests` must pass unmodified.
- `.ics`: the UID is `i.Uid ?? today's`. An off item with `ChangeKind == "cancel"` is written with its original UID, `SUMMARY:Cancelled: {Name} · {Kind}`, `TRANSP:TRANSPARENT` and `DESCRIPTION:Cancelled by {by}. {note}`. There is **no** `STATUS:CANCELLED` line (calendar apps hide those events, and the plan wants the class to stay with "Cancelled:" in its title). The old slot of a moved class is not written; the class appears at its new time under the original UID, so calendar apps move the entry in place. Moved, re-roomed and extra items add "Moved from Tue 2 Dec 10:00 by {by}" (or "Room changed by …", "Extra class added by …") and the note to the description. The class kind comes from `Kind` when set.
- `.txt`: cancelled lines read `- 10:00–11:30  CANCELLED by {by}: {Code} {Name}`; `Line()` adds `moved from Tue 10:00`, `room changed`, `extra class` and the note. "Next class" and "in class right now" use items only. The sentence "The student hasn't picked…" stays for students; the teacher feed says "No classes on this timetable yet."
- `GET /api/calendar/{token}.ics|.txt` look the token up in `Students`, then in `Teachers`. The teacher feed is named "Classy Teaching" and is built from `TeacherSchedule.ItemsAsync`. A removed teacher's token is dead with the row.

### 6.7 A teacher's change wins over a rep's

`PublishedPlan.For` gets one optional parameter: `Func<int, DateOnly, TimeOnly, bool>? shadowed = null`. In the per-week loop, when the event has a `Source` and `shadowed(source.SectionId, thatWeeksOriginalDate, source.Start)` is true, that week is skipped entirely: no exclusion and no replacement. `PlannerEngine.Calendar` gets the same optional parameter for its `Apply` (free rooms pass it; the reps' planner screen does not).

The rep's event is never edited, so its other weeks are untouched and it comes back if the teacher undoes. The planner is off in production, so this ships with tests only.

### 6.8 What deliberately stays raw

`TimetableExport`, the CSV and `GET /api/public/timetable` keep showing the university timetable. The CSV is uploaded back and must not bake teacher changes into the base (`DashboardTests.cs:139-171`).

---

## 7. When the university timetable changes

Everything happens at apply time, inside the apply transaction. Because a class change and an apply both hold the write lock, a change that commits before the apply begins is reconciled by it, and one that begins after is validated against the new timetable.

### 7.1 The decision function

```csharp
public enum Outcome { Keep, KeepTellRoom, Merge, MergeTell, Drop }
/// <summary>What a new meeting list does to one live change. Pure: saves nothing.</summary>
public static (Outcome What, string? Reason) Decide(ClassChange c, IReadOnlyList<ParsedMeeting> oldMeetings, IReadOnlyList<ParsedMeeting> newMeetings, SemesterSpan span);
```

- **base** = a new meeting matching the change's key (6.1).
- **came** (used for a cancel whose base is gone) = new meetings on the change's `OrigDate` (dated on it, or weekly on that weekday) whose start is not the start of any old meeting on that date. Only a session that appeared at a new time on that day counts. A room or end-time change on that date, or anything on another date of the section, does not: `ParsedMeeting` equality includes room and end, and the live timetable is thousands of dated rows, so a wider test would call most real merges a drop.
- **adopted** = a new meeting on `NewDate` (dated, or weekly on that weekday) starting at `NewStart`. **Same** = it also has the change's `NewEnd` and `RoomKey(NewRoom)`, or the teacher's room is "to be announced".

| Kind | New timetable | Outcome |
|---|---|---|
| cancel | base exists | **Keep**. Refresh `OrigEnd`, `OrigRoom`. A room-only university change never touches a cancel. |
| cancel | base gone, `came` empty | **Merge** quietly: the university dropped exactly what the teacher cancelled. |
| cancel | base gone, `came` not empty | **Drop** (`dropped-moved`). |
| move | base exists | **Keep**. Refresh the snapshot. If the university also lists the new time, the no-doubles rule (6.1) shows one class: the university's session, in the university's room. When that session is new in this apply (not in the old list at that date and start) and has another room or end than the teacher's, the teacher gets one `room-differs` notice (7.4). |
| move | base gone, adopted and same | **Merge** quietly: the university shows what the teacher did. |
| move | base gone, adopted but another room or end | **MergeTell**: ended as `merged`; students see the university's session; the teacher is told its room. |
| move | base gone, not adopted | **Drop** (`dropped-moved`). |
| room | base exists, its room now equals the teacher's room | **Merge** quietly. |
| room | base exists, its room differs from the stored `OrigRoom` (and from the teacher's) | **KeepTellRoom**: students keep the teacher's room; `OrigRoom` is refreshed, so the teacher is told once. |
| room | base exists, room unchanged | **Keep**. A changed end time needs nothing here: readers take a room change's time from its base (6.1). `OrigEnd` and `NewEnd` are refreshed for the log. |
| room | base gone | **Drop** (`dropped-moved`). |
| extra | adopted and same | **Merge** quietly. |
| extra | adopted but another room or end | **MergeTell**. |
| extra | not adopted | **Keep**. |

A room-only, end-time-only or instructor-only change by the university never drops a teacher's change, because none of those is in the key. That holds although every meeting id is reissued: it is the reason for the natural key.

Only changes whose latest date (`max(OrigDate, NewDate)`) is today or later are decided; older ones are history and stay as they are (the read-time guard covers them).

- Chosen because: when the university moves a class to the teacher's new time but another room, ending the change and telling the teacher follows the plan's "moved or dropped → dropped, teacher told" without a second kind of row surgery.

### 7.2 Where it runs

`ClassChanges.ReconcileAsync(db, semester, course, section, oldInstructor, newInstructor, oldMeetings, newMeetings, tell, now, ct)` applies `Decide` to the section's live changes, ends rows for Merge, MergeTell and Drop through `ChangeRules.EndAsync` (reasons `merged` and `dropped-moved`, which write no "back on" and no `undone`, 5.5), and refreshes snapshots. It does not write teacher notices itself: it adds what happened to `tell`, a list owned by the caller (the apply, or the section editor), and `EndSectionAsync` does the same. After the loop over sections the caller calls `ClassChanges.TellTeachersAsync(db, tell, now, ct)` once, which writes the notices of 7.4 without repeats. It returns whether what students see is unchanged (7.3). It runs inside the caller's transaction:

| Place | Call |
|---|---|
| `ScheduleImport.ApplyAsync`, a changed section | After `change` is found non-null and before `db.Meetings.RemoveRange(sec.Meetings)` (line 417). Sections with no difference are not touched. |
| `ScheduleImport.ApplyAsync`, `DropSection` and the `db.Courses.Remove(course)` branch | `ClassChanges.EndSectionAsync(db, section, "dropped-removed", now, ct)` before the section is removed: every live change is ended (upcoming ones with a notice), and accounts that added the section get a `section-removed` notice. The FK then nulls `SectionId` on `ClassChanges` and `TeacherSections`; the text copies keep the log and the "removed" list readable. |
| `ScheduleImport.ApplyAsync`, after `SaveChangesAsync`, before commit | `ClassChanges.AfterApplyAsync`: re-attaches `TeacherSection` rows whose `SectionId` is null to a section of this semester with the same `(CourseCode, SectionNumber)`; writes re-spelling hints (7.6). |
| `PUT /api/admin/sections/{id}` (`AdminEndpoints.cs:324-344`) | `BeginTransactionAsync` moves above the mutation; capture the old instructor and meetings, then call `ReconcileAsync`. A hand edit follows the same rules as a sync. |
| `POST …/days-off` (`DaysOff.cs:25-46`) and `PUT /api/admin/semesters/{id}/weeks` (`AdminEndpoints.cs:89-107`) | Both change `SemesterSpan`, so a **weekly** base can stop existing: on a date that is now off, or in a week that is no longer a teaching week. (Dated meetings keep their date, so they are not affected.) Each handler opens a transaction before its first read, as 5.2 does (neither has one today, so a change validated just before could otherwise be missed), saves, then calls `ClassChanges.EndWithoutBaseAsync(db, semester, reason, now, ct)`: it ends every live cancel, move and room change that has not started and now has no base (`dropped-day-off` for days off, `dropped-moved` for the weeks), and tells the teacher (7.4). Students: 7.3. Removing a day off or widening the weeks ends nothing. |
| Sync apply (`SyncEndpoints.cs:243-267`) | Goes through `ApplyAsync`; nothing extra. |

The whole block is skipped by one cheap check when the semester has no live `ClassChanges` and no `TeacherSections` and no `TeacherNames`. `ApplyAsync` keeps its signature.

### 7.3 "Merges quietly" also means no student alert

`ClassChanges.SameForStudents(oldMeetings, liveBefore, newMeetings, liveAfter)` compares what students see before and after: base meetings minus skipped dates, plus added sessions, as a set of `(date or weekday, start, end, RoomKey)`.

In `ApplyAsync` and the section editor, the line `if (markChanges) { sec.ChangedAt = now; sec.ChangeSummary = change; }` becomes `if (markChanges && !quiet)`, where `quiet` is true only when the section **had live changes**, the instructor is unchanged, and `SameForStudents` holds. So students are not told "{Code} changed" when the university adopts a teacher's move, or re-rooms a class the teacher cancelled or already re-roomed. For a section with no live change the behaviour is exactly today's.

Students get no extra notice from a drop made by an import or a section edit: the existing "{Code} changed" alert already covers the university change that caused it.

A day off or a teaching-weeks edit stamps no section, so nothing would tell students there. When `EndWithoutBaseAsync` ends an **announced move** (5.6), the class students were told about at the new time is gone, so it writes one `students` notice per section with the change's `Tag`, kind `dropped`: "{Code}: the class moved to Wed 3 Dec 11:30 is off" / "Tue 2 Dec is now a day off." (or "That week is no longer a teaching week."), `ExpiresAt` = the moved class's start. A dropped cancel or room change needs none: the class it was about does not meet on that date either way.

### 7.4 How the teacher is told

Teacher notices (in-app inbox plus push). The recipients of one event are a **distinct** set: the maker's account (`TeacherId`) and the accounts that may change one of the group's sections (the maker is normally one of them and is told once). Like every teacher notice (5.6) they are one per `(TeacherId, Kind, GroupId)`, the text listing the group's codes; `TellTeachersAsync` (7.2) builds them after the whole apply, so a merged class that the university moves gives its teacher one `dropped`, not one per code.

| Case | Kind | Text |
|---|---|---|
| Drop | `dropped` | "Your change to {Code} was dropped" / "The university timetable changed Tue 2 Dec 10:00. Your cancellation no longer applies; check the new time." |
| Section removed | `dropped` | Same title / "{Code} section {n} is no longer on the timetable." |
| Day off added | `dropped` | Same title / "Tue 2 Dec is now a day off." |
| Teaching weeks changed | `dropped` | Same title / "Tue 2 Dec is no longer in the teaching weeks." |
| KeepTellRoom | `room-differs` | "{Code}: the university lists another room" / "It now says B105 for Tue 2 Dec 10:00. Students still see your room, C201. Undo your change to use the university's room." |
| Move kept, the university newly lists the new time in another room or with another end | `room-differs` | "{Code}: the university lists it at your new time" / "It now says Wed 3 Dec 11:30 in B105. Students see that room." |
| MergeTell | `merged` | "{Code}: the university moved it too" / "It now lists Wed 3 Dec 11:30 in B105. Students see that." |
| Merge | none | Quiet. |

**University changes to a teacher's class in general** (plan section 7, last point). When `ReconcileAsync` runs for a section of the published semester that is being stamped as changed, it adds the section to `tell` for every account on it: linked to the old or the new instructor key, or added it, unless `MuteChanges`. Collecting at apply time means the teacher who was just replaced is still found. `TellTeachersAsync` then writes **one** `uni-changed` notice per teacher for the whole apply, covering only the sections for which that teacher got none of the other kinds in this apply (`dropped`, `room-differs`, `merged`, `reassigned`): one section reads "{Code} changed" / the same summary students get; several read "3 of your classes changed" / "BE1101 2, CS2101 1, CS2101-L 3". URL `/teach`, tag `tuni-{teacherId}`. So one university move of a merged class the teacher had changed is one `dropped` and no `uni-changed`.

- Chosen because: the outbox already exists and gives the teacher an inbox row as well as a push, where a second "changed since" window would give only the push.

### 7.5 Reassignment

When `NameKey(oldInstructor) != NameKey(newInstructor)` on a section (an import or the section editor):

- Changes are kept (the table in 7.1 never looks at the instructor).
- Accounts linked to the old name get `reassigned`: "{Code} section {n} is now listed under {new}." plus, with upcoming live changes, "Your {N} upcoming changes stay until someone undoes them."
- Accounts linked to the new name get `reassigned`: "{Code} section {n} is now under your name." plus "It has {N} upcoming changes made by {old}. You can undo them from the class."
- The new teacher may change the section (4.1), so they can undo; staff can from the log; the maker keeps the right to undo their own.
- One apply writes one `reassigned` notice per teacher per (old name, new name) pair, listing the sections ("BE1101 2 and CS2101 1 are now listed under {new}."), not one per section.

A re-spelling that leaves `NameKey` equal (case, spacing, punctuation, accents) is no reassignment. `DescribeChange` is not edited.

### 7.6 Name re-spelling

Never automatic. In `AfterApplyAsync`, for each `auto` or `approved` link whose `NameKey` is on no section of the semester any more: when every section that carried it before this apply now carries one single other name, that name is not a placeholder and has no live link, set `TeacherName.SuggestedName` to it. A name that is back on the timetable clears the hint.

The dashboard shows "Raed Bolbol → Raed Bolbul. Same person?"; one tap sets `Name` and `NameKey` to the suggestion and clears it. The account, office hours and added sections never depended on the spelling.

### 7.7 What the apply preview shows

`ImportPreview` gets a trailing optional member `List<TeacherChangeEffect>? TeacherChanges = null`:

```csharp
public record TeacherChangeEffect(string Effect, string Code, string Section, string What, string By);
// Effect: "dropped" | "merged" | "room-differs".  What: "Cancelled Tue 2 Dec 10:00".
```

`PreviewAsync` fills it by running `Decide` without saving, for updated sections, removed sections and removed courses. The upload preview and `GET /api/admin/semesters/{id}/sync` return it through the existing `preview` object. The two apply answers add `teacherChangesDropped` (a count, additive). It is information only: apply decides again inside its own transaction.

---

## 8. Office hours, profile, pages

### 8.1 Teacher side

- `GET /api/teach/office` → `{ name, email, officeRoom, note, showEmail, hours: [{ id, day, start, end, room }] }`.
- `PUT /api/teach/office` `{ officeRoom, note, showEmail, hours: [{ day, start, end, room }] }` replaces the list. Limits: 10 rows, `end > start`, room ≤ 60, office room ≤ 60, note ≤ 200, plain text. A frozen account gets 403 with the freeze message (5.9).
- In "view as" both act on the viewed account; 409 for a bare name. A "view as" save never turns `ShowEmail` on (409 "Only the teacher can show their email." when the body asks for it) and writes a `staff-edit` notice to the teacher (2.4).

Office hours show on the teacher's own timetable (`officeHours` in `/teach/timetable`) and feed. They skip days off and the weeks outside teaching. A single week can't be cancelled; the note can say "away this week".

### 8.2 On every class students see

Each class in `/me/timetable` and `/teach/timetable` carries `teachers`, built by `TeacherPages.RefsAsync(db, sections, ct)` in two queries (links by name key; added rows by section id):

```ts
export interface TeacherRef {
  name: string
  key: string                 // "n-…" or "t-…", for /teacher/{key}
  /** false for placeholder names: plain text, no page. */
  tappable: boolean
  /** true = named on the university timetable; false = a teacher who added this class. */
  listed: boolean
  officeRoom?: string; note?: string; email?: string | null
  officeHours?: { day: number; start: string; end: string; room: string }[]
}
```

- First the named instructor (left out when blank): `key = n-…`, `listed: true`, `tappable = !IsPlaceholder`, with the details of the account holding that name when there is one.
- Then each account that added the section: `key = t-…`, `listed: false`, shown by its shown name, straight away (plan section 2).
- `email` is present only when `ShowEmail` is on, and is always the sign-in address.

- Chosen because: with the teachers inside the timetable the class page needs no second request and works from the saved offline copy; the cost is two small indexed queries per load.

### 8.3 The teacher page

`GET /api/teachers/{key}` (`SignedInPolicy`, new file `Teachers/TeacherPages.cs`). `key` is `t-<teacherId>` or `n-<base64url of the UTF-8 name>`. A placeholder or unknown name → 404.

Teacher ids are sequential, so a `t-` key answers 404 unless students can already see that account somewhere: it holds an `auto` or `approved` name that is an instructor this semester, or it has a `TeacherSection` row with `Mode = added` and a non-null `SectionId` in the current semester. An account that only signed in (IT, the registrar, a teaching assistant with nothing added) has no page, so counting ids lists nobody and reveals no address. Two callers are exempt: the account itself (a teacher cookie with that id, or the student whose teaching profile it is: "See my page as students do") and owners, who see every account in the dashboard anyway.

```ts
interface TeacherPage {
  name: string
  hasAccount: boolean
  officeRoom: string; note: string
  email: string | null
  officeHours: { day: number; start: string; end: string; room: string }[]
  /** This Oman week, Sunday to Saturday, teachers' changes applied, merged classes as one row. */
  week: { from: string; to: string }
  classes: { codes: string[]; name: string; section: string; date: string; start: string; end: string; room: string;
             type: string; status: null | 'cancelled' | 'moved' | 'room' | 'extra'; note: string | null }[]
}
```

- For an account: the sections on its timetable (linked names + added − hidden).
- For a name with no account: the sections with that `NameKey`. A teacher who never signed in still has a page with their classes this week.
- Neither endpoint returns anything about students. No teacher endpoint reads `ClassNotes`, `Absences` or pick rows beyond counting.

---

## 9. Dashboard

New file `Admin/TeacherAdminEndpoints.cs` (`MapTeacherAdmin`), mapped next to `MapFeedback()`.

**Owners and RSA** (`StaffPolicy`):

| Endpoint | Purpose |
|---|---|
| `GET /api/admin/teachers` | Everything the page needs (below). |
| `POST /api/admin/teachers/links/{linkId}/approve` | `pending` → `approved` (`Conflict` cleared). When another account holds the name as `auto` or `approved`: 409 naming who, so staff reject or detach that one first. `name-approved` notice. |
| `POST /api/admin/teachers/links/{linkId}/reject` | → `rejected`; `name-rejected` notice. Also the one-tap reverse of an automatic link. |
| `POST /api/admin/teachers/links/{linkId}/same-person` | Accepts `SuggestedName` (7.6). |
| `POST /api/admin/teachers/{id}/names` `{ name }` | Attach another name: `approved`, `How = staff`. 409 when another account holds it. |
| `DELETE /api/admin/teachers/{id}/names/{linkId}` | Detach. |
| `PUT /api/admin/teachers/{id}/frozen` `{ frozen }` | Freeze or unfreeze; stamps `FrozenAt`, `FrozenBy`. Stops class changes, adding sections and saving office hours and details (5.9). |
| `POST /api/admin/teachers/{id}/sign-out` | Raises `SessionVersion`: every sign-in of that staff account ends on its next request (2.3). The account, its names, sections and office hours stay. 409 for a teaching assistant (their sign-in is the student one). |
| `POST /api/admin/teachers/{id}/remove` `{ undoChanges }` | Remove and block (below). Answers `{ undone }`. |
| `POST /api/admin/teachers/unblock` `{ email }` | Deletes the `BlockedEmail`. |
| `GET /api/admin/teacher-changes?semesterId=&teacherId=&flagged=&live=` | The log, newest first, 200 rows. |
| `POST /api/admin/teacher-changes/{groupId}/undo` | Undo any live change while `ChangeRules.CanUndo` allows it (5.4) (`EndReason = undone-by-staff`). Students are told "back on" by the 5.6 rule; the maker gets an `undone` notice ("Classy staff undid your change to …"). Not subject to freeze, the switch or the fresh-code rule. |

**Owners only** (`OwnerPolicy`):

| Endpoint | Purpose |
|---|---|
| `PUT /api/admin/teachers/switch` `{ enabled }` | Writes `teachers.changes.enabled`. |
| `GET/PUT/DELETE /api/admin/students/{id}/teaching` | `PUT { displayName }` creates (or renames) the `Teacher` row with `StudentId`, `CreatedBy`; `DELETE` removes it (with the same `undoChanges` offer, as a query flag); `GET` → `{ teaching: { teacherId, displayName } | null }`. Timetable names are attached on the Teachers page as for anyone. |

`GET /api/admin/teachers`:

```jsonc
{ "changesEnabled": true,
  "waiting": [ { "linkId": 7, "teacherId": 12, "email": "…", "name": "Ahmad Adeel", "sections": 4, "conflict": false, "heldBy": null } ],
  "teachers": [ { "id": 12, "email": "…", "name": "…", "kind": "staff", "frozen": false, "lastSeenAt": "…", "createdAt": "…",
      "names": [ { "linkId": 3, "name": "…", "state": "auto", "how": "email", "conflict": false, "sections": 5, "suggestedName": null } ],
      "added": [ { "code": "BE1101", "number": "2", "named": "Mariam Ebeid", "by": null } ],   // who added themselves to whose class; by = the owner for a "view as" add
      "hidden": 0, "changesToday": 1, "upcomingChanges": 3, "key": "t-12" } ],
  "names": [ { "name": "Raed Bolbol", "sections": 9, "placeholder": false, "teacherId": 12, "key": "n-…" } ],   // every distinct instructor this semester
  "blocked": [ { "email": "…", "blockedAt": "…", "by": "…" } ] }
```

Log row: `{ group, kind, codes, section, from, to, note, by (ByName), byLabel, onBehalfOf, namedTeacher, flagged, at, state, endedBy, endedAt, students (a count), canUndo }`. `state` is `live` or the `EndReason`.

**RSA privacy.** These payloads hold staff emails, teacher names and counts only. `ShownActor` alone is not enough for the log: it turns anything shaped like a student ID into "an owner", which would mislabel a teaching assistant's change. So `byLabel` and `endedBy` in a log row are worked out from the kind of actor (`TeacherAdminEndpoints.ShownBy`):

| Viewer | `ByKind` | Shown |
|---|---|---|
| Owner | any | `ByLabel` as stored |
| RSA | `teacher`, `admin` | `ByLabel` (a staff email or a dashboard username) |
| RSA | `student`, `OnBehalfOf` null | "a teaching assistant" (the teacher's name is in `by`) |
| RSA | `student`, `OnBehalfOf` set | "an owner" (a student owner in "view as") |

`endedBy` is stored with who it was (1.1): RSA see the email for `teacher:`, "a teaching assistant" for `assistant:`, `ShownActor(label)` for `staff:` (a username, or "an owner" for a student owner), and "the university timetable" for `system`. Other "by" fields (`FrozenBy`, `BlockedBy`, `DecidedBy`, `CreatedBy`, `TeacherSection.By`, `TeachNotice.ByLabel`) can only hold an owner's or RSA's name and pass through `ShownActor`. For a teaching assistant's row, `universityId` is included for owners only.

**"Upcoming"** means a live change that 5.4 still allows to be undone at that moment: `ChangeRules.CanUndo(change, now)`. `upcomingChanges`, the remove dialog's N, and the rows ended on removal and on `DELETE …/teaching` all use it. So a class moved from Thursday to Tuesday and already held on Tuesday is not "put back" on Thursday when its teacher is removed.

**Remove a teacher.** One transaction: when `undoChanges` is ticked, end every upcoming live change they made as `undone-on-remove` (students told "back on" by the 5.6 rule); insert `BlockedEmails(Email)` for a staff account and delete that address's `LoginCodes` rows; delete the `Teacher` row (names, sections, seen, slots, devices, office hours and their notices cascade; `ClassChanges.TeacherId` becomes null and the log keeps `ByName` and `ByLabel`). Their cookie is rejected on the next request. For a teaching assistant there is no email to block: removal just deletes the teaching profile.

**View as.** No endpoint. The Teachers page stores the teacher key in the tab and opens `/teach`; the server side is `TeachActor` (2.4).

**Dashboard home** (`GET /api/admin/stats`, additive inside `attention`): `teachersWaiting` (count of `pending` links), `busyTeachers: [{ id, name, changes }]` (more than 5 change groups since Oman midnight, as a UTC instant) and `teacherSignInPaused` (the time the new-address pause of 2.1 ends, read from `TeacherCodeLimiter`, or null).

**The "more than 5 changes" alert for staff** (plan section 8: owners and RSA are alerted). Student owners with a device get the push (5.5). RSA and dashboard owners have no devices, so their alert is in the dashboard: the `busyTeachers` row on the home, and a count badge on the sidebar's "Teachers" link = `teachersWaiting + busyTeachers.length`, which `AdminLayout` reads from `/admin/stats` when it loads and when the tab becomes visible again. An RSA on any dashboard page sees it.

**Course editor.** `GET /api/admin/courses/{id}` adds `teacherChanges`: the live changes of its sections (`{ sectionId, kind, date, start, what, by }`), so the editor can warn that saving another day or time drops them.

**Delete all data.** Section 1.2.

**Analytics.** Audience `teacher` in `UsageAnalytics.Record`. `Analytics.tsx` `feature()` puts `/api/teach` and routes starting `/api/teach/` in a "Teacher tools" row before its other checks (not `/api/teachers/…`, the page students open). Daily active and peak numbers stay student-only.

---

## 10. Web

New folder `web/src/teacher/`. No Capacitor or native work.

### 10.1 Routes and guards (`main.tsx`)

| Route | Element | Who |
|---|---|---|
| `/login` | `Login`; a signed-in teacher goes to `/teach` | |
| `/` | `StudentOnly → Lab`; a teacher goes to `/teach` with the same query string, so the installed app's start URL and "This week" shortcut work | student |
| `/teach` | `TeachGate` (sign-in, the name step, or `TeacherScope → Lab`) | anyone |
| `/teach/name` | `TeacherScope → PickName` | staff account (`teach.kind === 'staff'`); anyone else on the teacher side goes to `/teach`, and nothing ever sends them here (10.5) |
| `/teach/classes` | `TeacherScope → EditClasses` | teacher side |
| `/teach/office` | `TeacherScope → OfficeHours` | teacher side |
| `/teach/changes` | `TeacherScope → MyChanges` | teacher side |
| `/teach/rooms`, `/teach/colours` | `TeacherScope →` the existing `FreeRooms`, `Colours` | teacher side |
| `/teacher/:key` | `SignedIn → TeacherPage` | anyone signed in |
| `/admin/teachers`, `/admin/teacher-changes` | inside `StaffOnly` (owners and RSA) | staff |

Guards:

- **`TeachGate`** waits for the session. The teacher side is allowed when `session.kind === 'teacher'`, or `session.teaching` (a teaching assistant), or `session.role === 'owner'` with a stored "view as". An owner without one goes to `/admin/teachers`. Signed out → `TeachLogin`. Anyone else signed in → `TeachLogin` with "You're signed in as 24-0161. Sign out to sign in as a teacher." and a button that runs `logout()`, so that person's device is released first.
- **`TeacherScope`** is the same check without the sign-in screen (it redirects to `/teach`), and provides the teacher `Viewer` (10.3).
- **`StudentOnly`**: `kind === 'teacher'` → `<Navigate replace />` to the teacher's version of the page: `/` → `/teach` plus the query string; `/rooms` → `/teach/rooms` (the installed app's "Free rooms" shortcut, `manifest.json:42`); `/colours` → `/teach/colours`; everything else → `/teach`.
- **`StaffOnly`**: a teacher → `/teach`.
- **`SignedIn`** (new, small): any session with a kind (student, teacher, or a dashboard account: an owner in "view as" follows teacher-name links and "See my page as students do", and the API allows any signed-in user). Only signed out goes to `/login`.
- `Login.tsx`, `AdminLogin.tsx`, `Planner.tsx`, `NotFound.tsx`: a teacher goes to `/teach` instead of a student or dashboard screen. `Join.tsx`: a teacher sees "Groups are for students" with a link to `/teach`; the invite is not stored.
- Logout from the teacher side navigates to `/teach`.
- There is no "this device was a teacher's" memory: after a sign-in runs out, a teacher uses the chooser on the student screen.

- Chosen because: the plan makes the "Staff? Sign in here" link the only teacher hint students see; a remembered teacher device would show the teacher sign-in to the next student on a shared computer. The cookie slides for 60 days, so a lapse is rare.

### 10.2 Sign-in screens

- **The chooser.** `Login.tsx`'s "Staff? Sign in here" link opens a `Sheet` with two buttons: "Teacher" → `/teach` and "Dashboard" → `/admin/login`. That is the only teacher entry on the student screen.
- **`Login.tsx`** exports `Screen`, `Decor`, `CodeStep` and `waitSeconds`. `CodeStep` gets four optional props whose defaults are today's behaviour:

  ```ts
  verify?: (code: string) => Promise<{ isNew: boolean }>   // default: POST /auth/verify { universityId: id, code, endpoint }
  resend?: () => Promise<CodeSent>                          // default: POST /auth/request-code { universityId: id }
  backLabel?: string                                         // default: 'Back to university ID'
  welcome?: boolean                                          // default: true (the student "Let's set you up" screen)
  ```

  Both sign-ins send `endpoint` (`(await deviceSubscription())?.endpoint`) with verify.
- **`teacher/TeachLogin.tsx`**: the same `Screen` and `Decor`. One field "GUtech staff email" (`type="email"`, `autoComplete="username"`, placeholder `firstname.lastname@gutech.edu.om`); the client check mirrors `Normalize`. "Send me a code" → `POST /auth/teacher/request-code`; the answer becomes `{ ...r, testCode: null }`, so the test-mode line never renders. Then `CodeStep` with the teacher calls, `backLabel="Back to email"`, `welcome={false}`. Under the form: "Check your junk folder" and "Student? Sign in here" → `/login`. After verify: `await refresh()`, then `/teach/name` when `status === 'pick-name'`, else `/teach`.
- **`teacher/PickName.tsx`**: a search box over `GET /teach/names`, rows "Raed Bolbol · 5 classes". Tap → confirm → `POST /teach/names { name }` → home with the waiting banner. "My name isn't here" → `POST /teach/names { none: true }` → home, empty, with "Add the classes you teach".
- **`teacher/FreshCode.tsx`**: `useFreshCode()` returns `run(fn)`. When `session.codeFresh` is false (and the session is not a dashboard account) it first opens a full-screen step: "Your last sign-in code is more than a week old. We'll email a new one to r…@gutech.edu.om", requests a code with the endpoints of the signed-in kind, shows `CodeStep`, then `await refresh()`. It then calls `fn`; on a 403 whose body has `needsCode` it opens the step and calls `fn` once more.

### 10.3 The viewer and the data store

`lib/viewer.tsx` (new):

```ts
export interface Viewer {
  mode: 'student' | 'teacher'
  base: '/me' | '/teach'
  home: '/' | '/teach'
  /** The teacher key being viewed as, in this tab. */
  as: string | null
  /** The data hook of this side. */
  useHome: () => HomeData
}
export const useViewer = () => useContext(Ctx)          // default: the student viewer
export const useHome = () => useViewer().useHome()
export function TeacherScope({ children }: { children: ReactNode })
```

`lib/viewAs.ts` (new, **no imports**):

```ts
export const viewAs = { get(): string | null, name(): string | null, set(key: string, name: string): void, clear(): void }   // sessionStorage 'teach-as', 'teach-as-name'
```

Two rules keep module loading safe. `lib/api.ts` imports `viewAs` from `lib/viewAs.ts` only, never from `lib/viewer.tsx`: `viewer.tsx` needs `student/timetable.tsx`, which imports `lib/api`, and `main.tsx` loads `timetable.tsx` first (through `Colours`), so `api → viewer → timetable` would be a cycle. And the default viewer's hook is lazy, `useHome: () => useTimetable()`, not a reference read while the module loads: once `useTimetable` is a `const` built by `createHomeStore`, reading it early would throw "Cannot access before initialization" for every user, and neither eslint nor the build would catch it.

`lib/homeStore.ts` (new): the store that is `student/timetable.tsx` lines 12–129 today (module cache, `share`, saved copy, `load` with the generation counter, the hook), turned into a factory:

```ts
export interface HomeData {
  me: Me | null; teach: TeachMe | null; weeks?: Weeks
  tt: Timetable | null; sessions: Session[]; ghosts: Session[]
  error: string; markSeen: (sectionId: number) => void; offline: Date | null; retry: () => Promise<boolean>
}
export function createHomeStore(cfg: {
  savedKey: string                        // 'timetable' | 'timetable:teach'
  fetch: () => Promise<{ me: Me | null; teach: TeachMe | null; tt: Timetable | null }>
  seenUrl: string                         // '/me/seen' | '/teach/seen'
  build: (tt: Timetable) => Session[]
  canSave: () => boolean                  // false in "view as"
}): { useData: () => HomeData; clear: () => void; reset: () => void }
```

- `student/timetable.tsx` keeps its exports (`useTimetable`, `clearTimetableCache`, `resetTimetable`, `useNow`, `useBack`, the buttons, `NeedsSection`), now built on the factory with today's fetch (`/me`, then `/me/timetable` when setup is done).
- `teacher/teachData.ts` is a second instance: `/teach/me`, then `/teach/timetable`; `build = buildTeachSessions`; exports `useTeachTimetable`, `clearTeachCache`, `resetTeach`.
- Two instances share no memory, so a teaching assistant who uses both homes in one sitting cannot see one leak into the other. The teacher instance is reset whenever `viewAs` changes.
- Both: refetch on `visibilitychange` when the tab becomes visible and the data is older than 60 s, so a Cancelled mark shows without a reload.

- Chosen because: one store implementation with two isolated instances avoids both a copy that drifts and a shared cache that could mix a student's and a teacher's data.

`lib/api.ts`: in `request()`, when `url === '/teach' || url.startsWith('/teach/')` and `viewAs.get()` is set, add header `X-Teach-As` with that key. `/teachers/{key}` (the page students open) must not match. Nothing else changes.

### 10.4 Types and session building (`lib/types.ts`, `lib/slots.ts`)

`types.ts`, all optional on existing interfaces:

- `SessionInfo.kind` adds `'teacher'`; `role` adds `'teacher'`; `teaching?: boolean`; `codeFresh?: boolean`; `teacher?: { id: number; displayName: string }`.
- `Meeting.changeId?: number`, `Meeting.type?: 'lecture' | 'lab' | 'tutorial'`, `Meeting.merge?: string`.
- `TimetableClass.students?: number`, `own?: boolean`, `added?: boolean`, `teachers?: TeacherRef[]`.
- `Timetable.changes?: ClassChange[]`, `officeHours?: OfficeHour[]`, `removed?: { code: string; number: string }[]`.
- New: `ClassChange`, `TeacherRef`, `OfficeHour`, `TeachMe`, `TeachNotice`, `ChangeBody`, `ChangePreview`, `TeacherPage`.

`slots.ts`:

- `ClassType` adds `'office'` (with `TYPE_NAME` "Office hours" and a `TYPE_BG` entry).
- `Session` adds: `change?: ClassChange` (the change that put this session on); `off?: 'cancelled' | 'moved'` (a ghost: drawn, not happening); `merged?: Session[]` (teacher side: the sessions that are one real class); `office?: OfficeHour`.
- `buildSessions(tt)`: `type = meeting.type ?? sessionType(meeting.room)`; `change` = the `tt.changes` row whose `id` is `meeting.changeId`. Nothing else changes: a moved, re-roomed or extra class is already an ordinary dated meeting.
- The clash pass moves into an exported `markClashes(sessions, same)`, with `same` defaulting to "same `courseId`" (student behaviour unchanged). It first sets `clash = false` and `clashDates = []` on every session, so it can be run again. `buildSessions(tt, opts?: { clashes?: boolean })` runs it unless `clashes` is `false` (default `true`: the student call is unchanged).
- New `buildGhosts(tt)`: one dated, display-only `Session` per cancel and per move original (`from`), with `off` and `change`, `type = change.type`, `cls` = the class of `change.sectionId`.
- New `changeLine(s)`: "Cancelled by Raed Bolbol", "Moved to Wed 11:30", "Moved from Tue 10:00 by …", "Room changed: A101 → B202", "Extra class".
- `teacher/teachSessions.ts`, `buildTeachSessions(tt)`, in this order:
  1. `buildSessions(tt, { clashes: false })`. The student clash pass must not run here: it would mark BE1101, IBSM1101 and LOG1101 at one time as clashing with each other (different courses) before they are collapsed, and the mark would stay on every merged class.
  2. Add office hours as weekly sessions (`type: 'office'`, skipping `excludedDates`). Their `cls` is a stand-in like `slotClass()` with a negative id, so nothing mistakes one for a class.
  3. Collapse sessions that share a `meeting.merge` key into one block, on the key alone (for a university key only between different courses; for a `g:` key every section of the group, 4.2). The block is the lead session (first by course code, ordinal) with `merged` = all members, each keeping its own `excludedDates`. The block's own `excludedDates` is the **intersection** of the members': it is hidden on a date only when no member is on. `blockOn(s, date)` returns the members for which `onDate` is true; the code line ("BE1101 · IBSM1101 · LOG1101") and the size shown for a date come from them: `tt.mergeSizes[key]` when all members are on, else the sum of the `students` of those that are. So unticking one code from one week's change leaves a weekly block whole in every other week, and on that date the block shows the codes still on, next to the ghost (or the moved session) of the changed ones.
  4. `markClashes` once, with "same section, or the same merge key, or either is office hours", then sort again.

  Ghosts collapse on `change.group`.

Ghosts are returned **separately** from `sessions` on purpose. Everything that asks "what is really on" (live and next cards, "That's it", day counts, attendance totals, the share picture, clashes) keeps reading `sessions` and needs no edit. Only the day list, the week grid, the course page and the session page also read `ghosts`. `lib/absences.ts` adds `!s.office` to `counts`; `lib/shareImage.ts` only gets a colour for `'office'`.

Session addresses (`?session=`): students keep `<courseId>.<date>.<start>`; a ghost is `g<changeId>.<date>.<start>`; the teacher side uses `s<sectionId>.<date>.<start>`; planner events stay `p<id>…`. Office hours have no address: they open `/teach/office` (10.5).

The resolver in `Lab.tsx` also looks in `ghosts`, so an old reminder link to a class that was moved opens its greyed "Moved to…" page. For a course or section address it tries, in this order:

1. a session of that course (or section) on the date with the **exact** start;
2. a ghost with that course (or section), date and start;
3. only then, the first session of it that day (today's fallback).

So a "cancelled" link for the 08:00 lecture opens the greyed lecture, not the 13:15 lab of the same course that is still on. An `s` address matches a block when the id is its own section's **or any `merged` member's** (and likewise for ghosts collapsed by group), so a notice about IBSM1101 opens the block led by BE1101.

### 10.5 Reusing the student home (no fork)

| File | Change |
|---|---|
| `student/Lab.tsx` | `useTimetable()` → `useHome()`; `weeks` from the hook (lines 360, 745). `sessionLink` → `${viewer.home}?…` with the address forms of 10.4; `open` parses `s` and `g` prefixes and looks in `ghosts`. `WeekPage` also draws the week's ghosts: 55% opacity on the day-off hatch, code struck through, a 9px label "Cancelled" / "Moved", no live ring, no clash outline, the words in `aria-label`; "Moved", "Room changed" and "Extra" sessions get a ringed dot where the "changed" dot sits. The empty-cell test ignores ghosts. Menu: `viewer.mode === 'teacher' ? <TeachMenu/> : <MenuSheet/>`. In teacher mode `nav.add` and the empty-cell "+" open a two-button sheet ("My own slot" / "Extra class"); in "view as" (`teach.personal` false) there is no chooser and both open the extra-class form directly. `WEEKS` also includes `weekOf(date)` for every dated session and ghost outside week 1 to `lastWeek` (a make-up class on 28 Dec gets its week and its day page; the plan warns about such a date but never blocks it). The day list (`Lab.tsx:374`) and `WeekPage`'s columns call `weekDays` on that week's sessions **plus its ghosts**, so a cancelled Friday class still has a column and a day to be greyed in. An `office` session opens `/teach/office` from the week grid, never a session page; `'office'` gets an entry in `BLOCK`. Passes `viewer`, `teach`, `offline` to `SessionPage`. |
| `student/labNav.ts` | No shape change (`add` is already `(date) => void`). |
| `student/Today.tsx` | `useTimetable()` → `useHome()`. Student-only behind `viewer.mode === 'student'`: the `/setup` redirect, `RatePrompt`, `NeedsSection`. Teacher: `teach.kind === 'staff' && teach.status === 'pick-name'` → `<Navigate to="/teach/name">` (never for a teaching assistant or in "view as": `/teach/name` would send them straight back). `useClassLink` sends an `office` session to `/teach/office`, as `mine` opens the slot sheet; `<TeachBanner/>` in the `NeedsSection` spot; `NoClasses` reads "No classes yet. Add the classes you teach." → `/teach/classes`. `List` takes the day's ghosts and draws them in time order, greyed, time struck through, with `changeLine`. `Tags` adds "Moved", "Room changed", "Extra class". A day with only ghosts shows "Nothing on. 1 class cancelled." above the greyed rows. Headline logic keeps reading `sessions`. |
| `student/Session.tsx` | New props `viewer`, `teach`, `offline`. A cream banner above the navy panel, in the style of the "Changed" box: who, what, the note, and for a move a link to the other half. A ghost's page shows the banner and no attendance. Lecturer block → `<ClassTeachers cls={cls}/>` (falls back to the instructor text when `teachers` is missing). Student mode only: `Attendance`, `ClassNotes`. Teacher mode only: `<SessionActions/>`. `RemindButton` in both, passing the viewer's base to `useNotifySettings(base)` and `enableDevice(base)`; it is not rendered (and the settings hook is not mounted) when `teach.personal` is false, where those routes answer 409. `nextAfter` matches by `section.id` in teacher mode. |
| `student/Course.tsx` | Rows also list ghosts with their labels; `next` and `live` read `sessions` only; lecturer block → `<ClassTeachers/>`. Stays student-only (teachers stay on session pages). |
| `student/SlotSheet.tsx` | URLs from `useViewer().base`; `export { TimeButton, Clock }`. |
| `student/CalendarSheet.tsx`, `NotificationsSheet.tsx`, `lib/push.ts` | Base from the viewer, on every call that names `/me` today: `deviceSubscribed(base)` (it posts to `/me/push`, a 403 for a teacher cookie, which would show the device as off), `enableDevice(base)`, `disableDevice(base)`, `useNotifySettings(base)` (one shared value per base) and the test call in `NotificationsSheet.tsx:67` (`${base}/push/test`). The default base is `'/me'`, so student callers compile unchanged. Teacher wording: "Your classes, office hours and slots"; the mute switch reads "When the university timetable changes one of my classes", with small print "You are always told when someone else changes your class." (the switch does not cover those, point 13 of the settled list). |
| `student/FreeRooms.tsx`, `Colours.tsx` | `useBack(useViewer().home)`. |
| `student/StudentShell.tsx` | When `session.teaching`: a "Teaching" item (Tag "Teacher") → `/teach`. |

`teacher/TeachBanner.tsx`, in this order: the "view as" bar ("Viewing as Raed Bolbol. Changes are logged under you. Exit"); "Waiting for staff to confirm you are Raed Bolbol. You can add your classes meanwhile."; frozen, or changes switched off; `tt.removed` ("The university removed CS1102-L section 3."); `teach.notices` as a short list with "Got it" (`POST /teach/notices/seen`); in "view as" the list is shown without "Got it", so an owner looking around never clears the teacher's inbox. A name approval or rejection arrives as a notice, so a waiting teacher sees the result the next time they open Classy.

`teacher/TeachMenu.tsx`: name and email; Free rooms; Edit my classes; Office hours & details; My changes; Calendar and Notifications (hidden in "view as"); Pick my name (only when `teach.kind === 'staff'`, with status `pick-name`, `none` or `pending`); "Sign out my other devices" (`teach.kind === 'staff'` only, a `Confirm`, then `POST /teach/sign-out-everywhere`); "My student timetable" for a teaching assistant; "Dashboard" for an owner; Colours; Log out (→ `/teach`).

### 10.6 Edit my classes, office hours, my changes

- **`teacher/EditClasses.tsx`**: "On my timetable" (own and added) with a tick each. Unticking an own one hides it ("Not mine", for me only); unticking an added one removes it; both with the existing dropped-row Undo. Last semester's as "You added these last semester · Add again". A search box (code, name or teacher) over `GET /teach/sections?q=`; each section row shows number, teacher and times with a checkbox (multi-select, not the student radio chips). Each tick is one `PUT /teach/sections/{id} { on }` followed by `clearTeachCache()`. A note under the search: "You can change any class you add. The teacher it is listed under is told."
- **`teacher/OfficeHours.tsx`**: weekly rows (day, two `TimeButton`s with `Clock`, room), office room, a 200-character note, "Show my email to students" (off by default). Save is `PUT /teach/office`. A link "See my page as students do" → `/teacher/t-{id}`.
- **`teacher/MyChanges.tsx`**: the list from `GET /teach/changes`, upcoming first, who made each one, Undo where `canUndo`.

### 10.7 The session sheet and the change forms

`teacher/SessionActions.tsx`, inside `SessionPage`'s navy panel:

- "23 Classy users in this class", small print "Not the official enrollment." For a merged block, one line per code ("BE1101 12 · IBSM1101 8 · LOG1101 3").
- "Listed under Mariam Ebeid. They'll be told about any change." when the class isn't under the teacher's name.
- Buttons by state:

  | Session | Buttons |
  |---|---|
  | normal, upcoming | Cancel class · Move · Change room · Add extra class |
  | cancelled (ghost) | Undo, class is back on · Move instead |
  | moved (either half) | Undo move · Move again · Change room · Cancel class |
  | room changed | Undo · Change room · Cancel class · Move |
  | extra | Remove extra class · Edit |

  On a moved class, "Change room" is a replace that stays a move at the same new time with another room, and "Cancel class" is a replace with kind `cancel` (5.4). "Add extra class" opens the extra form with this block's codes ticked.
- Each button follows its own limit: the Undo buttons need `canUndo`, the others `canEdit` (6.2). After a moved class's original time has passed, "Undo move" is disabled with "The original time has passed" while Move again, Change room and Cancel class still work until the new start.
- All are disabled with one reason line when the class has started, offline (the home is showing a saved copy), frozen, or changes switched off.

`teacher/ChangeSheet.tsx`: one `Sheet`, one screen per kind, a real `<form>` (Enter submits, Esc closes).

| Part | Behaviour |
|---|---|
| Codes (cancel, move, room) | For a merged block: a checkbox per code from the preview's `codes`, all ticked; at least one stays. A code with `changedBy` is listed greyed with "already changed" and no checkbox. |
| For which classes (extra) | An extra class has no session to take its codes from, so the form lists every class on the teacher's timetable from `tt.classes` (a merged block as one row with its codes), with checkboxes. Opened from `nav.add` or an empty cell: none ticked, and submit is disabled until one is. Opened from a session's "Add extra class": that block's codes ticked. The ticked ids are the `sectionIds` sent to preview and create. |
| Other sessions | When the preview's `others` is not empty: "Also the 13:15 session that day", or "Also the other 2 sessions that day". |
| When (move, extra) | `<input type="date">`; the six class slots (`SLOTS`) as one-tap chips; "Other time" opens start and end `TimeButton` + `Clock`. |
| Room (move, room, extra) | "Free then" from `GET /teach/rooms`; the current room first as "A101 (current)"; "Type a room"; a "To be announced" chip. |
| Type (extra) | Lecture / Lab / Tutorial. |
| Note | Optional, 200 characters: "Students see this on the class and in the notification." |
| Warnings | `POST /teach/changes/preview`, debounced 400 ms after any input, shown as `Notice tone="warn"`. They never disable the button. |
| Submit | `useFreshCode().run(() => POST /teach/changes or PUT /teach/changes/{group})` → toast "Cancelled. Students with alerts on were told." with Undo → `retry()`. |

### 10.8 Student-side pages

- `student/ClassTeachers.tsx` (used by `Session.tsx` and `Course.tsx`): each `TeacherRef` as a link to `/teacher/{key}` when `tappable`, with office hours, office room, note and a `mailto:` link underneath. Placeholder names are plain text.
- `student/TeacherPage.tsx` (`/teacher/:key`): name; "Uses Classy" or "Hasn't joined Classy yet"; office hours ("Not on holidays or outside teaching weeks."); office room; note; email when present; "This week" rows (day, time, room, codes, status pill).
- An old server that sends no `changes` or `teachers` draws exactly as today.

### 10.9 One device, two people: offline copies and the service worker

Nothing may pass between a student and a teacher on one device.

1. **`sw.js` is unchanged.** It never caches `/api/` (line 98), Cache Storage holds only the shell, and it shows whatever push arrives. `/teach` is served the same shell. No cache-name bump. `manifest.json` is unchanged; `/` sends a teacher to `/teach`.
2. **Saved copies carry an owner.** `timetable` and `timetable:teach` are stored as `{ who, me | teach, tt, savedAt }` with `who = kind + ':' + name` of the session. A copy is used offline only when `who` equals the saved session's. A copy with no `who` (saved before this update) counts only for a student session, as today.
3. **`session.tsx` `refresh()`**: wipe when a stored `who` differs from the answered identity, or when there is no stored `who` and the session is **not** a student. Wiping removes `timetable` and `timetable:teach` and calls `resetTimetable()`, `resetTeach()`, `resetAbsences()`, `resetNotifySettings()`, `viewAs.clear()`. Then `store.set('who', who)`. When there is no stored `who` and the session is a student, it only sets `who`: that is every student's first load of this build, and their saved copy from before it (item 2) must survive.
4. **`logout()`** also removes `timetable:teach` and `who`, resets the teacher store and clears `viewAs`.
5. **"View as" data is never written to `localStorage`** (`canSave` is false), lives in `sessionStorage` for one tab, and has no offline copy.
6. **Signing in at `/teach` over another sign-in** goes through `logout()` first (10.1), which releases that person's push device on the server.
7. **Devices follow the sign-in.** Both verify calls send the browser's push `endpoint` and the server drops it from the other person's rows (2.2); `POST /teach/push` and `POST /me/push` each delete the endpoint from the other table (5.8). Teacher notifications carry `/teach…` URLs.
8. Offline, the teacher home shows the saved timetable under the existing banner, with every change button disabled.

### 10.10 Dashboard pages

- **`admin/Teachers.tsx`** (`/admin/teachers`). `AdminLayout.tsx`'s shared block gets two links, "Teachers" and "Teacher changes" (the log must be reachable without waiting for an alert). "Teachers" carries the count badge of section 9 (waiting names plus busy teachers).
  - **Waiting for approval**: Approve / Reject, the conflict note ("Also claimed by …").
  - **Teachers**: names (with "Reverse" on automatic links and "Same person?" hints), attach a name (a `Select` over unlinked names), the sections they added (code, number and whose class it is, with "added by {owner}" for a "view as" add), frozen tag, last seen, "changes today" and "upcoming changes" (each a link to the log filtered by that teacher, `/admin/teacher-changes?teacher={id}`), Freeze, "Sign out everywhere" (staff accounts), Remove (a dialog offering "Also undo their N upcoming changes (students are told the classes are back on)"), and for owners "Open teacher view".
  - **Timetable names**: every name with its section count and linked account; for owners "View as" (`viewAs.set(key, name); navigate('/teach')`).
  - **Blocked emails**: Unblock.
  - **Switch**: owners only, "Teacher class changes: On / Off" with a `Confirm`.
- **`admin/TeacherChanges.tsx`** (`/admin/teacher-changes`): the log with a "Not the named teacher" flag, state, student count and Undo; scoped by the sidebar semester.
- `AdminHome.tsx`: "3 teachers waiting for approval", "Raed Bolbol made 6 class changes today" and "New teacher sign-ins are paused until 14:20 (many wrong codes)" rows (all three fields typed optional).
- `StudentDetail.tsx`: new `admin/TeachingAccess.tsx` panel under `BatchRepAccess` (name field, Grant / Revoke).
- `Upload.tsx` `CheckStep`: a tab "Teacher changes" when `preview.teacherChanges?.length`, rows "BE1101 2 · Cancelled Tue 2 Dec 10:00 by Raed Bolbol · will be dropped". `EduWaveSync.tsx` reuses `CheckStep`.
- `CourseDetail.tsx`: a notice on a section listing its live teacher changes and that saving another day or time drops them; after Save it refetches and toasts when the count fell.
- `DeleteAllData.tsx`: the copy adds "teacher accounts and their class changes (blocked emails stay blocked)".
- `Analytics.tsx`: the "Teacher tools" row.

---

## 11. Tests

All in `server/UniSchedule.Tests`, in the existing styles (`IClassFixture<ApiFactory>`, a `World`, or in-memory SQLite with `Migrate()`).

`ApiFactory` gains: a `CapturingEmailSender : IEmailSender` registered through `ConfigureTestServices` and exposed as `factory.Emails` with `CodeFor(address)` and `CannotSignIn(address)` (teacher codes are never in a response); `RateLimits:TeacherRequestCode = 1000`. The `TeacherCodeLimiter` limits keep their defaults, so the tests that exercise them use their own host and addresses.

New `TeacherTestKit.cs`: `SignInTeacher(factory, email)` (request, read the captured code, verify), `AddTeacher(email, params string[] names)`, `AddTeachingStudent(studentId, name)`, and `TeachWorld : IDisposable` (a `ClockedApi`-style host with the movable clock, the capturing sender and a fake push service; a semester from inline **dated** CSV; an owner, an RSA, two teachers, students with picks; `AssertNoOrphans()`).

| File | Cases |
|---|---|
| `TeacherNameMatchTests.cs` (pure, `[Theory]`, invented names) | Two-word; "First Al Last" in four email spellings; "Ouled"; three and four words without a particle; shared first name; shared surname; look-alike spellings that must not match; middle name only; initials; reversed; single word; a digit; titles, hyphen and umlaut. `IsPlaceholder` for the eight shapes and for a blank. `NameKey` ignores case, spacing, punctuation, accents. `Normalize`: exact domain only, student domain, look-alike host, comma, double dot. `DisplayName` (digits kept). Underscore forms, a hyphen between first and last name, and a surname cut inside a word (`first.sur.name`) do not match; a hyphen inside a word does. `Shown`: an unlinked account whose email name equals an instructor's or a held name is shown by its local part as typed. |
| `TeacherSignInTests.cs` | An emailed code signs in; the answer has no `testCode` property with test mode on; student and foreign domains refused; a blocked email gets the normal 200, a "can't sign in" email and no code, and verify answers as for any wrong code; a code asked for before a block does not sign in after it; `/auth/session` for a teacher; resend wait, browser binding and the daily limits with an email key; 3 tries per code for an email key (students keep 5); the per-IP request limit; `TeacherCodeLimiter`: 30 wrong codes from one IP block that IP's untrusted teacher verifies and leave student verifies alone, 40 wrong codes for addresses with no account pause new-address verifies only (an existing teacher still signs in) and write one `staff` notice, 30 wrong codes from a trusted browser end that address's trust even after the sign-in cookie is cleared; sign-out-everywhere and the staff sign-out end other cookies, keep this browser signed in and do not refresh `code-at`; a removed teacher is signed out at once; logout drops only the teacher's device; student `request-code` and `verify` answers unchanged; a student with teaching rights gets no `testCode`; `/teach` serves the app shell. |
| `TeacherIdentityTests.cs` | Teacher 1 and student 1 with different data: the teacher gets 403 on every `/api/me/*`, `/api/admin/*` and `/api/rep/*` route (enumerated from the endpoint data source) and passes `/api/rooms/free`; a plain student gets 403 on `/api/teach/*`; a student with teaching rights reaches `/api/teach` and still gets their own `/api/me`. `X-Teach-As` works for owners (student and dashboard), by account and by name, and is 403 for RSA, teachers and students; "view as" writes are stored under the owner with `OnBehalfOf`; personal routes answer 409, reads included; a "view as" add stores `TeacherSection.By` and a "view as" office save writes a `staff-edit` notice with the owner's label; `showEmail` cannot be turned on through `X-Teach-As`; `POST /teach/notices/seen` ignores another teacher's ids and answers 409 in "view as"; another teacher's slot id is a 404; `/teach/push/remove` removes only the caller's row. Fresh code: a change is refused with `needsCode` after 7 days and one second, works after a new code; an old cookie with no claim is stale; a dashboard owner is exempt; a student who signed in before being given teaching rights needs a new code for their first change. `ActorName` for a teacher. |
| `TeacherLinkTests.cs` | Auto-link on exactly one match; none on two matches or digits; placeholders not in the picker; pick → pending → approve / reject (a teacher notice is written); a matching email wins over a hand-picked holder and flags the conflict; an email or staff holder keeps the name and the newcomer waits; one account per name; staff attach a second name; a reversed auto link is not made again; "My name isn't here"; auto-link at `/teach/me` when the name appears later; `first_last@` and `first-last@` are not linked and go to the picker; a pending account whose email name is a real teacher's is shown to students by its local part. |
| `TeacherTimetableTests.cs` | Linked + added − hidden; several names; a pending teacher sees added sections only; add shows at once; the same keys as `/me/timetable`; no `notes` key anywhere in the payload; exact `students`; a three-code class has one merge key and counts as siblings; two sections of one course are not merged; a non-room is not merged; a new semester starts with no added sections and offers `previous`; own slots CRUD; office hours in the payload skipping days off; `seen` clears `changed`; rep events only with the planner on; a teaching assistant with no names gets status `none` and an empty timetable; `mergeSizes` is the distinct count and equals the preview's `size`; a frozen account cannot add a section but can hide and remove. |
| `TeacherPagesTests.cs` | Office hours and profile save and limits; email hidden unless turned on; `teachers` on a student's class lists the named teacher and an adder; a placeholder is not tappable; the teacher page for an account, for a name with no account, 404 for a placeholder; 404 for the `t-` key of an account students see nowhere (signed in only), 200 once it adds a section, and 200 for the account itself; a frozen account cannot save office hours; this week's classes with statuses. |
| `ClassChangeTests.cs` (`TeachWorld`) | cancel, move, room, extra happy paths; every refusal in 5.2; fan-out to ticked codes only; a sibling that isn't one is refused; `alsoOther`; a second change on a session → 409; replace (history kept as `replaced`, an unticked code is back on); undo limits for each kind, including the earlier of two starts for a move; who may undo (maker, co-teacher, named teacher who hid it; a stranger is refused); frozen; the switch off (undo still works; an owner's "view as" passes); `Flagged`; preview: every warning code (with `same-class`), the clash count counts classes and ignores own slots and the moved session, room taken ignores the class's own room, exact size; preview answers 403 for a section not on the timetable and 403 with no numbers for a frozen account; no student ID or name in any teacher payload or notice (regex assert), including the `undone` notice after a teaching assistant's undo. A moved class after its original start: undo is refused, move-again, room and cancel work until the new start, turning it into a room change is refused. A three-code class moved to room "" is one block (one `g:` key). Cancel two of three codes, then cancel the third from its block with the default ticks: 200. `alsoOther` on a day with three sessions changes all three and lists three groups. A session with two base meetings: both skipped, one added. An extra class with no `sectionIds` is refused. |
| `ClassChangeEverywhereTests.cs` | Planner **off** throughout. For each kind: `/me/timetable` (`excludedDates`, the added meeting with `changeId`, the `changes` row); a client that ignores the new fields still gets a correct timetable; reminders; `.ics` ("Cancelled:" with the original UID and no STATUS line; a move under the same UID at the new time) and `.txt`; group week and rides; absence PUT and the GET filter; a note on a moved-to date; free rooms before and after. Undo restores every one. A room change shows the class once; two sessions of one day swapped with each other both show, and an absence on a swapped slot is counted; an extra class on a cancelled slot shows; a room change follows the university's new end time. The agreement test: for every date of the span, sessions rebuilt from the timetable JSON equal `ForAsync` + `Happens`, and the rooms `FreeRooms.OccupiedAsync` reports as taken equal the rooms of those sessions. The contract test: the old `/me/timetable` property set and types are unchanged. Existing `PlannerTests`, `FreeRoomsTests`, `CalendarTests` unmodified. |
| `ClassChangeConcurrencyTests.cs` | Two teachers post the same session in parallel: one 200, one 409, one live row. Undo twice: one notice. A teacher's undo racing a staff undo. A change posted while an apply runs ends either reconciled or refused; `AssertNoOrphans` after. |
| `TeachNoticeTests.cs` | Students of the section get one push; muted and device-less students none; a later state reuses the tag; made then undone before a round sends nothing; an edit before a round sends one with the new details; undo after sending sends "back on"; a claimed-but-unsent row is re-sent after 2 minutes; expired rows are not pushed (a row 1 minute past the start), but a move to an earlier time is still pushed after the new start while the old one is ahead; a change dropped inside one round pushes nothing, and a drop after the announcement sends no "back on"; a `students` row with no section is marked sent; the named teacher and a co-teacher are told, the maker is not; one cancel of a three-code class gives the named teacher one notice; staff undo tells the maker; inbox rows appear in `/teach/me` and clear; the sixth change of an Oman day writes one staff notice (clock at 20:30 UTC, and at 23:30 UTC, which is 03:30 the next Oman day); teacher reminders due / not due / one per merged block (also after a move to room "") / none for a cancelled class; teacher id N and student id N both have devices: a teacher reminder, a teacher notice and the test push reach only the teacher's device, and the student's reminder only the student's; a teaching assistant's reminder and notices reach their student device; the device moves student → teacher → student; a teaching assistant's device stays a student device; the teacher `.ics` and `.txt` by token, dead after removal. |
| `ClassChangeDecideTests.cs` (pure, `[Theory]`) | Every row of the table in 7.1. A cancel whose session is removed while another date of the section is re-roomed merges; one whose session reappears at another start that day drops. A move kept while the university newly lists the new time in another room tells the teacher once. |
| `ClassChangeImportTests.cs` (in-memory, the `ImportTests` pattern) | Through `ScheduleImport.ApplyAsync`: a room-only and an instructor-only re-import keep a cancel though meeting ids change; a moved session drops it and tells the teacher once; a removed session merges a cancel quietly; the university adopts a move: merged, no "changed" stamp; another room: the teacher's room stays and they are told once (a second identical import tells nobody); section removed and course removed: ended before the delete, log readable; an added section re-attaches when it returns; a dropped change does not come back when the timetable reverts; reassignment keeps changes and tells both; `uni-changed` notices; a re-spelling hint is set, accepted, and never links by itself; the preview lists the effects without saving; sync preview and apply; a hand edit in `PUT /admin/sections/{id}`; a day off added and a teaching-weeks edit each end the changes left without a base, tell the teacher, and tell students when an announced move is dropped; one apply that drops a changed three-code class gives its teacher one `dropped` and no `uni-changed`; an apply touching three of a teacher's classes gives one `uni-changed`. `ImportTests`, `SyncTests`, `DashboardTests` unmodified. |
| `TeacherDashboardTests.cs` | Owner and RSA manage teachers; a student and a teacher get 403; approve (409 while held), reject, attach, detach, same-person; freeze; remove with and without undoing upcoming changes (students told "back on"), email blocked, unblock; the log lists and flags; staff undo tells the teacher; the switch is owner-only; grant and revoke teaching rights (owner-only); `stats.attention` counts; `courses/{id}.teacherChanges`; no RSA payload contains a university ID; RSA see "a teaching assistant" on a teaching assistant's log row and "an owner" on a student owner's; removal ends only changes `CanUndo` allows (a move already held is left); the added-sections list; staff sign-out; a staff notice and the badge counts for a busy teacher; `teacherSignInPaused` while the new-address pause is on. |
| Extended | `DeleteAllDataTests` (teacher tables and changes emptied, blocked emails stay, an owner's teaching profile is gone); `UsageAnalyticsTests` (audience `teacher`; the student count unchanged); `AppShellTests` (`/teach`, `/teacher/x`); `PlannerTests` (planner on: a rep's move plus a teacher's cancel shows only the cancel, the rep's other weeks are untouched, the rep's event returns after undo); new `MigrationTests.cs` (a database migrated to `S1AuthBinding` with a student, a pick and a device keeps them after migrating to `Teachers`). |

Web: there are no unit tests in the repo. Each web stage must pass `cd web && npx eslint . && npm run build`, plus the manual checks listed in its stage.

---

## 12. Build stages

Each stage ends with `cd server && dotnet build && dotnet test` green and, from stage 6, `cd web && npx eslint . && npm run build` green. Stages 1 to 5 do not touch `web/`; the old web keeps working against the new server because every change is additive. Work stays on `teachers-update`. Nothing reaches `main` until stage 8 is done (a push to `main` deploys).

### Stage 1 (server): model, sign-in, identity, names

**Goal:** a teacher can sign in by email, is linked to a name or asked to pick one, and is told apart from students and dashboard accounts everywhere. No student behaviour changes.

- **Create:** `Teachers/TeacherModels.cs` (all ten entities of section 1, with `Teacher.SessionVersion`, `TeacherSection.By` and `TeachNotice.ByLabel`); `Auth/TeacherEmails.cs`; `Auth/CodeSignIn.cs`; `Auth/TeacherCodeLimiter.cs`; `Teachers/TeacherNames.cs`; `Teachers/TeachActor.cs` (the record, the filter, all four resolution branches, `CodeNeeded`); `Teachers/TeachEndpoints.cs` (`MapTeach`: `GET /teach/me`, `GET/POST /teach/names`, `POST /teach/notices/seen`, `POST /teach/sign-out-everywhere`); the `Teachers` migration and snapshot.
- **Change:** `Data/AppDb.cs` (DbSets, indexes, FKs); `Auth/Session.cs`; `Auth/AuthEndpoints.cs` (helpers, teacher endpoints, `/session`, `/logout`, the teaching-rights `showCode` line, `endpoint` on verify); `Auth/Errors.cs` (`CodeNeeded`); `Auth/EmailSender.cs` (`SendCannotSignInAsync`); `Program.cs` (the `auth-teacher-request-code` limiter, the `TeacherCodeLimiter` singleton, `api.MapTeach()` before the catch-all); `Admin/OwnerEndpoints.cs` (`ActorName`, lines 67 and 266); `Planner/PlannerEndpoints.cs` (lines 265, 272, 277); `Analytics/UsageAnalytics.cs`.
- **Endpoints:** `POST /auth/teacher/request-code`, `POST /auth/teacher/verify`; `/auth/session` and `/auth/logout` updates; `GET /teach/me` (with `notices` from the table, empty for now, and `changesEnabled` fixed at true until stage 3); `GET /teach/names`, `POST /teach/names`; `POST /teach/notices/seen`; `POST /teach/sign-out-everywhere`. The `sign-in-guessing` staff notice is written as a row from this stage on; it is pushed from stage 4 and shown on the dashboard from stage 5.
- **Tests:** `TeacherTestKit.cs`; `TeacherNameMatchTests`; `TeacherSignInTests`; `TeacherIdentityTests` (the fresh-code cases wait for stage 3, the sections, office and slot cases for stage 2, the device case for stage 4; "view as" is tested on `/teach/me`); `TeacherLinkTests` (approve and reject by writing rows directly; the endpoints come in stage 5); `MigrationTests`; the `UsageAnalyticsTests` addition; `ApiFactory` gets the capturing sender. `AuthFlowTests` and `SignInSafetyTests` pass unmodified.
- **Later stages may assume:** every table exists; `http.Teach()` returns the actor on any `/api/teach` route; `TeachActor.CodeNeeded`; `TeacherNames.NameKey / IsPlaceholder / Shown / TakenKeysAsync`; `SignInTeacher`, `AddTeacher`, `AddTeachingStudent`, `TeachWorld` in tests.

### Stage 2 (server): teaching timetable, own slots, office hours, pages

**Goal:** the teacher home has data; students can read teachers' details. No class changes yet.

- **Create:** `Schedule/TimetableView.cs`; `Schedule/SessionKinds.cs`; `Teachers/TeacherSchedule.cs` (`SectionsAsync`, `TeachersOfAsync`, `SizesAsync`, `MergeKey`, `Siblings`, `ItemsAsync`); `Teachers/TeacherPages.cs` (`RefsAsync`, `GET /api/teachers/{key}`, the key helpers).
- **Change:** `Schedule/Dtos.cs` (`MeetingDto`'s three trailing members); `Students/StudentEndpoints.cs` (`/me/timetable` calls `TimetableView`); `Students/PersonalEndpoints.cs` (`Fill` → `internal`, generic); `Students/FreeRooms.cs` (`NotARoom` → `internal`); `Students/CalendarFeed.cs` (`Kind` → `SessionKinds.Of`, no behaviour change); `Teachers/TeachEndpoints.cs`; `Program.cs` (`api.MapTeacherPages()`).
- **Endpoints:** `GET /teach/timetable`; `POST /teach/seen`; `GET /teach/sections`, `PUT /teach/sections/{sectionId}`; `GET/POST /teach/slots`, `PUT/DELETE /teach/slots/{id}`; `GET/PUT /teach/office`; `GET /api/teachers/{key}`; `teachers` on every class of `/me/timetable`. The section and office writes include their freeze refusals (5.9) and, in "view as", `TeacherSection.By`, the `showEmail` refusal and the `staff-edit` notice rows (2.4).
- **Tests:** `TeacherTimetableTests`; `TeacherPagesTests`; the stage 2 cases of `TeacherIdentityTests`; the `/me/timetable` contract test (in `ClassChangeEverywhereTests.cs`, started here). Every existing timetable test passes unmodified.
- **Later stages may assume:** `TeacherSchedule.SectionsAsync` is the only definition of "on my timetable" and "may change"; `Siblings` is the only definition of a merged class (in this stage it takes `ChangeSet.None`; stage 3 passes the live changes and adds the `g:` keys); `TimetableView.BuildAsync` is the one place the timetable JSON is made, with an empty `changes` for now.

### Stage 3 (server): class changes and the shared overlay

**Goal:** cancel, move, change room and extra class work and show identically everywhere students look, with the planner off. Notice rows are written; nothing sends them yet.

- **Create:** `Schedule/ClassChanges.cs` (`RoomKey`, `EnabledAsync`, `ChangeSet`, `LoadAsync`); `Teachers/ChangeRules.cs` (`StartUtc`, `CanUndo`, `CanEdit`, `PreviewAsync`, `NumbersAsync`, `CreateAsync`, `ReplaceAsync`, `UndoAsync`, `EndAsync`); `Teachers/ClassChangeEndpoints.cs` (section 5.1); `Teachers/TeachNotices.cs` (the texts and the `Add…` writers of 5.6 only).
- **Change:** `Schedule/StudentSchedule.cs` (the overlay, `Item`'s trailing fields, the `off` list); `Schedule/TimetableView.cs` (`excludedDates`, added meetings, `changes`); `Teachers/TeacherSchedule.cs` (`ItemsAsync` applies the overlay; `Siblings` and the merge keys take the live changes, 4.2); `Teachers/TeachEndpoints.cs` (`changesEnabled` in `/teach/me` reads the switch); `Students/Absences.cs` (the GET filter); `Students/PersonalEndpoints.cs` (note dates); `Students/CalendarFeed.cs` (`BuildFrom` / `TextFrom`, cancelled events, `Uid`); `Students/FreeRooms.cs` (`OccupiedAsync`); `Planner/PlannerEngine.cs` (the `shadowed` parameter on `PublishedPlan.For` and `Calendar`); `Program.cs`.
- **Endpoints:** the six of section 5.1.
- **Tests:** `ClassChangeTests`; `ClassChangeEverywhereTests`; `ClassChangeConcurrencyTests` (without the apply race); the fresh-code cases of `TeacherIdentityTests`; the `PlannerTests` additions. Every existing test file passes unmodified.
- **Later stages may assume:** `TeachNotices` rows exist for every event in 5.6 with `SentAt` null; `ChangeRules.EndAsync(db, rows, reason, by, now)` is the one way a change ends: it always deletes that session's unsent student notices, writes "back on" and `undone` only for the three undo reasons, and writes nothing for `replaced`, `merged` and the `dropped-…` reasons (the caller writes those notices, 5.5); `ChangeRules.CanUndo` and `CanEdit` are the only time limits; `ChangeSet` is the only reader of live changes, free rooms included.

### Stage 4 (server): sending, devices, reminders, calendar for teachers

**Goal:** students and teachers are told, once; teachers have reminders and a calendar link.

- **Create:** `Teachers/TeachDevices.cs` (`/teach/notifications`, `/teach/push`, `/teach/push/remove`, `/teach/push/test`, `/teach/calendar`, `/teach/calendar/reset`, `TeachReminders.DueAsync`).
- **Change:** `Teachers/TeachNotices.cs` (`RoundAsync`); `Students/Notifications.cs` (`PushSender.SendToTeacherAsync(db, teacher, note, ct)` sharing a private device loop with `SendAsync`, the only way teacher code reaches a device; `POST /me/push` deletes the endpoint from the teacher table; `NotificationWorker.RoundAsync` gets two more try-blocks: teacher reminders over the same window, and `TeachNotices.RoundAsync`; the student reminders block is not edited); `Students/CalendarFeed.cs` (the token lookup falls through to `Teachers`; the teacher feed); `Teachers/ChangeRules.cs` (the daily alert); `Program.cs`.
- **Tests:** `TeachNoticeTests`; the device case of `TeacherIdentityTests`. `WorkerSafetyTests` and `DaysOffAbsencesNotificationsTests` pass unmodified.
- **Later stages may assume:** writing a `TeachNotice` row is all it takes to tell someone.

### Stage 5 (server): university changes and the dashboard

**Goal:** re-imports, syncs and staff edits treat teacher changes by the rules of section 7; owners and RSA can manage everything. The server is complete.

- **Create:** `Admin/TeacherAdminEndpoints.cs` (section 9).
- **Change:** `Schedule/ClassChanges.cs` (`Decide`, `ReconcileAsync`, `EndSectionAsync`, `EndWithoutBaseAsync`, `TellTeachersAsync`, `AfterApplyAsync`, `SameForStudents`); `Schedule/ScheduleImport.cs` (the three call sites, the `quiet` stamp rule, `TeacherChangeEffect`, `ImportPreview.TeacherChanges`, the preview fill); `Admin/AdminEndpoints.cs` (the section editor's transaction and reconcile; the transaction and `EndWithoutBaseAsync` in `PUT /semesters/{id}/weeks`; `teacherChangesDropped` on upload apply; `stats.attention`; `teacherChanges` on `GET /courses/{id}`); `Admin/SyncEndpoints.cs` (`teacherChangesDropped`); `Schedule/DaysOff.cs` (the transaction and `EndWithoutBaseAsync`); `Admin/OwnerEndpoints.cs` (delete all data); `Program.cs` (`api.MapTeacherAdmin()`).
- **Endpoints:** section 9.
- **Tests:** `ClassChangeDecideTests`; `ClassChangeImportTests`; `TeacherDashboardTests`; the apply race in `ClassChangeConcurrencyTests`; the extended `DeleteAllDataTests`. `ImportTests`, `SyncTests`, `DashboardTests` pass unmodified.
- **Later stages may assume:** the whole API of sections 2 to 9 is final; every JSON shape in this document is live.

### Stage 6 (web): foundation, sign-in, the teacher home

**Goal:** a teacher can sign in, pick a name, tick their classes and use the home read-only with the extras. Students see no difference. Nothing leaks between people on one device.

- **Create:** `lib/viewer.tsx`; `lib/viewAs.ts`; `lib/homeStore.ts`; `teacher/teachData.ts`; `teacher/teachSessions.ts`; `teacher/TeachGate.tsx`; `teacher/TeachLogin.tsx`; `teacher/PickName.tsx`; `teacher/TeachBanner.tsx`; `teacher/TeachMenu.tsx`; `teacher/EditClasses.tsx`.
- **Change:** `main.tsx` (the `/teach…` routes, guards); `lib/types.ts`; `lib/session.tsx` (`who`, the wipes); `lib/api.ts` (the header); `lib/push.ts` (base); `lib/slots.ts` (`markClashes`, the `clashes` option of `buildSessions`, `meeting.type`, `'office'`); `lib/absences.ts`; `lib/shareImage.ts`; `student/timetable.tsx` (built on the factory, the owner stamp, the visibility refetch); `student/Login.tsx` (exports, `CodeStep` props, the chooser sheet, `endpoint`); `student/Join.tsx`; `student/NotFound.tsx`; `student/Lab.tsx`, `Today.tsx`, `Session.tsx` (the viewer, teacher branches and hides only; no marks yet); `student/SlotSheet.tsx`; `student/CalendarSheet.tsx`; `student/NotificationsSheet.tsx`; `student/FreeRooms.tsx`; `student/Colours.tsx`; `student/StudentShell.tsx` (the "Teaching" item); `planner/Planner.tsx`; `admin/AdminLogin.tsx`.
- **Check by hand (dev server; the teacher code is in the server log, never on screen):** student sign-in, home, week, session page, slots, offline copy and logout behave as before; then teacher sign-in, the name step, add and hide sections, merged blocks (a three-code class is one block with no Clash tag; with a weekly seed, too), own slots, office-hours blocks open the office page, calendar, notifications (the device shows as on after enabling, and the test push arrives); a teaching assistant with no names lands on an empty home, not on the name step; a student's saved offline copy from the old build survives the first load of the new one; a student's offline copy is dropped after signing in as a teacher on the same browser, and the reverse; the installed app's "Free rooms" shortcut opens `/teach/rooms` for a teacher; the app starts (no blank page) from a cold load of `/`, `/teach` and `/colours`; narrow and wide viewports.
- **Stage 7 may assume:** `useViewer()`, `useHome()`, `viewAs`, merged blocks (`Session.merged`), the `viewer` / `teach` / `offline` props on `SessionPage`, exported `Screen`, `Decor`, `CodeStep`, `TimeButton`, `Clock`.

### Stage 7 (web): class changes and what students see

**Goal:** teachers change classes; students see the marks, the teachers block and the teacher page.

- **Create:** `teacher/SessionActions.tsx`; `teacher/ChangeSheet.tsx`; `teacher/FreshCode.tsx`; `teacher/OfficeHours.tsx`; `teacher/MyChanges.tsx`; `student/ClassTeachers.tsx`; `student/TeacherPage.tsx`.
- **Change:** `lib/slots.ts` (`s.change`, `buildGhosts`, `changeLine`); `lib/homeStore.ts` (`ghosts`); `teacher/teachSessions.ts` (ghost collapse, `g:` blocks); `student/Today.tsx` (`Tags`, `List`, the only-ghosts day); `student/Lab.tsx` (ghost blocks, the weeks and weekend columns that only a change fills, the resolver order with the `g` and `s` forms, the add chooser); `student/Session.tsx` (the banner, `ClassTeachers`, `SessionActions`); `student/Course.tsx` (ghost rows, `ClassTeachers`); `main.tsx` (`/teacher/:key`, `/teach/office`, `/teach/changes`).
- **Check by hand (with `?now=`):** each change kind on a student account and a teacher account side by side; a cancelled class is greyed in the day list and week grid and not counted in "Next in", "N classes" or "Missed x of y"; a moved class shows both halves and links between them; a legacy `?session=<courseId>…` link still opens, and on a day with two sessions of one course a link to the cancelled one opens the greyed one, not the other; a notice link to a non-lead code of a merged class opens the block; an extra class from the "+" (pick the classes) and from a session; a class moved to a date after the last teaching week can be reached by swiping, and a cancelled Friday class is greyed in its own column; a moved class after its original time: no undo, but move again, change room and cancel work; warnings and Undo; the fresh-code step; a teaching assistant entering from the menu; an old build of the web against the new server still shows a correct timetable.
- **Stage 8 may assume:** `/teach` works for teachers and teaching assistants; "view as" only needs its entry point.

### Stage 8 (web and docs): dashboard, "view as", release

**Goal:** owners and RSA have their pages; the release is ready.

- **Create:** `admin/Teachers.tsx`; `admin/TeacherChanges.tsx`; `admin/TeachingAccess.tsx`; `docs/teachers.md` (how it works, in the style of `docs/semester-planner.md`).
- **Change:** `main.tsx` (admin routes); `admin/AdminLayout.tsx` (the two links and the badge); `admin/AdminHome.tsx`; `admin/StudentDetail.tsx`; `admin/Upload.tsx`; `admin/EduWaveSync.tsx`; `admin/CourseDetail.tsx`; `admin/DeleteAllData.tsx`; `admin/Analytics.tsx`; `README.md` (the sign-in section, `RateLimits:TeacherRequestCode`, `TeacherWrongCodesPerHour`, `TeacherWrongCodesNewPerHour`); `deploy/.env.example`; `deploy/README.md` ("First deploy with teachers": check the migration created tables only, sign in with "view as" from the dashboard, make and undo one change on a test section, confirm the switch works); `docs/plan.md` roles table (RSA sees teacher names, staff emails, the change log and counts; nothing about students).
- **Check by hand:** approve a claim; attach a name; freeze; remove with undo; the log and staff undo; the switch; "View as" for a name with and without an account (the banner, changes logged under the owner, students see "Classy staff"; no reminder button, no own-slot chooser, notices without "Got it"; an office-hours edit shows in the teacher's inbox; a teacher name opens its page for a dashboard owner); both sidebar links and the badge; "Sign out everywhere"; grant teaching rights to a student; a sync preview lists the changes it will drop.
- **Release:** full `dotnet test`; `npx eslint . && npm run build`; re-run the section 3 collision check against a fresh export of the live timetable; fetch and fast-forward; then one merge to `main`.

---

## Points settled here that the owner was not asked about

Say if any should differ.

1. **"View as" and personal extras.** An owner viewing as a teacher can do everything about classes, and office hours and added sections when the name has an account, but not that teacher's own slots, notifications or calendar link, and does not see the teacher's private own slots. An owner who wants to try those gives their own account teaching rights. Edits an owner makes to a teacher's sections or office hours are recorded under the owner and the teacher is told; an owner can never switch on "show my email" for a teacher.
2. **Students see "Classy staff"** as the maker of a change an owner made through "view as". The log holds the owner and the name.
3. **The 7-day code rule is per browser,** covers teaching assistants and student owners in "view as", and applies to undo as well. Dashboard password accounts are exempt.
4. **Owners in "view as" pass the all-changes-off switch,** as they pass the planner switch. Undo works for everyone while it is off.
5. **A frozen teacher cannot undo either;** staff undo for them.
6. **Staff undo ends when the class starts,** like a teacher's.
7. **A blocked email is told so, by email.** The sign-in screen answers as for any address (so nobody can test which staff addresses were removed), and the message "This email can't sign in to Classy. Ask the Classy team." arrives in that mailbox instead of a code.
8. **A signed-out teacher starts from the student screen's "Staff? Sign in here" link.** The device does not remember that a teacher used it.
9. **Short notice is under 2 hours.**
10. **A cancel whose session the university then removes merges quietly** when nothing new appeared in that section; otherwise it is treated as a move and the teacher is told.
11. **When the university moves a class to the teacher's new time but another room,** the teacher's change ends and students see the university's room; the teacher is told.
12. **Merging university sessions needs a real room.** Two classes "Online" at the same time under one name stay two blocks. A class a teacher moved stays one block whatever room it was given, "to be announced" included.
13. **Notices about someone changing your class ignore "mute changes".** That switch covers university timetable alerts only.
14. **Office hours send no reminders;** classes and own slots do.
15. **"At once" means within the worker's 30 seconds,** as for every other notification today.
16. **Extra guess limits on teacher sign-in.** Three tries per code instead of five; 30 wrong codes an hour per network; and 40 wrong codes an hour, site-wide, for addresses that have no account yet. The last one can be tripped on purpose to pause **first-time** teacher sign-ins for an hour (teachers who already have an account are not affected, and the per-address cap of 20 codes a day already allows the same against one address); the per-network one can pause teacher sign-ins from campus Wi-Fi on browsers that have not signed in before. Staff see the pause on the dashboard. With these, a made-up address can be guessed about 960 times a day in total, a chance of about 1 in 1,000 a day of someone getting an account that can change classes without owning a mailbox. **An 8-digit code for teachers would make that negligible**; it is not done here because plan section 1 says 6 digits. Say if it should be 8.
17. **Only plain `firstname.lastname` addresses link by themselves.** `raed_bolbol@` and `raed-bolbol@` pick their name and wait for staff, and an account that is not linked to a name is never shown to students under a real teacher's exact name (it shows as typed, "raed_bolbol").
18. **A moved class can be moved again, re-roomed or cancelled until its new start,** even after its original time has passed. Undo still ends at the earlier of the two times, as the plan says. A moved class cancelled after its original time shows as cancelled in its original (past) slot, the notice names the moved time, and that cancel is final.
19. **A frozen teacher also cannot add sections or edit office hours and details,** since both show to students. Hiding and removing sections still work.
20. **Staff and the teacher can end a teacher's sign-ins everywhere** without removing the account ("Sign out everywhere"). The next sign-in needs a new emailed code.
21. **A student given teaching rights must enter a new code once** before their first class change, even if they signed in a minute before the grant.
22. **A teacher account has a page for students only once students can see it somewhere** (a linked timetable name, or a section it added).
23. **RSA's alert for "more than 5 changes" is in the dashboard** (a home row and a badge on the Teachers link); only student owners with notifications on get a push.
24. **When a day off or a teaching-weeks edit drops a move students were told about,** they are told the moved class is off.

---

## Review decisions

Three reviews (security, data and sync, web compatibility) raised 47 issues. Each was checked against the code on `teachers-update` and against `plan.md`.

**Security**

1. Sign-in code guessable for made-up addresses: **accepted**. `LoginCodes.cs` and `WrongCodeLimiter.cs` confirm the only cross-key limit is 300 an hour per IP. Added `TeacherCodeLimiter`, 3 tries per code, trusted-browser cap (2.1, 2.2); the trusted cap is counted per address rather than per browser cookie, which could be cleared. The 8-digit option is left to the owner (settled point 16).
2. "View as" edits not logged, and can show a teacher's email: **accepted**. `TeacherSection.By`, `TeachNotice.ByLabel`, `staff-edit` notices, 409 on `showEmail` (1.1, 2.4, 8.1).
3. Teacher reminders and test push could reach a student with the same id: **accepted**. `Notifications.cs:350-351` and `PushSender.SendAsync` confirm it. One method, `SendToTeacherAsync`; reminders in their own try-block (5.7, 5.8).
4. `notices/seen` not limited to the caller, and works in "view as": **accepted**. Owner filter, 100 ids, 409 in "view as"; the same rule stated for slots and `push/remove` (4.4, 5.8, 10.5).
5. Unlinked account shown under a real teacher's name: **accepted**, and taken one step further: the match also refuses a surname cut inside a word (`raed.bol.bol`), which the reviewer's dots-only rule would still have linked (3.3, 3.5, 3.7).
6. A sign-in from before the grant counts as a fresh code: **accepted**. `code-at >= Profile.CreatedAt` (2.2, 2.5).
7. `t-{id}` lets students list every account: **accepted**. 404 unless students already see the account; the account itself and owners are exempt (8.3).
8. Preview gives numbers for any section and to a frozen account: **accepted** (5.1).
9. The blocked-email answer reveals removed staff: **accepted**. Same 200, the message goes by email (2.2).
10. Teaching assistant mislabelled for RSA; university ID could reach a notice: **accepted**. `OwnerEndpoints.cs:49-50` confirms `ShownActor` works on the ID's shape only. Label by kind of actor; notices use `ByName` only; `EndedBy` carries who it was (1.1, 5.6, 9).
11. No way to end a teacher's other sign-ins: **accepted**. `Teacher.SessionVersion` and two endpoints (1.1, 2.3, 9).
12. Freeze leaves sections and office hours open: **accepted** (4.4, 5.9, 8.1, 9).
13. Plan coverage of "try everything" and the RSA alert: **accepted in part**. The badge and the statement that the dashboard is the RSA alert are in (9, 10.10). Making a teacher's own slots and settings readable in "view as" is **rejected**: own slots are private notes ("Dentist"), the plan does not ask for owners to read them, and settled point 1 already tells the owner how to try those extras.

**Data and sync**

14. The "no doubles" rule hides room changes, swaps and classes on a vacated slot: **accepted** (5.3, 6.1, 6.4).
15. `EndAsync` writes "back on" for drops, merges and replacements; unsent notices survive a drop: **accepted** (5.5, 5.7, 7.2, stage 3).
16. A moved class is frozen once its original time passes: **accepted**. Undo and replace limits split; plan lines 41, 97 and 99 support it (5.2, 5.4, 10.7).
17. Merged identity on raw meetings only: **accepted**. `g:` keys for sessions a change put on; siblings leave out codes with their own change (4.2, 5.1).
18. Teacher notices repeated per section: **accepted**. One per teacher, kind and group; one `uni-changed` per teacher per apply (5.6, 7.2, 7.4, 7.5).
19. Stored and compared times must be UTC: **accepted**. `AppDb.cs:52` and `Notifications.cs:238-240` confirm it (section 1, 5.2, 5.5, 5.6).
20. Free rooms reads raw rows: **accepted**. Through `ChangeSet`; a `room-differs` notice when the university newly lists the new time (6.5, 7.1).
21. Teaching-weeks edit not covered; no lock; students untold when an announced move is dropped: **accepted**. `AdminEndpoints.cs:89-107` and `DaysOff.cs:25-46` have no transaction (7.2, 7.3).
22. Stale end time on a room change: **accepted**, by the simpler form: readers take a room change's time from its base (6.1, 7.1).
23. Cancel merges reported as drops: **accepted**. `came` narrowed to that date and a new start (7.1).
24. A move notice expires at the new start: **accepted**. The later of the two starts (5.6).
25. Weekly block split when one code is unticked: **accepted** (4.5, 10.4).
26. "The other session" assumes two: **accepted**. `others[]` (5.1, 5.2, 10.7).
27. The session key can match two meetings: **accepted**. `ScheduleImport.cs:140-143` allows it (5.2, 6.1).
28. "Upcoming" on removal not tied to the undo limit: **accepted**. `ChangeRules.CanUndo` (9).

**Web compatibility**

29. A teaching assistant bounced between `/teach` and `/teach/name`: **accepted** (4.4, 10.1, 10.5).
30. No control for an extra class's sections: **accepted** (5.1, 10.7).
31. Clash mark on every merged block: **accepted**. `slots.ts:129-135` marks different courses before the collapse (10.4).
32. Classes outside the teaching weeks and cancelled weekend classes unreachable: **accepted**. `Lab.tsx:361-379` and `slots.ts:146` confirm it (6.2, 10.5).
33. A legacy or cancel link can open the wrong session: **accepted**. `Lab.tsx:620-621` falls back to the first session of the day (10.4).
34. Teacher links resolve only for the lead section: **accepted** (4.5, 10.4).
35. A merged class moved to "to be announced" splits: **accepted**, with the fix of issue 17 (one `g:` merge key) instead of a second collapse rule in the web.
36. Weekly un-merge: **accepted**, the same change as issue 25.
37. "View as" shows controls that answer 409: **accepted** (10.5).
38. `deviceSubscribed()` and the reminder button left on `/me`: **accepted**. `push.ts:39`, `Session.tsx:195, 208` (10.5).
39. Import cycle through `viewAs`: **accepted**. `timetable.tsx:3` imports `lib/api` and `main.tsx:7` loads it first (10.3).
40. `/teach` prefix also matches `/teachers`: **accepted** (9, 10.3).
41. First load deletes every student's offline copy: **accepted** (10.9).
42. Office-hours block has no target: **accepted** (10.4, 10.5).
43. The change log has no way in: **accepted** (10.10).
44. `SignedIn` shuts out a dashboard owner: **accepted** (10.1).
45. Class size "distinct" in one place and "summed" in another: **accepted**. Distinct, sent as `mergeSizes` (4.2, 4.4, 10.4).
46. Mute label promises too much: **accepted** (10.5).
47. The "Free rooms" shortcut drops a teacher on the home: **accepted**. `manifest.json:42` (10.1).

Totals: 47 issues. 46 accepted in full, 1 accepted in part (issue 13, whose "readable personal routes" half is rejected), 0 rejected in full.

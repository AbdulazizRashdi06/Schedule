# Usage analytics

Collection starts when this version is deployed. It cannot reconstruct historical busiest days or peaks. Reports are available to owners at `GET /api/admin/analytics?days=30` (1–90 days). There is no analytics dashboard page yet.

## Collected

- Daily active student accounts, the largest observed count of accounts active within five minutes, and the time that peak was first observed. Calendar days use Oman time. First/last sample timestamps expose partial-day coverage.
- Hourly API request totals by route template, HTTP method, anonymous/student/staff audience, and status code. Counts cover matched API endpoints, excluding health, unknown routes, and the analytics report itself.
- Total and maximum response durations permit weighted average latency, error rates, and feature/API usage comparisons. They are server request durations, not page rendering measurements.
- Reports also summarize existing registration dates and current setup steps, majors, and years. These are current snapshots, not historical conversion funnels.

Operational aggregate rows contain no student IDs, account IDs, IP addresses, user agents, device identifiers, locations, referrers, full URLs, query parameters, invite codes, request bodies, or note/message contents. There is no third-party analytics service or new tracking cookie. Optional engagement measurement, described below, uses account-linked session records with explicit opt-in.

## Interpretation

Activity derives from the app's existing authenticated-account `LastSeenAt` field, updated at most once a minute. Daily counts and peaks are monotonic observed maxima, sampled after successful student requests. Students include student-owner accounts; staff accounts are separate. Concurrent activity means requests within a five-minute window, not people with a tab open. Activity spanning midnight can appear in the new day's concurrency peak without being active that calendar day. Offline use, logged-out visitors, and idle reading are not counted as active students. Background authenticated requests can count. Request counts are not visits, page views, or unique visitors. Peaks are observed approximations, especially around minute boundaries and concurrent requests.

Operational aggregates cannot give weekly/monthly unique people or session duration. Do not sum daily active users and label that unique people. Missing days mean no samples, not proven zero usage. A first day after deployment is partial, and service outages create gaps. Optional engagement reports measure only participating accounts and must not be presented as usage by all students.

## Optional engagement measurement

The student menu has an unchecked-by-default **Allow optional usage analytics** control. Consent is stored on the server with its version and last-change timestamp. The server checks consent for every sample. Turning it off stops local collection and transactionally deletes that account's session and screen records. Account deletion cascades to these records. Consent choices remain until account deletion or delete-all-data; they are not subject to the session retention period.

After opt-in, the client sends cumulative screen-category counters approximately every 15 seconds, plus navigation/background flushes. Only known screen names are accepted; URLs and entity IDs never leave the client. Session records link a random session ID to an internal account ID, start/last-report times, active milliseconds, sequence number, screen-category times and view totals. This is personal data, not anonymous data. Owner reports expose aggregates, not identifiers or individual sessions.

Reports add total and average active time per session and participating user, sessions per user, screen views per session, repeat users (more than one session during the selected period), per-screen time/views, and daily session totals. An engaged session has at least 10 seconds of active time or two screen views; `shortSingleScreenRate` is the complement, not a universal bounce-rate definition. Zero-participant averages return null. Current sessions are included, so averages are provisional. Daily session statistics attribute a session's whole duration to its start day; the report window selects sessions by start time.

Active time requires the document to be visible and focused and user interaction in the last 60 seconds. Device-sleep gaps over 30 seconds are discarded. A page load starts a session, and 30 minutes without interaction starts another on return. Reloads therefore count as another visit. Navigation is categorized (e.g. course to course stays within `course`); same-category navigation does not increment views. Long, uninterrupted reading after 60 seconds counts as idle. Native WebViews rely on visibility/focus events; native background lifecycle behavior has not been verified on devices.

Server sequence checks ignore duplicate/out-of-order samples, reject other accounts' session IDs, cap each credit to 30 seconds and elapsed wall time, and bound total time across competing tabs. This prevents time inflation from ordinary duplicate delivery or overlapping tabs; client-reported measurements are not tamper-proof. Network outages, closed browsers, abrupt logout, cleanup, and missed final flushes can undercount time. Cumulative counters are retried, but at most 30 seconds can be credited after a delivery gap; offline use is not reliably measured. Pulse and consent requests are excluded from operational usage totals to avoid tracker-driven request inflation, although normal authentication still updates existing last-seen timestamps.

Endpoints: `GET/PUT /api/me/analytics/consent` and `POST /api/me/analytics/pulse` require student authentication. Opt-in requires `version: 1`. Reports are under `engagement` on the existing owner analytics endpoint. The global collection switch pauses both operational and engagement collection. Session data is pruned on the same 90-calendar-day schedule.

## Controls

- `Analytics__Enabled=false` disables new collection. Existing reports remain accessible to owners.
- An hourly background cleanup, also run at startup, removes analytics older than the current Oman day plus the preceding 89 days. Cleanup continues while collection is disabled. Backup retention must be managed separately.
- The owner's existing delete-all-data action also clears analytics. The deletion request itself can create a new operational request counter afterward.
- Writes use atomic SQLite upserts. Analytics failures are logged without failing the user's request. Collection is synchronous and adds database work; monitor load before scaling.

## Privacy and operation

Aggregate storage minimizes new data but is not a legal certification. Existing account activity and small-count reports can still have privacy implications. Keep the service privacy notice accurate about operational measurement, purposes, retention, owner access, and contact details. Assess the service's lawful basis and any consent obligations before deployment; do not add identifiable histories or sensitive/device/location tracking under this implementation's scope.

Relevant official sources: [Oman's personal data protection guidance](https://www.mtcit.gov.om/sectors/governance/personal), [2026 law amendments](https://www.mtcit.gov.om/library-3/legislations-policies-8/laws-75/royal-decree-68-2026-amending-some-provisions-of-the-personal-data-protection-law-1481).

# Aggregate usage analytics

Classy collects server-side operational totals without a student opt-in flow. There is no browser tracker, analytics cookie, session heartbeat, consent checkbox, or account-linked session history.

Owner reports: `GET /api/admin/analytics?days=30` (1–90 days).

## Statistics

- Daily active student accounts, busiest observed day, largest observed count active within five minutes, and the first time that peak was observed. Calendar days use Oman time. First/last sample timestamps expose partial-day coverage.
- Hourly API request counts by route template, HTTP method, anonymous/student/staff audience, and HTTP status.
- Server errors, client errors, weighted average response time, and maximum response time.
- Existing registration totals and current setup-step, major, and study-year counts.

Analytics rows contain no student/account IDs, IPs, user agents, fingerprints, device IDs, locations, referrers, full URLs, query strings, invite codes, or request/response contents. There is no third-party analytics service.

## Limits

Active counts derive from the app's existing authenticated-account `LastSeenAt`, updated at most once a minute. Successful student requests sample daily counts and peaks. Student-owner accounts count as students; staff counts are separate. Concurrent activity means requests within a five-minute window, not people with a tab open. Offline use and idle reading are not measured. Background requests can count.

Counts are observed maxima; missing days mean no samples, not proven zero. Deployment days and outages create partial coverage. Do not sum daily active users and call that weekly/monthly unique people. Request counts are not visits or screen views. This version does not measure time spent in the app, session duration, individual journeys, returning-user cohorts, or bounce rate. Response time measures server performance, not a student's time in the app.

## Controls and retention

- `Analytics__Enabled=false` stops new collection; reports remain owner-only.
- Hourly cleanup, also run at startup, keeps the current Oman calendar day and preceding 89 days. Cleanup continues while collection is disabled. Backup retention is managed separately.
- Delete-all-data also clears analytics; that action can create a fresh operational request counter afterward.
- Atomic SQLite upserts prevent lost counts. Analytics write failures are logged without failing the user's request. Collection adds database work; monitor load before scaling.

The correction migration removes the previously deployed optional engagement tables and their data. Historical migrations remain so existing installations upgrade correctly. Old frontend clients' consent/pulse requests now return 404 and are excluded from aggregate usage counters.

Aggregate storage minimizes additional data and avoids an optional tracking flow; it is not a blanket legal certification. Keep the operational privacy notice accurate about purposes, retention, owner access, and contact details. Do not add personal histories or sensitive/device/location tracking under this implementation's scope.

Official references: [Oman's data protection guidance](https://www.mtcit.gov.om/sectors/governance/personal), [2026 amendments](https://www.mtcit.gov.om/library-3/legislations-policies-8/laws-75/royal-decree-68-2026-amending-some-provisions-of-the-personal-data-protection-law-1481).
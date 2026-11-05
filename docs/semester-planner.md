# Shared semester planner

## Access and rollout

Owners can open **Semester Planner** from the staff dashboard or their student menu. The initial rollout is owner preview: ordinary reps and student event delivery remain disabled until an owner enables the feature in the planner's **Settings**.

Under **Students → a student → Batch representative**, grant access and choose a home major. Majors with the same family code share one workspace per semester; a major without a family gets its own. Revocation takes effect on the next request. Home assignments stay independent of later student profile edits.

Reps may add any major/course to their workspace. A batch is an editable course/section selection, optionally prefilled from existing major/year defaults. It does not restrict student recipients by major or study year. Published events reach students who picked a targeted course and, when specified, its selected sections.

## Planning and publication

The weekly calendar shows classes by time and pins exams above their day. Monthly and four-month views emphasize identifiable exam cards; day agendas expose all matching classes and exams. Calendar ranges navigate beyond the initial four months. Phone calendars scroll within their own surface; desktop dragging and the **Move exam / edit** day-agenda action both open a proposal, preserving times and duration.

Saving or moving never changes a published event. Run the conflict check, review warnings and coverage gaps, and publish explicitly. Warnings require acknowledgment and an explanation. A second rep's edit or a changed schedule invalidates stale revisions/checks. History records published snapshots and supports proposing a restored version. A selected published event can be withdrawn with an explanation.

University-imported classes stay intact. Added classes and dated/weekly replacements/cancellations are applied as a separate layer. Source meetings have content fingerprints, not fragile meeting IDs; a changed import makes an old adjustment stale. Students then see the updated university occurrence until a rep reviews/replaces the adjustment.

Slot searches use selected academic course/section timetables, published adjustments/exams, and shared drafts. They never read private custom slots or expose student rosters. Searches check all recurring occurrences and report missing class times. Default hours: Sunday–Thursday, 08:00–18:15, Oman time. Default warnings: a second exam in one day or a third within a rolling seven days; workspace settings can change those thresholds. Location checks reflect recorded timetables, not room-booking guarantees.

## AI

The API calls the existing internal Codex gateway, not OpenAI directly. Server configuration:

| Setting | Default |
| --- | --- |
| `Planner__Enabled` | `false`; an owner-controlled database setting overrides it |
| `Planner__AiUrl` | `http://codex:8090/v1/chat/completions` |
| `Planner__AiModel` | `gpt-6-luna` |

Classy and the Codex service must share the existing Zarqa Docker network. The gateway owns and refreshes the ChatGPT sign-in. No Codex credentials are copied into Classy or the browser. Classy's worker runs one inference at a time and accepts up to two outstanding requests per author. Requests support text/plain text files and up to three PNG/JPEG images under 6 MB combined.

AI jobs are asynchronous, cancellable and private to their author within an authorized workspace. Screenshots are removed from persisted job input after processing/cancellation; jobs expire after one day. Inputs, images, model output and credentials are not logged. Course, section and source references are checked against current data. AI produces an editable interpretation or slot-search settings, never a proposal write or publication. Failed requests preserve the browser's input for explicit retry or manual entry.

Run **Actions → Check planner AI → Run workflow** for live synthetic text and image verification through the VPS's authenticated gateway. The workflow is manual so ordinary deployments do not consume inference capacity. It uses the existing deployment SSH secrets and never publishes events.

## Verification

Backend: `dotnet test server/UniSchedule.Tests`. Frontend: run `npm run build` and `npm run lint` in `web`. Integration tests cover permissions, draft/publication boundaries, concurrency, actual enrollment/section targeting, source changes, recurring slots, AI schema transport, capacity/login failures, unknown model IDs and screenshot cleanup.

Production release uses the existing GitHub deployment pipeline, which backs up the database before additive migrations. Enable reps/student delivery only after the pipeline, browser checks, and manual gateway smoke check pass. Physical iPhone gesture testing remains a separate hardware check.

# Semester Planner direction contract

Mode: Operate. Extension of the existing Classy interface, not a redesign.

## Direction contract

**THESIS:** Shared academic planning with identifiable exams and a clear draft/publication boundary, not a dashboard of metrics.

**OWN-WORLD:** Inherit Classy's Bricolage Grotesque/JetBrains Mono, cream/navy/sky/ice, pill controls, paper inputs and thin calendar rules.

**STORY:** Choose batches/courses, inspect the calendar, find a slot or interpret a lecturer message, review conflicts, then publish explicitly.

**FIRST VIEWPORT:** Desktop places context/actions above filters and the calendar beside a 350px tool panel. Months show all seven weekday columns on desktop. Phones use a contained scroller with readable exam cards and tools below. Primary action: Add exam / class.

**FORM:** User-pinned weekly/monthly/four-month calendars with connected proposal, slot-finder and AI tools, as explicitly requested in the approved implementation plan. Existing visual world preserved. No concept seed was run; no seed key or tournament approval is claimed.

**FINISH:** unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## First viewport

At desktop width show the semester/family context, filters, a readable calendar and a right-hand workspace tool panel. Identify individual exams by title, course and time. Use text and dashed outlines for draft status as well as colour. At phone width stack tools below the calendar; every action is reachable by touch and keyboard. Month cells expose an explicit day agenda when crowded.

## Interaction

Keep the calendar and selected filters in place while switching between proposals, slot finding and AI. A moved exam opens a proposal with its time and duration preserved. Publication is separate from saving. Polling does not overwrite an open editor. Errors preserve input. Shared edits use versions and report conflicts.

## Existing system

Use Bricolage Grotesque and JetBrains Mono, cream background #f8f3ea, navy #0b1957, sky #9eccfa and ice #d1e8ff. Existing pill buttons and rounded paper inputs carry primary actions. Thin navy rules structure the calendar. No decorative imagery, new palette, hero statistics or changes to student navigation.

## Acceptance

Review desktop and mobile monthly, weekly and four-month layouts; proposal editing and warnings; empty/loading/error states; identifiable exam cards; mobile Move exam controls; owner access assignment; keyboard and touch targets. Preserve original content and student gestures outside this surface.

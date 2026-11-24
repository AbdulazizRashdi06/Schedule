---
target: Classy student app
total_score: 25
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 4
target_identity: "file:C:\\Projects\\Scheduele\\web\\src\\student"
timestamp: 2026-11-21T11-22-17Z
slug: web-src-student
---
Method: dual-agent (A: design review · B: detector + browser)

## Design Health Score: 25/40 (Acceptable)
| # | Heuristic | Score | Key issue |
|---|---|---|---|
| 1 | System status | 3 | Today stays stale after adding a slot until reload; login "Step 2 of 2" vs setup's own 5-bar progress |
| 2 | Real world | 3 | Personal slots are called classes ("First class · GYM", "Room TBA"); on Saturday "This week" shows the week that just ended |
| 3 | Control & freedom | 2 | Tapping a course name in setup unticks it with no undo; no review before "Skip and finish"; Add-a-slot has no Cancel |
| 4 | Consistency | 2 | Section times shown in Edit but hidden in Setup; progress bar tracks completion, not position; day pages have Menu, week pages don't |
| 5 | Error prevention | 3 | Excellent clash detection; picks save last-write-wins, so an older tab/device can overwrite newer choices |
| 6 | Recognition | 2 | Section chips show only "1 2 3"; times live only in the aria-label |
| 7 | Efficiency | 3 | Import, calendar feed, ChatGPT, rides; no jump-to-date or course search |
| 8 | Minimalist | 3 | ~28 "+" marks on an empty week grid; "Free day." card dwarfs the next class |
| 9 | Error recovery | 2 | No confirmation of what was actually saved |
| 10 | Help | 2 | No help for section, LS, clash waiver, import, Groups |

## Specificity
Domain logic is authored for GUtech (six fixed slots, Sun–Thu week, year from ID, clash waiver, rides from first/last class, honest free-rooms disclaimer). Navigation structure (day pager + one Menu sheet + many bottom sheets) is generic. Detector: CLI 0 findings (Tailwind hides issues from static scan); live DOM: 9–10px text in Lab.tsx week grid (lines 811, 825, 896, 920), placeholder contrast 2.4:1 (Login.tsx:194), width transition (Today.tsx:285), bounce easing (index.css:74). False positives: cream palette, date kicker, all-caps mono labels, off-screen carousel pages.

## Priority issues
1. [P1] Navigation hidden and flat: 31-day swipe pager + vertical snap to week; daily tools (Free rooms, Groups) share a flat 8-item menu with Colours and Log out. Fix: bottom tab bar (Today · Week · Rooms · Groups), grouped settings, visible date/week position, jump-to-date.
2. [P1] Section chips hide their times in Setup. Fix: show day/time under each chip, as Edit does.
3. [P1] Home doesn't adapt to the moment: free-day/weekend states, personal slots worded as classes, stale after edits, Saturday week. Fix: time-aware home, slot wording, cache refresh, Fri/Sat = coming week.
4. [P1] Rate prompt fires on first landing after onboarding. Fix: never in the first session; after N active days.
5. [P2] Accessibility: unnamed next-class link; all ~31 day pages and ~10 week grids in the a11y tree at once; background reachable behind Menu sheet; 9–10px week-grid text.
6. [P2] Saves are last-write-wins with no "Saved · N courses" confirmation.

## Personas
Jordan: no help for SEC/LS/clash waiver; course-name tap unticks; pager/week hidden; rate pop-up on arrival; placeholder is the owner's real ID.
Casey: Menu top-right; day position shown only by a tiny dash strip; easy accidental "+" in 60px week cells; no Cancel on Add-a-slot.
Sam: unnamed primary link; duplicate controls ×11; non-modal sheets; tiny low-contrast labels.

## Questions
Should home be one answer at 7:55am? Is the product "where do I need to be and who am I with"? Should onboarding start from the GUtech import? Should Week be a main tab? How does Classy prove it's trustworthy each day?

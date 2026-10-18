# Classy Sync

A browser extension that fetches the GUtech timetable from EduWave in your signed-in browser and sends it to Classy. Staff then check the changes and apply them from **Upload** in the dashboard.

## Install (Chrome, Brave or Edge)
1. Open `chrome://extensions` (`brave://extensions` in Brave) and turn on **Developer mode**.
2. Press **Load unpacked** and pick this `extension` folder.
3. Pin **Classy Sync** to the toolbar.

## Connect once
1. Sign in to the Classy dashboard (https://schedule.tryzarqa.com/admin) as an owner in the same browser.
2. Open the Classy Sync popup and press **Connect to Classy**. That makes a sync token, which can do nothing but send timetables. It's listed under **Accounts → Classy Sync**, where you can disconnect it.
3. Sign in to EduWave and open **Advisor Plan** through its menu. The extension reads the page's form from there.

## Syncing
- Press **Sync now**, or leave **Sync every morning at 06:30** on.
- It fetches every course in Classy's current semester, **one a second** (about 4 minutes for 217 courses).
- It works through an open Advisor Plan tab when there is one, and otherwise in the background. Moving to another tab doesn't stop it.
- Progress is saved after each course. If EduWave signs you out, the badge turns red **!**. Sign in and open Advisor Plan again, and it carries on from where it stopped.
- When it's done, Classy keeps the result as a **pending sync**. Nothing changes for students until someone presses **Apply** under **Upload → From EduWave**.

## Safety
- Your EduWave password is never stored. The extension uses your normal browser session, and it only reads course sections.
- Classy refuses a sync where more than a quarter of the courses failed or would be removed, since that usually means EduWave sent error pages.
- Courses EduWave didn't answer for keep their current classes.

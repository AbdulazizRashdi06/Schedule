# Handoff: Classy iOS and Android apps

Paste this into the next session, in the repo `AbdulazizRashdi06/Schedule`.

## Where things stand
- **Classy** is live at https://schedule.tryzarqa.com and is public with real GUtech students.
- **Web app:** React 19, Vite 6, React Router 7, Tailwind 4, in `web/`.
- **API:** .NET 10 minimal API with SQLite, in `server/`, running on the Zarqa VPS behind Caddy.
- **Deploys:** pushing to `main` deploys through GitHub Actions (`.github/workflows/deploy.yml`). Every deploy backs up the database first.
- **The app is mobile-first already:** phone layouts, `viewport-fit=cover`, safe-area insets, an offline snapshot of the timetable (`web/src/student/timetable.tsx`, saved in localStorage), and the email sign-in code input has `autoComplete="one-time-code"`.
- **Goal:** store apps for iOS (App Store) and Android (Play Store) without rewriting the UI.

## Approach: Capacitor around the existing web app
Wrap `web/` with **Capacitor**: one codebase, real native shells, native plugins where they help. Don't build a separate React Native or Swift app.

- Add `@capacitor/core`, `@capacitor/cli`, `@capacitor/ios` and `@capacitor/android` to `web/`, then run `npx cap init Classy om.tryzarqa.classy --web-dir dist`.
  - The bundle ID `om.tryzarqa.classy` is a suggestion. Confirm it with the owner before the first upload, because it can't change afterwards.
- The native projects go in `web/ios/` and `web/android/`, committed to the repo.
- Build loop: `npm run build` → `npx cap sync` → open in Xcode or Android Studio, or build in CI.

### 1. API calls and sign-in (the main technical issue)
- **Today:** `web/src/lib/api.ts` calls `fetch('/api' + url)` on the same origin. The session is an HttpOnly cookie `unischedule` (`server/UniSchedule.Api/Program.cs`: `SameSite=Lax`, 60 days, sliding).
- **In the app:** the bundled pages run from `capacitor://localhost` (iOS) or `https://localhost` (Android), so `/api` would point at the phone, and the WebView treats the server's cookies as third-party (iOS blocks those).
- **Fix 1, API address:** add `VITE_API_BASE`, empty on the web and `https://schedule.tryzarqa.com` for native builds, and use it in `api.ts`: `fetch(API_BASE + '/api' + url)`.
- **Fix 2, cookies:** turn on Capacitor's native HTTP so requests and cookies go through the native networking stack, not the WebView:
  `capacitor.config.ts`: `plugins: { CapacitorHttp: { enabled: true }, CapacitorCookies: { enabled: true } }`.
  The cookie session then works unchanged, with no server auth change and no CORS. Check on a real device that sign-in survives closing and reopening the app.
- **Fallback, only if cookies misbehave on a device:** issue a bearer token on `/api/auth/verify` and send it as `Authorization`. That needs server work, so avoid it unless needed.

### 2. Store rules Classy must meet before review (important)
- **Account deletion (Apple 5.1.1(v)):** apps that create accounts must let people delete them in the app. Add **Menu → Delete my account**. It needs a confirmation and a new `DELETE /api/me` that removes the student; picks, slots, notes and reports already cascade. Owners are excluded.
- **User-generated content (Apple 1.2, Google UGC policy):** public notes need reporting (done), staff moderation (done), a way to **block a user** (add "Hide notes from this student" on a note, stored per student) and **terms** accepted before posting public notes (a short checkbox the first time).
- **Privacy policy URL:** required by both stores. Add a simple `/privacy` page in the web app covering:
  - **What's stored:** university ID, email, picks, slots and notes.
  - **Email:** codes are sent through Resend.
  - **No tracking**, and how to delete your account.
- **Minimum functionality (Apple 4.2):** a pure website wrapper gets rejected. The native features below (reminders) and offline use are what make it an app.

### 3. Native features (in this order)
1. **Class reminders:** the course page already has a placeholder "Remind me 15 min before" button (`web/src/student/Course.tsx`). Use `@capacitor/local-notifications`:
   - Schedule a notification 15 minutes before each upcoming session of that class. The sessions come from `buildSessions` in `web/src/lib/slots.ts`, and they're dated.
   - Re-schedule whenever the timetable loads.
   - Keep the choice per course in `store`.
   - On the web, leave the button as it is ("coming soon"), or hide it.
2. **Status bar and splash:** `@capacitor/status-bar` takes the page background colour (`--color-cream`), which already changes with Colours, so update it whenever the theme changes. `@capacitor/splash-screen` uses the cream background and the Classy mark.
3. **App icon and splash images:** make a 1024×1024 icon (navy background, cream mark, matching the timetable blocks on the sign-in page), then run `npx @capacitor/assets generate`.
4. **Push notifications** when a picked section changes (later): FCM/APNs through `@capacitor/push-notifications`. The server would store device tokens and send on section edits and uploads. Do this only after the first store release.
5. **Links:** tapping an email or link to schedule.tryzarqa.com opens the app (Universal Links / App Links). Optional, later.

### 4. Builds and accounts (the owner has a Windows laptop, no Mac)
- **The owner must do these** (they need their identity or a payment):
  - Join the **Apple Developer Program** ($99 a year) and create the app in **App Store Connect**.
  - Join **Google Play Console** ($25 once).
  - Create an **App Store Connect API key** and an **Android upload keystore**, and add them as GitHub secrets. Never paste them in chat.
- **iOS builds without a Mac:** a GitHub Actions `macos-latest` workflow `.github/workflows/mobile.yml` builds with Xcode and uploads to **TestFlight** using fastlane (`match` or the API key for signing).
- **Android:** the same workflow builds an `.aab` on `ubuntu-latest` and uploads it to the Play internal testing track. It can also be built locally in Android Studio on Windows.
- **Testing:** release to testers first (TestFlight and Play internal testing), then submit to the stores.

## Steps for the next session
1. Make the API address configurable (`VITE_API_BASE`) and set up Capacitor in `web/`, with native HTTP and cookies turned on.
2. Add `ios/` and `android/`, the icon and the splash screen. Run on the Android emulator; iOS has to be checked through TestFlight.
3. Add Delete my account, block user, terms before public notes, and the `/privacy` page, with server tests for delete and block.
4. Add local-notification reminders behind the existing button.
5. Add the `mobile.yml` workflow. When the owner has the accounts and secrets, push to TestFlight and Play internal testing.
6. Once testers are happy, write the store listings (screenshots, description, privacy answers) and submit.

## Keep in mind
- **The web app is live with real students.** Every server change must be additive, deploys keep the database backup step, and all tests must stay green (106 today).
- **Don't change sign-in for the web.** The native build only adds the API address and native HTTP.
- **The offline snapshot and the colour themes already work** inside the WebView. Just check them on a device.

## How to check it works
- **Web:** `dotnet test` (server) and `npx tsc -b`, `npx eslint .`, `npm run build` (web) still pass, and the web app works unchanged with an empty `VITE_API_BASE`.
- **Android emulator:**
  - sign in with an email code, close and reopen the app and stay signed in
  - Today, Week and Day views
  - add a slot
  - post and report a note
  - change the colours (the status bar follows)
  - turn on a reminder and see the notification fire, using a near-future test class
  - Delete my account
- **iOS through TestFlight:** the same checks on a real iPhone.

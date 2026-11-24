# Classy

## Decisions

- **No native apps.** The iOS and Android apps are cancelled. Classy is a website only, installable from the browser
  (web/public/manifest.json). Build features for the web: don't add native-only features or Capacitor plugins. The
  Capacitor setup (web/android, web/ios, capacitor.config.ts, the @capacitor packages) is left over from that plan.

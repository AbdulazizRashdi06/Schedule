import type { CapacitorConfig } from '@capacitor/cli'

// The iOS and Android apps: the built web app (dist/, built with `--mode native`) inside native shells.
const config: CapacitorConfig = {
  appId: 'om.tryzarqa.classy',
  appName: 'Classy',
  webDir: 'dist',
  backgroundColor: '#f8f3ea',
  plugins: {
    // Requests and cookies go through the phone's own networking, so the sign-in cookie works like on the web.
    CapacitorHttp: { enabled: true },
    CapacitorCookies: { enabled: true },
    SplashScreen: { launchShowDuration: 600, backgroundColor: '#f8f3ea', showSpinner: false },
    // Edge to edge: the page (and its colour theme) shows behind the status bar. Dark icons, since every background is light.
    SystemBars: { style: 'LIGHT', insetsHandling: 'css', initialViewportFitValueHint: 'cover' },
  },
}

export default config

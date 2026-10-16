import { Capacitor, SystemBars, SystemBarsStyle } from '@capacitor/core'

/** True inside the iOS and Android apps, false on the web. */
export const isNative = Capacitor.isNativePlatform()

/** App-only setup: dark status bar icons (every page background is light). */
export function setUpNative() {
  if (!isNative) return
  SystemBars.setStyle({ style: SystemBarsStyle.Light }).catch(() => {})
}

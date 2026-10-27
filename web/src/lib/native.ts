import { Capacitor, SystemBars, SystemBarsStyle } from '@capacitor/core'

/** True inside the iOS and Android apps, false on the web. */
export const isNative = Capacitor.isNativePlatform()

/** App-only setup: dark status bar icons (every page background is light). */
export function setUpNative() {
  if (!isNative) return
  SystemBars.setStyle({ style: SystemBarsStyle.Light }).catch(() => {})
}

/** Saves a picture as a download (the web fallback when the browser can't share files). */
export function saveImage(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

/** Whether "Share" can hand a picture to other apps here: the phone apps always, browsers when they share files. */
export function canShareImages() {
  if (isNative) return true
  try {
    return typeof navigator.canShare === 'function' && navigator.canShare({ files: [new File([new Blob()], 'x.png', { type: 'image/png' })] })
  } catch {
    return false
  }
}

/**
 * Opens the share sheet with a picture: the phone apps write it to the cache and share the file (their WebView has
 * no Web Share); browsers use Web Share. Returns false when the person closed the sheet.
 */
export async function shareImage(blob: Blob, name: string, title: string): Promise<boolean> {
  if (isNative) {
    const [{ Filesystem, Directory }, { Share }] = await Promise.all([import('@capacitor/filesystem'), import('@capacitor/share')])
    const data = await new Promise<string>((resolve, reject) => {
      const r = new FileReader()
      r.onload = () => resolve(String(r.result).split(',')[1] ?? '')
      r.onerror = () => reject(r.error)
      r.readAsDataURL(blob)
    })
    const file = await Filesystem.writeFile({ path: name, data, directory: Directory.Cache })
    try {
      await Share.share({ title, files: [file.uri] })
      return true
    } catch {
      return false
    }
  }
  try {
    await navigator.share({ title, files: [new File([blob], name, { type: 'image/png' })] })
    return true
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') return false
    throw e
  }
}

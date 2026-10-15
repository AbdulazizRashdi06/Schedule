import { Confirm } from './ui'

/** "Are you sure you want to log out?" with Cancel / Log out, used by every log-out button. */
export default function LogoutConfirm({ open, onClose, onLogout }: { open: boolean; onClose: () => void; onLogout: () => Promise<void> }) {
  return <Confirm open={open} onClose={onClose} title="Log out?" body="Are you sure you want to log out?" confirmLabel="Log out" danger onConfirm={onLogout} />
}

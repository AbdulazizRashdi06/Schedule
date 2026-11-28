import { API_BASE } from '../lib/api'

/** Downloads every class of the semester as a CSV in the upload's own columns (it uploads back unchanged). */
export default function ExportButton({ semesterId }: { semesterId: number }) {
  return (
    <a
      href={`${API_BASE}/api/admin/semesters/${semesterId}/export.csv`}
      download
      className="press inline-flex h-11 items-center gap-2 rounded-full px-4 text-[15px] font-extrabold shadow-[inset_0_0_0_2px_color-mix(in_srgb,var(--color-navy)_20%,transparent)] tint"
    >
      Export CSV
    </a>
  )
}

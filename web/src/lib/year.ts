/** Study year 0 is the foundation year; then Year 1–4; 5 is postgraduate (MSc/PhD). */
export const yearName = (year: number) => (year === 0 ? 'Foundation' : year >= 5 ? 'MSc/PhD' : `Year ${year}`)

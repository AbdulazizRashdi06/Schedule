# Uni Schedule frontend

React + TypeScript + Vite + Tailwind. Student routes live in `src/student/`, the dashboard in `src/admin/`, and shared code in `src/components/` and `src/lib/`.

```bash
npm install
npm run dev            # http://localhost:5173, proxies /api to the API on :5085
npx tsc -b && npx eslint .
```

See the [root README](../README.md) for running the API, and [docs/plan.md](../docs/plan.md) for the product plan.

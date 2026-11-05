# Synthetic lecturer message

`lecturer-message.png` is a browser-rendered test screenshot, created from this synthetic HTML. It contains no real lecturer or student data and is not a shipping UI asset:

```html
<main style="font:28px Arial;padding:36px;color:#111;background:#fff">
  <h1>CS201 — Midterm exam</h1>
  <p>Thursday, 12 November 2026</p>
  <p>10:00–12:00 · Room B105</p>
  <p>All sections. Bring your university ID.</p>
</main>
```

Rendered in Chromium at 800 × 450. The manual **Check planner AI** workflow verifies text and screenshot extraction against the production JSON schema through the existing private gateway; it never publishes an event.

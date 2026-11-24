# Programmatic motion graphics workflows

## What workflow should a beginner use for Claude Code and Remotion?

### Takeaway
Remotion is a direct, well-documented starting point for React-based video. Claude generates and modifies the source, while Remotion previews and renders the composition.

### Cited Findings
- The official workflow requires Node.js and a coding agent, then runs `npx create-video --yes --blank my-video`, `cd my-video`, `npx remotion skills add`, and `npm run dev`. Open another terminal in that folder and run `claude`; prompt the agent there. — [Remotion coding agent guide](https://www.remotion.dev/docs/ai/coding-agents)
- The official skills repository contains the agent guidance. — [Remotion skills](https://github.com/remotion-dev/skills)
- Remotion animations use `useCurrentFrame()` and can map frame intervals to property values through `interpolate()`. CSS transitions or other animations not driven by the current frame may flicker during render. — [Animating properties](https://www.remotion.dev/docs/animating-properties)
- Export uses `npx remotion render <entry-point|serve-url>? <composition-id> <output-location>`; dimensions, frame rate, duration, codec and other settings are configurable. Ask Claude to identify the registered composition ID before rendering. — [Remotion render CLI](https://www.remotion.dev/docs/cli/render)
- Remotion permits individuals and companies with up to three people free commercial use subject to its terms; larger companies need a company license. — [Remotion pricing](https://www.remotion.dev/docs/license/pricing)

### Inferences
- Use this choice for branded promos, typography, captions, charts, data-driven variants, and video built from reusable components; include brand files and reference images, then provide concrete visual and timing notes.
- Ask Claude to render draft frames and a low-resolution video, inspect stills at transitions and visual peaks, then watch the final output with audio; a successful render alone does not establish design quality.

### Gaps
- Exact production times and render performance depend on hardware, media, and the composition; no general benchmark established here.

## What other tools can Claude Code use?

### Takeaway
HTML-oriented HyperFrames and Python-oriented Manim provide alternative authoring styles; choose based on the type of motion and desired editable source.

### Cited Findings
- HeyGen's Apache-2.0 HyperFrames renders HTML/CSS/media and seekable animations to MP4; it uses headless Chrome and FFmpeg. It supports GSAP, CSS, Lottie, Three.js, Anime.js and Web Animations API adapters, and requires Node.js 22+ and FFmpeg. — [HyperFrames project](https://github.com/heygen-com/hyperframes)
- Its documented Claude plugin installation is `claude plugin marketplace add heygen-com/hyperframes`, then `claude plugin install hyperframes@hyperframes`. The CLI can initialize, preview, and render: `npx hyperframes init my-video`, `cd my-video`, `npx hyperframes preview`, `npx hyperframes render`. — [HyperFrames project](https://github.com/heygen-com/hyperframes)
- The project includes a catalog of editable transitions, kinetic typography, captions, charts, 3D and other components, plus a [showcase](https://hyperframes.heygen.com/showcase) of examples. — [HyperFrames project](https://github.com/heygen-com/hyperframes)
- Manim Community generates animations from Python scenes. The quickstart demonstrates `manim -pql main.py CreateCircle`, producing and playing an MP4; it covers transformations and geometric animation. — [Manim quickstart](https://docs.manim.community/en/stable/tutorials/quickstart.html)
- Third-party `/manimate` is a skill whose author says it creates scene code for Manim, renders with Cairo, stitches using FFmpeg, and optionally validates layout. Its installation command is `npx skills add bassimeledath/manimate`; it requires ManimCE, Python, FFmpeg, and optionally LaTeX. Treat the capabilities as project author claims. — [manimate repository](https://github.com/bassimeledath/manimate)
- Example tutorial for Claude + Remotion, with setup and a demo: [Claude Code Can Make Videos Now](https://www.youtube.com/watch?v=Vu_XOKKgJtA). A separate collection demonstrates 36 styles with tool mappings and prompts: [36 Motion Graphic Styles](https://mndr.ai/blog/motion-graphics-with-claude/); its author describes their own tests rather than an independent evaluation.
- Research on generated Manim animations identifies potential visual defects such as overlap, misalignment and broken continuity. — [See Before You Code](https://arxiv.org/abs/2605.15585)

### Inferences
- HyperFrames fits users comfortable with HTML/GSAP; Remotion fits React and templated productions; Manim fits math, diagrams and educational visualization. These are overlapping tools, not exclusive categories.
- Review third-party skill source and dependencies before installation, and keep originals and project backups. The output remains source code and rendered video, so creative review and iteration remain necessary.

### Gaps
- No reliable independent head-to-head quality or speed study across these tools was identified; vendor comparisons are not neutral benchmarks.

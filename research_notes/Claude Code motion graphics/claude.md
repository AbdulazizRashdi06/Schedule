# Claude Code capabilities and setup

## How do Claude Code and motion graphics fit together?

### Takeaway
Claude Code is an agent that edits files and runs commands; a motion framework executes that code and renders the video. Remotion's official setup explicitly supports Claude Code.

### Cited Findings
- Remotion's guide requires Node.js and a coding agent and instructs users to run `npx create-video --yes --blank my-video`, `cd my-video`, `npx remotion skills add`, and `npm run dev`; launch `claude` in a separate terminal in the project. — [Remotion coding-agent guide](https://www.remotion.dev/docs/ai/coding-agents)
- Claude Code CLI requires a supported subscription, Console account, or supported cloud provider. Its Windows PowerShell installer is `irm https://claude.ai/install.ps1 | iex`. Git for Windows is recommended for Bash; otherwise PowerShell is used. — [Claude Code quickstart](https://code.claude.com/docs/en/quickstart)
- Claude Code's Desktop Code tab shares its underlying engine, provides project selection, visual diffs and web preview; on Windows local desktop sessions require Git for Windows. — [Desktop documentation](https://code.claude.com/docs/en/desktop)

### Inferences
- Begin with a short code-generated animation and iterate through preview, snapshots, and render, rather than assuming a text prompt guarantees finished creative quality.

### Gaps
- No direct testing was performed on the user's computer.

## What do skills, project instructions and MCP contribute?

### Takeaway
Skills provide reusable workflow and domain guidance; MCP connects external tools. They are optional complements, not the renderer.

### Cited Findings
- Project skills live in `.claude/skills/<name>/SKILL.md`; personal skills live in `~/.claude/skills/<name>/SKILL.md`. Claude can select relevant skills or users invoke them by slash command. — [Claude Code skills](https://code.claude.com/docs/en/skills)
- `CLAUDE.md` stores persistent project instructions, while skills hold on-demand guidance and MCP provides external tools and services. — [Claude Code extensions overview](https://code.claude.com/docs/en/features-overview)
- Remotion's hosted documentation MCP server shut down October 26, 2026. Remotion recommends `npx remotion skills add` and `/remotion-docs`, restarting the agent if necessary. — [Remotion MCP shutdown and migration](https://www.remotion.dev/docs/ai/mcp)
- MCP configuration can be local, project-scoped or user-scoped; project server approval and workspace trust matter. — [Claude Code MCP](https://code.claude.com/docs/en/mcp)
- The community motion graphics skill at `haidrrrry/claude-remotion-skill` describes Remotion frame inspection and design guidance; it is independently maintained, not Anthropic official. — [Skill catalog entry](https://skills.sgomez.dev/en/s/remotion-motion-graphics)

### Inferences
- A practical CLAUDE.md can record brand colors, type, output dimensions, media paths and verification steps, while a custom skill can codify repeatable storyboard-to-render procedure.

### Gaps
- Quality and security of community skills was not independently tested; inspect source and dependencies before installation.

## How can you provide references and verify output?

### Takeaway
Image references and an inspectable preview offer useful feedback, but humans should also watch and listen to the rendered animation.

### Cited Findings
- In CLI, `Alt+V` pastes an image from clipboard on Windows and WSL. — [Interactive mode](https://code.claude.com/docs/en/interactive-mode)
- Claude in Chrome can test a local web app and save screenshots; its browser extension uses a real Chrome window. — [Claude Code and Chrome](https://code.claude.com/docs/en/chrome)
- Remotion provides a Studio preview via `npm run dev`. — [Remotion coding-agent guide](https://www.remotion.dev/docs/ai/coding-agents)
- Claude Code security docs advise reviewing commands and explain permission and sandbox boundaries. — [Claude Code security](https://code.claude.com/docs/en/security)

### Inferences
- Request representative stills at scene starts, transitions and endings, check overlap, text clipping and safe areas, and watch and listen to the final full-speed export. Still frames alone cannot assess timing, smoothness, sound or motion continuity.

### Gaps
- Image inspection is not a guarantee of visual or audio quality, and tool support differs by environment.

## What are the costs and practical limits?

### Takeaway
Plan consumption and API usage are different, and rendering and media tools may incur additional costs or license obligations.

### Cited Findings
- `/usage` shows token and plan usage; for Pro, Max and Ultra subscription users, session cost does not represent their subscription billing. API token pricing and subscription plans are distinct. — [Claude Code costs](https://code.claude.com/docs/en/costs); [Claude pricing](https://claude.com/pricing)
- Native Claude Code runs tools under the user's permissions; approved shell commands can affect files beyond the project boundary, unless further restricted. — [Claude Code security](https://code.claude.com/docs/en/security)
- Context, code and conversations are sent for model processing in local sessions; see current applicable commercial and consumer data terms. — [Claude Desktop](https://code.claude.com/docs/en/desktop); [Claude Code security and data](https://code.claude.com/docs/en/security)

### Inferences
- Rendering time, third-party music and footage, licensed software and optional generation APIs are separate considerations from Claude subscription access.

### Gaps
- Usage varies widely; no universal cost per finished video can be supported.

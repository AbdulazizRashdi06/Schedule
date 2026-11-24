# Make motion graphics with Claude Code

Claude Code can create motion graphics by writing and revising code, scripts, and project files; a separate application or framework previews and renders the result. **Remotion is the clearest starting point for a new, code-based video** because its official documentation provides a Claude-compatible setup and motion-specific skills. For editable After Effects projects, Claude can write ExtendScript and expressions or use a third-party bridge; Blender and Cinema 4D offer routes for 3D work. These approaches differ in their deliverables, setup, creative control, and security. The material below is a practical map of available workflows, not a claim to cover every project on the internet. Findings and links were researched on **31 October 2026**; third-party integrations were not installed or tested for this report.

## Start with Remotion for code-driven video

Remotion builds video compositions with React and renders them to video. Claude Code edits the project and can use instructions from Remotion's official skills, while Remotion Studio gives you a timeline and browser preview. This is a useful fit for kinetic typography, explainers, animated charts, branded promos, captions, and template-based variations. Remotion's [coding-agent guide](https://www.remotion.dev/docs/ai/coding-agents) documents the following starting workflow.

On Windows, install a current [Node.js](https://nodejs.org/) release and [Claude Code](https://code.claude.com/docs/en/quickstart), and sign in using a supported plan or account. Anthropic's PowerShell installer is shown below; review an installation command before running it. Git for Windows is recommended, and the Claude desktop app's local Windows sessions require it ([Claude Code quickstart](https://code.claude.com/docs/en/quickstart), [desktop documentation](https://code.claude.com/docs/en/desktop)).

```powershell
irm https://claude.ai/install.ps1 | iex
```

Create the video project in a folder where you want to keep your work. The following commands create a blank Remotion project, add the official skills, and start the Studio preview:

```powershell
npx create-video --yes --blank my-video
cd my-video
npx remotion skills add
npm run dev
```

Open a second terminal in `my-video`, run `claude`, and describe the animation. If newly installed skills do not appear, restart the agent. **Do not follow old instructions to use Remotion's hosted documentation MCP:** its documentation states that server shut down on 26 October 2026; use `npx remotion skills add` and the `/remotion-docs` skill instead ([migration notice](https://www.remotion.dev/docs/ai/mcp), [official skills repository](https://github.com/remotion-dev/skills)).

A useful first prompt is:

```text
Create an 8-second 1920x1080 motion graphic at 30 fps in Remotion.
Use the exact text “Ideas in motion”, followed by “Designed to move”.
Style: dark navy background, warm off-white type, one vivid orange accent;
clean editorial typography with generous margins. Animate the first line in
over 24 frames, hold for readability, transition to the second line, then
finish on a clean end frame. Use frame-driven animation and smooth easing.
Keep the text editable, avoid external assets, and register a composition.
Show me how to preview it, render representative frames and export an MP4.
Check for clipping and overlapping text before finishing.
```

Remotion's `useCurrentFrame()` and `interpolate()` make motion a function of the current frame, which is important for deterministic rendering; ordinary CSS transitions can flicker in rendered video ([animation documentation](https://www.remotion.dev/docs/animating-properties)). For export, identify the registered composition and use the documented `npx remotion render` command, supplying its composition ID and output path as appropriate ([render CLI](https://www.remotion.dev/docs/cli/render)). Ask Claude to inspect the project's actual configuration rather than guessing an ID. Remotion permits free commercial use for individuals and companies of up to three people under its terms; larger companies should check the current license before production ([pricing and license](https://www.remotion.dev/docs/license/pricing)).

## Choose the tool by the deliverable

| Desired result | Route | Main consideration |
| --- | --- | --- |
| Reusable, data-driven videos and branded variants | [Remotion](https://www.remotion.dev/docs/ai/coding-agents) with React | Strong documented agent workflow; delivered source is a code project, not an After Effects project. |
| HTML and web-animation-style video | [HyperFrames](https://github.com/heygen-com/hyperframes) | HTML/CSS-oriented framework with a Claude plugin; its project documents Node.js 22+ and FFmpeg requirements. |
| Mathematical and educational animation | [Manim Community](https://docs.manim.community/en/stable/tutorials/quickstart.html) | Python scene code works well for geometry, equations and diagrams; rendering dependencies and optional LaTeX require setup. |
| Native editable After Effects layers | Claude-written ExtendScript and expressions; optionally a community MCP bridge | Best when the final project must be worked on inside After Effects. Bridges need installation and permissions and were not tested here. |
| 3D scenes and product motion | Blender Python or MCP; Cinema 4D MCP | Native scene editing and rendering, with additional setup and significant script-execution permissions. |

For HyperFrames, its maintainers document `claude plugin marketplace add heygen-com/hyperframes` followed by `claude plugin install hyperframes@hyperframes`. Its CLI offers `npx hyperframes init my-video`, `npx hyperframes preview`, and `npx hyperframes render`; see the [project repository](https://github.com/heygen-com/hyperframes) and [showcase](https://hyperframes.heygen.com/showcase). These are project-provided claims and examples, not a comparative benchmark. For Manim, the [quickstart](https://docs.manim.community/en/stable/tutorials/quickstart.html) shows a small Python scene and `manim -pql main.py CreateCircle`; the independent [manimate skill](https://github.com/bassimeledath/manimate) describes a Claude-oriented authoring and rendering workflow. Neither alternative was tested for this report.

## Use native creative applications when editability matters

In **After Effects**, start with the least complicated route: ask Claude to create a reviewed `.jsx` script and run it on a duplicate project. Adobe documents ExtendScript for automating project operations, including the menu path **File > Scripts > Run Script File** and Windows `afterfx -r C:\script_path\example_script.jsx`; access to write files and communicate over the network is disabled by default ([Adobe scripting guide](https://helpx.adobe.com/after-effects/desktop/automate-in-after-effects/automate-animation/scripts.html)). Expressions control property values, while scripts perform application operations ([Adobe expression basics](https://helpx.adobe.com/after-effects/desktop/work-with-expressions/expression-basics/expression-language.html)). Adobe also documents command-line rendering through `aerender`, including project, composition, and output arguments ([automated rendering](https://helpx.adobe.com/after-effects/desktop/render-and-export/automate-rendering/automated-rendering-network-rendering.html)).

For repeated inspection and iteration, the independent [after-effects-mcp](https://github.com/23d1/after-effects-mcp) project describes a Claude Code bridge for compositions, layers, keyframes, expressions and rendering; [mograph-mcp](https://github.com/coltranesx/mograph-mcp) describes a separate CEP-panel and WebSocket approach. Their compatibility and feature lists are maintainer claims, not Adobe or Anthropic guarantees. Check your application version and inspect each repository and its permissions before installation. A brief that explicitly asks for **editable native layers, named compositions, reversible operations, a duplicate project, and a draft render** gives the agent a better-defined target.

For **Blender**, Claude can write Python scripts and Blender can render a saved scene from the command line ([Blender rendering documentation](https://docs.blender.org/manual/en/latest/advanced/command_line/render.html)). Blender's own [experimental MCP integration](https://www.blender.org/lab/mcp-server/) requires Blender 5.1 or later, an add-on, a client and a server. Its documentation warns that LLM-generated code executes without safeguards against destructive file operations or remote transmission; consider a virtual machine or a system without sensitive data. [MCP for Blender](https://github.com/ahujasid/mcp-for-blender) and [claude-blender](https://github.com/minihellboy/claude-blender) are separate community projects with different implementations, not the same official experiment. An independent [Claude Code and Blender demonstration](https://www.youtube.com/watch?v=f2yPoquP77g) shows an iterative product-animation workflow, but it is an example rather than a benchmark.

For **Cinema 4D**, Maxon documents an MCP server in the full application, with Cinema 4D running before a compatible client connects. Its FAQ excludes Cinema 4D Lite, Team Render, c4dpy and Commandline; Claude Code compatibility should be checked against the specific client and configuration rather than assumed ([server preferences](https://help.maxon.net/c4d/2026/en-us/Content/html/MCPADAPTERPREFS.html), [Maxon FAQ](https://help.maxon.net/c4d/en-us/Content/html/MCP_Server_FAQ.html)). Python access expands what a client can do and deserves the same care as running local scripts.

## Treat direction and review as part of the workflow

Give Claude a production brief rather than only a style adjective: include aspect ratio, resolution, frame rate, duration, exact on-screen copy, brand colors and font files, visual references, timing, intended audience, audio, safe margins, delivery format, and whether the source must remain editable. Break longer pieces into a storyboard and a few short scenes; then ask for a low-resolution preview, targeted changes, and final export. A project `CLAUDE.md` can hold recurring brand and output requirements, while a skill holds reusable, on-demand process instructions ([Claude Code extensions](https://code.claude.com/docs/en/features-overview), [skills](https://code.claude.com/docs/en/skills)). On Windows and WSL, Claude Code documents `Alt+V` for pasting an image reference from the clipboard ([interactive mode](https://code.claude.com/docs/en/interactive-mode)).

Review a contact sheet or representative frames at scene starts, transitions, peaks and endings for clipping, legibility, alignment, and safe areas. Then **watch the complete video at normal speed and listen to the audio**: still images cannot establish pacing, motion continuity, timing or sound quality. Claude's Chrome integration can help inspect a local web preview and screenshots when available ([Claude Code and Chrome](https://code.claude.com/docs/en/chrome)). Generated animation research also identifies visual problems including overlap and continuity defects, reinforcing the need for review ([See Before You Code](https://arxiv.org/abs/2605.15585)).

Claude Code can run commands under your user permissions. Review requested commands and third-party skills or MCP servers, work with backups, and avoid granting unnecessary file, network, or arbitrary code access ([Claude Code security](https://code.claude.com/docs/en/security)). Costs depend on your Claude plan or API usage and the task: `/usage` reports usage, while subscription and API billing differ. Rendering, creative software, fonts, footage, music and generation APIs can add separate costs ([Claude Code costs](https://code.claude.com/docs/en/costs), [Claude pricing](https://claude.com/pricing)). No reliable universal cost or independent head-to-head speed and quality benchmark was established in this research.

## Learning resources and next step

Start with Remotion's [official coding-agent guide](https://www.remotion.dev/docs/ai/coding-agents) and [animation fundamentals](https://www.remotion.dev/docs/animating-properties), then explore the [HyperFrames showcase](https://hyperframes.heygen.com/showcase) for editable examples. For community inspiration, see the independent [Claude and Remotion tutorial](https://www.youtube.com/watch?v=Vu_XOKKgJtA) and the creator's [36 motion graphic styles](https://mndr.ai/blog/motion-graphics-with-claude/); treat their results as demonstrations rather than guarantees. If you already work in After Effects or Blender, begin with a small duplicate project and a single scripted animation so that the resulting source remains understandable and editable.

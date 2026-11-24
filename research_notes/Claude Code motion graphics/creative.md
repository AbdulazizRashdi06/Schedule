# Creative applications and motion production

## How can Claude Code operate After Effects?

### Takeaway
Claude can generate native After Effects scripts and expressions, and independent MCP bridges can expose an open After Effects project to tool calls. These are third-party bridges, not an Adobe claim of native Claude integration.

### Cited Findings
- Adobe says After Effects scripts are ExtendScript (.jsx/.jsxbin), can automate repetitive project operations, and can be run using File > Scripts > Run Script File or Windows `afterfx -r C:\script_path\example_script.jsx` in the existing instance. Script file/network access is disabled by default. [Adobe scripts](https://helpx.adobe.com/after-effects/desktop/automate-in-after-effects/automate-animation/scripts.html)
- Expressions define a property’s value; scripts tell the application to perform operations. Expressions automate animation and connect layer properties. [Adobe expressions](https://helpx.adobe.com/after-effects/desktop/work-with-expressions/expression-basics/expression-language.html)
- Adobe supports automated command-line rendering via `aerender -project <path> -comp <name> -output <path>`; verify actual output templates/codecs for the installation. [Adobe aerender](https://helpx.adobe.com/after-effects/desktop/render-and-export/automate-rendering/automated-rendering-network-rendering.html)
- [23d1/after-effects-mcp](https://github.com/23d1/after-effects-mcp) is a third-party bridge whose maintainer reports tests on AE 2026 on Windows/macOS; it builds JSX and dispatches to AE, exposes comp/layer/keyframe/expression/effect and frame/render tools, requires Node 18+, running AE and the file/network scripting preference. Its README documents build, `claude mcp add --scope user after-effects -- node <absolute-path>/dist/index.js`, `claude mcp list`, and `npm run doctor`. Project claims, not independently tested here.
- [mograph-mcp](https://github.com/coltranesx/mograph-mcp) is a different third-party architecture: a Node controller and CEP panel communicate via WebSocket and ExtendScript; its documentation lists AE 2024–2026, Node 18+, FFmpeg, panel deployment, visual review and examples. The authors link a title sequence [demo](https://www.youtube.com/). Treat capabilities and claimed production quality as project claims, not a performance guarantee.
- Adobe documents scripting access for adding supported properties to Essential Graphics and exporting motion graphics templates. [Adobe scripting access](https://helpx.adobe.com/after-effects/desktop/automate-in-after-effects/automate-animation/scripts.html)

### Inferences
- Script-only workflow is a useful low-integration starting point: generate a JSX in a project folder, review it and run on a duplicate project; request native editable layers and reversible undo groups. MCP is useful for repeated iteration when scene inspection and feedback are needed.
- A prompt should specify canvas, duration/fps, exact copy, typography, safe margins, reference, timing in frames, easing, delivery and editable project; inspect frames at meaningful moments and play back a low-resolution preview to judge motion before final render.

### Gaps
- Neither community bridge was installed or verified against this user's version of After Effects. An example project linking a demo is not proof of universal quality or compatibility.

## How can Blender and Cinema 4D be used?

### Takeaway
Blender can be driven through Python and both experimental official and community MCP bridges. Maxon's full Cinema 4D supplies a built-in MCP server; client-specific compatibility and security settings should be checked.

### Cited Findings
- [Blender Lab MCP](https://www.blender.org/lab/mcp-server/) is an experimental Blender-provided integration; Blender has no built-in LLM connectivity. The page specifies Blender 5.1+, an add-on, client, and MCP server; the project explicitly warns LLM-generated code runs with no safeguards against file deletion or remote transmission, recommending a VM or a system without sensitive information. Its own tested scene analysis missed a modifier in one initial result.
- [MCP for Blender](https://github.com/ahujasid/mcp-for-blender) is a separate third-party project, not affiliated with Blender; its README identifies `mcp-for-blender` as the current package and describes Windows setup, Blender add-on, and visual feedback. [Tool documentation](https://www.mcp-for-blender.com/docs/tools/look) describes camera, multi-angle and animation strip previews. Avoid confusing it with Blender Lab's official experiment or assuming identical security.
- [claude-blender](https://github.com/minihellboy/claude-blender) is another small third-party Claude Code project; its README claims inline viewport screenshots, draft renders, animation rendering and multi-angle views with example files. Maintainer claims have not been tested here.
- Blender can render animation saved in a blend file from the command line. [Blender command line rendering](https://docs.blender.org/manual/en/latest/advanced/command_line/render.html)
- [Maxon MCP documentation](https://help.maxon.net/c4d/2026/en-us/Content/html/MCPADAPTERPREFS.html) says Cinema 4D's MCP server accepts compatible command-line clients; Cinema 4D must be started first. Its [FAQ](https://help.maxon.net/c4d/en-us/Content/html/MCP_Server_FAQ.html) says it is built into the full application, not Cinema 4D Lite, Team Render, c4dpy or Commandline. Unlisted clients use copied configuration and are not necessarily officially tested. Python access gives more power and requires security judgment. The cloud client may send tool results to its provider.
- An independent creator describes a [Claude Code + Blender MCP animation workflow video](https://www.youtube.com/watch?v=f2yPoquP77g), including reference-based product animation and iterative camera fixes. This is a creator demonstration rather than a benchmark.

### Inferences
- Use Blender for editable 3D motion, product turns, cameras and procedural scenes; inspect contact sheets, camera clipping and full animation playback. Visual verification and creative direction remain human responsibilities.
- Select one known bridge, inspect its source and permissions, prefer a sandbox and disposable project, and do not expose an MCP server or its token to untrusted networks. Back up work before granting editing or arbitrary script execution.

### Gaps
- Live tests of Claude Code with the above projects were not performed. Maxon's available documentation supports MCP clients generally but doesn't establish that every Claude Code version has been validated by Maxon.

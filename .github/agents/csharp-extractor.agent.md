---
name: csharp-extractor
description: Inspect a C# SDK and return source-backed extraction evidence using the Markdown agent definition.
tools: [read, search, edit, execute]
---

# C# extractor entry point

Read `.agents/agents/csharp-extractor.agent.md` in full and follow it as the
agent definition. Then read every skill it lists, starting with
`.agents/skills/extract-csharp/SKILL.md`.

Use that definition's inputs, output report, workflow, boundaries, and
orchestrator handoff. This file exists only for hosts that discover agents in
`.github/agents`; do not maintain a second independent workflow here.

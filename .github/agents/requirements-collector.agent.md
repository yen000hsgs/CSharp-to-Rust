---
name: requirements-collector
description: Derive testable migration requirements from extraction evidence using the Markdown agent definition.
tools: [read, search, edit, execute]
---

# Requirements collector entry point

Read `.agents/agents/requirements-collector.agent.md` in full and follow it as
the agent definition. Then read every skill it lists, starting with
`.agents/skills/collect-requirements/SKILL.md`.

Use that definition's inputs, output report, workflow, boundaries, and
orchestrator handoff. This file exists only for hosts that discover agents in
`.github/agents`; do not maintain a second independent workflow here.

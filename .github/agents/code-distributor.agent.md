---
name: code-distributor
description: Partition collected requirements into dependency-aware feature work packages for the orchestrator.
tools: [read, search, edit, execute]
---

# Code distributor entry point

Read `.agents/agents/code-distributor.agent.md` in full and follow it as the
agent definition. Then read every skill it lists, starting with
`.agents/skills/distribute-code/SKILL.md`.

Use that definition's inputs, output artifact, workflow, boundaries, and
orchestrator handoff. This file exists only for hosts that discover agents in
`.github/agents`; do not maintain a second independent workflow here.

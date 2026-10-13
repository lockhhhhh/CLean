# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Layout: single-context

This repo is **single-context**: one repo, one shared domain glossary at the root, one place for architecture decisions.

- **`CONTEXT.md`** at the repo root — the domain glossary shared by the whole repo: the terms this project uses, what they mean, and the synonyms to avoid.
- **`docs/adr/`** — architecture decisions, one file per decision.

There is no `CONTEXT-MAP.md` here and no per-subproject glossary, because the repo has no context split to map. Those only earn their place in a genuinely multi-package repo; if this one ever grows a split, the map file is where that gets recorded.

If some skill's own wording says `GLOSSARY.md` / `GLOSSARY-MAP.md`, read it as this repo's `CONTEXT.md`: the layout decision recorded here wins.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root.
- **`docs/adr/`**: read ADRs that touch the area you're about to work in.

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) creates them lazily when terms or decisions actually get resolved.

## File structure

```
/
├── CONTEXT.md          ← 全仓库共用的一份域词汇表（按需惰性创建，尚未存在）
├── docs/
│   ├── adr/            ← 架构决定，一条一文件（按需惰性创建，尚未存在）
│   └── agents/         ← 本目录：技能配置（issue tracker / 标签词汇 / 本文件）
├── src/                ← 外壳源码：Launcher.cs、GuiLauncher.cs、GuiPages.cs、GuiAnims.cs、Build.ps1
└── *.ps1               ← 10 个清理脚本，作为资源内嵌进单文件 EXE
```

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `CONTEXT.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0007 (event-sourced orders), but worth reopening because…_

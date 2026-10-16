# 005 — 结果卡进入淡入

- **Status**: DROPPED (verdict below)
- **Commit**: ce85e78
- **Severity**: MEDIUM
- **Category**: 遗漏的机会（状态切换）
- **Estimated scope**: 1 个文件（`src/GuiAnims.cs` 之外）—— 实为 `src/GuiAnims.cs` **不可动**，
  因此本计划只改调用侧的可见性/时机；`SummaryView` 的 `OnPaint` 内加淡入属于改动 `GuiAnims.cs`，
  **见「Boundaries」——本计划降级为「不改自绘，只改呈现时机」**，若无法在不碰 `GuiAnims.cs` 的前提下
  达成，就**放弃本计划并报告**。

## Problem

`SummaryView` 是「上次扫描结果」卡片，`ShowResult` 时整块内容瞬时出现：

```csharp
// src/GuiAnims.cs:489-520 — ShowResult（节选）
internal void ShowResult(string state, string meta, string counts, string key, Color tone)
{
    State = state; Meta = meta; Counts = counts; Key = key; Tone = tone;
    Hint = "";
    Publish();
    Invalidate();
}
```

而 `src/GuiAnims.cs:460-567` 属于**用户明确划出的"既有动画基础"**：`AnimBase` 与十个 motif
一字不改。`SummaryView` 与它们同在 `GuiAnims.cs`，且它本身是自绘控件。

## Target

**在不改 `GuiAnims.cs` 任何一行的前提下**，让结果卡的出现有个 210ms 的交代。可行的做法只有一条：
在**调用侧**控制「先空一帧、再显示」。检查 `src/GuiPages.cs` 里所有设 `Summary.ShowResult(...)` 的位置，
确认 `Summary` 是否已经因为「本页第一次扫描」而本来就不可见；若是，则本计划的收益不存在，
**应当报「no-op，不值得做」而不是硬凑一个动画**。

- 若 `Summary` 由 `Visible = false` → `true` 切换：给它套一个 001 的 `Channel`，
  在 owning page 的 `OnPaint` 之外**无法**做淡入（WinForms 的 `Control` 没有 opacity）。
  → 结论：**WinForms 里给一个不透明自绘控件做淡入必须改它的 `OnPaint`**，
  而它就在不可动的 `GuiAnims.cs` 里。
- 唯一替代：切换时给 `Summary` 换成一个 001 提供的**包装控件**（`internal sealed class FadeHost : Control`，
  内含子控件、`OnPaint` 里用 `Control.DrawToBitmap` + `ColorMatrix` 画子控件快照，透明度渐变）。
  代价：多一次位图抓取、多一层控件、`SummaryView` 的任何自身重绘都要重新抓图。

## Repo conventions to follow

- 见 `plans/001-motion-tokens.md`（`Motion` / `Channel`）。
- `SummaryView` 的使用点全部在 `src/GuiPages.cs`（`ScanPage` / `ScriptPage` 系），
  先 `grep -n "Summary" src/GuiPages.cs` 确认调用面。

## Steps

1. 先做**判定**（不改代码）：`grep -n "Summary" src/GuiPages.cs` + 读上下文，确认
   `ShowResult` 被调用时 `Summary.Visible` 是否发生 false→true 变化、卡片是否在同一帧里
   从空变成有内容。把判定结果写进本计划的 Status 备注。
2. **若判定为「本来就在可见状态下换内容」**（最可能）：本计划的正确结论是
   *报告 no-op*：在 WinForms 里给这一个自绘控件加淡入必须侵入 `GuiAnims.cs`，
   违反用户边界，收益（一张平时不看的卡片）不值这个代价。**停下，把结论写进 `plans/README.md`，
   状态改为 `DROPPED`。**
3. **若判定为「false→true 且值得」**：实现 `FadeHost` 包装（约 50 行），放在 `src/Motion.cs` 里
   （和页面的 `PageFade` 区分开，命名 `SoftReveal`），并只在 `ScanPage` 的结果卡上启用。

## Boundaries

- **绝不修改 `src/GuiAnims.cs` 的任何一行**（用户边界，优先级高于本计划）。
- 不要为了做这个动画去重写 `SummaryView`，也不要把它的绘制逻辑复制到别处。
- 不要给结果卡加位移。

## Verification

- **Mechanical**：`git diff --stat src/GuiAnims.cs` 必须为空（这是本计划最重要的一条机械检查）；
  `.\src\Build.ps1 -Gui` 通过；`.\src\Tests.ps1` 全绿。
- **Feel check**：只有在 Steps 3 被触发时才需要——扫描完成后卡片应 210ms 淡入，不闪、不位移。
- **Done when**：要么 `GuiAnims.cs` 零改动且卡片有 210ms 淡入，要么本计划标为 `DROPPED` 并附判定理由。

## 判定结果（2026-10-16）：DROPPED

探针（`.scratch/probe/Probe.cs`，反射驱动真实 `MainForm`）实测：

    summary: visible=True parentVisible=True size=1008x176
    summary after ShowResult: visible=True size=1008x176

`SummaryView` 从一开始就 `Visible=true`、尺寸固定（`_tlp` 的第 4 行），
`Reset()` / `ShowResult()` 只替换它**内部**的文字与色调。本计划 Step 1 要找的
"从隐藏变成出现"的边界不存在，因此没有可以加淡入的过渡。

按本计划自己的收尾条件，标 DROPPED 并附此判定；`src/GuiAnims.cs` 保持**零改动**
（`git diff --stat src/GuiAnims.cs` 为空，满足本计划最重要的那条机械检查）。

如果以后想让"结果换新"有个交代，正确做法是 002 那种在控件上叠一层快照淡出，
但那必须改 `src/GuiAnims.cs` —— 超出用户边界，不做。
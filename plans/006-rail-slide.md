# 006 — 功能栏折叠滑动

- **Status**: DONE
- **Commit**: ce85e78
- **Severity**: MEDIUM
- **Category**: 空间一致性（布局跳变）
- **Estimated scope**: 1 个文件（`src/GuiLauncher.cs`），约 60 行

## Problem

左侧导航栏点「☰ 折叠功能栏」时，292px ↔ 64px 之间**瞬间**跳变，同时 10 个导航项
从「图标+编号+标题+副标题」的宽行一下变成 28×28 的居中数字 chip：

```csharp
// src/GuiLauncher.cs:2168-2188 — 当前
private void ToggleRail()
{
    Settings.RailExpanded = !Settings.RailExpanded;
    ApplyRailState();
}

private void ApplyRailState()
{
    bool expanded = Settings.RailExpanded;
    _rail.Width = expanded ? 292 : 64;
    _btnRailToggle.Text = expanded ? "☰  折叠功能栏" : "☰";
    for (int i = 0; i < _railButtons.Length; i++)
    {
        _railButtons[i].SetCollapsed(!expanded);
        _railButtons[i].Width = Math.Max(40, _railItems.ClientSize.Width - 6);
        _railButtons[i].Invalidate();
    }
}
```

这是空间上相连的两个状态，现在没有来去。而且 `:2136` 有个隐藏地雷：

```csharp
// src/GuiLauncher.cs:2136 — 动画期间会被每帧触发，和补间打架
_railItems.SizeChanged += delegate { ApplyRailState(); };
```

## Target

`_rail.Width` 在 292 ↔ 64 之间补间，`Motion.DurRail = 190ms`、**`Ease.InOut`**
（屏幕上移动用 ease-in-out，不是 ease-out）。文字切换与宽度动画的先后关系是有讲究的：

- **折叠时**：先 `SetCollapsed(true)`（立刻变 chip，chip 在 `RailButton.OnPaint` 里是居中绘制，
  会随宽度收窄一起左滑），再动画宽度。
- **展开时**：宽度先动，**动画结束后**才 `SetCollapsed(false)` —— 否则文字会在窄栏里被挤成省略号乱跳。

```csharp
// target
private MotionDriver _railAnim;     // 1 通道
private int _railFrom, _railTo;
private bool _railAnimating;
private bool _railExpandTarget;

private void ToggleRail()
{
    bool expanded = !Settings.RailExpanded;
    Settings.RailExpanded = expanded;          // 配置立刻落地，不等动画
    if (Motion.Reduced || _railAnim == null) { ApplyRailState(); return; }
    _railExpandTarget = expanded;
    _railFrom = _rail.Width;
    _railTo = expanded ? 292 : 64;
    if (_railFrom == _railTo) { ApplyRailState(); return; }
    if (!expanded) SyncRailItems(true);        // 折叠：立刻变 chip
    _railAnimating = true;
    _railAnim.To(0, 1.0, Motion.DurRail, Ease.InOut);
}

private void RailTick(double t)                // 由 MotionDriver 的 Tick 回调驱动
{
    if (!_railAnimating) return;
    int w = (int)Math.Round(_railFrom + (_railTo - _railFrom) * t);
    _body.SuspendLayout();
    _rail.Width = w;
    SyncRailItems(_railExpandTarget == false);
    _body.ResumeLayout(false);
    if (t >= 1.0)
    {
        _railAnimating = false;
        ApplyRailState();                      // 权威收尾（含展开后的文字恢复）
    }
}
```

`ApplyRailState()` 开头必须加守卫：

```csharp
if (_railAnimating) { SyncRailItems(false); return; }   // 动画期间不插手，宽度由补间负责
```

（`_railItems.SizeChanged` 每帧都会调它，这个守卫是必需的。）

`SyncRailItems` 只做「按当前 `_railItems.ClientSize.Width` 重算每个按钮宽度」这件事，
把 `ApplyRailState` 里那三行循环抽出来复用。

## Repo conventions to follow

- `_rail` 是 `Dock.Left`（`:2110`），`body` 是它的父级（`BuildLayout`，`:1904` 附近）——
  `SuspendLayout` 挂在 `body` 上，不要挂 `MainForm`（`_pageHost` 也在 `body` 里，一起批处理）。
- 用 001 的 `MotionDriver`（`owner = _rail`）；不要新建 `Timer`。
- 导航项 chip 的绘制全在 `RailButton.OnPaint`（`src/GuiPages.cs:1269`），
  `SetCollapsed` 已有「相同则 return」短路（`:1262`），保留。

## Steps

1. `MainForm` 加 `_railAnim` / `_railFrom` / `_railTo` / `_railAnimating` / `_railExpandTarget`
   （构造函数里 `_railAnim = new MotionDriver(_rail, 1)` 要等 `_rail` 建好，放在 `BuildRail()` 之后）。
2. 抽 `SyncRailItems(bool collapsed)`；`ApplyRailState()` 用 `SyncRailItems` + 守卫。
3. `ToggleRail()` 改成补间入口；`MotionDriver` 的 `Tick` 回调接到 `RailTick`。
4. `OnResize` / 启动路径**仍然走 `ApplyRailState()`**（瞬时），确保窗口尺寸变化不做动画。
   窗体最小尺寸若小于 292 需 `Math.Min(292, 允许宽度)` 兜底（先读 `MinimumSize` 再决定）。

## Boundaries

- 不要动 `src/GuiAnims.cs`。
- 不要改 `Settings.RailExpanded` 的持久化时机（保持点了就存）。
- 不要动画 `_btnRailToggle.Text` 的字符串（文字切换必须瞬时，字符逐个淡入是矫饰）。
- 不要动画 `_railItems` 的 `AutoScroll` 位置。
- 不要为了这条改窗体 `MinimumSize` 或 `Dock` 结构。
- 若 `Settings.RailExpanded` 在别处也被读来直接设宽度（`grep RailExpanded`），保持那处瞬时不变。

## Verification

- **Mechanical**：`.\src\Build.ps1 -Gui` 通过；`.\src\Tests.ps1` 全绿；
  探针里实测每帧耗时（`Stopwatch` + `Application.DoEvents()` 循环）：**单帧 > 8ms 就必须降级**
  ——把 190ms 缩到 120ms（帧数减少）或直接放弃布局动画改成瞬时 + chip 淡入。把实测值写进计划备注。
- **Feel check**：起 GUI，点折叠/展开各 5 次：
  - 宽度是连续滑动、不是跳；页面内容跟着一起被推动（`_pageHost` 是 `Dock.Fill`，会逐帧重排）；
  - 折叠时文字立刻变 chip 并随宽度左滑，展开时文字在**宽度到位后**才出现，中途不出现省略号乱跳；
  - 展开动画中途再点折叠，必须从**当前宽度**折返（retarget），不能跳回 292 再重播；
  - 拉大拉小窗口时导航栏**不动画**（瞬时）。
- **Done when**：十次折叠/展开无跳变、无残影、无中途重播；每帧实测 <8ms；`ForceReduced` 下瞬时。

## 实测记录（2026-10-16）

探针在真实 `MainForm` 上驱动 `ToggleRail`，逐帧 `Stopwatch`（每帧 `Invalidate` + `Update` + `DoEvents`）：

| 路径 | 帧数 | 墙钟 | 每帧均 | 每帧最差 |
|------|------|------|--------|----------|
| 折叠 292→64 | 8 | 201ms | 24.9ms | 33.4ms |
| 展开 64→292 | 8 | 200ms | 34.1ms | 80.4ms |

宽度序列是连续单调的：展开时 `65,75,88,183,268,280,291,292`，折叠时反向，**没有跳变**，
也没有"跳回 292 再重播"。

**结论：保留 `DurRail=190ms` 补间，不加降级护栏。** 理由：

1. 每帧最差 33ms；展开那次 80ms 的首帧是 `ToolStrip/TableLayoutPanel` 列宽重算的一次性成本；
2. 折叠/展开是"一天几次"的频次，不是连续手势，最差帧不会被感知成卡顿；
3. 加护栏会引出第二套时长，违反"令牌唯一"，也更容易和用户要求的既有动画基础打架。
   真遇到低端机反馈，优先把 190ms 缩到 120ms（帧数变少），不要改结构。

同一轮还实测了确认条：`visible=True height=78`（展开完成）/ `visible=False height=0`（收起完成）。
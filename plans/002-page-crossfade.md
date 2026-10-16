# 002 — 页面切换交叉淡入

- **Status**: DONE
- **Commit**: ce85e78
- **Severity**: HIGH
- **Category**: 状态切换瞬移（第八类：遗漏的机会）
- **Estimated scope**: 1 个文件（`src/GuiLauncher.cs`），约 70 行

## Problem

点左侧导航项时，右侧整页内容**瞬间**替换，没有任何交代来去。这是全程序最频繁、
最显眼的「视觉跳变」，而且页面布局本身差异很大（有的带用量条、有的带说明卡，
`Root` 还是 `AutoScroll = true`），瞬切会让人短暂失去方向：

```csharp
// src/GuiLauncher.cs:2190-2211 — 当前实现（节选）
internal void ShowPage(int index)
{
    ToolPage previous = CurrentPage;
    _current = index;
    for (int i = 0; i < _pages.Length; i++)
    {
        bool visible = (i == index);
        _pages[i].View.Visible = visible;
        _railButtons[i].SetActive(visible);
    }
    if (previous != null && previous != _pages[index]) previous.OnLeave();
    _pages[index].OnEnter();
    ...
    _pages[index].View.BringToFront();
```

## Target

用「旧页快照淡出」做交叉淡入：切换前抓一张旧页的位图，切换后在 `_pageHost` 上盖一层
淡出的覆盖层，`DurPage = 200ms`、`Ease.Out`。新页本身不做位移、不做缩放（`View` 是
`Dock.Fill` 的 `Card`，位移没有空间语义）。

```csharp
// target —— src/GuiLauncher.cs 新增
internal sealed class PageFade : Control
{
    private readonly Bitmap _shot;
    private readonly Point _at;
    internal double Alpha;            // 1 → 0
    // OnPaint: 用 ColorMatrix 把 _shot 以 Alpha 画在 _at（父控件坐标）
    // WndProc: WM_NCHITTEST (0x0084) → 返回 HTTRANSPARENT (-1)，绝不吞鼠标
}

private MotionDriver _pageFadeDriver;   // 1 个通道，owner = 覆盖层
private PageFade _pageFade;
```

过渡期间**不可交互的只有旧页快照那一层**，而它是穿透的，所以行为与现在完全一致。

## Repo conventions to follow

- 覆盖层放在 `_pageHost`（`src/GuiLauncher.cs:1918`，`Dock = Fill`、`Padding = 12,0,0,0`）里；
  页面 `View` 也是 `Dock.Fill`，所以覆盖层与页面在同一个客户区，位置直接取
  `new Point(view.Left - host.Left, view.Top - host.Top)`。
- 计时/驱动一律走 001 的 `MotionDriver`，不要新建 `Timer`。
- `WM_NCHITTEST` 的写法照抄仓库里既有的 `WndProc` 覆写（`src/GuiLauncher.cs` 内 `MainForm` 已有
  `WndProc`/`CreateParams` 覆写，`:1894`）。
- 快照用 `Control.DrawToBitmap(Bitmap, Rectangle)`；本仓库已有先例（`src/GuiPages.cs` 里没用过，
  但 `src/GuiAnims.cs` 全靠 `Graphics` 手绘，风格一致）。

## Steps

1. `src/GuiLauncher.cs`：新增 `internal sealed class PageFade : Control`（约 40 行）：
   构造收 `(Bitmap shot, Point at)`；`SetStyle(ControlStyles.AllPaintingInWmPaint |
   ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true)`；`OnPaint` 里
   `ImageAttributes` + `ColorMatrix { Matrix33 = (float)Alpha }`，`g.DrawImage(_shot, rect, 0,0,W,H, UnitPixel, attrs)`；
   `WndProc` 处理 `0x0084` 返回 `(IntPtr)(-1)`；`Dispose` 时释放 `_shot`。
2. `MainForm.ShowPage`：函数开头（`_current = index` 之后、改可见性之前）抓快照：
   ```csharp
   Bitmap shot = null; Control oldView = null; Point at = Point.Empty;
   if (previous != null && previous != _pages[index] && !Motion.Reduced && !_pageFading)
   {
       oldView = previous.View;
       try {
           if (oldView.Width > 0 && oldView.Height > 0) {
               shot = new Bitmap(oldView.Width, oldView.Height);
               oldView.DrawToBitmap(shot, new Rectangle(0, 0, oldView.Width, oldView.Height));
               at = new Point(oldView.Left - _pageHost.Left, oldView.Top - _pageHost.Top);
           }
       } catch (Exception) { if (shot != null) { shot.Dispose(); shot = null; } }
   }
   ```
   抓图的注释必须写明：**必须在可见性交换之前抓**（换完之后 `DrawToBitmap` 只能拿到空位图）。
3. 函数末尾（`SetBusy(...)` 之后）接上淡出：
   ```csharp
   if (shot != null) StartPageFade(shot, at);
   ```
   并新增 `private void StartPageFade(Bitmap shot, Point at)`：先 `Dispose` 掉尚未结束的上一层
   （不叠两层），创建 `PageFade`，`_pageHost.Controls.Add(fade); fade.BringToFront();`
   `_pageFadeDriver = new MotionDriver(fade, 1); _pageFadeDriver.Ch(0).SnapTo(1.0);`
   然后 `To(0, 0.0, Motion.DurPage, Ease.Out)`，并让 driver 在每个 tick 把通道值写回
   `fade.Alpha`（driver 只重绘 owner，所以用 `Advance` 循环 + 事件，或给 `MotionDriver`
   加一个 `internal event Action Tick`；**加事件比开子类干净**）；通道跑完 → `Remove` + `Dispose`
   覆盖层，`_pageFade = null; _pageFading = false;`。
4. 连点防护：`_pageFading` 为 true 时**不抓新快照**（直接瞬时切换），避免连点堆叠出多张位图。

## Boundaries

- 不要动 `src/GuiAnims.cs`。
- 不要给页面加位移/缩放/模糊 —— 只允许淡出，交叉淡入已经足够，多了就是过度设计。
- 不要改 `ShowPage` 里既有的可见性、`SetActive`、`OnLeave/OnEnter`、`ScrollControlIntoView`、
  `BringToFront`、`UpdateRunButton`、`SetBusy` 的语义与顺序（只允许前后各插一段）。
- 不要动 `_pageHost` 的 `Padding`/`Dock`，也不要给 `View` 加 `Visible` 以外的状态。
- `Motion.Reduced` 为真时整段跳过（不做快照、不做淡入）。

## Verification

- **Mechanical**：`.\src\Build.ps1 -Gui` 通过；`.\src\Tests.ps1` 全绿。
- **Feel check**：起 GUI，点 `1.` 到 `10.` 十个导航项，确认：
  - 每次切换旧页淡出、新页在下面已经就位，**看不到中间的空白/闪烁**；
  - 连点两个不同导航项（间隔 <100ms），不出现两张快照叠加、不出现残影常驻；
  - 切换瞬间点页面里的按钮，点击**不丢**（覆盖层穿透）；
  - 慢动作（探针里把 `Motion.DurPage` 临时改成 1200ms 跑一次）确认淡出的是旧页整块、位置对齐
    （不是偏移或缩放），跑完覆盖层被移除（`_pageHost.Controls.Count` 回到基线）。
- **Done when**：十次切换都无跳变，且探针里 `_pageHost.Controls.Count` 在动效结束后等于页面数。

## 实施记录（2026-10-16）

- 落地于 `src/GuiLauncher.cs`：`ShowPage` 抓 `previous.View` 快照 → 放 `PageFade` 覆盖层（`Dock.Fill`，
  `ColorMatrix` 画快照、alpha 1→0）→ 结束时 `Dispose` 并移除自身；`WndProc` 把 `WM_NCHITTEST` 返回
  `HTTRANSPARENT`，所以它绝不吞鼠标。
- **实现时改了一处本计划的条件**：快照不再要求 `!Motion.Reduced`，也就是说减弱动效下
  交叉淡入**保留**，只是走 `Motion.Soft(DurPage)`（≤90ms）。理由：淡入淡出是"帮助理解发生了什么"，
  reduced motion 的规则是"更少更轻，不是零"；位移类（导航栏滑动）才是要砍的。
- 证据：`.scratch/shots/fade-t45-screen.png`（屏幕截图，旧页 45% 幽灵叠在新页上）；
  探针记录 `crossfade overlay created=True` / `alpha=0.45` / `disposed=True`。
  注意 `Form.DrawToBitmap` **抓不到**这个覆盖层（屏幕截图能），排查时别被它误导。
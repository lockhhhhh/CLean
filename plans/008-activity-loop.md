# 008 — 活动条无缝循环

- **Status**: DONE
- **Commit**: ce85e78
- **Severity**: LOW
- **Category**: 物理感与打磨
- **Estimated scope**: 1 个文件（`src/GuiLauncher.cs`），约 8 行

## Problem

底部那个「正在扫描」的细条，片段在**左端凭空出现、右端凭空消失**，不是滑进滑出：

```csharp
// src/GuiLauncher.cs:1206-1213 — 28ms 节拍
_timer.Interval = 28;
_timer.Tick += delegate { _pos = (_pos + 7) % span; Invalidate(); };
```

```csharp
// src/GuiLauncher.cs:1234-1251 — OnPaint（节选）
int span = Math.Max(40, Width);
int seg = Math.Max(30, Width / 3);
int x = _pos - seg;
if (x + seg > Width - 1) x = Width - 1 - seg;   // 右侧被钳住 → 不滑出，是「贴边后消失」
if (x < 0) x = 0;                               // 左侧被钳住 → 不从屏外滑入，是「原地出现」
```

两处钳位是问题根源：片段从来没真正离开过可视区，所以循环点露馅。匀速本身是对的
（跑马灯/进度就是 linear，AUDIT.md 明确规定），**不要改成 ease**。

## Target

让取模基准等于「片段从右侧完全滑出所需的位置」，并把两处钳位删掉：

```csharp
// target —— 构造函数/tick
int seg = Math.Max(30, Math.Max(1, Width) / 3);
_pos = (_pos + 7) % (Math.Max(1, Width) + seg);     // 起点 = -seg（整个片段在左侧屏外）
```

```csharp
// target —— OnPaint
int seg = Math.Max(30, Width / 3);
int x = _pos - seg;      // _pos ∈ [0, Width+seg) ⇒ x ∈ [-seg, Width)，右端恰好完整滑出后才回头
```

`_pos` 的定义域变成 `[0, Width + seg)`，`x` 在 `[-seg, Width)` 上单调推进：
片段从左侧屏外整体进入、完整滑过、到右端**完全**滑出（此时 `x = Width`，整段在屏外）后回到 `x = -seg`，
接缝落在「两端都在屏外」的瞬间，因此看不到弹出/消失。

## Repo conventions to follow

- `ActivityBar` 在 `src/GuiLauncher.cs:1193`，字段 `_pos`、`_timer`（28ms）；
  `Start()` 会 `_pos = 0; Visible = true; _timer.Start();`，`Stop()` 停表 + `Visible = false`。
- `Start()` 里的 `_pos = 0` 要改成 `_pos = 0`（保持不变，它对应 `x = -seg`，也就是屏外起点——**正是想要的**）。
- 绘制沿用既有的 `Theme.Track` / `Theme.Accent` 与 `Theme.FillRound`，圆角 `Height / 2` 不变。
- 这是既有魔数（28ms / 7px）唯一被允许保留的地方：它是匀速跑马灯，与 001 的令牌体系不冲突，
  **不要**把 28ms 换成 `Motion` 令牌（001 的边界里也写了不改既有魔数）。

## Steps

1. 把 `span` 相关计算替换成 `Width + seg`：tick 里的取模（`:1206-1213` 附近）与 `OnPaint`
   里 `span` 的定义（`:1234` 附近）。
2. 删掉 `OnPaint` 里那两行钳位（`if (x + seg > Width - 1)` 与 `if (x < 0)`）。
3. 保留 `Width` 为 0 时的兜底（`Math.Max(1, Width)`），避免除零/负数取模。

## Boundaries

- 不要动 `src/GuiAnims.cs`。
- 不要把匀速改成缓动，不要加 `Ease`。
- 不要改条子的高度、圆角、颜色、`Start`/`Stop` 的 `Visible` 语义。
- 不要给 `ActivityBar` 换成 001 的 `MotionDriver`（它是持续匀速循环，不是补间）。

## Verification

- **Mechanical**：`.\src\Build.ps1 -Gui` 通过；`.\src\Tests.ps1` 全绿。
- **Feel check**：起 GUI 触发一次扫描，盯着底部细条看 20 秒：
  - 片段从左侧**整体滑入**（不是原地出现），到右侧**完整滑出**（不是贴边消失），接缝处看不出来；
  - 窗口拉窄/拉宽后依然无缝（`Width` 变了以后取模基准跟着变）；
  - 条子静止时（`Stop()`）不占带宽（`Visible = false`，计时器已停）。
- **Done when**：连续观察 6 个循环周期，检查不出任何一个「凭空出现/消失」。

## 实施记录（2026-10-16）

- 落地：`ActivityBar` 的取模基准从 `Math.Max(40, Width)` 改成 `Width + seg`，`x = _pos - seg`
  不再钳位 → 片段从左侧屏外滑入、完整滑出右侧再从左边进来；`Math.Max(30, Width/3)` 给的
  起步宽度小于 `Width`，所以取模跨度始终为正，不存在"宽度小于片段"的除零/回绕。
- 保持 linear 匀速（跑马灯/进度条就是 linear），`28ms` tick 与既有 `AnimBase` 的 `33ms` 节奏不变。
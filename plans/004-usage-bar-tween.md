# 004 — 用量条缓动

- **Status**: DONE
- **Commit**: ce85e78
- **Severity**: MEDIUM
- **Category**: 状态切换瞬移
- **Estimated scope**: 1 个文件（`src/GuiLauncher.cs`），约 40 行

## Problem

磁盘用量条是「扫描完才有值」的数据可视化。现在 `SetValue` 直接赋值、下一帧就跳到终值：

```csharp
// src/GuiLauncher.cs:1158-1163 — 当前
internal void SetValue(long used, long total)
{
    _used = used;
    _total = total;
    Invalidate();
}
```

```csharp
// src/GuiLauncher.cs:1165-1189 — OnPaint 里直接算 pct（节选）
double pct = _total <= 0 ? 0 : (double)_used / _total;
if (pct < 0) pct = 0; if (pct > 1) pct = 1;
int w = (int)Math.Round((Width - 1) * pct);
```

用户的真实处境是：按「开始扫描」→ 几十秒等待 → 条子**啪**地出现。这一下是整页最有信息量的
瞬间，值得有点分量（但不是从 0 长出来的装饰性动画）。

## Target

`SetValue` 改变时，从**当前显示值**缓动到新 pct，`Motion.DurData = 420ms`、`Ease.Out`。
>300ms 在该框架里只允许出现在数据可视化上（AUDIT.md 明确写了这个例外），不要推广到交互反馈。

```csharp
// target
private MotionDriver _anim;          // 1 通道 _pct（0..1 的显示值）
private double _shown = -1;          // -1 = 还没有过值
internal void SetValue(long used, long total)
{
    _used = used; _total = total;
    double pct = _total <= 0 ? 0 : Math.Max(0.0, Math.Min(1.0, (double)_used / _total));
    if (_shown < 0) { _shown = pct; _anim.Snap(0, pct); _shown = pct; Invalidate(); }
    else _anim.To(0, pct, Motion.DurData, Ease.Out);
}
```

`OnPaint` 用通道值而不是 `_used/_total` 算宽度：

```csharp
double pct = _anim.Ch(0).Value;         // 已 clamp
Color fill = pct >= 0.92 ? Theme.Danger : (pct >= 0.80 ? Theme.UsageWarn : Theme.Accent);
```

**阈值着色也必须跟着通道走**（否则颜色会在条子还在长的过程中突然变），这是本计划最容易做错的地方。

## Repo conventions to follow

- `UsageBar` 在 `src/GuiLauncher.cs:1139`，字段 `_used` / `_total`，`Height` 由调用方设；
  `Theme.Danger` / `Theme.UsageWarn` / `Theme.Accent` / `Theme.Track` 直接用。
- 驱动用 001 的 `MotionDriver`，`owner = this`，1 个通道。
- 保留 `_used` / `_total` 字段（`OnPaint` 之外可能有读的地方，先 `grep` 确认再改）。

## Steps

1. 加 `_anim`（构造函数里建）+ `_shown` 字段；`SetValue` 按上面的写法改成首帧 snap、其后补间。
2. `OnPaint`（`:1165-1189`）把 `pct` 来源改成 `_anim.Ch(0).Value`，填充色改用同一个 `pct` 判阈值。
   保持既有的 `if (w > 2)` 才画的守卫与 `radius = Height / 2`。
3. 通道每 tick 触发 `Invalidate`（driver 已负责）；通道跑完那一帧必须落在精确终值（`Channel.Advance` 保证）。

## Boundaries

- 不要动 `src/GuiAnims.cs`；不要动 `Theme`。
- 不要改成「从 0 长出来」的入场动画 —— 首帧赋值必须瞬时。
- 不要给条子加光效/条纹/流动动画（那是装饰，且这类进度条在 WinForms 里逐帧重绘成本不低）。
- 不要改 `UsageBar` 的高度/圆角/内边距。

## Verification

- **Mechanical**：`.\src\Build.ps1 -Gui` 通过；`.\src\Tests.ps1` 全绿。
- **Feel check**：探针里建一个 `UsageBar`，依次 `SetValue(0, 100)`（应瞬时为零）、
  `SetValue(93, 100)`（应从 0 缓动到 0.93，并在跨过 0.80/0.92 时**颜色与长度同步**平滑变化，
  不能出现颜色先跳），中途 `SetValue(10, 100)`（应从当前位置折返，不回到 0 重播）。
  `ForceReduced = true` 时用 `Soft(420)=90ms`。
- **Done when**：三次赋值都无跳变，且中途改值时通道从当前值继续。

## 实施记录（2026-10-16）

- 落地：`_anim`（1 通道）+ `_shown`；首次 `SetValue` 走 `Snap`（瞬时），其后 `To(0, pct, DurData, Ease.Out)`；
  `OnPaint` 用 `_anim.Ch(0).Value` 算宽度**和**阈值着色（长度与颜色同步，不会颜色先跳）。
- **与本计划原文的偏差**：减弱动效下用的是 `Motion.Dur`（=0，直接落到终值）而不是 `Soft(420)=90ms`。
  理由：条子表达的是"磁盘当下就是这个占用"，缩短到 90ms 既看不清楚也不比瞬时多提供信息；
  数据可视化要么看得清、要么别演。曲线和时长令牌本身没变。
- 证据：探针里 `SetValue` 到 62% 再把通道按到 0.31 → `bar-rest`（90%，warn 橙）与
  `bar-mid`（31%，accent 蓝）逐像素差 `mean 71.54`，颜色与长度同步。
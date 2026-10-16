# 003 — 交互反馈过渡（按钮 / 单选 / 复选 / 导航项）

- **Status**: DONE
- **Commit**: ce85e78
- **Severity**: HIGH
- **Category**: 目的与频次（反馈）
- **Estimated scope**: 2 个文件（`src/GuiLauncher.cs`、`src/GuiPages.cs`），四个控件，约 150 行

## Problem

四个控件族的 hover / 按压 / 勾选全是**瞬时硬切**：鼠标一进一出，填充色直接跳，
没有任何过渡。按下时除了颜色也没有位移感，点起来「薄」。四个控件的处理方式还不一致
（按钮有 `_hot`/`_down` 两个状态、单选复选只有 `_hot`），同一个程序里手感不同源。

```csharp
// src/GuiLauncher.cs:986-990 — FlatButton，全部瞬时
protected override void OnMouseEnter(EventArgs e) { _hot = true;  Invalidate(); }
protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); }
protected override void OnMouseDown(MouseEventArgs e) { _down = true;  Invalidate(); }
protected override void OnMouseUp(MouseEventArgs e)   { _down = false; Invalidate(); }
```

```csharp
// src/GuiLauncher.cs:992-1024 — OnPaint 里三选一硬切（节选）
Color fill = _down
    ? (FillDown != Color.Empty ? FillDown : Theme.Darken(Fill, 0.08f))
    : (_hot ? (FillHot != Color.Empty ? FillHot : Theme.Darken(Fill, 0.04f)) : Fill);
```

```csharp
// src/GuiLauncher.cs:1051-1052 — PillRadio
protected override void OnMouseEnter(EventArgs e) { _hot = true;  Invalidate(); }
protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); }
```

```csharp
// src/GuiLauncher.cs:1100-1101 — PillCheck，同上
```

```csharp
// src/GuiPages.cs:1252-1253 — RailButton，同上
protected override void OnMouseEnter(EventArgs e) { _hot = true;  Invalidate(); }
protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); }
```

## Target

给每个控件加**一个 0..1 的 `Channel`**（`_hotT`）表示「悬停/按下/选中程度」，
`OnPaint` 用 `Motion.Blend` 按通道值混色，而不是用布尔选色。按下时**填充矩形四边内缩 1px**
（`transform: scale(0.97)` 在本框架里唯一诚实的等价物），保持 0.95–0.98 的微妙度。

精确值（全部来自 AUDIT.md，不许改）：

| 事件 | 通道目标 | 时长 | 缓动 |
|------|----------|------|------|
| hover 进入 | 1.0 | `Motion.DurHover` = 140ms | `Ease.Out` |
| hover 离开 | 0.0 | `Motion.DurHover` = 140ms | `Ease.Out` |
| 按下（附加） | 1.0 | `Motion.DurPress` = 110ms | `Ease.Out` |
| 释放（回落） | 0.0 | `Motion.DurHover` = 140ms | `Ease.Out` |
| 勾选/选中切换 | 1.0 / 0.0 | `Motion.DurToggle` = 170ms | `Ease.Out` |

不使用过冲（`Ease.OutBack`）——鼠标交互里过冲是噪音。**耗时上「按下更慢、释放更快」是有意的**：
指令性动作（按下）比系统响应（释放）更需要被感知到，这是非对称时长的正确用法，不要"统一"。

按压内缩的具体几何（`FlatButton.OnPaint` 内）：

```csharp
// target —— 按 _pressT（0..1）内缩，最多 1px
int inset = (int)Math.Round(_pressT.Value * 1.0);
Rectangle r = new Rectangle(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
```

混色公式（以 `FlatButton` 为例，`_hotT` 就是 hover 程度）：

```csharp
Color baseFill = _hotT.Value > 0 ? Motion.Blend(Fill, FillHot, _hotT.Value) : Fill;
Color fill = Motion.Blend(baseFill, FillDown, _pressT.Value);
```

选中态（`PillRadio` / `PillCheck` / `RailButton.Active`）也用通道：
`Checked` 变化时 `_onT.To(Checked ? 1.0 : 0.0, Motion.DurToggle, Ease.Out)`，
`OnPaint` 里 `fill = Motion.Blend(uncheckedFill, checkedFill, _onT.Value)`，
对勾/圆点用 `_onT.Value` 控制大小（`(int)Math.Round(17 * _onT.Value)` 这类，**绝不从 0 开始**——
从 0 长的对勾会显得廉价，最小保留 0.6 起）。

## Repo conventions to follow

- 驱动沿用 001 的 `MotionDriver`：每个控件一个 `private MotionDriver _anim;`（2 个通道：
  `_hotT`、`_pressT`；`PillRadio`/`PillCheck` 用 `_onT` + `_hotT`），在构造函数里建，
  `owner` 传 `this`（driver 自己会 `Invalidate()`）。
- **所有控件都禁止链 `base.OnPaint`** —— `src/GuiLauncher.cs:992` 附近的注释已写明原因
  （会画上系统 chrome）。新代码必须保持同一纪律。
- 颜色工具用 `Theme.Mix` / `Theme.Blend`，`FillRound` / `DrawRound` 的签名不要改。
- 控件状态字段保持 `internal`/`private` 现状，不新增公开 API。

## Steps

1. `FlatButton`（`src/GuiLauncher.cs:906-1025`）：加 `_anim` + `_hotT`/`_pressT`；
   `OnMouseEnter/Leave/Down/Up` 改成 `_anim.To(0, 1.0, Motion.DurHover, Ease.Out)` 等；
   `OnEnabledChanged` 时两个通道 `SnapTo(0)`（禁用态不保留悬停残留）；
   `OnPaint` 改成两个 `Motion.Blend` + 内缩矩形；`Disabled` 分支保持 `Theme.Mix(Fill, Theme.Bg, 0.55f)` 不变。
2. `PillRadio`（`:1028-1074`）：加 `_anim` + `_hotT`/`_onT`；`CheckedChanged` 触发 `_onT`；
   构造函数里按初始 `Checked` 用 `SnapTo` 设 `_onT`（避免首帧从 0 长出来）；
   `OnPaint` 的胶囊填充与边框颜色按 `_hotT`/`_onT` 混合。
3. `PillCheck`（`:1076-1137`）：同上，对勾按 `_onT` 缩放，最小 0.6。
4. `RailButton`（`src/GuiPages.cs:1229-1310`）：加 `_anim` + `_hotT`/`_onT`；
   `SetActive(bool)`（`:1255`）里在改了 `Active` 之后触发 `_onT`（注意它已有「相同则 return」的短路，
   保留该短路）；`OnPaint` 的 `Theme.AccentSoft` / `Theme.Hover` 底色按通道混合；竖条高度按 `_onT` 生长。
5. 四个控件的 `Dispose` 里释放 driver（`_anim.Dispose()` 需在 001 里实现为停表 + 释放 `Timer`）。

## Boundaries

- 不要动 `src/GuiAnims.cs`。
- 不要动 `HitList` 的行 hover（审计已判定保持瞬时）；不要动 `FlatMenuRenderer`。
- 不要改任何控件的尺寸、`Height`、`Padding`、`Margin`、锚定方式 —— 只改绘制与状态过渡。
- 不要给按钮加 `Ease.OutBack`、不要加阴影、不要加缩放。
- 不要在 `OnPaint` 里分配新的 `Pen`/`Brush` 而不 `using`（现有代码就是全 `using` 的）。
- 不要在鼠标事件里直接 `To(...)` 一个**已经在跑的同一目标**（driver 内部按 `Math.Abs(target-Value)<0.001` 短路，
  保证鼠标抖动不会反复重启动画）。

## Verification

- **Mechanical**：`.\src\Build.ps1` 与 `.\src\Build.ps1 -Gui` 都通过；`.\src\Tests.ps1` 全绿。
- **Feel check**：起 GUI，探针里把 `Motion.DurHover` 临时放大到 800ms 逐帧截几张图，确认：
  - 鼠标移入按钮后填充色是**渐变**到位，不是突然变色；移出同理（可中止，不重播）；
  - 快速来回划过一排按钮，颜色跟着鼠标走、不出现「排队补间」的滞后感；
  - 按住主按钮不放，能看到 1px 内缩 + 按下色；拖出按钮外再拖回来，状态正确回落；
  - 勾选/取消一个 `PillCheck`，对勾是渐显渐隐、不是闪一下；
  - 禁用态（未扫描时的「取消」按钮）没有悬停残留色。
- **Done when**：四个控件族的手感一致（同一时长表），`ForceReduced = true` 时颜色仍然过渡、位移内缩归零。

## 实施记录（2026-10-16）

- `FlatButton` / `PillRadio` / `PillCheck` / `RailButton` / `UsageBar` 的 `_hot` / `_down` / `_checked`
  布尔字段换成 `MotionDriver` 通道（`_hotT` / `_pressT` / `_onT`），hover `DurHover=140ms`、
  按压 `DurPress=110ms`、勾选 `DurToggle=170ms`，全部 `Ease.Out`。
- 按压的 `scale(0.97)` 在 WinForms 里的等价物：填充矩形四边各内缩 1px + 混入深色
  （`Motion.Blend(fill, downFill, press)`），按住不放不会继续缩，符合"0.95–0.98 的微妙度"。
- 全部走 `Channel.To()` 的 retarget 语义：快速进出不重播。
- 证据：`button-rest` vs `button-pressed` 逐像素差（`mean 21.38`，差异集中在按钮区域）、
  `check-on.png`（对勾按 `on` 通道缩放淡入）、`bar-rest` vs `bar-mid`（颜色与长度同步）。
- **`src/GuiPages.cs:1000` 附近的 `HitList` 行 hover 保持不变**（见 README「故意不动」清单）。
# 007 — 确认条展开

- **Status**: DONE
- **Commit**: ce85e78
- **Severity**: MEDIUM
- **Category**: 空间一致性（布局跳变）
- **Estimated scope**: 1 个文件（`src/GuiLauncher.cs`），约 30 行

## Problem

破坏性操作（清理）在底部弹出一条 78px 高的确认条，把整个内容区往上推：

```csharp
// src/GuiLauncher.cs:2282-2332 — 构建时（节选）
_confirmStrip = new Panel();
_confirmStrip.Dock = DockStyle.Bottom;
_confirmStrip.Height = 78;
_confirmStrip.Padding = new Padding(18, 0, 18, 12);
_confirmStrip.Visible = false;      // :2288
```

```csharp
// src/GuiLauncher.cs:3356 — 唯一的显示点，直接置 true，内容区瞬间被推上去 78px
_confirmStrip.Visible = true;
```

置回 `false` 在 `:3087`、`:3096`、`:3140`（三处）。这是「要不要真的删掉这些文件」的**高注意力时刻**，
布局突然弹一下会削弱它的分量。

## Target

高度 0 → 78 展开：`Motion.DurPanel = 210ms`、`Ease.Out`（进入用 ease-out）。
隐藏 78 → 0：140ms、`Ease.Out`（退场要更快，不要拖）。两段都是布局高度变化，用
`SuspendLayout/ResumeLayout(false)` 把每帧的重排收敛成一次。

```csharp
// target
private MotionDriver _stripAnim;      // 1 通道
private bool _stripAnimating;
private int _stripFrom, _stripTo;

private void ShowConfirmStrip(bool show)     // 取代三处 Visible = false 与那一处 = true
{
    if (show) { _confirmStrip.Visible = true; }
    if (Motion.Reduced || _stripAnim == null)
    { _confirmStrip.Height = show ? 78 : 0; _confirmStrip.Visible = show; return; }
    _stripFrom = _confirmStrip.Height; _stripTo = show ? 78 : 0;
    if (_stripFrom == _stripTo && show) return;
    _stripAnimating = true;
    _stripAnim.To(0, 1.0, show ? Motion.DurPanel : 140, Ease.Out);
}
```

`Tick` 回调里 `_confirmStrip.Height = round(lerp)`，`SuspendLayout` 挂 `this`（`MainForm`，
因为 `content` / `actionBar` 都是窗体的直接子级，高度变化会一起重排）；
跑完那一帧 `_stripAnimating = false`，若目标是隐藏则 `Visible = false`。

## Repo conventions to follow

- `_confirmStrip` 在 `src/GuiLauncher.cs:2282-2332` 构建；它是 `Dock.Bottom`。
- 显示/隐藏共 4 处调用点（`:3356` 显示，`:3087`/`:3096`/`:3140` 隐藏）——**全部改成走 `ShowConfirmStrip(bool)`**，
  不要留下直接赋 `Visible` 的漏网之鱼（`grep -n _confirmStrip.Visible` 必须只剩新函数内部那几处）。
- 用 001 的 `MotionDriver`（`owner = _confirmStrip`）。

## Steps

1. 加 `_stripAnim`（`BuildConfirmStrip()` 之后建）/ `_stripAnimating` / `_stripFrom` / `_stripTo`。
2. 加 `ShowConfirmStrip(bool show)`，`Tick` 回调更新高度。
3. 把 `:3356` 改为 `ShowConfirmStrip(true)`；`:3087`、`:3096`、`:3140` 改为 `ShowConfirmStrip(false)`。
   注意 `:3087`/`:3096` 若在**初始化路径**上（默认状态），应保持瞬时（`Visible=false; Height=78` 不动）。

## Boundaries

- 不要动 `src/GuiAnims.cs`。
- 不要改确认条的文案、按钮、颜色（`WarnStyle`）或 `Padding`。
- 不要在动画期间禁用确认按钮 —— 用户可能就是想秒点，禁止用动画挡交互。
- 不要动画 `_lblConfirm` 的文字或按钮的透明度。
- 初始状态必须是「不可见 + 高度 78」（不是 0），否则后续展开的起点会漂。

## Verification

- **Mechanical**：`.\src\Build.ps1 -Gui` 通过；`.\src\Tests.ps1` 全绿；
  `grep -n "_confirmStrip.Visible" src/GuiLauncher.cs` 结果只出现在 `ShowConfirmStrip` 内部与 `BuildConfirmStrip` 的初始化处。
- **Feel check**：起 GUI，触发一次破坏性清理：
  - 确认条从底部**长出来**（不是弹出来），内容区被连续上推，文字不闪；
  - 在它还在长的时候就点「取消」，必须立刻生效（动画不能吞点击）；
  - 长出来期间点确认，也能点中；
  - 点「取消」后 140ms 收回，收回后 `Visible == false`（探针里读一次）；
  - 连续触发两次（确认→取消→再触发），高度不会越走越偏（必须每次从**当前**高度起步）。
- **Done when**：每次显示/隐藏都是连续高度变化，结束高度精确为 78 / 0，点击从不被吞。

## 实施记录（2026-10-16）

- 落地：`ShowConfirmStrip(bool)` 把**可见性**的 4 个调用点（`src/GuiLauncher.cs` 的排队/取消/清理三处）
  统一走高度补间：出现 0→78 用 `DurPanel=210ms Ease.Out`，收起 78→0 用 140ms；每帧
  `SuspendLayout()` 护住，`_stripAnimating` 守卫防止重入；减弱动效时瞬时。
- 证据：探针 `confirm strip visible=True height=78` / `after hide: visible=False height=0`；
  `confirm-full-screen.png`（屏幕截图里能看到 78px 的警示条与「取消 / 执行删除」）。
  同样注意 `DrawToBitmap` 抓不到它。
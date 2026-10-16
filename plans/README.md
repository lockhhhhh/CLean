# CLean-GUI 动效改造计划

审计范围：`src/GuiLauncher.cs`（窗口/控件）、`src/GuiPages.cs`（页面/扫描）、`src/GuiAnims.cs`（既有动画基础）。
审计基线 commit：`ce85e78`。

这份目录是「什么时候动、动多少」的唯一依据。所有曲线与时长都来自
[`emilkowalski/skills · improve-animations · AUDIT.md`](https://github.com/emilkowalski/skills)，
**不要再自己编新的时长或曲线**；新动效一律复用 `src/Motion.cs` 里的令牌。

## 计划表

| # | 计划 | 严重度 | 审计类别 | 状态 |
|---|------|--------|----------|------|
| [001](001-motion-tokens.md) | 动效令牌与补间引擎（`src/Motion.cs`） | HIGH（前置） | 凝聚力 | DONE |
| [002](002-page-crossfade.md) | 页面切换交叉淡入 | HIGH | 状态切换瞬移 | DONE |
| [003](003-interaction-feedback.md) | 交互反馈过渡（按钮/单选/复选/导航项） | HIGH | 目的与频次 | DONE |
| [004](004-usage-bar-tween.md) | 用量条缓动 | MEDIUM | 状态切换瞬移 | DONE |
| [005](005-summary-enter.md) | 结果卡进入淡入 | MEDIUM | 遗漏的机会 | DROPPED（实测无隐藏→出现边界） |
| [006](006-rail-slide.md) | 功能栏折叠滑动 | MEDIUM | 空间一致性 | DONE |
| [007](007-confirm-reveal.md) | 确认条展开 | MEDIUM | 空间一致性 | DONE |
| [008](008-activity-loop.md) | 活动条无缝循环 | LOW | 物理感与打磨 | DONE |

八条全部收口。每个计划末尾的「实施记录」写的是**实际代码**与计划原文的差异及理由；
005 的判定证据也在它自己的文件里。审计基线 commit `ce85e78`。

## 实施记录（超出计划范围的两处）

1. **`005` DROPPED**：`SummaryView` 从来没有"隐藏→出现"的边界（探针实测它一直
   `Visible=true`、尺寸固定），所以没有可加的进入动画。`src/GuiAnims.cs` 保持零改动。
2. **新增：动效偏好开关**（audit 里没有，实现时发现必须补）。
   本机 `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` 返回 **0**，也就是
   「显示动画」是关的 —— 于是 `Motion.Reduced` 永远为真，**新动效一个都看不到**，
   连"这个改动到底有没有效果"都无法验证。
   因此加了 `Motion.Preference`（`auto` / `on` / `off`，存 `Settings.MotionPreference`，注册表项）
   与「外观」菜单里的三选项，默认 `auto`（跟随系统）。
   - `auto`：跟随 Windows；`on`：无视系统设置强制播放；`off`：全部瞬时。
   - 只有 `auto` 才可能被系统关掉；`on`/`off` 是用户显式选择，优先级高于系统。
   - 菜单项的文字由 `Motion.Label(value)` 生成，会把 Windows 当前状态写进去，
     所以用户不会以为"我明明开了动画却没动静"。

## 执行顺序与依赖

1. **001 必须先做**：002–008 全部依赖它导出的 `Motion` / `Channel` / `MotionDriver`。
2. 之后 002 与 003 是感知收益最大的两条，建议接着做。
3. 004–008 彼此独立，可任意顺序；006 与 007 会触发逐帧布局，需要实测帧耗时（见各自 Verification）。

## 审计里「故意不动」的清单

不是漏掉，是判断后决定保持瞬时。复审计时不要把它们当新发现：

- `src/GuiAnims.cs` 的 `AnimBase` 与十个 motif —— **用户明确要求的边界**，本次一个字都不改。
- `src/GuiPages.cs` 扫描结果列表 `HitList` 的行 hover（`RepaintRow` 免擦除单行重绘）——
  命中频率是「几十次/天」档，且 owner-draw 逐行重绘加动画就是纯损耗。
- `FlatMenuRenderer` / 外观下拉菜单的 hover —— Windows 原生菜单惯例就是瞬时，跟随系统。
- 主题切换时整窗过渡 —— Windows 自己也是瞬切；要动就得改每个控件的 `ApplyPalette`，代价与收益不成比例。
- 日志窗口 `LogWindow` 的出现 —— OS 级窗口动画，且它不是常用路径。
- 页面内各区块的 30–80ms 错峰进入 —— 区块是 `Label` / `TableLayoutPanel` 单元格而非自绘控件，
  且 002 的交叉淡入已经覆盖了「页面突然换内容」这唯一的真实缝。

## 约定

- 源码是 **UTF-8 无 BOM + LF**，`csc /codepage:65001`，**C# 5 语法**（禁用 `?.`、`nameof`、字符串插值、
  表达式体成员、自动属性初始化器）。
- 任何改动都要在 `src/Tests.ps1` 里加覆盖（AGENTS.md 要求），并在交付前全绿。
- 机械验收：`.\src\Build.ps1` 与 `.\src\Build.ps1 -Gui` 都要通过。

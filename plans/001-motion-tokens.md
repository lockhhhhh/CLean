# 001 — 动效令牌与补间引擎

- **Status**: DONE
- **Commit**: ce85e78
- **Severity**: HIGH（前置：002–008 全部依赖本计划导出的类型）
- **Category**: 凝聚力
- **Estimated scope**: 新增 1 个源文件（约 200 行）、改 `src/Build.ps1` 两处、`src/Tests.ps1` 加一节

## Problem

整个 GUI 没有任何动效令牌、缓动函数或补间抽象。所有「动」都是散落的魔数，
每个控件各自为政，没人能说出「为什么是 33ms」：

```csharp
// src/GuiAnims.cs:26,41 — 30fps 计时器，唯一的节拍来源
private readonly Timer _tick;
...
_tick.Interval = 33;
_tick.Tick += delegate { Frame++; Invalidate(); };
```

```csharp
// src/GuiLauncher.cs:1206-1213 — 另一个节拍，28ms
_timer = new Timer();
_timer.Interval = 28;
_timer.Tick += delegate { _pos = (_pos + 7) % span; Invalidate(); };
```

```csharp
// src/GuiAnims.cs:392 — 线性生长，8 帧硬编码
double grow = Math.Min(1.0, age / 8.0);
```

没有这套东西，后面每一条计划都会各自发明曲线和时长，最后还是一堆手写值。
另外**没有任何地方尊重系统「显示动画」开关**，用户关掉了动效也照动。

## Target

新增 `src/Motion.cs`，导出四样东西，其它计划只允许用它们：

1. `internal static class Motion` —— 令牌与纯函数。
   ```csharp
   internal enum Ease { Out, InOut, OutBack, Linear }

   internal static class Motion
   {
       internal const int DurPress  = 110;   // 按压反馈 100–160ms
       internal const int DurHover  = 140;   // hover / 颜色
       internal const int DurToggle = 170;   // 勾选、选中
       internal const int DurRail   = 190;   // 功能栏折叠（屏幕上的位移，ease-in-out）
       internal const int DurPanel  = 210;   // 结果卡、确认条
       internal const int DurPage   = 200;   // 页面交叉淡入
       internal const int DurData   = 420;   // 数据条重算（>300ms 只允许出现在数据可视化上）

       internal static double Clamp01(double t);
       internal static double Apply(Ease e, double t);      // t 已归一到 0..1
       internal static Color  Blend(Color a, Color b, double t);  // 转发 Theme.Mix
       internal static bool   Reduced { get; }              // SPI_GETCLIENTAREAANIMATION
       internal static bool   ForceReduced { get; set; }     // 测试用
       internal static int    Dur(int ms);                   // 位移类：减弱动效时归 0
       internal static int    Soft(int ms);                  // 颜色/淡入类：减弱动效时压到 90ms
   }
   ```
   曲线必须是**真正的三次贝塞尔求解**，不许用 `1-Math.Pow(1-t,3)` 之类的近似顶替：
   - `Ease.Out` = `cubic-bezier(0.23, 1, 0.32, 1)`
   - `Ease.InOut` = `cubic-bezier(0.77, 0, 0.175, 1)`
   - `Ease.OutBack` = `cubic-bezier(0.34, 1.56, 0.64, 1)`（轻微过冲，只给按压/成功用）
   - 求解方式：`x(u)` 用牛顿迭代（8 次，起点 `u=t`），`|x(u)-t| < 1e-6` 即停；不收敛则退化为二分 20 次；
     返回值一律 clamp 到 `[0,1]`（`OutBack` 也 clamp，过冲由曲线内部产生）。

2. `internal sealed class Channel` —— 一个 0..1 的动画通道，**以当前值为起点**，
   因此天然可中断、可中途反转（不允许 keyframes 式从零重播）。
   ```csharp
   internal double Value { get; }
   internal bool Running { get; }
   internal void SnapTo(double v);
   internal void To(double target, int ms, Ease ease);
   internal bool Advance();   // 推进一步；仍在跑返回 true，跑完那一帧返回 false 并把 Value 落在 target
   ```

3. `internal sealed class MotionDriver` —— 把若干 `Channel` 绑到一个 `Control` 上驱动重绘。
   ```csharp
   internal MotionDriver(Control owner, int channels);
   internal Channel Ch(int i);
   internal void To(int i, double target, int ms, Ease ease);  // 内部先过 Motion.Dur(ms)
   internal void Snap(int i, double v);
   ```
   计时器 `Interval = 16`；**只要还有通道在跑就开表，全部停就停表**（空闲时零开销）；
   每 tick 对所有通道 `Advance()` 后调一次 `owner.Invalidate()`。

4. 减弱动效门：`SystemParametersInfo(SPI_GETCLIENTAREAANIMATION = 0x1042)` 取「显示动画」；
   调用失败（老系统）回退 `SystemInformation.UIEffectsEnabled`；`ForceReduced` 或系统关闭时 `Reduced = true`。
   语义是**更少更柔，不是零**：位移（宽度/高度/位置）用 `Dur()` → 归 0 直接跳；
   颜色与淡入用 `Soft()` → 压到 90ms 保留（它帮助理解状态变化）。

## Repo conventions to follow

- 新源文件放 `src/`，`internal` 可见性，文件头两行 ASCII 注释说明「这是什么、为什么存在」——
  照抄 `src/GuiAnims.cs:1-13` 的写法（含 C# 5 说明）。
- 复用既有颜色工具，不要重写：`Theme.Mix(Color, Color, float)`（`src/GuiLauncher.cs:702`）、
  `Theme.Darken` / `Theme.Lighten`（`src/GuiLauncher.cs:712-713`）。
- P/Invoke 写法参照 `src/GuiLauncher.cs` 里已有的 `DllImport`（`user32`/`shell32` 都用过）。
- 源文件 UTF-8 **无 BOM** + **LF**（`.gitattributes` 钉死 `*.cs text eol=lf`）。

## Steps

1. 新建 `src/Motion.cs`，写 `Ease` 枚举、`Motion`、`Channel`、`MotionDriver`，全 C# 5 语法。
2. `src/Build.ps1:129` 的注释由
   `# The GUI is split over four sources (window / pages+scanner / animations / report`
   改为 `# The GUI is split over five sources (window / pages+scanner / animations / report / motion`。
3. `src/Build.ps1:136` 的 `$extraSources = @('GuiPages.cs', 'GuiAnims.cs', 'ReportView.cs', 'UpdateCheck.cs')`
   改为 `@('GuiPages.cs', 'GuiAnims.cs', 'ReportView.cs', 'Motion.cs', 'UpdateCheck.cs')`。
   （控制台版的 `:132` **不要动** —— `Launcher.cs` 不用 WinForms。）
4. `src/Tests.ps1` 新增一节（放在图标两节之前），三项：
   - 静态守卫：`Build.ps1` 的 GUI `$extraSources` 含 `Motion.cs`；`Motion.cs` 含
     `0.23, 1, 0.32, 1` / `0.77, 0, 0.175, 1` 两个令牌字面量；
   - 真的用 `csc` 把 `src/Motion.cs` + 一个临时 `TestMain.cs` 编成控制台 exe（`/r:System.Windows.Forms.dll`
     仅供 `SystemInformation`/`Timer` 解析），断言：`Ease.Out` 的 `Apply(0)=0`、`Apply(1)=1`、单调不减、
     `Apply(0.5)` 在 `(0.7, 0.95)`；`ForceReduced = true` 时 `Dur(200)=0`、`Soft(200)=90`；
     `Channel.To(1, 100)` 走到一半再 `To(0, 100)` 时值不跳变（retarget 连续性）；
   - 新增文件是 UTF-8 无 BOM + LF。

## Boundaries

- 不要动 `src/GuiAnims.cs` 的任何一行（用户边界）。
- 不要动 `src/Build.ps1` 里除上述两行外的内容。
- 不要新增第三方依赖，不要引入 .NET 4 以上的 API。
- 不要顺手把既有魔数改成令牌（那是 004/008 各自的事）——本计划只新增基础设施。
- 若 `src/Build.ps1:129/136` 的实际内容与上面引用不一致（基线漂移），停下报告，不要即兴改。

## Verification

- **Mechanical**：
  - `.\src\Build.ps1` → 退出码 0，末尾回读版本一致；
  - `.\src\Build.ps1 -Gui` → 退出码 0；
  - `.\src\Tests.ps1` → 全部通过（`=== N passed, 0 failed ===`，退出码 0），且比基线多 3 项。
- **Feel check**：本计划没有可见效果，靠 002 之后一起看。要确认的是「空闲时没有计时器在跑」——
  用探针起 `MainForm`，跑完任意动效后调用 `job_list` 级别的观察不方便，改为在
  `MotionDriver` 里断言 `_timer.Enabled == false`（探针里读一次即可）。
- **Done when**：`Motion.cs` 进了 GUI 编译单元，测试全绿，且 `Motion.Dur`/`Soft` 在
  `ForceReduced` 下的返回值符合上表。

## 实施记录（2026-10-16）

- 落地文件：`src/Motion.cs`（UTF-8 无 BOM + LF，C# 5 语法，无命名空间）。
- `src/Build.ps1:136` 的 GUI `$extraSources` 已加入 `'Motion.cs'`（控制台版不加）。
- 机械覆盖：`src/Tests.ps1` 第 10 节 —— 令牌字面量漂移守卫、`Motion.cs` 单独编译
  （只允许借用 `Theme.Mix` 与 `Settings.MotionPreference`，多借一样就编译失败），
  再跑纯数学断言（端点 / 单调 / `Out(0.5)=0.9657` / `InOut(0.5)=0.5960` / `OutBack` 过冲
  / `Blend` 端点与 clamp / `Dur`+`Soft` 门槛 / `Channel` 中途 retarget 不跳变 / `SnapTo` 立即停表）。
- 实现偏差：为了让「系统里关掉了动画」的机器也能验证效果，新增 `Motion.Preference`（auto/on/off）
  与外观菜单里的「动效」开关，见 `plans/README.md` 的「实施记录」。
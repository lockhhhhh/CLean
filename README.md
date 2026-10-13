# CLean —— Windows C 盘清理工具

一套可复用的 Windows C 盘清理脚本，含安全底座、只读分析、**点到点**清理与空间反升诊断。

> **设计原则**
> 1. **默认只读**：除 `*.ps1 -Execute` 外，所有脚本都不删除任何文件。
> 2. **空间只降不升**：每个清理目标都用 **卷的可用空间计数器** 前后实测；一旦可用空间
>    下降就**立即中止**，绝不继续删。报告里只有"净释放"（NetFreeMB）算数。
> 3. **点到点**：每个目标是"确切目录 + 确切文件名模式 + 时限"，不再整目录整棵树扫。
>
> v1 的失败模式是"报告的释放量是假的"，根因见第二节。

---

## 零、下载与安装（普通用户）

到 [Releases](https://github.com/lockhhhhh/CLean/releases) 下载 `CLean-Setup-<版本>.exe` 双击安装：

| | |
|---|---|
| 安装位置 | `%LOCALAPPDATA%\Programs\CLean`（**按当前用户安装，装的时候不弹 UAC**） |
| 开始菜单 | 「CLean」窗口版 + 「CLean 控制台」两项；桌面图标可选 |
| 卸载 | 设置 → 应用 → CLean；或运行安装目录里的 `unins000.exe` |
| 卸载之后 | `%LOCALAPPDATA%\CLean\`（清理报告、主题、更新缓存）**会保留**，卸载程序不碰用户数据 |
| 运行权限 | 两个程序都带 `requireAdministrator` 清单，**启动时**才提权（清理系统缓存必须管理员） |

**不想装？** Release 里同时附了两个单文件版本 —— `CLean-GUI.exe`（窗口版）和 `CLean.exe`（控制台版），
拷到任意 Win10 / Win11 双击就能跑，零依赖、不需要安装。

> ⚠️ **SmartScreen 提示**：本项目没有购买代码签名证书，所以首次运行安装包时 Windows 可能弹出
> 「Windows 已保护你的电脑」。点「更多信息」→「仍要运行」即可。这是"未签名安装包"的必然结果，
> 不代表文件有问题。

### 更新检查

窗口版启动时会在后台问一次 GitHub「有没有新版本」，控制台版在主菜单按 `[u]` 查。规则：

- **24 小时缓存**：正常启动一天最多发一次请求，结果存在 `%LOCALAPPDATA%\CLean\update.txt`；
- **失败静默**：断网 / 超时（5 秒）/ 证书问题都只算"查不到"，绝不弹错、绝不阻塞窗口，**不采集任何数据**；
- **只提示，不自动安装**：发现新版本后，窗口版把标题下那行说明变成
  「发现新版本 vX.Y.Z —— 点击打开下载页」，点它用默认浏览器打开 Releases 页；控制台版在菜单顶部提示，
  按 `[u]` 进详情；
- 无头查询（给脚本 / CI 用）：

  ```powershell
  .\CLean.exe --check-update          # 退出码 0 = 已最新，10 = 有新版本，1 = 查询失败
  .\CLean.exe --check-update --force  # 忽略 24 小时缓存，强制联网复查
  .\CLean.exe --version               # 只打印版本号；不联网、不落盘、不释放脚本
  .\CLean-GUI.exe --check-update      # 窗口版同样支持，结果另写一份 update-cli.json
  ```

---

## 一、单文件程序（拷到任意 Win10 / Win11 直接双击）

两个外壳，同一个脚本内核（10 个 `.ps1` 内嵌）：

| 程序 | 界面 | 源码 | 体积 |
|---|---|---|---|
| `CLean.exe` | 控制台菜单 | `src\Launcher.cs` | 167 KB |
| `CLean-GUI.exe` | **WinForms 窗口**（见 1.1；v0.3 颜色主题，v0.4 分页 + 内置扫描器，v0.5 每个功能独立动画、页面不再显示日志） | `src\GuiLauncher.cs` + `GuiPages.cs` + `GuiAnims.cs` | 260 KB |

> 版本号从 v1.0.0 起**统一**：以前两个外壳各自计版本（控制台 2.0 / 窗口版 0.5），现在由仓库根目录的
> `VERSION` 文件统一定义，编译时注入两个程序的可执行文件、程序集元数据和安装包，三处不会跑偏。

两者都是**零依赖单文件**：不需要 PowerShell 模块、.NET SDK、联网，**也不需要旁边的 `.ps1` 文件**。
脚本作为资源内嵌，首次运行释放到 `%LOCALAPPDATA%\CLean\runtime\`（可读、可审计）。

```
  [1] 空间分析               只读，含目录级缓存清单      [6] 开发环境缓存          需确认
  [2] 系统缓存清理           点到点删除 + 净空间保证      [7] 全盘大目录扫描        只读
  [3] 无用缓存发现           只读，扫描未列入清单的缓存   [8] 隐藏项目缓存扫描      只读
  [4] 显卡着色器缓存         自动识别 N 卡 / A 卡        [9] 空间反升诊断          只读
  [5] C 盘根目录残留         需确认                      [u] 检查更新              只读
                                                          [r] 打开报告目录        [0] 退出
```

`[2]` 会先问**清理深度**（标准/深度）与**清理档位**（保守/标准/完整），再跑一遍 dry-run
打印计划，输入 `y` 回车才真正执行。取消则一个文件都不删。

报告写入 `%LOCALAPPDATA%\CLean\runtime\reports\`，菜单 `[r]` 可直接打开。

---

### 1.1 窗口版（`CLean-GUI.exe`）

把上面这套控制台菜单换成窗口，功能、参数、安全契约完全一致。

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│ [C:]  CLean        [输出日志 • 3][外观 ▾][打开报告目录][刷新][以管理员身份重启] │  白色 header + 1px 分隔线
├────────────────────────────────────────────────────────────────────────────────────┤
│ ┌─ C: 系统盘 ──────────────────────────────────[Windows 11 专业版][管理员权限]───┐ │  磁盘卡片
│ │  104.89 GB 可用                                                                │ │
│ │  共 232.77 GB · 已用 127.88 GB (55%)  ▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │ │  用量条按占用变色
│ └────────────────────────────────────────────────────────────────────────────────┘ │
│ ┌─ 功能 ──────┐ ┌─ 9. 隐藏项目缓存扫描 ─────────────────────[需确认·预演后执行]┐ │  收纳式功能栏
│ │ ☰ 折叠功能栏│ │ 扫描 node_modules / .cache / .next / venv…                  │ │  （可折叠成
│ │ ① 空间分析  │ │ 扫描范围 (用户目录+桌面)(仅用户目录)(整个C盘)                │ │   一排数字）
│ │ ② 系统缓存… │ │ 最小体积 (≥20MB)(≥50MB)(≥100MB)   扫描深度 (5层)(不限层数)   │ │
│ │ ▎⑨ 隐藏项目…│ │ ┌──────────────────────────────────────────────────────────┐│ │
│ │  …（共10项）│ │ │ ◍◍◍ 扫描完成 · 找到 1 项                                 ││ │  该功能自己的动画
│ │             │ │ │ 88,446 个文件 · 21.3 GB · 0.95 秒 · …\npm-cache\_npx\1e7f ││ │  （页面不再显示日志）
│ │             │ │ └──────────────────────────────────────────────────────────┘│ │
│ │             │ │ ┌──────────────────────────────────────────────────────────┐│ │
│ │             │ │ │ ◉ node_modules   213.5 MB   …\_npx\1e7f…   213.5 MB      ││ │  勾选式结果列表
│ │             │ │ │ ☐ C:\Users\…\project\node_modules  1.2 GB  · 8,120 文件  ││ │  （行内体积条）
│ │             │ │ └──────────────────────────────────────────────────────────┘│ │
│ │             │ │ 共 1 项 · 213.5 MB；已选 1 项 · 213.5 MB  [删除选中项 (1)]  │ │  汇总 + 页内操作
│ └─────────────┘ └────────────────────────────────────────────────────────────┘ │
│ ┌─ 预演完成，尚未删除任何文件。现在执行「删除 1 个项目缓存（213.5 MB）」？ ─────┐ │  琥珀色确认条
│ │                                                  [取消] [执行删除]           │ │
│ ├──────────────────────────────────────────────────────────────────────────────┤ │
│ │ 就绪 · 扫描用时 3.2 秒                              [取消当前任务][开始扫描] │ │  底部操作栏
└────────────────────────────────────────────────────────────────────────────────────┘
```

**分页与收纳式功能栏**（v0.4）：10 个功能各占一页，左侧功能栏可折叠（`☰`，状态记在
`%LOCALAPPDATA%\CLean\ui.txt`）：展开时显示序号 + 名称 + 一行说明，折叠后只剩数字，
页面因此多出约 230 px 宽度。页内布局固定为「标题 + 模式徽章 / 说明 / 该功能自己的选项 /
内容区」，删除类页面的按钮就在内容区下方，不再挤在底部一条。

**扫描动画与结果勾选**：两个扫描页（全盘大目录、隐藏项目缓存）在扫描时显示自绘动画条
（雷达扫掠 + 实时计数：已扫描文件数 / 目录数 / 体积 / 速度 / 当前目录），扫描完成后把
**文件夹清单**列出来：勾选框 + 名称 + 父路径 + 体积 + 相对体积条，支持「全选 / 全不选 /
仅选大项」和空格键切换。下方汇总实时显示「共 N 项 · X GB；已选 M 项 · Y GB」。删除走的是
**和命令行完全相同的两段式契约**：点「删除选中项」只发起**预演**（`04-Clean-Hidden.ps1 -Paths …`
不带 `-Execute`，逐条重新过白名单/黑名单并测体积），预演成功后才弹出确认条，点「执行删除」
才真的删。取消则一个文件都不动。

**内置快速扫描器**（v0.4，`src\GuiPages.cs` 的 `FastScan`）：窗口版的扫描不再调用
`05-Scan-LargeDirs.ps1` / `04-Clean-Hidden.ps1` 的 `Get-ChildItem` 遍历，而是直接
`FindFirstFileEx`（`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`，即 WizTree/Everything
那一套：不逐个打开文件、目录缓冲 64 KB），并按顶层子树并行（`ProcessorCount` 线程）。
长路径自动加 `\\?\` 前缀，重解析点只计数不进入（和脚本同一条规则），无权访问的目录计入
`Denied` 而不中断。

| 对比（同一台机器，同一时刻顺序执行） | 结果 |
|---|---|
| **精度**：`C:\Windows\WinSxS`（116,158 文件 / 49,821 目录） | 内置扫描器 **12,233.3 MB / 116,158 文件**；PowerShell `Get-ChildItem -Recurse \| Measure Length` **12,233.3 MB / 116,158 文件** —— 完全一致（差值 0.0 MB） |
| **速度**：同一目录 | 内置扫描器 **6.9 秒**；旧 `05-Scan-LargeDirs.ps1` **38.4 秒**（≈5.6×）；`Get-ChildItem` 参考实现 23.0 秒 |
| 隐藏项目缓存：整个用户目录（21.3 GB / 88,446 文件 / 4,751 目录） | **0.95 秒**，命中 `…\npm-cache\_npx\…\node_modules` 213.5 MB |
| 大目录分析：`AppData\Local`（10.5 GB / 69,752 文件） | **1.84 秒**，元凶前列：Edge 用户数据 728 MB、WPS addons 705 MB×2、QQ Guild 531 MB |

**「定位元凶」怎么算**：单遍遍历后只保留 ≥ 阈值的目录节点，再**只报「最下面那一层」**——
某个目录之所以大往往只是因为它的子目录大，所以「有更大的子节点」的目录会被剔除，剩下的才是
真正吃掉空间的那几个（旧脚本按固定深度聚合，永远只会报那三个顶层目录）。每行还画出相对体积条。

**每个功能自己的动画，页面不再显示日志**（v0.5）：页面里原来会铺脚本原始输出（尾行窗），
现在**全部去掉** —— 每个页面在标题/选项下方有一条属于该功能的**自绘动画条**（`GuiAnims.cs`），
它同时承担状态显示：空闲时是「就绪 · 功能名」+ 一句提示，运行时变成实时计数
（`已用 N 秒 · 正在运行 xx.ps1 · 成功 a · 警告 b`），结束后报「完成 · 退出码 0」或失败原因。
动画**始终在跑**（只有当前显示的页面开定时器，隐藏页不占 CPU），所以一眼就能看出这页在做什么。

| 页面 | 动画（各不相同的自绘图形） |
|---|---|
| 空间分析 | 磁盘环 + 扫掠指针 + 底部柱图 |
| 系统缓存清理 | 一格格的缓存方块被扫掠线擦掉 |
| 无用缓存发现 | 放大镜在点阵上巡游，镜内格子放大高亮 |
| 清理清单一览 | 纸张逐行把清单打印出来（带光标） |
| 显卡着色器缓存 | 旋转线框三角 + 顶点脉冲 + 状态灯 |
| C盘根目录残留 | 根节点分出枝叶，叶子逐个掉落（剪枝） |
| 开发环境缓存 | 包裹堆叠上下浮动 + 清理箭头脉动 |
| 全盘大目录分析 | **实时柱状图**：扫描中不断长出新的大目录柱 + 扫描线 |
| 隐藏项目缓存扫描 | **实时瓷贴**：每找到一个缓存目录就弹出一块瓷贴（带迷你体积条） |
| 空间反升诊断 | 卷影区柱体增长 + 滚动波形线 |

最后两个动画直接吃扫描线程的实时数据（`ScanRun.ReportHit` / `LiveHits`），所以不需要等扫描结束，
成果就在眼前一块块出现。

**脚本类页面的结果卡**：运行结束后页面内容区显示一张结果卡（`SummaryView`），给出
状态（完成 / 已取消 / 退出码）、用时、`成功 a · 警告 b · 失败 c`，以及**最后一条关键结论**
（取自脚本自己打印的 `[+]` 行，例如 `GUARANTEE HELD: C: used space shrank by 47.0 MB …`）。
页面不再铺日志流，但「这次成没成、结论是什么」一目了然。

**输出日志独立成窗口**：header 的 `[输出日志]` 按钮是查看原始输出的**唯一入口**（独立窗口，
`LogWindow`）：支持自动滚动、清空、保存到文件（UTF-8 带 BOM，记事本不会乱码），并跟随主题换色。
窗口没开时按钮上显示未读行数（`输出日志 • 14`），打开后清零；扫描/清理的每一条原始输出
仍然完整地记在里面，只是不再挤在功能页面上。

**视觉与实现**（全部代码绘制，无设计器、无图片资源、无第三方库）：

| 元素 | 做法 |
|---|---|
| 卡片 / 圆角 | `Card : Panel` 自绘圆角矩形 + 1px 描边，先铺父容器底色再画圆角（圆角处不留白块） |
| 按钮 | `FlatButton` 自绘：primary（强调蓝）/ ghost（白底描边）/ danger（红），带 hover / pressed / disabled 三态 |
| 清理深度、档位 | `PillRadio`：选中＝实心药丸，未选中＝白底描边；hover 变强调色边框 |
| 各种开关 | `PillCheck`：圆角方框 + 手绘勾，选中＝强调蓝 |
| 功能栏（收纳） | `RailButton`：展开＝序号胶囊 + 名称 + 一行说明，折叠＝只剩胶囊；选中＝浅强调底 + 左侧强调条 |
| 每功能动画 | `AnimBase` + 十个 `DrawMotif` 子类：统一深色圆角面板 + 左侧 68×68 动画方块 + 标题/实时详情 + 运行时的滑动进度线；空闲/运行/完成三种状态 |
| 结果卡 | `SummaryView`：浅灰圆角面板 + 状态圆点 + 状态/用时/计数/关键结论 + 一句"原始输出在日志窗口" |
| 结果清单 | `ResultList`（`DrawMode.OwnerDrawFixed`，行高 46）：勾选框 + 名称 + 父路径与文件数 + 右对齐体积 + 相对体积条 |
| 用量条 | `UsageBar`：圆角轨道 + 按占用率变色（<80% 强调色 / ≥80% 橙 / ≥92% 红） |
| 运行指示 | `ActivityBar`：自绘来回滑动的强调色段（取代系统 ProgressBar） |
| 日志窗口 | 独立 `Form`（`LogWindow`）：深色 `Card` + 等宽 TextBox + 自动滚动/清空/保存到文件；跟随主题换色 |
| 外观菜单 | `FlatMenuRenderer`（自绘 `ToolStripRenderer`）：没有系统渐变、没有系统勾选块，强调色项左边画出真实色块 |
| 无障碍 | 自绘控件把可见文字写进窗口文本（`AnimBase.Publish` / `SummaryView.Publish`），否则读屏软件与自动化只能看到一片无名面板 |
| 字体 | Microsoft YaHei UI（标题 16pt / 区块 10.5pt / 正文 9pt / 小字 8.25pt），日志 Consolas 9pt |
| 闪烁 | 所有自绘控件开 `AllPaintingInWmPaint + OptimizedDoubleBuffer + ResizeRedraw`，窗体 `DoubleBuffered`；功能栏与结果列表另见下方说明 |

#### 颜色主题（v0.3 新增，仅窗口版）

header 上的 `[外观 ▾]` 打开外观菜单，两组单选：

- **浅色 / 深色 / 跟随系统**：默认 **跟随系统**（跟随 Windows 的"应用"明暗设置）；
- **强调色**：蓝 · 青 · 绿 · 橙 · 紫 · 玫红（默认蓝），菜单里每项左边画出该强调色的真实色块。

选完立即生效、**不用重启**，并写入 `%LOCALAPPDATA%\CLean\theme.txt`：

```ini
mode=system      # system | light | dark
accent=blue      # blue | teal | green | orange | purple | rose
```

删掉这个文件就回到出厂默认（跟随系统 + 蓝）。文件读不出来/写不进去都不算错误：读失败用默认值，
写失败只影响"下次记住"。

| 项 | 做法 |
|---|---|
| 调色板 | `Theme` 只是门面（`Theme.Accent` / `Theme.Card` …），值全部来自当前 `Palette`；`Palette` 由 `Build(mode, accent)` 从两组基础色 + 强调色**算出来**，没有逐主题手写的配色表 |
| 切换时机 | `ApplyPaletteAll()` 遍历整棵控件树：缓存了颜色的控件实现 `IThemed.ApplyPalette()`，其余控件按 `Tag` 里的颜色角色（如 `card\|muted`）重新上色 —— **首帧和每次切换走同一条路径**，不存在"只有刚启动才对"的主题 |
| 文字对比度 | `Theme.On(color)` 按感知亮度自动挑墨色：深色模式的强调色偏亮，按钮/药丸/勾选框上用近黑字（`#12141A`），浅色模式用白字 —— 新增强调色不可能配出读不清的组合 |
| 跟随系统 | 读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`（`0` = 深色，值缺失按浅色）。窗口开着时每 **1.5 秒**轮询一次，变化就重建调色板并整窗重绘；用轮询而非 `SystemEvents`，是因为系统按计划切换明暗时**根本不发消息**，而且轮询不需要额外消息泵 |
| 标题栏 | 深色模式用 `DwmSetWindowAttribute(20)`（1809/1903 回退到 `19`）把标题栏也压深。失败只是外观问题，功能不受影响 |
| 边界 | 日志面板在两种模式下**都保持深色**（设计如此：它就是个控制台）；主题只改颜色 —— 不碰任何清理参数、净空间守卫与安全契约 |

实测（2026-09-21，真实窗口截图 + 逐像素取值，`Bitmap.GetPixel`，隔点采样 262,260 px）：

| 用例 | 期望 | 实测 |
|---|---|---|
| 浅色 + 蓝 | 卡片 `#FFFFFF` / 背景 `#F3F5F9` / 强调 `#2B6CF6` | 148,598 / 19,785 / **3,867** px ✓ |
| 深色 + 蓝 | 卡片 `#161922` / 背景 `#0E1014` / 强调 `#6E9CFF` | 148,173 / 19,554 / **3,867** px ✓（浅色背景 0 px） |
| 深色 + 紫（存档后重启读回） | 强调 `#A992FF` | **3,867** px ✓ |
| 菜单点「深色」 | 当场变深、`theme.txt` 变 `mode=dark` | ✓（浅色背景 0 px） |
| 菜单点「绿」 | 当场换色、`theme.txt` 变 `accent=green` | ✓（新强调 3,867 px，旧蓝 0 px） |
| 跟随系统 + 运行中把系统切成深色 | 1.5 秒内整窗跟随 | ✓（窗口内浅色背景 0 px） |

> 六种强调色下"强调色像素"都是 **3,867 px** —— 同一套几何、只换色相，这也是"换主题不是重新布局"的旁证。
> 控制台版（`CLean.exe`）没有主题功能：它的配色由终端决定。

> ⚠️ 自绘 `Button` 派生控件时**不要调用 `base.OnPaint`**：`Button.OnPaint` 会把系统默认外观
> （背景、文字、焦点框）画在你的自绘内容**之上**，结果是"代码里明明是强调蓝按钮，运行时却是白底黑字"。
> 第一版就是这样翻车的，靠逐像素取色（`Bitmap.GetPixel`）比对才发现。

> ⚠️ 功能列表（`ActionListBox`）的 hover 高亮**闪了三次才修对**，走过的路都记在这里：
>
> 1. **整控件 `Invalidate()` → 整列闪白。** `AllPaintingInWmPaint + OptimizedDoubleBuffer` 对它
>    **无效**：`ListBox` 是原生 comctl32 控件，行是它自己画的，这两个 style 只对 WinForms 亲自
>    绘制的控件（`UserPaint`）生效。整控件作废会让它先擦白整块客户区再重画 10 行。
>    → 改成只重画 hover 状态变化的那 1～2 行（`GetItemRectangle`），并用
>    `InvalidateRect(..., bErase: FALSE)` 请求（不带擦除标志；`OnDrawItem` 本来就会整行铺底色，
>    跳过擦除不留残影）。
> 2. **单个 row 重画 → 那一行的文字还在闪。** 普通重画是"先把该行擦成背景色，再画高亮和文字"，
>    原生控件的行绘制没有双缓冲，每次跨行都能看见文字消失一瞬。
> 3. **离屏渲染 + `DrawImageUnscaled` 贴图 → 文字变样了（发虚/发毛）。** 实测：贴图行的编号胶囊
>    与平台绘制的结果在 961 个像素里有 430 个不同、最大通道差 237，文字从"有抗锯齿"变成
>    "没有抗锯齿"（`core<200` 像素 201 → 586，中间调 296 → 0）——GDI 文字画在 GDI+ 位图 DC 上
>    不走字体平滑，与窗口 DC 的渲染不是同一套。**这条路放弃。**
>    另外两个坑：`TextRenderer.DrawText` 底层是 GDI、**忽略 Graphics 变换**，所以位图里必须用
>    0 基坐标而不是 `TranslateTransform`；`WS_EX_COMPOSITED` **不能挂在 ListBox 上** ——
>    `CreateWindowEx` 对该窗口类直接拒绝，句柄创建失败（`创建窗口句柄时出错`），列表整个消失。
>
> ✅ 最终做法是**让系统合成整个窗口**：`MainForm` 用 `CreateParams` 加上 `WS_EX_COMPOSITED`
> （挂窗体上没问题），渲染路径一个字节都没改，所以文字/图形与从前逐像素一致（实测高亮行与
> 平台绘制最大通道差 15，正好只等于底色 255→240 的差），而"擦除后还没画完"的半成品帧由窗口
> 管理器挡在屏幕外。第 1 步的单行重画保留，因为它把每次 hover 的重画量压到最小。


**删除类功能的两段式流程**（窗口版的核心，也是唯一会删文件的路径）：

1. 点页内主按钮（`[开始预演（不删除文件）]` 或扫描页的 `[删除选中项 (N)]`）—— 实际命令
   **不带 `-Execute`**（例：`02-Clean-System.ps1 -Profile standard`，
   或 `04-Clean-Hidden.ps1 -ScanRoots '…' -Paths '…'`）；
2. 输出实时滚动，脚本自己标出「被占用、不会释放」的项（`04` 在 PLAN 模式下逐条重新过白名单
   与黑名单，并把被拒绝的路径标成 `REFUSED`）；预演**正常结束（退出码 0）且未被取消**时，
   底部才出现琥珀色确认条（按钮变为可用，状态栏同时改为「等待确认」）；
3. 点确认条上的 `[执行删除]` 才以 `-Execute` 重跑同一命令；点 `[取消]` 则一个文件都不删。

选项面板按页面各管各的：只有 `系统缓存清理` 有清理深度/档位与 explorer/回收站开关，
只有 `C盘根目录残留` 有 HLS / 酷狗 / `$WinREAgent` 开关，扫描页有自己的范围/阈值/深度。
`C盘根目录残留` 默认只勾选两个安全缓存（HLS、酷狗 P2P），`$WinREAgent`（更新回滚数据）默认不勾；
三项全不勾时会被拦下并提示，避免"确认条走完却什么都没做"。选项旁都有 ToolTip 说明。

**实时输出与取消**：子进程用
`-Command "[Console]::OutputEncoding=UTF8; & '<脚本>' <参数>"` 启动，stdout/stderr 双流异步读取后
追加到日志（因此中文不乱码，`05` 这种自己没设 UTF-8 的脚本也一样）；运行中状态栏显示
`运行中 · 功能名 [预演] · 已用 N 秒`，`[取消当前任务]` 用 `taskkill /T /F` 终止整棵进程树，
脚本自身的净空间守卫仍会如实记录已发生的变化。扫描页的 `[取消当前任务]` 则在扫描线程内
协作取消（`ScanRun.Cancel`），立即停手，不会留下半个列表。

**审计命令**：

```powershell
.\CLean-GUI.exe --print-plan
# 把每个页面在「预演 / 执行」两阶段的真实命令写入（不碰磁盘，随时可跑）
#   %LOCALAPPDATA%\CLean\gui-command-plan.txt   (UTF-8 带 BOM)

.\CLean-GUI.exe --scan-dirs  C:\Users      # 只读：大目录分析（内置扫描器）
.\CLean-GUI.exe --scan-cache C:\Users      # 只读：项目缓存扫描
# 两者都写 %LOCALAPPDATA%\CLean\runtime\reports\scan-cli.json，
# 用于核对扫描器的体积/文件数与耗时（上面的精度对比就是这么测的）
```

**崩溃排查**：界面异常写入 `%LOCALAPPDATA%\CLean\gui-crash.log`（含完整堆栈），
同时弹窗告知路径。

#### MVP 边界（窗口里没有暴露的参数）

以下参数仍只在命令行可用，窗口版不提供（属刻意收窄，避免误点）：

| 未暴露 | 原因 |
|---|---|
| `-Only` / `-Skip` 目标级筛选 | 需要先看 `-ListCatalog` 的目标 id，文本输入框留到下一版 |
| `-ShadowTrim`（回收卷影副本，**销毁旧还原点**） | 破坏性太强，不适合一个按钮 |
| `-IncludeComponentStore`（DISM，10 分钟级） | 耗时长、进度不可见 |
| `-NoGuard` / `-NoAbortOnRegression` | 等于关掉净空间守卫，窗口版不提供 |
| `05 -Top` / `-Root`，`02 -DiscoverMinMB` | 扫描页用自己的扫描器与阈值选项，这两个脚本参数只留给控制台版 |
| `04 -MinMB` / `-Depth` | 扫描页给的是「最小体积 / 扫描深度」三档预设，不开放任意数值 |

> 窗口版 v0.4 起，两个扫描页**不再调用** `05-Scan-LargeDirs.ps1` / `04-Clean-Hidden.ps1`
> 的扫描逻辑（改用内置 C# 扫描器，见 1.1），但**删除仍然交给 `04-Clean-Hidden.ps1 -Execute -Paths`**
> —— 白名单、黑名单、净空间实测这些守卫一条都没绕过。控制台版继续使用脚本自身的扫描。

需要这些时用控制台版或直接跑 `.ps1`。

### ⚠️ 关于杀毒软件 / SmartScreen

未签名、会自我提权、会删除系统缓存目录 —— 行为特征与恶意软件相似，可能被 Defender / 360
误报，首次运行可能触发 SmartScreen。源码完全公开可审计（`src\Launcher.cs` + 10 个 `.ps1`），
被拦截时选「仍要运行」。

### 重新打包

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\src\Build.ps1                                  # 控制台版 -> CLean.exe
.\src\Build.ps1 -Gui                             # 窗口版   -> CLean-GUI.exe
.\src\Build.ps1 -Gui -OutputName '我的清理工具.exe'
```

用系统自带 `csc.exe`（`%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\`），
**不需要 .NET SDK / NuGet / 联网**。窗口版由三个源文件编译而成
（`GuiLauncher.cs` 外壳与主题/绘图基元，`GuiPages.cs` 分页、扫描器与日志窗口，
`GuiAnims.cs` 十个功能的动画与结果卡），`Build.ps1` 的 `$extraSources` 负责一起送进 csc。
三个关键细节：

- `/codepage:65001`：`Launcher.cs` / `GuiLauncher.cs` 无 BOM 且含中文字面量，不加此参数 csc 按
  ANSI(936) 解析，中文全乱码。
- **在 ASCII 临时目录中编译**，再把成品复制成最终文件名：PS 5.1 向原生命令传参按控制台码页转换，
  中文路径会损坏并报 `CS2021`。

`-Gui` 额外引用 in-box 的 `System.Windows.Forms.dll` / `System.Drawing.dll` 并改用 `/target:winexe`
（无控制台窗口），其余流程完全相同。

> ⚠️ **`src\Build.ps1` 保持纯 ASCII。** 它以前含中文产物名，而 Windows PowerShell 5.1 会把无 BOM 的
> `.ps1` 按 ANSI(936) 解析 —— 中文会变成带 `?` 的乱码文件名，`Copy-Item` 只会报「路径中具有非法字符」。
> v1.0.0 把产物名改成 ASCII（`CLean.exe` / `CLean-GUI.exe`）之后这个坑消失了；脚本仍保留那道检查：
> `-OutputName` 里一旦出现非法字符就直接点明原因。

`Build.ps1` 会自动收集项目根目录下**所有** `*.ps1` 并逐个校验是否成功内嵌，新增脚本无需改构建脚本。

#### 版本号只有一个来源

仓库根目录的 `VERSION`（内容形如 `1.0.0`）是唯一来源：

- `src\Build.ps1` 读它，生成 `VersionInfo.cs` 到**临时编译目录**（不落库、不会漂移），
  并写入 `AssemblyVersion` / `FileVersion` / `ProductName` 等程序集元数据；
- 编译末尾会**回读程序集版本**与 `VERSION` 比对，不一致就直接构建失败；
- `installer\CLean.iss` 读同一个文件，也支持 `/DMyAppVersion=x.y.z` 覆盖（CI 用它）。

所以「程序里显示的版本」和「安装包 / 更新检查比较的版本」不可能不一致。

#### 打安装包

```powershell
winget install -e --id JRSoftware.InnoSetup        # 一次性：装 Inno Setup 6
.\src\Build.ps1; .\src\Build.ps1 -Gui              # 先产出两个 EXE（安装包从这里取）
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\CLean.iss
# -> dist\CLean-Setup-1.0.0.exe
```

`installer\CLean.iss` 按**当前用户**安装（`PrivilegesRequired=lowest`，`{autopf}` 因此解析为
`%LOCALAPPDATA%\Programs\CLean`），带卸载程序、开始菜单两项、可选桌面图标；卸载**不删**
`%LOCALAPPDATA%\CLean`（用户数据）。

#### 发版

```powershell
# 1. 改 VERSION（例如 1.0.1）并提交
# 2. 打 tag 推上去 —— 剩下的交给 GitHub Actions
git tag v1.0.1
git push origin main --tags
```

`.github/workflows/release.yml` 在 `windows-latest` 上执行：读 `VERSION` 并**校验 tag 与之一致**
（不一致直接失败，免得发出一个版本号对不上的包）→ 用 runner 自带的 `csc.exe` 编两个 EXE
（**仍然不需要 .NET SDK / NuGet**）→ `choco install innosetup` → 编译安装包 → 发 Release，
附上安装包**和**两个单文件 EXE。

只推 `main` 不发 Release：同样构建，把安装包当 workflow artifact 传上去，方便先验证。

---

## 二、为什么 C 盘空间会「反升」（根因，实测）

这是 v1 的核心问题：**删了 11.06 GB，可用空间只涨 3.71 GB**，有时甚至当场下降。
原因不是统计错误，而是三个真实机制，现已全部被工具量化。

### 2.1 卷影副本（系统还原）的写时复制 —— 最大的一项

C 盘开启了系统还原。被删数据块**仍被还原点引用**，因此：

- 删除只是解除目录引用，**还原点还占着那些块**；
- 一旦这些簇被重新使用（Windows 更新重下、浏览器缓存、索引、页面文件），
  VSS 会先把**快照里的旧内容**复制进 diff 区，于是刚刚"释放"的空间又被吃掉；
- diff 区就在 C 盘上，静默增长直到上限（本机上限 10 GB）。

本机实测（`vssadmin list shadowstorage` + 受控写入探针）：

| 项目 | 数值 |
|---|---|
| 卷影副本数量 | 3 个（2026-09-11、09-14×2） |
| diff 区占用 / 上限 | **8.59 → 8.68 GB / 10 GB（86.8%）** |
| 写入 800 MB 后 diff 区增长 | **+87.3 MB** |
| 探针文件删除后 diff 区 | **仍为 +87.3 MB（永不归还）** |
| 结论 | 一次 800 MB 的写入/删除循环，**永久吃掉 87.3 MB** |

也就是说：清理放出的簇被系统重用一次，就有一部分被折算成 diff 区的永久占用。
**清理 ≠ 释放**，除非同时处理卷影副本。

### 2.2 句柄未释放（delete-pending）—— 本次实测抓到

NTFS 上"删除成功"≠"空间归还"：文件被别的进程打开时，unlink 之后其簇会保留到最后一个句柄关闭。

- `C:\Windows\SystemTemp` dry-run 显示可选 **182.2 MB**，实际执行只释放 **0.2 MB**（13 项被占用）；
- 缩略图缓存 `thumbcache_*.db` 被 explorer 持有：30 个文件全部"删除成功"，**净释放 0.0 MB**。

v1 会把这两项记成"已释放 182.2 MB / 9.0 MB"，这就是数字虚高的直接来源。
现在 dry-run 会**逐个试探独占打开**，提前标出"被占用、不会释放空间"的项（实测预测 0.0 MB vs 实际 0.2 MB，吻合）。

### 2.3 缓存会重新长出来（再生）—— 现场抓到的最强证据

被删目录属于某个服务，服务会立刻重建。诊断脚本在本机**抓到了完整的再生事件**（2026-09-16 14:10→14:11，相隔约 1 分钟）：

| 目录 / 指标 | 14:10 | 14:11 | 变化 |
|---|---|---|---|
| `SoftwareDistribution\Download` | 0 MB | **803.9 MB** | 重下 |
| 传递优化 `DeliveryOptimization\Cache` | 0 MB | **4,958.9 MB** | 重下 |
| C 盘**已用**空间 | — | — | **+480 MB（空间反升）** |
| 卷影副本 diff 区 | 8.68 GB | 8.85 GB | +170 MB（写时复制） |
| 服务状态 | `wuauserv` Stopped | Running + TrustedInstaller + DoSvc | 更新服务已启动 |

也就是说：**09-16 那次清掉的 5,114 MB 传递优化缓存，被 Windows 原样下回来了**，
而且在下回来的过程中 C 盘占用是**上升**的。这正是"越清越满"的真相。

继续跟踪 3 秒采样（工具新增的预检）：

```
update caches now: 6,189.9 MB   (change over 3 s: 35.8 MB)   running: DoSvc,TrustedInstaller,wuauserv
[!] Windows is DOWNLOADING UPDATES RIGHT NOW.
[!] Cleaning the update caches now will be undone within minutes and C: used
[!] space will grow - this is the "cleaned it and it got fuller" case.
[!] Recommended: pause updates first, or exclude these targets:
[!]    -Skip wu.*,do.*,bits.*
```

因此 `02-Clean-System.ps1` 现在会在清理前**自动做这项预检**（采样 3 秒，看更新缓存是否在增长），
发现正在下载就明确告知：现在清等于白清。其它再生情况：

| 目录 | 09-16 清理后 | 现状复查 |
|---|---|---|
| Prefetch | 0.6 MB | 1.1 → 1.4 MB |
| 缩略图缓存 | 0.3 MB | 9.3 MB（被 explorer 持有句柄，见 2.2） |
| WebCache | — | 17.5 MB（**v1 从未清理此项**） |
| Edge Cache + Code Cache | — | 663.3 MB |

**结论：更新缓存不是"清一次就干净"的东西。** 要么先暂停更新再清，要么接受它会被下回来。

### 2.4 逻辑大小 ≠ 实际占用

v1 的"释放量"是 `FileInfo.Length` 求和。这会高估：硬链接（删一个链接释放 0 字节）、
NTFS 压缩、稀疏文件、以及上面 2.1/2.2 的两种情况。本机 `Downloader` 样本中硬链接数为 0，
但机制存在，现已加入硬链接筛选（`fsutil hardlink list`，只对 ≥1 MB 的文件抽样探测）。

### 2.5 结论与对策

| 机制 | 工具对策 |
|---|---|
| 卷影副本写时复制 | 每目标实测净空间；报告 diff 区增长；`-ShadowTrim` 可主动回收（**会失效旧还原点**） |
| 句柄未释放 | dry-run 预判被占用项；`-RestartExplorer` 解锁缩略图；执行后标注 delete-pending |
| 缓存再生 | 清理前自动预检"更新是否正在下载"并告警；`-Skip wu.*,do.*,bits.*` 排除 |
| 逻辑大小虚高 | 净空间（NetFreeMB）为唯一口径；硬链接文件直接跳过 |

**一句话结论**：v1 的"释放 11.06 GB"是**逻辑大小**的账面数字，而 C 盘实际先涨后落、
净收益远小、甚至为负；真正的元凶是**卷影副本写时复制**、**句柄未释放**与**更新缓存再生**三者的叠加。
v2 把这三项全部变成可测量、可中止、可归因的机制。

---

## 三、脚本清单

| 脚本 | 作用 | 是否删除 |
|---|---|---|
| `_Common.ps1` | 安全底座：白名单/黑名单、**净空间守卫**、卷影副本探测、硬链接探测、点到点删除引擎 | — |
| `_SystemCatalog.ps1` | **系统无用缓存清单**（19 / 49 / 51 条目对应保守/标准/完整档位）+ 缓存目录发现 | — |
| `01-Analyze.ps1` | 只读分析：磁盘 + 固定清单 + **清单级缓存汇总** | ❌ |
| `02-Clean-System.ps1` | **系统缓存清理（已重写）**：清单驱动、点到点、净空间保证 | 需 `-Execute` |
| `03-Clean-Dev.ps1` | 开发缓存（npm / pnpm / pip / yarn / go 等） | 需 `-Execute` |
| `04-Clean-Hidden.ps1` | 隐藏项目缓存扫描（node_modules / `.cache` / venv）；`-Paths` 不带 `-Execute` = 逐条预演计划，带 `-Execute` 才删 | 需 `-Execute -Paths` |
| `05-Scan-LargeDirs.ps1` | 单遍全盘扫描，定位大目录 | ❌ |
| `06-Clean-RootLeftovers.ps1` | C:\ 根目录残留（`$WinREAgent` / HLS 缓存 / 酷狗 P2P） | 需 `-Execute` |
| `07-Clean-GpuShaderCache.ps1` | 显卡检测（N 卡 / A 卡）+ 对应着色器缓存 | 需 `-Execute` |
| `08-Diagnose-SpaceGrowth.ps1` | **空间反升诊断**：量化第二节的每个机制 | ❌ |

## 四、用法

Windows 默认禁止运行未签名脚本，请用**进程级** Bypass（不修改系统策略）：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
```

### 4.1 先看，再删

```powershell
# 只读：清单里有什么
.\02-Clean-System.ps1 -ListCatalog -Profile standard

# 只读：扫描"清单之外"的缓存目录（这是 v2 新增的发现能力）
.\02-Clean-System.ps1 -Discover -DiscoverMinMB 20

# 只读：为什么清完反而更满
.\08-Diagnose-SpaceGrowth.ps1

# dry-run：打印每个目标"可能释放"，并标出被占用、不会释放的项
.\02-Clean-System.ps1

# 只读：隐藏项目缓存的「预演计划」（给 -Paths 但不给 -Execute = 只测不删，
# 逐条过白名单/黑名单并把被拒绝的路径标成 REFUSED；报告写 hidden.dryrun.json）
.\04-Clean-Hidden.ps1 -ScanRoots $env:USERPROFILE -Paths "$env:USERPROFILE\proj\node_modules"
```

### 4.2 执行（逐目标实测净空间，反降即中止）

```powershell
.\02-Clean-System.ps1 -Execute                     # 标准档位 + 1 天时限
.\02-Clean-System.ps1 -Execute -Profile safe       # 最保守：临时文件/日志/转储
.\02-Clean-System.ps1 -Execute -Profile aggressive -Discover   # 含发现的未列清单缓存
.\02-Clean-System.ps1 -Execute -TempAgeDays 0      # 连今天的临时文件一并删
.\02-Clean-System.ps1 -Execute -Only temp.user,logs.windows,dump.livekernel
.\02-Clean-System.ps1 -Execute -Skip browser.*,app.*
.\02-Clean-System.ps1 -Execute -RestartExplorer    # 解锁缩略图缓存（会重启 explorer）
.\02-Clean-System.ps1 -Execute -IncludeRecycleBin
.\02-Clean-System.ps1 -Execute -IncludeGpuShader -IncludeD3DSCache
.\02-Clean-System.ps1 -Execute -IncludeComponentStore   # DISM 组件存储清理（10 分钟级）
.\02-Clean-System.ps1 -Execute -ShadowTrim -ShadowTargetGB 2   # 回收卷影副本空间（销毁旧还原点）
```

清理档位含义：

| 档位 | 内容 |
|---|---|
| `safe` | 只有绝对无争议的：临时文件、日志、转储、WER、更新缓存、缩略图、INetCache、字体缓存 |
| `standard`（默认） | 再加浏览器各配置的 Cache / Code Cache / GPUCache（**不含** Cookie、登录态、历史） |
| `aggressive` | 再加应用缓存发现结果、搜索索引等重建代价高的项 |

### 4.3 净空间保证是怎么工作的

1. 每个目标删除**前**读一次卷可用空间，删除**后**（等 800 ms）再读一次；
2. 该目标的 `NetFreeMB` = 两次之差，同时记录卷影副本 diff 区的增长；
3. 累计口径一旦出现**下降超过 16 MB**（`-RegressionToleranceMB`，噪声容差）→
   **立即中止剩余所有目标**，并在报告中写明是哪个目标、降了多少；
4. 结束时报告 `GAIN / NEUTRAL / REGRESSION` 与"C 盘已用空间变化"，
   只有 `GAIN` 才算成功；`REGRESSION` 会明确提示去跑 `08`。

想观察"不中止"的行为用 `-NoAbortOnRegression`；想完全关掉守卫用 `-NoGuard`（不推荐）。

### 4.4 参数速查（02）

| 参数 | 作用 |
|---|---|
| `-Profile safe\|standard\|aggressive` | 清理档位（默认 standard） |
| `-Discover` / `-DiscoverMinMB` / `-DiscoverTop` | 只读：发现未列清单的缓存目录 |
| `-ListCatalog` | 只读：打印全部目标（含风险、模式、时限、是否需管理员） |
| `-Only` / `-Skip` | 按 id 或组筛选，支持通配符 |
| `-TempAgeDays` | 临时文件时限（默认 1 天） |
| `-RestartExplorer` | 重启 explorer 以解锁缩略图缓存 |
| `-IncludeRecycleBin` / `-IncludeGpuShader` / `-IncludeD3DSCache` | 可选附加项 |
| `-IncludeComponentStore` | DISM `StartComponentCleanup`（WinSxS） |
| `-ShadowTrim` / `-ShadowTargetGB` | 回收卷影副本空间（销毁旧还原点） |
| `-RegressionToleranceMB` / `-NoAbortOnRegression` / `-NoGuard` | 守卫行为 |

---

## 五、缓存查找能力（v2 相对 v1 的实测提升）

v1 硬编码 12 个目录，整目录清空。v2 使用 `_SystemCatalog.ps1` 的准入规则：
**确切目录 + 确切文件名模式 + 排除项 + 时限 + 风险级别 + 需要停哪些服务**。

### 5.1 本机同一时刻的对比

| 项目 | v1 | v2 |
|---|---|---|
| 目标数量（standard） | 12（硬编码） | **49** |
| dry-run 计划释放 | 7.3 MB | **1,408 MB** |
| 未列清单缓存发现 | 无 | **17 个目录 / 1,963.7 MB** |

v1 完全没碰、v2 发现的实例：

| 目标 | 体积 | 说明 |
|---|---|---|
| `C:\Windows\SystemTemp` | 182.2 MB | Win11 系统暂存目录，v1 清单里没有 |
| `AppData\Roaming\360se6\...\Cache` | 369.7 MB | 360 浏览器缓存 |
| `%LOCALAPPDATA%\Microsoft\Windows\WebCache` | 17.5 MB | v1 把它写进白名单却**从未真正清理** |
| `C:\Windows\ServiceProfiles\LocalService\...\FontCache` | 40.4 MB | 字体缓存 |
| `C:\ProgramData\Microsoft\Diagnosis` | 22.8 MB | 遥测暂存 |
| `SoftwareDistribution\DataStore\Logs` | 10.0 MB | 更新数据库日志 |
| `AppData\Roaming\bilibili\Cache` | 332.6 MB | 发现模式命中 |
| `AppData\Local\ToDesk\Logs` | 184.1 MB | 发现模式命中 |
| `AppData\Local\Doubao\User Data\...\log` | 166.3 MB | 发现模式命中 |
| `AppData\Roaming\Tencent\QQLive\Log` | 165.1 MB | 发现模式命中 |

### 5.2 点到点到底"点"到什么程度

- 单文件目标：`C:\Windows\MEMORY.DMP` 通过 `Add-AllowedFile` **精确登记单个文件**，
  避免把 `C:\Windows` 整个目录变成可删根。
- 模式目标：`C:\Windows\Logs` 只删 `*.log/*.etl/*.cab/*.txt/*.old/*.bak`，且必须早于 1 天。
- 目录目标：`thumbcache_*.db`、`iconcache_*.db` 只删这两个模式，同目录下其他文件不动。
- 删完文件后**只清理因此变空的子目录**，绝不整树删除未知内容。
- 不跟随也不删除重解析点（junction / symlink），记入 `reports\skipped.log`。
- `C:\Windows\System32\LogFiles` **故意不列入**：黑名单禁止 System32 之下任何路径，
  这条安全底线比那几 MB 更值钱。

安全底线仍然生效：每个目标都必须命中 `Add-AllowedRoot` / `Add-AllowedFile`，
否则 `REFUSED` 拒绝执行（本机已验证：白名单外的路径直接被拒）。

---

## 六、验证记录（2026-09-16）

### 6.1 真实执行（保守子集）

```powershell
.\02-Clean-System.ps1 -Execute -Only logs.windows,logs.telemetry,shell.thumbnails,temp.system
```

| 目标 | 净释放 | 逻辑删除 | 删除项 | 被占用 |
|---|---|---|---|---|
| `C:\Windows\SystemTemp` | 0.2 MB | 0.2 MB | 9 | 13（delete-pending） |
| `C:\Windows\Logs` | 24.0 MB | 24.0 MB | 116 | 183 |
| `C:\ProgramData\Microsoft\Diagnosis` | 22.8 MB | 22.8 MB | 64 | 0 |
| 缩略图缓存 | **0.0 MB** | 9.0 MB | 30 | 0（explorer 持有句柄） |
| **合计** | **47.0 MB** | 56.0 MB | 219 | 196 |

- 工具报告净释放 **47.0 MB**；独立复测（脚本外自行读取卷可用空间）**46.9 MB** —— 吻合。
- 结论行：`GUARANTEE HELD: C: used space shrank by 47.0 MB (free 105.93 GB -> 105.98 GB)`。
- 卷影副本 diff 区本次增长 **0.0 MB**（守卫同时报了这一点）。

### 6.2 中止机制

模拟并发写入 256 MB 后，守卫：

```
free space fell -256.0 MB during 'simulated-concurrent-writer' (tolerance 16 MB)
verdict = NEUTRAL  regressions = simulated-concurrent-writer: -256.0 MB
```

即：**检测到反降 → 记录 → 中止剩余目标**，不会"边反降边继续删"。

### 6.3 dry-run 预测精度
| 目标 | 预测"可能释放" | 实际净释放 |
|---|---|---|
| `C:\Windows\SystemTemp` | 0.0 MB（182.2 MB 中 10 项被占用） | 0.2 MB |
| `C:\Windows\Logs` | 3.3 MB | 3.3 MB |
| `C:\Windows\ServiceProfiles\...\FontCache` | 8.0 MB（40.4 MB 中 6 项被占用） | — |

### 6.4 窗口版 v0.4 界面验收（2026-09-21，自动化）

用 UI Automation 按控件名定位、按控件真实边界点击，另用 `PrintWindow` 截图逐像素核对，
共 **28 项断言全部通过**（脚本读的是本进程窗口树；整桌面树里包含浏览器正在显示的会话文本，
按名字全局搜索会把聊天记录当成控件 —— 这一点在第一次跑测试时踩到过，断言因此必须限定进程）：

| 断言 | 实测 |
|---|---|
| 功能栏 10 个分页齐全 / 可折叠 | 10/10；折叠后页面左边界 `x 1003 → 775`（+228 px 给页面），`ui.txt` 记录 `collapsed` |
| 主题在分页布局下仍生效 | 卡片 `#FFFFFF` 161,763 px / 页面底 `#F3F5F9` 20,310 px / 强调 `#2B6CF6` 4,001 px |
| 扫描时动画在绘制 | 深色动画面板 13,571 px（扫描中截图） |
| 大目录页给出元凶 | `占用最大：6.11 GB（占本次扫描 40.55 GB 的 15.1%）— C:\Users\…\Roaming\Tencent\xwechat\radium\users` |
| 隐藏缓存页扫描 + 勾选 | `共 1 项 · 213.5 MB`；点复选框后按钮变 `删除选中项 (1)`、汇总变 `已选 1 项 · 213.5 MB` |
| 两段式确认 | 点删除 → `04` PLAN 模式预演 → 确认条「已预演：将删除勾选的 1 个目录（213.5 MB）。确认执行？」→ 点取消 → 确认条消失，**被勾选的 213.5 MB 目录仍在原地** |
| 独立日志窗口 | 出现第二个顶层窗口，含「保存到文件 / 清空 / 自动滚动」，按钮角标从 `输出日志 • 14` 清零 |
| 崩溃日志 | 6 条（均为本次改动之前 12:47 的历史记录），全程未新增 |

同一次验收里量到的一个**真实缺陷**（已修）：隐藏缓存扫描最初把整个用户目录报成
「39 个目录 / 61 个文件 / 11.3 MB」——`Walker` 复用了同一个 entry 缓冲并递归，递归返回后
父层还在按**子目录**的条目继续循环，于是大部分子树根本没走。改成每次调用现开缓冲后，
同一根目录变成 **4,751 个目录 / 88,446 个文件 / 21.3 GB，950 ms**，并正确命中
`…\npm-cache\_npx\…\node_modules` 213.5 MB。这个 bug 只看代码不容易发现，是
`--scan-cache` 的 CLI 输出把数字摆在面前才暴露的。

### 6.5 窗口版 v0.5（每功能动画 + 页面无日志）验收

| 断言 | 实测 |
|---|---|
| 十个功能各自的动画 | 逐页截图取动画方块（68×68）的 8×8 平均色签名：**10 种互不相同**，且都不是空白 |
| 动画在动 | 同一页间隔 500 ms 两张截图的签名不同 |
| 页面里不再有日志面板 | 十页「动画条下方最长连续深色游程」全部 < 300 px（另有运行中截图复核：8 px） |
| 动画条如实报状态 | 运行时 `正在运行 · 开发环境缓存 [预演 dry-run] · …`；结束 `完成 · 退出码 0 · 用时 1 秒 · 成功 0 · 警告 2 · 失败 0` |
| 结果卡给出计数与结论 | `完成 · 用时 1 秒 · 03-Clean-Dev.ps1 · 成功 0 · 警告 2 · 失败 0` |
| 原始输出仍有去处 | 「输出日志」独立窗口里能找到 `.ps1` 命名的完整输出 |
| 两段式删除没打折 | 隐藏缓存页全深度扫描 `共 1 项 · 213.5 MB` → 勾选后 `已选 1 项 · 213.5 MB`、按钮 `删除选中项 (1)` → `04` PLAN 预演 → 确认条「已预演：将删除勾选的 1 个目录（213.5 MB）。确认执行？」→ 点取消 → 确认条消失，**被勾选的 213.5 MB 目录仍在原地** |
| 崩溃日志 | 6 条（均为本次改动之前的历史记录），未新增 |

同期修掉的两个 UX 问题（都是被上面的验收打出来的）：
① 扫描完成但一个都没找到时，汇总还写着「尚未扫描」——现在明确写「已扫描完成 · 没有符合条件的
目录（可把阈值调小或深度改为「不限层数」）」；② 扫描过后主按钮仍显示「开始扫描」——现在按
「是否扫描过」显示 `重新扫描`，与结果多少无关。

> 验收方法本身也踩了三个坑，记下来免得下次重犯：
> ① **`PrintWindow` 不能用来截一直在动画的窗口**：`WS_EX_COMPOSITED` 的窗口在半渲染状态被
> 抓到时，画面下半部分整块是黑的，看起来活像"页面里还留着一个日志面板"（实测主色 `0,0,0`、
> 占满半屏）；改用屏幕抓取（`CopyFromScreen`）后一切正常。
> ② **按名字在整个桌面树上找控件会命中浏览器里正在显示的会话文本**：连 `共 232.77 GB` 都能
> 被"匹配"到我的聊天记录上。所有 UIA 查询都必须限定在**本进程的窗口**内。
> ③ **十个页面同时在控件树里**：隐藏页的元素报的是旧坐标，直接点会打空甚至点到别的功能
> （有一次误跑了 `03-Clean-Dev`）。定位时优先取「不在屏外」的那个。

---

## 七、安全机制（`_Common.ps1`）

- **路径白名单**：每个目标必须命中 `Add-AllowedRoot` 注册的根，否则 `REFUSED`。
- **精确文件白名单**：`Add-AllowedFile` 只放行单个文件，不放行其父目录。
- **硬性黑名单**：`System32` / `SysWOW64` / `WinSxS` / `Windows\Installer` / `Program Files` /
  用户配置根 / 桌面·文档·下载等一律拒绝。
- **不跟随重解析点**：自带栈式遍历器 `Get-TreeEntries`，遇到 junction / symlink 既不递归也不删除
  （PowerShell 5.1 的 `Get-ChildItem -Recurse` 会跟着 junction 走，这是被替换掉的原因）。
- **净空间实测**：见 4.3，唯一口径是卷可用空间差。
- **失败不中断**：被占用文件逐个 `try/catch` 跳过并记录。
- **服务可恢复**：停用 `wuauserv` / `bits` / `DoSvc` / `FontCache` / `WSearch` 后，`finally` 中重启。
- **预演不覆盖战果**：dry-run 写 `<tag>.dryrun.json`，真实执行写 `<tag>.json`。

## 八、环境备注（重要）

本机未安装 PowerShell 7，`pwsh` 经 PATH 落到 **Windows PowerShell 5.1**。
在 DSH 的 **ACL 沙箱限制令牌**下，任何 `pwsh` 子进程都会在早期 DLL 初始化阶段以
`STATUS_DLL_INIT_FAILED (0xC0000142)` 死亡。
**因此这些脚本必须在非受限（或提权）会话中运行**；`danger-full-access` 下一切正常。
同理，`vssadmin` / `dism` / `fsutil` 在受限 PATH 下**找不到**——脚本一律通过
`Get-SystemToolPath` 拼 `%SystemRoot%\System32\` 全路径（32 位 PowerShell 下自动改用 `Sysnative`）。

脚本文件保持 **纯 ASCII**：Windows PowerShell 5.1 在缺少 BOM 时按 ANSI(GBK) 解析 `.ps1`，
非 ASCII 字符会乱码（本次已修掉 `_Common.ps1` 里一个无 BOM 的 UTF-8 破折号）。
新增脚本请保持纯 ASCII，或确保写入 BOM。

## 九、报告文件（`reports\`）

| 文件 | 内容 |
|---|---|
| `before.json` / `before-v2.json` | 只读分析基线（v2 含清单级汇总） |
| `system.json` | 系统清理真实执行明细（净释放/逻辑/被占用/卷影增长/判定） |
| `system.dryrun.json` | 预演记录（含"被占用、不会释放"的预测） |
| `discover.json` | 无用缓存发现结果 + 大块残留 |
| `spacegrowth.json` | 空间反升诊断（卷影副本、再生、硬链接、系统文件） |
| `skipped.log` | 被跳过项（重解析点 / 被占用 / 硬链接） |
| `gpu-shadercache.json` / `rootleftovers.json` / `largedirs*.json` | 各专项记录 |
| `largedirs-gui.json` / `hidden-gui.json` | 窗口版两个扫描页的结果（内置扫描器写：总字节、文件/目录数、耗时、线程数、每条命中与其体积） |
| `scan-cli.json` | `--scan-dirs` / `--scan-cache` 命令行扫描的结果（用于核对扫描器精度与速度） |

> 窗口版还有两个**不是报告**的文件，落在 `%LOCALAPPDATA%\CLean\`（`reports\` 的上一级）：
> `theme.txt`（外观设置）与 `ui.txt`（功能栏折叠状态），删掉即回到出厂默认。
> `gui-command-plan.txt` 与 `gui-crash.log` 也在这一级。

> 修正记录：早期版本 dry-run 与真实执行写同一文件，一次测试用预演把 11,329 MB 的真实战果
> 覆盖为 `FreedMB=0`。现按 `Mode` 分离为 `<tag>.json` 与 `<tag>.dryrun.json`。

## 十、尚未执行的清理项（等你决定）

| 组 | 内容 | 预计释放 |
|---|---|---|
| A | `-Profile standard` 全量（含 Edge/360 浏览器缓存、字体缓存、SystemTemp） | ≈1.4 GB（其中被占用部分不会真正释放） |
| B | `C:\8423fec2…`(HLS 缓存) 6.08 GB、`C:\$Windows.~BT` 205 MB | ≈6.3 GB，走 `06` |
| C | 未列清单的应用缓存（bilibili / ToDesk / Doubao / QQLive / Tencent 日志等） | ≈1.9 GB，`-Profile aggressive -Discover` |
| D | 卷影副本 diff 区（8.68 GB / 10 GB） | ≈6.7 GB，`-ShadowTrim`（**销毁旧还原点**） |
| E | DISM 组件存储（WinSxS） | `-IncludeComponentStore`，视版本而定 |

**明确不碰**：微信聊天数据（`Roaming\Tencent\xwechat`）、`C:\Recovery`、`C:\Windows\Installer`、
`C:\Windows\System32\*` 全部、下载/桌面/文档等用户目录、浏览器 Cookie 与登录态。

---

## 许可证

[MIT](LICENSE)。

> 这是一个会**真正删除文件**的工具。作者按 MIT 条款「按原样」提供，不对任何数据丢失负责；
> 工具的安全契约（默认只读、净空间守卫、白名单、先预演后确认）是为了把风险降到最低，
> 不构成任何形式的担保。

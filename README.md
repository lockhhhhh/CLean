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

两个外壳，同一个脚本内核（11 个 `.ps1` 内嵌）：

| 程序 | 界面 | 源码 | 体积 |
|---|---|---|---|
| `CLean.exe` | 控制台菜单 | `src\Launcher.cs` | 167 KB |
| `CLean-GUI.exe` | **WinForms 窗口**（见 1.1；v0.3 颜色主题，v0.4 分页 + 内置扫描器，v0.5 每个功能独立动画，v1.1 只读页把 JSON 报告渲染成表格） | `src\GuiLauncher.cs` + `GuiPages.cs` + `GuiAnims.cs` + `ReportView.cs` | 279 KB |

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

窗口长这样（功能栏是展开态）：

| 区域 | 内容 |
|---|---|
| header | 卷指示 `[C:]` + `CLean`，右侧 `[输出日志 • N]` `[外观 ▾]` `[打开报告目录]` `[刷新]` `[以管理员身份重启]` |
| 磁盘卡片 | 卷标、可用空间大字、`共 X GB · 已用 Y GB (Z%)`、按占用率变色的用量条、系统版本与权限徽章 |
| 左侧功能栏 | 7 个功能各占一页（见下）；`☰` 可折叠成只剩数字，折叠后页面多出约 230 px |
| 页面 | 标题 + 模式徽章（**只读** / **需确认**）→ 一行说明 → 该功能自己的选项 → **该功能自己的动画条** → 内容区（报告表格或结果清单 + 页内操作按钮） |
| 底部操作栏 | 状态文字 + 自绘滑动指示条 + `[取消当前任务]` + 主按钮（`开始扫描` / `开始预演（不删除文件）`） |
| 确认条 | 只在预演成功、退出码 0 且未取消时出现：**预演实测数字** + 「现在执行「X」？」 + `[取消]` `[执行删除]` |

7 个功能位（v1.1 起）：

| # | 功能 | 性质 |
|---|---|---|
| 1 | 空间分析 | 只读 · 报告表 |
| 2 | 系统缓存清理 | 预演后确认 · 报告表（每个目标的实测数字）+「查看清理清单」 |
| 3 | 无用缓存发现 | 只读 · 报告表 |
| 4 | C盘根目录残留 | 预演后确认 · 报告表 |
| 5 | 开发环境缓存 | 预演后确认 · 报告表 |
| 6 | 扫描（大目录 / 项目缓存） | 大目录=只读，项目缓存=预演后确认 |
| 7 | 空间反升诊断 | 只读 · 报告表 |

> 这里原本是一张手绘的 ASCII 窗口图。它每次布局改动都要重新对宽度，而且图里的序号和文案
> 已经三次与真实界面对不上（最后一次是功能位 10 → 7）。换成表格之后，"对不齐"这件事不
> 会再发生，过时也从"看不出来"变成"一眼可见"。

**分页与收纳式功能栏**（v0.4）：每个功能各占一页（v1.1 起 **7** 个，见上表：原「显卡着色器缓存」
并入「系统缓存清理」，「清理清单一览」并入「系统缓存清理」，「全盘大目录分析」与「隐藏项目缓存扫描」
合并为「扫描（大目录 / 项目缓存）」），左侧功能栏可折叠（`☰`，状态记在
`%LOCALAPPDATA%\CLean\settings.txt`）：展开时显示序号 + 名称 + 一行说明，折叠后只剩数字，
页面因此多出约 230 px 宽度。页内布局固定为「标题 + 模式徽章 / 说明 / 该功能自己的选项 /
内容区」，删除类页面的按钮就在内容区下方，不再挤在底部一条。

**合并扫描页的两种模式**（v1.1）：它们本来就是同一个问题的两种问法 ——「空间去哪了」和
「哪些可以删」共用同一个扫描器、同一张按体积排序的清单、同一个动画和同一个范围选择器。
分成两页时还必须有一条**页面之间的移交通道**，而那条通道有个用户无法处理的失败态：在只读页
选中一个大目录，如果它的名字不在缓存名单里，你会依次看到「已记录」和「没找到已记录的目录」。

合并后**模式开关保护的是这条安全线**：

| | 大目录（只读） | 项目缓存（可勾选删除） |
|---|---|---|
| 徽章 | **只读** | **需确认** |
| 勾选框 | 不画、不能勾（`ResultList.ShowCheck = false`） | 有 |
| 删除按钮 | 不存在 | `删除选中项 (N)` |
| 阈值 | 元凶阈值 100/500/1000 MB | 最小体积 20/50/100 MB + 扫描深度 5 层/不限 |
| 行操作 | 打开位置 / 复制路径 / **在此目录下找可清理缓存** | 全选 / 全不选 / 仅选大项 |
| 结果 | 只报「最大的那一层」，并列出一行结论「占用最大：…（占本次扫描 …%）」 | 项目缓存目录，父级命中后剪枝，体积不叠加 |

「在此目录下找可清理缓存」**不是**把那一行排进删除队列，而是**切到项目缓存模式、把范围改成
那个目录重新扫一遍** —— 让缓存名单规则自己决定里面有没有可删的东西。这就是这个页面永远不会
被说服去删一个 Documents 目录的原因，也是上面那个死胡同消失的原因（里面有缓存就扫得到，
没有就是空状态，不会留下一条"已记录"却永远等不到的消息）。

**大文件 Top 100（v1.1）**：同一次遍历顺手维护「最大的单个文件」清单（每个并行 worker 各自
一个有界列表，最后归并 —— 不是为了省内存，而是**每个文件加一次锁会把扫描器最值钱的东西
（速度）赔掉**）。「看什么」在「目录（谁在吃空间）」和「最大文件 Top 100」之间切换，**不会重扫**：
两个答案都来自同一次遍历，切视图只是把内存里的另一份结果放进列表。实测（`--scan-dirs` 的
JSON 里也带 `TopFiles`）：

```
C:\Users\Administrator\AppData\Local   91,398 文件 / 17,002 目录 / 1148 ms / 13,740.2 MB
   394.7 MB  …\AppData\Local\Temp\360UnPackTmp\chrome.7z
   304.8 MB  …\AppData\Local\Doubao\Application\app\Doubao.dll
   275.9 MB  …\AppData\Local\@deepseek-aidsh-desktop-updater\installer.exe
```

**把文件夹拖进窗口（v1.1）**：拖一个文件夹（或一个文件，取它所在目录）到窗口任意位置，
就只分析那个文件夹 —— 比"整个 C 盘"快几个数量级。拖放**强制切到只读的大目录模式**，
所以拖一个文件夹永远不会是删除它的第一步。拖放处理器挂在**整棵控件树**上（208 个控件）：
WinForms 把 drop 投给光标下最深的控件，没订阅的卡片或列表会把 drop 吃掉，窗体根本收不到。

**清理账本（v1.1）**：把 `reports\` 里每一次清理的实测净释放按时间列成一张表，并给出 C 盘
可用空间相对每次运行结束时的变化 —— 这是这个工具存在的意义却一直答不上的问题：「上次清出来的
空间还在吗？」报告里一直有这些数字（每轮的 `NetFreeMB` / `Verdict`，以及惯犯卷影副本占用），
只是没人把它们加起来。

| | |
|---|---|
| 入口 | 「空间反升诊断」页的「清理账本（历次战果与之后的变化）」；无头用法 `CLean-GUI.exe --ledger` |
| 产物 | `reports\ledger.json`（和别的报告一样能被表格渲染、能导出 CSV） |
| 实测输出 | `FreeNowGB 84.5 · CleanupRuns 1 · TotalNetFreedMB 47.0`，一条 `2026-09-16 system 执行 NetFreeMB 47.0 Verdict GAIN ShadowUsedPct 86.7 SinceThenGB -21.48` |
| **口径** | 最后一列是**卷自身计数器**的变化，不是归因：Windows 更新、浏览器、页面文件都会改动它。所以列名叫「之后的变化」，账本只声称它能测到的东西 |

**暂停 Windows 更新（v1.1，`09-Pause-Updates.ps1`）**：这是第二节那个头号根因的**对策**，
不再是"建议"。更新缓存被清掉后 Windows 会在几分钟内下回来（实测 0 → 803.9 MB / 0 → 4,958.9 MB，
期间 C 盘**已用**空间 +480 MB），所以清理更新缓存之前先暂停才是有意义的动作。

| | |
|---|---|
| 改什么 | 只改「设置」应用写的那 6 个暂停值（`HKLM\…\WindowsUpdate\UX\Settings` 的 `Pause*`）：不停服务、不建策略键、不禁止任务 |
| 为什么敢跑 | 写之前把**原值快照**到 `reports\updates-pause-state.json`；`-Resume` 还原的是快照，不是"取消暂停"——原本就有的暂停会被原样写回，本工具新建的值会被**删掉**，本工具没碰过的键值完全不动 |
| 契约 | 默认只读（`-Status` 只读；不带 `-Execute` 只预演要改哪几个值）；窗口版走同样的两段式确认 |
| 实测往返 | 前：键里只有它自己的 4 个无关值、无任何 `Pause*` → `-Execute` 写入 6 个值（回读校验通过，到期 `2026-10-23`）→ `-Status` 报 `paused for another 7.0 day(s)`，原始注册表读回同一时间 → `-Resume -Execute` 逐项 `2026-… -> (not set)`（回读校验通过）→ 后：`no pause values set`，**键仍存在且仍是原来那 4 个值** |
| 边界 | 到期 Windows 自己会恢复；暂停不会阻止已下载的更新安装 |

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
页面不再铺日志流，但「这次成没成、结论是什么」一目了然。只读报告页在这张卡下面还有
报告表格（下一节）。

**报告表：每个脚本页都渲染自己那份 JSON 报告**（v1.1，`src\ReportView.cs`）：v1.0 的四个只读功能
（空间分析 / 无用缓存发现 / 清理清单一览 / 空间反升诊断）跑完只剩一行结论，真正的清单只
能在日志窗口里看 —— 一个干活的页面看起来像什么都没做。而**脚本本来就在写结构化报告**
（`before.json` / `discover.json` / `catalog.json` / `spacegrowth.json`），只是没人读它。
现在 GUI 自己读回来渲染：

| 项 | 做法 |
|---|---|
| 解析 | 手写的最小 JSON 读取器（`JsonRead` / `JNode`），无序列化库、无依赖；容忍 BOM 与 CRLF，**任何失败都返回 null**，页面退回到「没找到报告」而不是抛异常 |
| 表格从哪来 | 报告**自己就是 schema**：顶层「对象数组」字段自动成表（列＝键），标量字段进「事实」区，数组标量成单列表，嵌套对象展平一层。不写死任何一份报告的结构，脚本加字段不用改 GUI |
| 列的取舍 | 每表最多 6 列：名称列（`Path`/`Id`/`Name`/`Label`）→ 体积列（`*MB`/`*Bytes`/`Size`）→ 结论列（`Risk`/`Mode`/`Verdict`/`*Count`）→ 其余按原顺序；被丢掉的列数写进表标题（`Targets（还有 5 列未显示）`） |
| 行的取舍 | 只取「第一个元素的键 + 出现率 ≥90% 的键」：`before-v2.json` 的 `Targets` 是混合格式（发现式行与目录式行字段不同），纯并集会让 6 个列位里 3 个在前 23 行全空 |
| 元数据过滤 | `Time`/`Tag`/`Mode`/`Root`/`Tool`/`Page` 属审计信息，不进「事实」区；`Title` 用作报告标题 |
| 载入时机 | 打开页面就显示**上次运行**写入的报告，卡片写明「已载入上次报告 · reports\x.json · N 个表 · M 行 · 标题」——「尚未运行」配一张有数据的表是自相矛盾的 |
| 导出 | 每个报告页有「导出为 CSV」（表间空行分隔、字段含 `,`/`"` 时按 CSV 规则转义、行尾 CRLF、**UTF-8 带 BOM** 所以 Excel/记事本不乱码），写到 `reports\export-<报告名>-<时间戳>.csv` |
| 未暴露 | 报告**只是显示与导出**，它不参与任何删除决策：删除路径仍然只有「04 的白名单 + 净空间守卫」那一条 |

> 实测（`--scan-cache C:\Users` 与真实报告，见 6.6）：48 行的 `catalog.json` 渲染成
> `Path | Id | Risk | Mode | Group | MinAgeDays` + 「还有 5 列未显示」；`before.json` 得到
> 3 张表 90 行。**顺带补上**：`02 -ListCatalog` 以前只往 stdout 打印，现在同时写
> `reports\catalog.json`，否则清单页没有可渲染的报告。

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
| 每功能动画 | `AnimBase` + 七个 `DrawMotif` 子类：统一深色圆角面板 + 左侧 68×68 动画方块 + 标题/实时详情 + 运行时的滑动进度线；空闲/运行/完成三种状态 |
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

选完立即生效、**不用重启**，并写入 `%LOCALAPPDATA%\CLean\settings.txt`：

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
| 菜单点「深色」 | 当场变深、`settings.txt` 变 `mode=dark` | ✓（浅色背景 0 px） |
| 菜单点「绿」 | 当场换色、`settings.txt` 变 `accent=green` | ✓（新强调 3,867 px，旧蓝 0 px） |
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
   底部才出现琥珀色确认条（按钮变为可用，状态栏同时改为「等待确认」）。**v1.1 起确认条上先
   写预演实测结果**：`预演结果：预计可释放 3,090.5 MB，涉及 29 个目标；其中 40 项被其他程序
   占用，不会真的释放空间。` 这句话来自预演写出的 `<tag>.dryrun.json`（`EstimatedMB` 与
   `LockedPredicted` 字段），不是从日志文本里猜的；读不到报告就退回原来那句，不编数字；
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
| `07-Clean-GpuShaderCache.ps1` 的独立页 | v1.1 起显卡着色器缓存并入「系统缓存清理」的勾选项（`-IncludeGpuShader` / `-IncludeD3DSCache`，本来就在 02 的清单里，只是窗口版没暴露），07 脚本保留给控制台版；连同「清理清单一览」与两个扫描页的合并，功能位由 **10 降到 7** |
| `04 -MinMB` / `-Depth` | 扫描页给的是「最小体积 / 扫描深度」三档预设，不开放任意数值 |
| `09-Pause-Updates.ps1` 的控制台菜单项 | 暂停/恢复更新只在窗口版有按钮（v1.1）。控制台版要跑就直接跑脚本；菜单从 v1.0 起就是 9 项，加一项要同时改菜单、`switch` 与提示文案，留到下次 |
| 清理账本 | 入口在窗口版（「空间反升诊断」页的按钮）与 `CLean-GUI.exe --ledger`；控制台版没有 |

> 窗口版 v0.4 起，两个扫描页**不再调用** `05-Scan-LargeDirs.ps1` / `04-Clean-Hidden.ps1`
> 的扫描逻辑（改用内置 C# 扫描器，见 1.1），但**删除仍然交给 `04-Clean-Hidden.ps1 -Execute -Paths`**
> —— 白名单、黑名单、净空间实测这些守卫一条都没绕过。控制台版继续使用脚本自身的扫描。

需要这些时用控制台版或直接跑 `.ps1`。

### ⚠️ 关于杀毒软件 / SmartScreen

未签名、会自我提权、会删除系统缓存目录 —— 行为特征与恶意软件相似，可能被 Defender / 360
误报，首次运行可能触发 SmartScreen。源码完全公开可审计（`src\Launcher.cs` + 11 个 `.ps1`），
被拦截时选「仍要运行」。

### 重新打包

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\src\Build.ps1                                  # 控制台版 -> CLean.exe
.\src\Build.ps1 -Gui                             # 窗口版   -> CLean-GUI.exe
.\src\Build.ps1 -Gui -OutputName '我的清理工具.exe'
```

用系统自带 `csc.exe`（`%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\`），
**不需要 .NET SDK / NuGet / 联网**。窗口版由四个源文件编译而成
（`GuiLauncher.cs` 外壳与主题/绘图基元，`GuiPages.cs` 分页、扫描器与日志窗口，
`GuiAnims.cs` 动画与结果卡，`ReportView.cs` JSON 读取器、报告模型与只读报告列表），
`Build.ps1` 的 `$extraSources` 负责一起送进 csc。三个关键细节：

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

#### 项目缓存名单也只有一个来源（v1.1）

项目缓存名（`node_modules` / `.venv` / `__pycache__` …）曾经有三份拷贝：`04-Clean-Hidden.ps1`、
`05-Scan-LargeDirs.ps1` 和窗口版的 C# 扫描器，而且**已经不一致**（C# 那份知道 `.tox` 不知道
`.nuget`，05 那份正好相反）。现在：

- 名单只定义在 `_SystemCatalog.ps1` 的 `Get-ProjectCacheNames`；`04` 直接调它（不再自带一份）；
- `05` 的名单**故意不同**并在注释里写明原因：它只用来给一行**贴标签**「这个大目录是缓存」，
  从不授权删除，所以额外覆盖系统缓存（`Cache` / `Code Cache` / `GPUCache` / `Temp` / `Logs`）；
- 窗口版需要的 C# 常量由 `Build.ps1` 在编译时**从那个函数生成**到 staging 目录
  （`ProjectCacheNames.cs`，不落库），和 `VersionInfo.cs` 是同一套做法；生成结果少于 5 个名字
  就直接让构建失败。所以「脚本认定的缓存」和「GUI 认定的缓存」不可能再跑偏。

同理，`04-Clean-Hidden.ps1` 的 `-ScanRoots` 与 `02` 的清单都读同一份 `_SystemCatalog.ps1`。

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
| `04-Clean-Hidden.ps1` | 隐藏项目缓存扫描（node_modules / `.cache` / venv）；`-Paths` 不带 `-Execute` = 逐条预演计划，带 `-Execute` 才删。缓存名单来自 `_SystemCatalog.ps1` 的 `Get-ProjectCacheNames`（v1.1 起不再自带一份） | 需 `-Execute -Paths` |
| `05-Scan-LargeDirs.ps1` | 单遍全盘扫描，定位大目录 | ❌ |
| `06-Clean-RootLeftovers.ps1` | C:\ 根目录残留（`$WinREAgent` / HLS 缓存 / 酷狗 P2P） | 需 `-Execute` |
| `07-Clean-GpuShaderCache.ps1` | 显卡检测（N 卡 / A 卡）+ 对应着色器缓存 | 需 `-Execute` |
| `08-Diagnose-SpaceGrowth.ps1` | **空间反升诊断**：量化第二节的每个机制 | ❌ |
| `09-Pause-Updates.ps1` | **暂停 Windows 更新**（v1.1）：让清理更新缓存这件事真的成立。只改「设置」应用写的那 6 个暂停值，写之前先快照，`-Resume` 精确还原 | 需 `-Execute`（改注册表） |

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

暂停更新（清理更新缓存之前先做，否则几分钟内被下回来 —— 见第二节）：

```powershell
.\09-Pause-Updates.ps1                      # 只读预演：会改哪 6 个值、到期时间是多少
.\09-Pause-Updates.ps1 -Execute             # 真的暂停 7 天（快照写在前面）
.\09-Pause-Updates.ps1 -Execute -Days 14    # 暂停 1~35 天
.\09-Pause-Updates.ps1 -Status              # 只读：现在是不是暂停中、还剩几天
.\09-Pause-Updates.ps1 -Resume -Execute     # 精确还原到快照里的状态
```

`-Resume` 还原的是**快照**，不是"取消暂停"：如果你原本就有一个自己设的暂停，它会被原样写回；
本工具新建的值会被删掉，本工具没碰过的键值（例如 `ActiveHoursStart`）**完全不动**。

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

### 6.6 窗口版 v1.1（报告表 + 功能位 10→7 + 缺陷修复）验收

功能位 10 → 7：显卡着色器缓存并入系统缓存清理、清理清单一览并入系统缓存清理、两个扫描页
合并为「扫描（大目录 / 项目缓存）」。07 / 05 脚本仍由控制台版使用。

| 断言 | 实测 |
|---|---|
| 功能栏 7 项 | 读出 7 项：1 空间分析 / 2 系统缓存清理 / 3 无用缓存发现 / 4 C盘根目录残留 / 5 开发环境缓存 / 6 扫描（大目录 / 项目缓存） / 7 空间反升诊断 |
| **徽章与页面真实行为一致** | 逐页读徽章（`CLEAN_START_PAGE` 把启动页当参数跑 7 次，**全程不点鼠标**）：**3 个「只读」**（1/3/7）+ **4 个「需确认」**（2/4/5/6）。修复前「全盘大目录分析」的徽章写的是「需确认」，而那一页只读 |
| **扫描页的模式开关真的切换了行为** | 默认（项目缓存）：徽章`需确认`、有「最小体积」「扫描深度」、有「删除选中项」、无「元凶阈值」；向「大目录（只读）」单选按钮**只投一条 `PostMessage`**（不动光标、不抢前台）后：徽章变`只读`、「元凶阈值」出现、「最小体积/扫描深度」消失、「删除选中项」**消失**、行操作（打开位置 / 复制路径 / 在此目录下找可清理缓存）出现、提示语换成只读版 |
| 每个脚本页都渲染自己的报告表 | 第 1 页 `before.json · 3 个表 · 90 行`；**第 2 页（会删文件的页）`system.dryrun.json · 2 个表 · 43 行`** —— 每个目标的预计释放数字第一次在界面上可见，而不再只是一行结论；第 2 页另有「查看清理清单（不删除）」按钮 |
| 表格列取舍在真数据上可读 | `catalog.json` 的 `Targets`（48 行 × 11 字段）→ `Path \| Id \| Risk \| Mode \| Group \| MinAgeDays` + `（还有 5 列未显示）`；`before.json` 的 `TopDirs` → `Path \| MB \| Files` |
| **确认条带预演实测结果** | 点一次「开始预演」（`PostMessage` 投给该按钮自己的 HWND；**从不点确认条**）：`预演结果：预计可释放 3,090.5 MB，涉及 29 个目标；其中 40 项被其他程序占用，不会真的释放空间。` + `预演完成，尚未删除任何文件。现在执行「系统缓存清理」？`，预演用时 8 秒、退出码 0 |
| 命令计划不再有空白页 | `--print-plan` 里隐藏缓存页以前**整段空白**（`PlanLines` 没覆写），而它恰恰是唯一手拼删除命令的页面；现在写出两阶段形状 + 扫描范围/深度/最小体积，并按模式给出不同内容 |
| 扫描器与新字段 | `--scan-cache C:\Users`：**18,585.7 MB / 83,759 文件 / 3,163 目录 / 948 ms**，`Denied 81`、`ReparseSkipped 778`、`DepthCapped 2389`（后者是新增字段，现在也写进摘要：「N 处因深度封顶未展开」）；工作区自扫 138 文件 / 16 ms |
| 缓存名单单一来源 | 构建输出 `cache names: 16 generated from _SystemCatalog.ps1`；`04` 改为调用同一函数后仍能正常扫描并报 `no cache folder >= 1 MB found` |
| **设置文件单一来源** | 先只放 v1.0 的 `theme.txt`(`mode=dark` `accent=rose`) + `ui.txt`(`rail=expanded`)，启动一次 → 生成 `settings.txt`：`accent=rose` `mode=dark` `rail=expanded`（键排序）；**删掉那两个旧文件再启动** → 值仍在，且旧文件不再被创建 |
| **大文件 Top 100（真数据）** | `--scan-dirs C:\Users\Administrator\AppData\Local`：91,398 文件 / 17,002 目录 / **1148 ms** / 13,740.2 MB，`TopFiles` 100 条，最大 `…\Temp\360UnPackTmp\chrome.7z` 394.7 MB；同一次运行的目录命中未变（8 条，Edge Default 1,160.2 MB），说明没有因为记录文件而变慢 |
| **拖放目录** | 用合成 `DataObject` 走窗体自己的处理函数：控件树 **208 个控件、AllowDrop 208**；`DragEnter` 接受为 `Copy`；投递后切到第 6 页、`_mode` = **0（大目录只读）**、`Roots` = 被拖入的目录、扫描立刻开始 |
| **清理账本** | `--ledger` → `ledger.json` 用 `ConvertFrom-Json` 能解析（`FreeNowGB 84.5 CleanupRuns 1 TotalNetFreedMB 47.0 Entries 2`）；点页面上的按钮（`BM_CLICK`）→ `ledger.json` 被重写、弹出「账本已生成：2 条记录」、报告卡变成 `已载入上次报告 · ledger.json · 1 个表 · 2 行` |
| **暂停更新的往返** | 前：键里只有它自己的 4 个无关值、无任何 `Pause*` → `-Execute` 写 6 个值并回读校验、到期 `2026-10-23T04:54:13Z` → `-Status` 报 `paused for another 7.0 day(s)` → `-Resume -Execute` 逐项 `2026-… -> (not set)`、回读校验通过 → 后：`no pause values set`，**键仍存在、仍是原来那 4 个值**；脚本纯 ASCII（0 个非 ASCII 字节） |
| 两个外壳 | 控制台 173,568 字节、窗口版 307,712 字节，均 `BUILD OK`、**11 个脚本**内嵌校验通过 |

本轮的四个坑（都记下来，下次省时间）：

> ① **页面在窗体构造函数里就建好了，而 `RuntimeDir` 原本到 `OnLoad` 才赋值** —— 新的报告页在
> 构建期去 `Path.Combine(null, ...)`，`值不能为 null。参数名: path1` 直接把窗口在**启动时**
> 打崩。编译器不会发现，`--print-plan` 那类不显示窗口的路径也不走 `OnLoad`。修法是把
> `_runtimeDir` / `_powershell` 提前到 `BuildLayout()` 之前解析，并让报告路径在拿不到根目录时
> 返回 null。**是逐页探针把它逼出来的**，只看代码不容易注意到。
>
> ② **原生 owner-draw `ListBox` 的行不通过 UI Automation 暴露**：报告表已经渲染出 48 行，探针
> 却读到「0 个元素」，一度让人以为 JSON 没解析成功。这类控件的验证要么走 `DrawToBitmap` 渲染，
> 要么把「有几行」发布到一个会暴露文本的控件上（现在结果卡会写 `N 个表 · M 行`，既能自证也更好读）。
>
> ③ **不要在有人正在用的桌面上发合成鼠标事件**：窗口开在用户前台程序后面，`SetCursorPos` +
> `mouse_event` 会把点击送进那个程序（实测点进了正在播放的视频）。要驱动自己的窗口，用
> `PostMessage` 把消息直接投给目标控件自己的 HWND —— 光标不动、前台不抢，而且能精确指定只点
> 哪一个按钮（这也是为什么确认条的验证敢做：只投「开始预演」，绝不投「执行删除」）。
>
> ④ **`PostMessage` 投合成鼠标消息对 `Button` 无效 —— 对 `RadioButton` 有效**：同一个探针点
> 单选按钮成功（徽章当场变只读），点 `FlatButton` 却毫无反应，而按钮既 enabled 又有合法 HWND、
> 也没有弹任何对话框。原因是 `Button` 在 mouse-up 时会检查**真实光标位置**（`MousePosition`），
> 而合成消息并不会移动光标，于是那一下被判成"在按钮外面松开"而被丢弃。**点按钮要用 `BM_CLICK`**
> （`0x00F5`），它直接进按钮自己的点击逻辑，不依赖光标。这个坑值得记：症状看起来像"处理器没写对"，
> 实际是输入注入方式不匹配。
>
> ⑤ **改了一个控件的宽度，锚定不会替你重排它**：徽章从「需确认」变成「只读」时宽度会变，
> 而 `Anchor = Top|Right` 只在**父容器**尺寸变化时才生效，子控件自己变宽不会触发。结果就是
> 徽章要么压住标题、要么右边留一条缝。修法是把 `PlacePill()` 从 `private` 放宽到 `protected`
> 并在 `SetContent` 之后显式调一次。顺带修掉一个**早就存在**的裁切：底部操作栏固定 520 px，
> 而最忙的扫描模式五个按钮加边距要 588 px，左边那个按钮一直被切掉一角 —— 现在 604 px。

### 6.7 窗口版 v1.1 修复轮（用户报的两个 + 崩溃日志里挖出的一个）

| 症状 | 根因 | 修法 | 验证 |
|---|---|---|---|
| 启动弹「释放运行组件失败: 对路径 …`09-Pause-Updates.ps1`… 的访问被拒绝」 | `ExtractScripts` 在**第一个**失败文件上直接 `throw`：一个文件的瞬时拒绝就中断整轮、弹模态框，还让运行目录里的脚本半新半旧 | 每个文件独立、失败重试一次、只记录不中断；只有「一个都写不出去」或「一个都没内嵌」才抛。失败日志写明处理办法 | 人为造一个只读副本后启动：**顶层窗口 1 个**（没有对话框）、其余 10 个照常重写、那个文件也被清掉只读位后重写 |
| 运行目录里 `09-Pause-Updates.ps1` **永远**不被重写（用户看到的那次「拒绝」早就过去了） | 该文件变成「**可创建、不可覆盖**」：非只读属性、ACL 与 10 个正常兄弟逐项相同、独占打开成功、换内容无效、从构建里去掉它之后其余全部成功 —— 逐条排除后只剩「这个文件自身的 OS 状态」 | 原地覆写失败就**删掉重建**（实测：手工删掉后下一次启动立刻写对） | `scripts rewritten: 11 of 11` |
| 切到「大目录」模式时动画抛 `ArgumentOutOfRangeException`（**只落在 `gui-crash.log` 里，界面上看不出来**） | 合并动画时把「取最大值」的循环边界从 `_live.Count` 误写成 `count`：空闲态 `count` 退化成 6，而 `_live` 是空的 | 边界改回 `_live.Count` | 反射直接调 `DrawMotif`，空数据与非空数据**都不抛**；启动全程 `gui-crash.log` **33 → 33 行**（未新增） |
| 报告卡上「N 个表 · M 行」与提示语**重叠** | 卡片固定 96 px：提示语画在 `Height-26`（=70），而带结果时计数行在 y=72 | 有结果时把提示语一并清掉（它只属于空闲态），并把卡片升到 176 px | 离屏渲染结果态/空闲态两张卡并逐张看：结果态四行不再重叠，空闲态提示语照常显示 |
| （顺带）扫描页「占用最大：…」被面板边缘切掉且没有省略号 | 判定行标签宽度写死 760 px，比窄窗口下的页面还宽 | 宽度跟随所在行的 `Resize` | 代码级修法，未单独截图 |

> ⑥ **一个「可创建、不可覆盖」的文件**（上表第二行）是本轮最费时的一个。排除法走过的路都记下来，免得下次重走：**不是**只读属性（标志位实测是清的）、**不是** ACL（与 10 个正常兄弟逐项相同，无 Deny）、**不是**被占用（独占打开成功）、**不是**内容（加一行注释无效）、**不是**循环位置（把它从构建里去掉，原本最后一个文件正常写入）、**不是**权限（同一个「删除+重建」动作，换个文件就成功）。删掉它，下一次启动就写对了 —— 所以产品里现在内置了这个回退：覆写失败就删掉重建，把「永久失败」降级成一次性事件。
>
> ⑦ **`Get-Content` + `Set-Content` 会把 LF 悄悄换成 CRLF**：本仓库的 `.ps1` 是 **LF-only**，我用它做了一次「删掉一行」的编辑，文件从 12,097 涨到 12,388 字节 —— 正好每行 +1。功能没坏，但 diff 会整篇变红。改行尾要用 `[System.IO.File]::ReadAllText` / `WriteAllText` + `Replace("\r\n","\n")`，并用「CRLF=0 / LF-only=N」实测确认（其他脚本：`02` LF-only 531、`_Common` LF-only 1054）。

### 6.8 v1.1.1：把 dry-run 从 83 秒压到 13 秒（外加两个被它藏起来的数字错误）

用户报的现象是「日志每次出现 `GROUP: UPDATE (risk=safe)` 之后就卡住，一次 1196 秒」。定位到的不是死锁，而是**同一个目标（`SoftwareDistribution\Download`，309,730 个文件）被反复整树遍历**：

| 步骤 | 改前 | 改后 | 说明 |
|---|---:|---:|---|
| `selBytes` 求和 | 29,423 ms | ~1,000 ms | `children` 模式下每个被选中的**目录**都要单独跑一次 `Get-PathBytes`；那个目录下有 51 个目录项 |
| `BeforeMB` + `AfterMB` | ~16,000 ms | 742 ms | dry-run 什么都没删，`AfterMB` 又走了一遍整树 |
| `Get-TreeEntries` | 2,243 ms | 2,243 ms | 本来就不慢（走的是 `GetFileSystemInfos()`） |
| 锁占用探测 | 1,140 ms | ~400 ms | 上限 4000 个文件，实测 0.41 ms/次 |
| **单目标合计** | **53,257 ms** | **5,027 ms** | |
| **整个 standard profile（43 个目标）** | **60.8 s** | **9.3 s** | |
| **完整 `02-Clean-System.ps1` dry-run** | **83 s** | **13 s** | |

三处改动：

1. **`Get-PathBytes` 换成 `DirectoryInfo.GetFileSystemInfos()` 显式栈求和**，不再用 `Get-ChildItem -Recurse`：同一个树 **16,077 ms → 742 ms**，且逐字节结果与旧实现**完全一致**（在 4 个真实目录上比对过）。顺带也不再跟进 junction（和删除引擎的既有规则一致）。
2. **`Get-DirSizes`：一次遍历给出所有目录的子树大小**，`BeforeMB` 直接取根节点那一项；dry-run 分支里 `AfterMB = BeforeMB`（什么都没删，本来就应该相等）。
3. **锁占用探测也改走「实际删除集」**，不再遍历原始选择集。

**修出来的两个数字错误**（都是「按每一层祖先各算一遍」造成的重复计数，属于本项目最在意的那类问题）：

- **`wu.download` 报「已选 10,092.6 MB」，真实目录只有 2,806.1 MB**（3.6 倍）；`do.cache` 6,882.3 MB 报成 13,764.6 MB（正好 2 倍）。`children` 模式会把一个目录**和它里面的每一个条目**都选进来，于是「目录子树 + 目录里的每个文件」被重复相加。现在删除集先收敛成**最上层选择**（父目录也被选中时不再单独计它），数字与真实目录大小逐项对上。整个 profile 的 `SELECTED (logical)` 从 27,215.4 MB 变成 12,119.3 MB。
- **「probably free」一度算成负数**（`-6,882.0 MB of 6,882.3 MB`）：锁占用那一段还在用旧的（重复计数的）选择集，减出来比总数还大。两处统一到同一个删除集之后不再可能为负。
- **附带**：真实 `-Execute` 运行时，先 `Remove-Item -Recurse` 掉整个目录、再去删它里面的文件，会让那些文件全部落进 `catch` 被记成 `LOCKED` —— `skipped.log` 里成千上万条假失败。现在只对删除集里的最上层条目动手，不会再有这个问题。

> ⑧ **`Control.Visible` 在祖先被隐藏时返回 `false`**：窗口版每个页面只有当前那个是可见的，我用 `Options.Visible` 判断「要不要给选项区留高度」，结果**所有非当前页的选项区高度都塌成 1 px**（离屏渲染时才发现）。布局判断要用「自己有没有子控件」，不要用 `Visible`。
>
> ⑨ **`Dock = Fill` 的子控件永远不会触发父容器 `AutoScroll`**（它恒等于客户区大小，永远不溢出）。要做「整页上下滑动」必须把内容面板 `Dock = Top` 并自己算高度，再在 `Resize` 里重算 —— 见 `ToolPage.LayoutPage()`。选项区则**限高 + 自己滚动**（`AutoSize = false` + `AutoScroll = true`），这样再小的窗口也不会把结果区挤没。
>
> ⑩ **`Get-ChildItem -Recurse` 比 `DirectoryInfo.GetFileSystemInfos()` 慢一个数量级**（同一个 309,730 文件的树：16.1 s vs 0.7 s）。本仓库所有热路径的目录遍历都已经换掉；新写遍历时别再引入 `Get-ChildItem -Recurse`。

### 6.9 v1.1.1 修订：净空间守卫误报，以及上一轮优化带来的一个退化

真实 `-Execute` 跑出过这么三行：

```
[x] GUARANTEE BROKEN: C: used space GREW by 3.5 MB.
[x] The run reports which target went backwards; the guard stopped work on the first one.
```

**3.5 MB 这个数字，比这块盘自己半小时的抖动还小。** 空转实测（什么都不跑，只采样 C: 可用空间）：

| 指标 | 实测 |
|---|---:|
| 30 秒内抖动 | ±3.9 MB |
| 漂移速率 | **7.3 MB/min（436 MB/hour）** |
| 守卫原来的判定阈值 | **0 MB**（`Stop-NetGainGuard` 只判 `netFree < 0`，任何下降都算 GEREGRESSION）|

也就是说，Defender / 遥测 / 浏览器自己写一会儿就能造出这个"破坏保证"。两处改动：

1. **噪声下限**：`Start-NetGainGuard` 现在带 `NoiseFloorMB = 32` 与 `NoisePerMinMB = 8`（实测值写进注释），
   `Step-NetGainGuard` 与 `Stop-NetGainGuard` 的阈值都是 `max(下限, 速率 × 实际耗时)`。
   低于下限不再叫「保证被破坏」，而是 `NEUTRAL`，措辞也改成「本次运行没有测到可回收的空间」。
2. **归因**：`Step-NetGainGuard -RemovedItems <本目标实际删掉的条目数>`。**如果这个目标一个东西都没删掉，
   那空间下降不可能是它干的**，不再据此中止整个运行。—— 这正是那次的情况：更新缓存 309,887 次删除全被拒绝，
   目标释放 0 字节，而其他进程一直在写。真实退化（实测 >200 MB）仍然会中止：见下面的回归测试。

**同时修掉上一轮我引入的一个退化（重要）**：v1.1.1 的性能优化把删除集收敛成"最上层条目"，
于是 `children` 模式从"逐文件删"变成"整个目录树一次 `Remove-Item -Recurse`"。
**递归删除是全有或全无**：树里只要有一个文件被占用，整棵子树一个字节都释放不掉。
现在树删失败会**回退成逐文件删**（能吃多少吃多少），并带一个安全阀：同一棵树连续 200 次被拒就放弃并写明原因
（原来的逐文件逻辑在 309,887 个全部被拒的文件上磨了 20 分钟、写出 78 MB 日志、释放 0 字节）。

**`skipped.log` 本身也修了**：原来是**每个条目一次 `Add-Content`**，且**没有编码参数**（ANSI）。
这台机器上它长到 **78 MB / 344,131 行**，而且中文错误信息读出来全是乱码。
现在改成**每 500 条批量追加 + UTF-8**，并在超过 **16 MB 时轮转**（保留一份 `.1`）。

回归测试（都在本机跑过，用的是被占用文件的真实夹具）：

| 测试 | 断言 | 结果 |
|---|---|---|
| 一棵树里 1 个文件被独占打开 | 未占用的 2 个文件必须被删掉、`locked == 1` | **PASS**（改前是整棵树留下、释放 0） |
| 一棵树里 250 个文件全被占用 | 应该在 200 次连续被拒后放弃，而不是磨完 250（更不是磨完 30 万） | **PASS**（2 秒结束，`locked == 200`） |
| 17 MB 的 `skipped.log` | 应该轮转成 `.1`，新行写进新文件 | **PASS** |
| 守卫：-3.5 MB 且该目标什么都没删 | 不得中止 | **PASS** |
| 守卫：-3.5 MB 且该目标删掉了东西 | 不得中止（低于噪声下限） | **PASS** |
| 守卫：-200 MB 且删掉了东西 | **必须**中止 | **PASS** |
| 守卫：整轮 -3.5 MB | 判定 `NEUTRAL`，不是 `REGRESSION` | **PASS** |

> ⑪ **别用低于噪声阈值的差值做「保证」判定**：这块盘的背景写入速率是 7 MB/min 量级，
> 任何以 MB 为单位的净空间结论都要先给出噪声下限（本仓库用 32 MB + 8 MB/min）。
>
> ⑫ **递归删除是不可回退的放大器**：`Remove-Item -Recurse` 遇到一个占用就整棵树失败。
> 想快就得先算（一次遍历），但**删**的时候必须保留逐文件回退，否则"优化"会变成"什么都删不掉"。
>
> ⑬ **`.ps1` 必须保持纯 ASCII**：这一轮我往注释里写了中文，`_Common.ps1` 立刻多了 62 个非 ASCII 字节。
> Windows PowerShell 5.1 无 BOM 时按 ANSI 读脚本，非 ASCII 会变味。每次改完都要复核。

### 6.10 v1.1.1 修订：报告写入被占用、以及「删不掉却不说为什么」

一次真实 `-Execute` 的收尾是这样的：

```
SET-EQUIVALENT:  LOGICAL (file lengths) : 0.0 MB   <-- NOT reclaimed space
                 NET FREE (ground truth): -3.3 MB
[!] 9473 item(s) were locked / in use and were SKIPPED
[stderr] Set-Content : 对路径 "...\runtime\reports\system.json" 的访问被拒绝。
```

**问题一：报告被占用，整轮数字就丢了。** 窗口版正在显示那份报告时，它握着读句柄，
`Set-Content -LiteralPath` 直接覆写就「访问被拒绝」（`_Common.ps1:1288`，脚本最后一步）。
现在所有报告写入都走 `Write-ReportText`：**先写临时文件再替换**（读者握的是旧文件的句柄，
替换能成功），连替换都被拒时退回**带时间戳的文件名**，并打印新路径。8 处报告写入全部改过
（`_Common` 的 `Show-Summary` + 01/02×2/05/08/09×2）。

**问题二：删不掉却不说是什么占着。** 那次运行把 11.91 GB 列为"选中"，
实际 `LOGICAL 0.0 MB` —— 一条都没删掉，因为 20,080 个条目被拒。把 `skipped.log` 按目标归并：

| 占用方 | 被拒条目 |
|---|---:|
| Edge 缓存 | 8,370 |
| 360 安全浏览器 | 5,312 |
| 用户 Temp | 4,984 |
| Windows 日志 | 611 |
| Windows Update 缓存 | 498 |
| Delivery Optimization | 165 |

**是「程序正在运行」占着这些文件，不是 ACL、不是 VSS**（同一次运行里 `copies=0`，即没有任何卷影副本）。
现在 `Show-Summary` 会把这几个占用方**直接列在结尾**，并告诉用户「关掉这些程序再跑一次」。

顺带修掉一个测试才发现的问题：`Get-DirSizes` 原来把 `%TEMP%` 传来的 **8.3 短路径**
（`C:\Users\ADMINI~1\...`）和 `DirectoryInfo.FullName` 的**长路径**混在同一个字典里，
查询会静默失配。现在先把根解析成长路径，并保留调用方写法的别名。

**新增测试** `src/Tests.ps1`（21 项断言，失败返回码 1）：

```
.\src\Tests.ps1
=== 21 passed, 0 failed ===
```

覆盖：大小遍历器必须与旧 `Get-ChildItem` 求和逐字节一致、被占用文件时的删除回退、
每棵树 200 次连续被拒的安全阀、守卫的噪声下限与归因、报告写入被占用时不丢数字、
跳过快照日志的 UTF-8/批量/16 MB 轮转、**以及所有脚本必须纯 ASCII + LF**
（这条正是靠它抓到我这一轮又把中文写进 `_Common.ps1` 注释）。

> ⑭ **诊断「删不掉」时，先把拒绝按目标归并**：`skipped.log` 里 20,080 条拒绝一眼看是噪音，
> 按目录归并后立刻指向 Edge / 360 浏览器 —— 结论从"权限有问题"变成"关掉浏览器"。
> 工具自己就该做这件事（现在做了）。
>
> ⑮ **写自己的报告文件永远不要用"就地覆写"**：谁在显示它，谁就握着句柄。先写新的、再替换。

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

**沙箱限制令牌下 `pwsh` 起不来**：在 DSH 的 ACL 沙箱（`workspace-write` 文件策略）里，任何
`pwsh` 子进程都会在早期 DLL 初始化阶段以 `STATUS_DLL_INIT_FAILED (0xC0000142)` 死亡 ——
本机复现过，连一句 `Get-ChildItem` 都跑不出来。**因此脚本与构建必须在非受限
（`danger-full-access`）或提权会话里跑**。

**解释器版本不是固定的**：本机在非受限会话下 `pwsh` 是 **PowerShell 7.6.6**，不是 5.1；
`csc.exe` 在 `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\`。脚本保持 PS 5.1 兼容语法，
两个版本都能跑，但**不要把某次会话的版本当前提**（早期 README 断言「本机未安装 PowerShell 7」，
换一个会话就不成立了）。

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
| `catalog.json` | 清理清单（v1.1 起 `02 -ListCatalog` 除了打印还写这份；窗口版「系统缓存清理」页的「查看清理清单」把它渲染成本页的报告表） |
| `spacegrowth.json` | 空间反升诊断（卷影副本、再生、硬链接、系统文件） |
| `skipped.log` | 被跳过项（重解析点 / 被占用 / 硬链接） |
| `gpu-shadercache.json` / `rootleftovers.json` / `largedirs*.json` | 各专项记录 |
| `largedirs-gui.json` / `hidden-gui.json` | 「扫描」页两种模式各自的结果（内置扫描器写：总字节、文件/目录数、耗时、线程数、深度封顶计数、是否被取消、每条命中与其体积，以及 `TopFiles` —— 同一次遍历得到的大文件 Top 100） |
| `scan-cli.json` | `--scan-dirs` / `--scan-cache` 命令行扫描的结果（用于核对扫描器精度与速度） |
| `ledger.json` | 清理账本（v1.1）：`reports\` 里每一次清理的实测净释放按时间排列 + C 盘可用空间相对每次结束时的变化。由 GUI 自己汇总（`--ledger` 也能无头生成），不是脚本写的 |
| `updates-pause.json` / `updates-pause.dryrun.json` | 暂停/恢复 Windows 更新的记录（改了什么、每个值的前后对比、`Verdict` = `PAUSED` / `RESTORED` / `MISMATCH`） |
| `updates-pause-state.json` | **暂停前的原值快照**：`-Resume` 还原的唯一依据。**别删** —— 删了就只剩"取消暂停"而不是"还原原状" |
| `export-*.csv` | 窗口版报告页「导出为 CSV」的产物（UTF-8 带 BOM，Excel / 记事本不乱码） |

> 窗口版还有一个**不是报告**的文件，落在 `%LOCALAPPDATA%\CLean\`（`reports\` 的上一级）：
> `settings.txt`（v1.1 起唯一的一份界面设置：`mode` / `accent` / `rail`，删掉即回到出厂默认）。
> `gui-command-plan.txt` 与 `gui-crash.log` 也在这一级。
>
> v1.0 曾把设置分成 `theme.txt`（外观）与 `ui.txt`（功能栏折叠）两个文件，各自一套手写的
> `key=value` 解析。v1.1 合并成一个 `settings.txt` 与一个解析器；**旧的两个文件会在首次
> 运行时被读取一次并迁移**（实测：`mode=dark` / `accent=rose` / `rail=expanded` 全部保留），
> 之后就不再生成它们。

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

# AGENTS.md

Windows C 盘清理工具的一个仓库：**单文件、零依赖**的两个外壳（控制台菜单版 + WinForms 窗口版），
把 10 个 `.ps1` 清理脚本作为资源内嵌进 EXE，首次运行释放到 `%LOCALAPPDATA%\CDiskCleaner\runtime`。

安全契约（默认只读、删除类动作先 dry-run 再确认、脚本侧白名单 + 净空间守卫）与全部实测记录见
[`README.md`](README.md)。

## 构建

用系统自带的 `csc.exe`，不需要 .NET SDK / NuGet / 联网：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\src\Build.ps1        # 控制台版 -> C盘清理工具.exe
.\src\Build.ps1 -Gui   # 窗口版   -> C盘清理工具-GUI.exe
                       #            （GuiLauncher.cs + GuiPages.cs + GuiAnims.cs 一起送进 csc）
```

`reports/`（本机清理报告）与构建产物 `*.exe` 不入库，见 `.gitignore`。

> ⚠️ 本机 DSH 会话里 `HOME` 指向 `C:\dcam`，因此 `git config --global` 会写到 `C:/dcam/.gitconfig`，
> 而你自己的终端读的是 `C:\Users\Administrator\.gitconfig`。跑 git 命令时显式带上正确的 HOME，
> 否则配了等于没配。

## Agent skills

### Issue tracker

GitHub Issues in `lockhhhhh/cdisk-cleaner`, driven with the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, using the default label strings (`needs-triage` / `needs-info` / `ready-for-agent` / `ready-for-human` / `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

single-context. See `docs/agents/domain.md`.

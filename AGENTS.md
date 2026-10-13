# AGENTS.md

**CLean** —— Windows C 盘清理工具的一个仓库：**单文件、零依赖**的两个外壳（控制台菜单版 + WinForms 窗口版），
把 10 个 `.ps1` 清理脚本作为资源内嵌进 EXE，首次运行释放到 `%LOCALAPPDATA%\CLean\runtime`。

安全契约（默认只读、删除类动作先 dry-run 再确认、脚本侧白名单 + 净空间守卫）与全部实测记录见
[`README.md`](README.md)。

## 版本号

仓库根目录的 `VERSION`（`x.y.z`）是**唯一**来源，别在别处写死版本号：

- `src/Build.ps1` 读它，把 `VersionInfo.cs` 生成到临时编译目录（不落库），并写入程序集元数据；
  编译末尾会回读程序集版本比对，不一致则构建失败；
- `installer/CLean.iss` 读同一个文件（也接受 `/DMyAppVersion=` 覆盖，CI 用这个）；
- 发版时 tag 必须等于 `v` + `VERSION`，`.github/workflows/release.yml` 会强制校验。

## 构建

用系统自带的 `csc.exe`，不需要 .NET SDK / NuGet / 联网：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\src\Build.ps1        # 控制台版 -> CLean.exe
.\src\Build.ps1 -Gui   # 窗口版   -> CLean-GUI.exe
                       #            （GuiLauncher.cs + GuiPages.cs + GuiAnims.cs + UpdateCheck.cs）
```

`src/UpdateCheck.cs`（向 GitHub Releases API 问「有没有新版本」）**两个外壳共用**，改动它等于同时改两边。

## 安装包与发版

```powershell
winget install -e --id JRSoftware.InnoSetup       # 一次性
.\src\Build.ps1; .\src\Build.ps1 -Gui             # 安装包的输入
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\CLean.iss
# -> dist\CLean-Setup-<VERSION>.exe
```

`installer/` 是安装包脚本，`dist/` 是产物，两者都不入库（见 `.gitignore`）。
正式发版走 tag：改 `VERSION` → 提交 → `git tag v1.0.1 && git push origin main --tags`，
剩下的由 `.github/workflows/release.yml` 完成（windows runner 自带 `csc.exe`，所以 CI 里也不需要 SDK）。

`reports/`（本机清理报告）与构建产物 `*.exe` 同样不入库。

## 环境陷阱

> ⚠️ 在沙箱化 / 容器化的 agent 会话里 `HOME` 可能被指向别处（本项目实测遇到过指向 `C:\dcam`），
> 于是 `git config --global` 写到的不是用户真正的 `%USERPROFILE%\.gitconfig`：提交会报「没有作者身份」，
> 或者署错名。跑会提交 / 写配置的 git 命令前，先确认 `HOME` 与 `%USERPROFILE%` 一致，
> 不一致就临时对齐：`$env:HOME = $env:USERPROFILE`。
>
> ⚠️ 同理，工作目录不一定是仓库：有的会话把 workspace 放在别处，命令要显式带工作目录，
> 否则 `spawn ... ENOENT`（因为默认 CWD 不存在，而不是可执行文件缺失）。

## Agent skills

### Issue tracker

GitHub Issues in `lockhhhhh/CLean`, driven with the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, using the default label strings (`needs-triage` / `needs-info` / `ready-for-agent` / `ready-for-human` / `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

single-context. See `docs/agents/domain.md`.

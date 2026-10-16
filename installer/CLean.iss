; ============================================================================
;  CLean - Inno Setup 6 script
;
;  Build:  ISCC.exe installer\CLean.iss
;          ISCC.exe /DMyAppVersion=1.2.3 installer\CLean.iss   (override)
;
;  Version comes from ..\VERSION (single source of truth).
;  Output: ..\dist\CLean-Setup-<version>.exe
;
;  SAVE THIS FILE AS UTF-8 *WITH* BOM. It contains a Chinese shortcut name
;  ("CLean 控制台"); without the BOM Inno Setup assumes the system ANSI code
;  page and the name turns into mojibake in the Start Menu. Note that many
;  editors silently drop the BOM - check the first three bytes are EF BB BF.
;
;  Notes:
;   * PrivilegesRequired=lowest -> per-user install in
;     {localappdata}\Programs\CLean, so Setup itself never raises a UAC prompt.
;     The two shipped EXEs still carry a requireAdministrator manifest of their
;     own, so launching them (including the optional postinstall run) does
;     raise UAC - that is expected and intentional.
;   * PrivilegesRequiredOverridesAllowed must be "commandline", never "dialog":
;     "dialog" pops a visible "Select Setup Install Mode" window, and
;     /VERYSILENT does NOT suppress it (only /ALLUSERS or /CURRENTUSER do), so
;     every unattended install would wait forever for a click. "commandline"
;     keeps /ALLUSERS available for IT deployments without ever prompting.
;   * The uninstaller deliberately does NOT touch %LOCALAPPDATA%\CLean:
;     that directory holds the user's cleanup reports and theme settings.
; ============================================================================

#define MyAppName "CLean"
#define MyAppPublisher "lockhhhhh"
#define MyAppURL "https://github.com/lockhhhhh/CLean"

#ifndef MyAppVersion
  #define MyAppVersion Trim(FileRead(FileOpen(AddBackslash(SourcePath) + "..\VERSION")))
#endif

[Setup]
AppId={{8F3A1C7E-2B4D-4E6A-9C15-7D2B5E9A4F31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\CLean
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
OutputDir=..\dist
OutputBaseFilename=CLean-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\CLean-GUI.exe
UninstallDisplayName={#MyAppName} {#MyAppVersion}
; 程序图标：与 src\Build.ps1 用 /win32icon 嵌进两个 EXE 的是同一个文件
; （所以只改那一处，Setup.exe、卸载程序和所有快捷方式的图标一起变）。
; 这个 .ico 的 256px 帧是 PNG 压缩的，Inno Setup 6 支持。
SetupIconFile=..\assets\CLean.ico
WizardStyle=modern
; Version resource of Setup.exe itself, so the Windows Properties tab (and any
; security software) sees a real version instead of 0.0.0.0. The trailing .0
; turns the three-part VERSION into the four-part form the field wants.
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}.0
VersionInfoDescription={#MyAppName} Setup
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=MIT licensed - see LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\CLean.exe";     DestDir: "{app}"; Flags: ignoreversion
Source: "..\CLean-GUI.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";     DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";       DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";           Filename: "{app}\CLean-GUI.exe"
Name: "{group}\{#MyAppName} 控制台";     Filename: "{app}\CLean.exe"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";     Filename: "{app}\CLean-GUI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CLean-GUI.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

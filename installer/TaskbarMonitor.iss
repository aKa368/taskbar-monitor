; TaskbarMonitor installer — standard, transparent Inno Setup package.
; Build from a clean self-contained win-x64 publish directory.
; Inno Setup creates the installer. Authenticode signing is performed by
; jobs\Sign-Release.ps1 after ISCC has produced the final installer.
; CI may override MyAppVersion and MyReleaseRoot with /D preprocessor defines.

#define MyAppName "TaskbarMonitor"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#define MyAppPublisher "aKa368"
#define MyAppURL "https://github.com/aKa368/taskbar-monitor"
#define MyAppExeName "TaskbarMonitor.exe"
#ifndef MyReleaseRoot
  #define MyReleaseRoot "release-1.0.2"
#endif


[Setup]
AppId={{D2BDFD68-623E-42ED-ACE6-BD4FBB231E42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\{#MyReleaseRoot}
OutputBaseFilename=TaskbarMonitor-Setup-v{#MyAppVersion}-win-x64
SetupLogging=yes
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Windows 11 taskbar system monitor
VersionInfoCopyright=Copyright (C) 2026 aKa368

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Main payload. Source is a clean self-contained publish directory.
Source: "..\{#MyReleaseRoot}\staging\TaskbarMonitor\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Config\config.json"
; First-install default only. Runtime settings live in LocalAppData and are never overwritten.
Source: "..\{#MyReleaseRoot}\staging\TaskbarMonitor\Config\config.json"; DestDir: "{localappdata}\TaskbarMonitor\Config"; DestName: "config.json"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

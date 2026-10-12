#define MyAppName "CCC 资料打印端"
#define MyAppVersion "0.6.29"
#define MyAppPublisher "CCC 资料打印系统"
#define MyAppExeName "CCC资料打印端公开版.exe"

[Setup]
AppId={{5A42B3DB-95E2-4BC4-96A3-990B6DA39F13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\CCC资料打印端公开版
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist\0.6.29\installer
OutputBaseFilename=CCC资料打印端公开版安装包_v{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=yes
SetupLogging=yes
SetupIconFile=..\assets\app-icon.ico
VersionInfoVersion=0.6.29.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "..\dist\0.6.29\client\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\0.6.29\client\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "..\dist\0.6.29\client\x64\pdfium.dll"; DestDir: "{app}\x64"; Flags: ignoreversion
Source: "..\dist\0.6.29\client\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\0.6.29\client\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\0.6.29\client\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall

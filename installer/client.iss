#define MyAppVersion "0.6.29"
#define MyAppPublisher "CCC 资料打印系统"
#ifdef CompanyEdition
#define EditionSuffix ""
#define InstallerId "{0B43C92F-4FB6-4B40-9261-625EE17A6E26}"
#else
#define EditionSuffix "公开版"
#define InstallerId "{5A42B3DB-95E2-4BC4-96A3-990B6DA39F13}"
#endif
#ifndef PublishRoot
  #error PublishRoot must be supplied by the build script.
#endif
#define MyAppName "CCC 资料打印端" + EditionSuffix
#define MyAppExeName "CCC资料打印端" + EditionSuffix + ".exe"

[Setup]
AppId={{#InstallerId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\CCC资料打印端{#EditionSuffix}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#PublishRoot}\installer
OutputBaseFilename=CCC资料打印端{#EditionSuffix}安装包_v{#MyAppVersion}
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
Source: "{#PublishRoot}\client\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishRoot}\client\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#PublishRoot}\client\x64\pdfium.dll"; DestDir: "{app}\x64"; Flags: ignoreversion
Source: "{#PublishRoot}\client\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishRoot}\client\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishRoot}\client\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall

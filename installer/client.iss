#define MyAppName "CCC 资料打印端"
#define MyAppVersion "0.6.29"
#define MyAppPublisher "CCC 资料打印系统"
#define MyAppExeName "CCC资料打印端.exe"

[Setup]
AppId={{0B43C92F-4FB6-4B40-9261-625EE17A6E26}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\CCC资料打印端
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist\0.6.29\installer
OutputBaseFilename=CCC资料打印端安装包_v{#MyAppVersion}
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

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM "{#MyAppExeName}"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Sleep(800);
  Result := '';
end;

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

#ifndef PackageDir
  #define PackageDir AddBackslash(SourcePath) + "..\artifacts\Release\WlanGO-Package\app"
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\artifacts\Release\WlanGO-Installer"
#endif

[Setup]
AppId={{2A1BBDE1-45D7-4DA8-A248-7EC7E071D70C}
AppName=WlanGO
AppVersion={#AppVersion}
AppVerName=WlanGO {#AppVersion}
AppPublisher=WlanGO contributors
AppPublisherURL=https://github.com/Cooanyh/wlangohost
AppCopyright=Copyright (C) 2026 WlanGO contributors
VersionInfoVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\WlanGO
DefaultGroupName=WlanGO
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
AllowNoIcons=yes
CloseApplications=no
RestartApplications=no
SetupIconFile=..\src\PadP2P.App\Assets\PadP2P.ico
UninstallDisplayIcon={app}\WlanGO.exe
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=WlanGO-Setup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "languages\ChineseSimplified.isl"

[CustomMessages]
english.DesktopIconTask=Create a desktop shortcut
chinesesimp.DesktopIconTask=创建桌面快捷方式

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "WlanGO-LICENSE.txt"; Flags: ignoreversion
Source: "..\..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.md"; Flags: ignoreversion
Source: "..\..\third_party\Windows-classic-samples\LICENSE"; DestDir: "{app}"; DestName: "Windows-classic-samples-LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WlanGO"; Filename: "{app}\WlanGO.exe"
Name: "{autodesktop}\WlanGO"; Filename: "{app}\WlanGO.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WlanGO.exe"; Description: "{cm:LaunchProgram,WlanGO}"; Flags: postinstall nowait skipifsilent unchecked

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue: string;
  InstalledExe: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'WlanGO', RunValue) then
    begin
      InstalledExe := '"' + ExpandConstant('{app}\WlanGO.exe') + '"';
      if CompareText(Copy(RunValue, 1, Length(InstalledExe)), InstalledExe) = 0 then
        RegDeleteValue(HKCU,
          'Software\Microsoft\Windows\CurrentVersion\Run', 'WlanGO');
    end;
  end;
end;

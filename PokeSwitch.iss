[Setup]
; App Metadata
AppName=PokeSwitch
AppVersion=1.0.0
AppPublisher=MdAsifInIT
AppPublisherURL=https://github.com/MdAsifInIT/pokeswitch
ArchitecturesInstallIn64BitMode=x64

; Output Configuration
DefaultDirName={autopf}\PokeSwitch
DefaultGroupName=PokeSwitch
DisableProgramGroupPage=yes
OutputBaseFilename=PokeSwitchSetup
OutputDir=Output
Compression=lzma2/ultra64
SolidCompression=yes

; Admin privileges requested (since PokeSwitch itself requires admin)
PrivilegesRequired=admin

; Icon for the installer
SetupIconFile=PokeSwitch\Resources\PokeSwitch.ico
UninstallDisplayIcon={app}\PokeSwitch.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main Executable and bundled dependencies
Source: "PokeSwitch\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu Shortcut
Name: "{autoprograms}\PokeSwitch"; Filename: "{app}\PokeSwitch.exe"
; Desktop Shortcut
Name: "{autodesktop}\PokeSwitch"; Filename: "{app}\PokeSwitch.exe"; Tasks: desktopicon

[Run]
; Run after installation
Filename: "{app}\PokeSwitch.exe"; Description: "{cm:LaunchProgram,PokeSwitch}"; Flags: nowait postinstall skipifsilent

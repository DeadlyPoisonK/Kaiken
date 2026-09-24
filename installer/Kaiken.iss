; Instalador de Kaiken (Inno Setup).
; Cada vez que agregues una función nueva: sube AppVersion abajo, compila el add-in para
; cada versión de Revit que soportamos (build_addin 2025, build_addin 2026 — copiando cada
; .dll resultante a installer\build\Kaiken-<version>.dll), y vuelve a generar
; este instalador (build_installer). El .exe resultante queda en installer\output\ listo
; para mandarle a tu compañero. Un solo instalador deja el add-in andando tanto en Revit
; 2025 como en 2026 (cada Revit sólo lee su propia carpeta de Addins).
;
; Para un instalador de una sola versión fuera del combo (ej. 2024):
;   ISCC "/DRevitVersion=2024" Kaiken.iss
; (usa el .dll genérico de bin\Release, sin pasar por installer\build\)

#define MyAppName "Kaiken"
#define MyAppVersion "1.12.0"
#define MyAppPublisher "Kevin Perez"

; Si se pasó /DRevitVersion=... por línea de comandos, generamos un instalador de una sola
; versión (comportamiento legacy). Sin ese flag (caso normal), generamos el combinado.
#ifdef RevitVersion
  #define SingleVersion
#endif

[Setup]
AppId={{F1A3AC6D-C39D-4E5C-A635-83D1B4E65D79}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={userappdata}\Kaiken
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
#ifdef SingleVersion
OutputBaseFilename=Kaiken-Setup-{#MyAppVersion}-R{#RevitVersion}
#else
OutputBaseFilename=Kaiken-Setup-{#MyAppVersion}
#endif
Compression=lzma
SolidCompression=yes
InfoBeforeFile=CIERRA_REVIT_ANTES_DE_INSTALAR.txt
UninstallDisplayName={#MyAppName}
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
#ifdef SingleVersion
Source: "..\src\Kaiken\bin\Release\net8.0-windows\Kaiken.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\{#RevitVersion}"; DestName: "Kaiken.dll"; Flags: ignoreversion
Source: "..\manifest\Kaiken.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\{#RevitVersion}"; Flags: ignoreversion
#else
Source: "build\Kaiken-2025.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; DestName: "Kaiken.dll"; Flags: ignoreversion
Source: "..\manifest\Kaiken.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Flags: ignoreversion
Source: "build\Kaiken-2026.dll"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; DestName: "Kaiken.dll"; Flags: ignoreversion
Source: "..\manifest\Kaiken.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion
#endif

[Messages]
#ifdef SingleVersion
FinishedLabel=Kaiken {#MyAppVersion} se instaló correctamente.%n%nAbre (o reinicia) Revit {#RevitVersion} y busca la pestaña "Kaiken" en la cinta.
#else
FinishedLabel=Kaiken {#MyAppVersion} se instaló correctamente para Revit 2025 y 2026.%n%nAbre (o reinicia) Revit y busca la pestaña "Kaiken" en la cinta.
#endif

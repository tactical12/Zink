@echo off
setlocal

set "ROOT=%~1"
set "CONFIG=%~2"
set "VPL=%~3"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSINSTALL="

if "%ROOT%"=="" exit /b 2
if "%CONFIG%"=="" exit /b 2
if "%VPL%"=="" exit /b 2

if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
)

if "%VSINSTALL%"=="" (
  for /d %%i in ("%ProgramFiles%\Microsoft Visual Studio\*") do (
    if exist "%%~i\Community\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\Community"
    if exist "%%~i\Professional\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\Professional"
    if exist "%%~i\Enterprise\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\Enterprise"
    if exist "%%~i\BuildTools\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\BuildTools"
  )
)

if "%VSINSTALL%"=="" (
  echo Visual Studio C++ build tools were not found.
  exit /b 1
)

call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%

if not exist "%ROOT%\x64\%CONFIG%" mkdir "%ROOT%\x64\%CONFIG%"
if not exist "%ROOT%\obj\Native\IntelVplEncoder\%CONFIG%" mkdir "%ROOT%\obj\Native\IntelVplEncoder\%CONFIG%"

where cl.exe >nul 2>nul
if errorlevel 1 (
  echo cl.exe was not found after initializing the Visual Studio build environment.
  exit /b 1
)

cl.exe /nologo /LD /MD /EHsc /O2 /std:c++17 ^
  /I"%VPL%\lib\native\include" ^
  /Fo"%ROOT%\obj\Native\IntelVplEncoder\%CONFIG%\\" ^
  /Fe"%ROOT%\x64\%CONFIG%\ZinkIntelVplEncoder.dll" ^
  "%ROOT%\Native\IntelVplEncoder\ZinkIntelVplEncoder.cpp" ^
  /link /LIBPATH:"%VPL%\lib\native\win-x64" vpl.lib

exit /b %errorlevel%

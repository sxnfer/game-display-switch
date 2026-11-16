@echo off
rem Builds and runs the checks in tests\ against the app's own source files. Exit code 1 if any check fails.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=%~dp0tests\HdrSwitchTests.exe

"%CSC%" /nologo /codepage:65001 /target:exe /main:HdrSwitch.Tests.Run /out:"%OUT%" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  "%~dp0HdrSwitch.cs" "%~dp0Flyout.cs" "%~dp0Island.cs" "%~dp0Theme.cs" "%~dp0Motion.cs" "%~dp0Roll.cs" "%~dp0Resolution.cs" "%~dp0Nvidia.cs" ^
  "%~dp0tests\*.cs"
if errorlevel 1 exit /b 1
"%OUT%"

@ECHO OFF
pwsh.exe -NoLogo -NoProfile -ExecutionPolicy ByPass -Command "& """%~dp0build-windows.ps1""" %*"
EXIT /B %ERRORLEVEL%

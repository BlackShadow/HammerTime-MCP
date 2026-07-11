@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SERVER=%~dp0Server\hammertime-mcp.exe"
set "CLIENTS=%~1"
set "SCOPE=%~2"
set "HAMMERTIME_DIR=%~3"

echo.
echo Stopping any running HammerTime MCP server...
taskkill /F /IM hammertime-mcp.exe /T 2>nul
if %ERRORLEVEL%==0 echo HammerTime MCP server was running and has been stopped.

if not defined SCOPE set "SCOPE=user"

if /I "%SCOPE%"=="project" goto ScopeOk
if /I "%SCOPE%"=="user" goto ScopeOk
echo Invalid scope "%SCOPE%". Use "project" or "user".
exit /b 2
:ScopeOk

if exist "%SERVER%" goto ServerOk
echo Missing MCP server executable:
echo   %SERVER%
exit /b 1
:ServerOk

if not defined CLIENTS call :SelectClients
if not defined CLIENTS goto NoClientsSelected

pushd "%~dp0..\.." >nul 2>nul
if not errorlevel 1 goto ProjectDirOk
echo Could not resolve project directory:
echo   %~dp0..\..
exit /b 1
:ProjectDirOk
set "PROJECT_DIR=%CD%"
popd >nul

echo.
echo Installing HammerTime MCP...
echo   Clients:    %CLIENTS%
echo   Scope:      %SCOPE%
echo   Project:    %PROJECT_DIR%
if defined HAMMERTIME_DIR echo   HammerTime: %HAMMERTIME_DIR%
echo.


net session >nul 2>nul && goto InstallAllHere

call :InstallPluginElevated
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" goto InstallFailed

call :InstallClientsHere
set "EXIT_CODE=%ERRORLEVEL%"
goto AfterInstall

:InstallAllHere
if defined HAMMERTIME_DIR goto InstallAllExplicit
"%SERVER%" install --clients "%CLIENTS%" --scope "%SCOPE%" --project-dir "%PROJECT_DIR%"
set "EXIT_CODE=%ERRORLEVEL%"
goto AfterInstall
:InstallAllExplicit
"%SERVER%" install --hammertime-dir "%HAMMERTIME_DIR%" --clients "%CLIENTS%" --scope "%SCOPE%" --project-dir "%PROJECT_DIR%"
set "EXIT_CODE=%ERRORLEVEL%"
goto AfterInstall

:InstallPluginElevated
echo Requesting administrator privileges for HammerTime plugin files...
if defined HAMMERTIME_DIR goto InstallPluginElevatedExplicit
powershell -NoProfile -ExecutionPolicy Bypass -Command "$q=[char]34; $server='%SERVER%'; $args='install --plugin-only --project-dir ' + $q + '%PROJECT_DIR%' + $q; $p=Start-Process -FilePath $server -ArgumentList $args -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
exit /b %ERRORLEVEL%
:InstallPluginElevatedExplicit
powershell -NoProfile -ExecutionPolicy Bypass -Command "$q=[char]34; $server='%SERVER%'; $args='install --plugin-only --hammertime-dir ' + $q + '%HAMMERTIME_DIR%' + $q + ' --project-dir ' + $q + '%PROJECT_DIR%' + $q; $p=Start-Process -FilePath $server -ArgumentList $args -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
exit /b %ERRORLEVEL%

:InstallClientsHere
echo.
echo Installing MCP client configs as current user...
if defined HAMMERTIME_DIR goto InstallClientsExplicit
"%SERVER%" install --clients-only --clients "%CLIENTS%" --scope "%SCOPE%" --project-dir "%PROJECT_DIR%"
exit /b %ERRORLEVEL%
:InstallClientsExplicit
"%SERVER%" install --clients-only --hammertime-dir "%HAMMERTIME_DIR%" --clients "%CLIENTS%" --scope "%SCOPE%" --project-dir "%PROJECT_DIR%"
exit /b %ERRORLEVEL%

:AfterInstall
if "%EXIT_CODE%"=="0" goto Done
:InstallFailed
echo.
echo Install failed with exit code %EXIT_CODE%.
if defined HAMMERTIME_DIR goto Done
echo If HammerTime was not detected, re-run with its folder, for example:
echo   install.bat %CLIENTS% %SCOPE% "C:\Program Files (x86)\HammertimeEditor"

:Done
echo.
pause
exit /b %EXIT_CODE%

:NoClientsSelected
echo.
echo No AI clients selected. Nothing was installed.
set "EXIT_CODE=0"
echo.
pause
exit /b %EXIT_CODE%

:SelectClients
set "CLIENTS="
set "SELECT_FILE=%TEMP%\hammertime-mcp-clients-%RANDOM%%RANDOM%.txt"
set "HAMMERTIME_MCP_SELECT_FILE=%SELECT_FILE%"
rem Keep the keyboard selector encoded so CMD does not parse PowerShell pipes,
rem braces, or redirection characters before powershell.exe sees them.
set "SELECT_SCRIPT_B64="
set "SELECT_SCRIPT_B64=!SELECT_SCRIPT_B64!JGl0ZW1zID0gQCgKICAgIFtwc2N1c3RvbW9iamVjdF1Ae0lkPSd2c2NvZGUnO0xhYmVsPSdWUyBDb2RlJztQYXRocz1AKCRlbnY6QVBQREFUQSArICdcQ29kZVxVc2VyJyk7Q29tbWFuZD0nY29kZSc7Q2hlY2tlZD0kZmFsc2U7RGV0ZWN0ZWQ9JGZhbHNlfSwKICAgIFtwc2N1c3RvbW9iamVjdF1Ae0lkPSd2c2NvZGUtaW5zaWRlcnMnO0xhYmVsPSdWUyBDb2RlIEluc2lkZXJzJztQYXRocz1AKCRlbnY6QVBQREFUQSArICdcQ29kZSAtIEluc2lkZXJzXFVzZXInKTtDb21tYW5kPSdjb2RlLWluc2lkZXJzJztDaGVja2VkPSRmYWxzZTtEZXRlY3RlZD0kZmFsc2V9LAogICAgW3BzY3VzdG9tb2JqZWN0XUB7SWQ9J2N1cnNvcic7TGFiZWw9J0N1cnNvcic7UGF0aHM9QCgkZW52OlVTRVJQUk9GSUxFICsgJ1wuY3Vyc29yJyk7Q29tbWFuZD0nY3Vyc29yJztDaGVja2VkPSRmYWxzZTtEZXRlY3RlZD0kZmFsc2V9LAogICAgW3BzY3VzdG9tb2JqZWN0XUB7SWQ9J2NsYXVkZSc7TGFiZWw9J0NsYXVkZSBEZXNrdG9wJztQYXRocz1AKCRlbnY6QVBQREFUQSArICdcQ2xhdWRlJyk7Q29tbWFuZD0nJztDaGVja2VkPSRmYWxzZTtEZXRlY3RlZD0kZmFsc2V9LAogICAgW3BzY3VzdG9tb2JqZWN0XUB7SWQ9J2NsYXVkZS1jb2RlJztMYWJlbD0nQ2xhdWRlIENvZGUnO1BhdGhzPUAoJGVudjpVU0VSUFJPRklM"
set "SELECT_SCRIPT_B64=!SELECT_SCRIPT_B64!RSArICdcLmNsYXVkZScsICRlbnY6VVNFUlBST0ZJTEUgKyAnXC5tY3AuanNvbicpO0NvbW1hbmQ9J2NsYXVkZSc7Q2hlY2tlZD0kZmFsc2U7RGV0ZWN0ZWQ9JGZhbHNlfSwKICAgIFtwc2N1c3RvbW9iamVjdF1Ae0lkPSdjb2RleCc7TGFiZWw9J0NvZGV4IENMSSc7UGF0aHM9QCgkZW52OlVTRVJQUk9GSUxFICsgJ1wuY29kZXgnKTtDb21tYW5kPSdjb2RleCc7Q2hlY2tlZD0kZmFsc2U7RGV0ZWN0ZWQ9JGZhbHNlfSwKICAgIFtwc2N1c3RvbW9iamVjdF1Ae0lkPSdraW1pLWNvZGUnO0xhYmVsPSdLaW1pIENvZGUnO1BhdGhzPUAoJGVudjpVU0VSUFJPRklMRSArICdcLmtpbWknKTtDb21tYW5kPSdraW1pJztDaGVja2VkPSRmYWxzZTtEZXRlY3RlZD0kZmFsc2V9LAogICAgW3BzY3VzdG9tb2JqZWN0XUB7SWQ9J29wZW5jb2RlJztMYWJlbD0nT3BlbkNvZGUnO1BhdGhzPUAoJGVudjpVU0VSUFJPRklMRSArICdcLmNvbmZpZ1xvcGVuY29kZScpO0NvbW1hbmQ9J29wZW5jb2RlJztDaGVja2VkPSRmYWxzZTtEZXRlY3RlZD0kZmFsc2V9LAogICAgW3BzY3VzdG9tb2JqZWN0XUB7SWQ9J2FudGlncmF2aXR5JztMYWJlbD0nQW50aWdyYXZpdHknO1BhdGhzPUAoJGVudjpVU0VSUFJPRklMRSArICdcLmdlbWluaVxhbnRpZ3Jhdml0eScpO0NvbW1hbmQ9Jyc7Q2hlY2tlZD0kZmFsc2U7RGV0ZWN0ZWQ9"
set "SELECT_SCRIPT_B64=!SELECT_SCRIPT_B64!JGZhbHNlfSwKICAgIFtwc2N1c3RvbW9iamVjdF1Ae0lkPSdhbnRpZ3Jhdml0eS1jbGknO0xhYmVsPSdBbnRpZ3Jhdml0eSBDTEknO1BhdGhzPUAoJGVudjpVU0VSUFJPRklMRSArICdcLmdlbWluaVxhbnRpZ3Jhdml0eS1jbGknKTtDb21tYW5kPSdhbnRpZ3Jhdml0eSc7Q2hlY2tlZD0kZmFsc2U7RGV0ZWN0ZWQ9JGZhbHNlfSwKICAgIFtwc2N1c3RvbW9iamVjdF1Ae0lkPSdnZW1pbmktY2xpJztMYWJlbD0nR2VtaW5pIENMSSc7UGF0aHM9QCgkZW52OlVTRVJQUk9GSUxFICsgJ1wuZ2VtaW5pJyk7Q29tbWFuZD0nZ2VtaW5pJztDaGVja2VkPSRmYWxzZTtEZXRlY3RlZD0kZmFsc2V9LAogICAgW3BzY3VzdG9tb2JqZWN0XUB7SWQ9J3dpbmRzdXJmJztMYWJlbD0nV2luZHN1cmYnO1BhdGhzPUAoJGVudjpVU0VSUFJPRklMRSArICdcLmNvZGVpdW1cd2luZHN1cmYnKTtDb21tYW5kPScnO0NoZWNrZWQ9JGZhbHNlO0RldGVjdGVkPSRmYWxzZX0sCiAgICBbcHNjdXN0b21vYmplY3RdQHtJZD0nZ2VuZXJpYyc7TGFiZWw9J0dlbmVyaWMgY29uZmlnICgubWNwLmpzb24pJztQYXRocz1AKCk7Q29tbWFuZD0nJztDaGVja2VkPSRmYWxzZTtEZXRlY3RlZD0kZmFsc2V9CikKCmZvcmVhY2ggKCRpdGVtIGluICRpdGVtcykgewogICAgZm9yZWFjaCAoJHBhdGggaW4gJGl0ZW0uUGF0aHMpIHsKICAgICAg"
set "SELECT_SCRIPT_B64=!SELECT_SCRIPT_B64!ICBpZiAoJHBhdGggLWFuZCAoVGVzdC1QYXRoIC1MaXRlcmFsUGF0aCAkcGF0aCkpIHsgJGl0ZW0uRGV0ZWN0ZWQgPSAkdHJ1ZSB9CiAgICB9CiAgICBpZiAoLW5vdCAkaXRlbS5EZXRlY3RlZCAtYW5kICRpdGVtLkNvbW1hbmQpIHsKICAgICAgICBpZiAoR2V0LUNvbW1hbmQgJGl0ZW0uQ29tbWFuZCAtRXJyb3JBY3Rpb24gU2lsZW50bHlDb250aW51ZSkgeyAkaXRlbS5EZXRlY3RlZCA9ICR0cnVlIH0KICAgIH0KfQoKJGluZGV4ID0gMAp3aGlsZSAoJHRydWUpIHsKICAgIENsZWFyLUhvc3QKICAgIFdyaXRlLUhvc3QgJ0hhbW1lclRpbWUgTUNQIC0gc2VsZWN0IEFJIGNsaWVudHMgdG8gY29uZmlndXJlJwogICAgV3JpdGUtSG9zdCAnJwogICAgV3JpdGUtSG9zdCAnVXAvRG93bjogbW92ZSAgICBTcGFjZTogdGljay91bnRpY2sgICAgRW50ZXI6IGluc3RhbGwgICAgRXNjOiBjYW5jZWwnCiAgICBXcml0ZS1Ib3N0ICdOb3RoaW5nIGlzIHNlbGVjdGVkIGJ5IGRlZmF1bHQuJwogICAgV3JpdGUtSG9zdCAnJwoKICAgIGZvciAoJGkgPSAwOyAkaSAtbHQgJGl0ZW1zLkNvdW50OyAkaSsrKSB7CiAgICAgICAgJG1hcmsgPSBpZiAoJGl0ZW1zWyRpXS5DaGVja2VkKSB7ICdbeF0nIH0gZWxzZSB7ICdbIF0nIH0KICAgICAgICAkY3Vyc29yID0gaWYgKCRpIC1lcSAkaW5kZXgpIHsgJz4nIH0gZWxz"
set "SELECT_SCRIPT_B64=!SELECT_SCRIPT_B64!ZSB7ICcgJyB9CiAgICAgICAgJGRldGVjdGVkID0gaWYgKCRpdGVtc1skaV0uRGV0ZWN0ZWQpIHsgJyAoZGV0ZWN0ZWQpJyB9IGVsc2UgeyAnJyB9CiAgICAgICAgJGxpbmUgPSAnezB9IHsxfSB7Mn17M30nIC1mICRjdXJzb3IsICRtYXJrLCAkaXRlbXNbJGldLkxhYmVsLCAkZGV0ZWN0ZWQKICAgICAgICBpZiAoJGkgLWVxICRpbmRleCkgeyBXcml0ZS1Ib3N0ICRsaW5lIC1Gb3JlZ3JvdW5kQ29sb3IgQ3lhbiB9IGVsc2UgeyBXcml0ZS1Ib3N0ICRsaW5lIH0KICAgIH0KCiAgICAka2V5ID0gW0NvbnNvbGVdOjpSZWFkS2V5KCR0cnVlKQogICAgc3dpdGNoICgka2V5LktleSkgewogICAgICAgICdVcEFycm93JyB7CiAgICAgICAgICAgIGlmICgkaW5kZXggLWxlIDApIHsgJGluZGV4ID0gJGl0ZW1zLkNvdW50IC0gMSB9IGVsc2UgeyAkaW5kZXgtLSB9CiAgICAgICAgfQogICAgICAgICdEb3duQXJyb3cnIHsKICAgICAgICAgICAgaWYgKCRpbmRleCAtZ2UgJGl0ZW1zLkNvdW50IC0gMSkgeyAkaW5kZXggPSAwIH0gZWxzZSB7ICRpbmRleCsrIH0KICAgICAgICB9CiAgICAgICAgJ1NwYWNlYmFyJyB7CiAgICAgICAgICAgICRpdGVtc1skaW5kZXhdLkNoZWNrZWQgPSAtbm90ICRpdGVtc1skaW5kZXhdLkNoZWNrZWQKICAgICAgICB9CiAgICAgICAgJ0VudGVyJyB7CiAgICAgICAgICAgICRz"
set "SELECT_SCRIPT_B64=!SELECT_SCRIPT_B64!ZWxlY3RlZCA9IEAoJGl0ZW1zIHwgV2hlcmUtT2JqZWN0IHsgJF8uQ2hlY2tlZCB9IHwgRm9yRWFjaC1PYmplY3QgeyAkXy5JZCB9KQogICAgICAgICAgICBpZiAoJHNlbGVjdGVkLkNvdW50IC1ndCAwKSB7CiAgICAgICAgICAgICAgICBTZXQtQ29udGVudCAtTGl0ZXJhbFBhdGggJGVudjpIQU1NRVJUSU1FX01DUF9TRUxFQ1RfRklMRSAtVmFsdWUgKCRzZWxlY3RlZCAtam9pbiAnLCcpIC1FbmNvZGluZyBBU0NJSQogICAgICAgICAgICB9CiAgICAgICAgICAgIGV4aXQgMAogICAgICAgIH0KICAgICAgICAnRXNjYXBlJyB7CiAgICAgICAgICAgIGV4aXQgMgogICAgICAgIH0KICAgIH0KfQ=="
powershell -NoProfile -ExecutionPolicy Bypass -Command "$script=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:SELECT_SCRIPT_B64)); Invoke-Expression $script"
set "SELECT_EXIT=%ERRORLEVEL%"
set "SELECT_SCRIPT_B64="
set "HAMMERTIME_MCP_SELECT_FILE="
if not "%SELECT_EXIT%"=="0" (
    if exist "%SELECT_FILE%" del "%SELECT_FILE%" >nul 2>nul
    exit /b %SELECT_EXIT%
)
if exist "%SELECT_FILE%" (
    set /p "CLIENTS="<"%SELECT_FILE%"
    del "%SELECT_FILE%" >nul 2>nul
)
if defined CLIENTS echo Selected: %CLIENTS%
exit /b 0

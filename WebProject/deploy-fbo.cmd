@echo off
setlocal
rem Build WebProject (Release, MSBuild cua Visual Studio) roi chep vao mot chuong trinh FBO.
rem   deploy-fbo.cmd "\\172.168.5.14\CustomerPro\FBO\FSD_Dev\SP229"
rem Chep: WebProject.dll -> bin, ApiLogView.ashx -> AppHandler, zcrptapilog.aspx -> Main, api-log.css -> Css, api-log.js -> ClientScript.
rem Khong chep Web.config va FastBusiness.AjaxControlExtender.dll (chuong trinh FBO da co).
rem Chep DLL vao bin lam IIS khoi dong lai ung dung: nguoi dung dang mo FBO phai dang nhap lai.
if "%~1"=="" (
  echo Thieu duong dan chuong trinh FBO.
  exit /b 1
)
set PROGRAM=%~1
if not exist "%PROGRAM%\App_Data\Controllers" (
  echo "%PROGRAM%" khong phai chuong trinh FBO ^(khong co App_Data\Controllers^).
  exit /b 1
)
set MSBUILD=
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSBUILD=%%i
if "%MSBUILD%"=="" (
  echo Khong tim thay MSBuild cua Visual Studio.
  exit /b 1
)
cd /d "%~dp0"
"%MSBUILD%" WebProject.csproj -p:Configuration=Release -v:minimal -nologo
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
copy /Y "bin\WebProject.dll" "%PROGRAM%\bin\" || exit /b 1
copy /Y "AppHandler\ApiLogView.ashx" "%PROGRAM%\AppHandler\" || exit /b 1
copy /Y "Main\zcrptapilog.aspx" "%PROGRAM%\Main\" || exit /b 1
copy /Y "Css\api-log.css" "%PROGRAM%\Css\" || exit /b 1
copy /Y "ClientScript\api-log.js" "%PROGRAM%\ClientScript\" || exit /b 1
echo DONE
endlocal

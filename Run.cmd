@echo off
setlocal
pushd "%~dp0"

dotnet build -c Debug -nologo -v minimal
if errorlevel 1 (
    popd & exit /b 1
)

dotnet run --no-build -c Debug -- %*

popd
endlocal

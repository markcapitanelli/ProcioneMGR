@echo off
REM ============================================================================================
REM  Scorciatoia per la plancia di comando (tools/Procione).
REM
REM  Compila alla prima esecuzione e poi lancia direttamente l'eseguibile: la plancia serve
REM  soprattutto quando qualcosa non va, e in quel momento aspettare un `dotnet run` che ricompila
REM  ogni volta e' esattamente cio' che fa scegliere di non guardarla.
REM
REM  Uso:  procione            plancia interattiva
REM        procione stato      un quadro e via
REM        procione aiuto      tutti i comandi
REM
REM  Per ricompilare dopo una modifica: procione --ricompila
REM ============================================================================================
setlocal
set "PROGETTO=%~dp0tools\Procione\Procione.csproj"
set "EXE=%~dp0tools\Procione\bin\Release\net10.0\procione.exe"

if /I "%~1"=="--ricompila" (
    dotnet build "%PROGETTO%" -c Release --nologo -v minimal
    exit /b %ERRORLEVEL%
)

if not exist "%EXE%" (
    echo Compilo la plancia di comando...
    dotnet build "%PROGETTO%" -c Release --nologo -v quiet
    if errorlevel 1 exit /b 1
)

"%EXE%" %*
exit /b %ERRORLEVEL%

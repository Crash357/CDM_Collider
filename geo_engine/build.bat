@echo off
REM CDM Geo Engine — Build Script (Windows, MinGW/GCC)
REM ====================================================
REM Benötigt: MinGW (gcc.exe muss im PATH sein)
REM Ausführen: build.bat   → erzeugt cdm_geo_engine.dll

echo Baue cdm_geo_engine.dll ...
gcc -O2 -shared -fPIC -static-libgcc -o cdm_geo_engine.dll cdm_geo_engine.c -lm
if %ERRORLEVEL% == 0 (
    echo Fertig: cdm_geo_engine.dll
) else (
    echo FEHLER! GCC nicht gefunden oder Compile-Fehler.
    echo Installiere MinGW: https://winlibs.com/
)
pause

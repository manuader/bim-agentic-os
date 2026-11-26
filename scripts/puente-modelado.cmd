@echo off
rem Doble clic para abrir el puente de Modelado 3D. Dejar esta ventana abierta.
where pwsh >nul 2>nul
if errorlevel 1 (
  echo Falta PowerShell 7. Instalalo desde Microsoft Store: "PowerShell".
  pause
  exit /b 1
)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0puente-modelado.ps1"
echo.
echo El puente se cerro. Si hay un error arriba, sacale una foto y mandasela a Claude.
pause

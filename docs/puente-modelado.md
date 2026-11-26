# Puente de Modelado 3D

`scripts/puente-modelado.ps1` conecta la página **Modelado 3D** de la oficina virtual (Vercel) con esta PC:
recibe los encargos y archivos, los guarda en la carpeta de encargos (`.bim/modelado-3d-root.txt`) y le pasa el
trabajo a Claude Code (`claude -p`) en esta carpeta, con la habilidad `modelado-3d`.

- Escucha solo en `http://127.0.0.1:47610`; nada de afuera de la PC puede entrar.
- Cada pedido necesita el token de `.bim/puente-token.txt` (se crea solo) y un origen de `.bim/puente-origenes.txt`.
- Flujo: crear encargo → subir archivos → `ficha` (Claude arma `ficha.md`, sin tocar Revit) → `aprobar`
  (Claude retoma la misma sesión y modela) → la página lista y descarga `salida\`.
- Prueba: `pwsh -File tests/integration/test-puente-modelado.ps1` (usa un Claude falso).

Para usarlo: doble clic en `scripts/puente-modelado.cmd` y dejar la ventana abierta (usa PowerShell 7; "Ejecutar con PowerShell" abre la versión vieja y se cierra al instante).

Seguridad: el token es la única llave entre la página y Claude Code en esta PC (que puede usar la terminal y leer
los archivos subidos). No compartas `puente-token.txt` ni agregues orígenes que no sean tuyos.

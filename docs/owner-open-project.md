# Abrir un proyecto real para que Claude lo maneje (modo proyecto)

Con esto Revit abre **su** archivo `.rvt` (sin copiarlo) en una ventana de Revit **nueva**, con el canal de BIM Agentic OS activo, y Claude puede trabajar en él con las herramientas `revit_*` del MCP. Los Revit que ya tenga abiertos no se tocan.

## Una sola vez: habilitar la carpeta

El archivo `.bim/revit-enabled-roots.txt` del repositorio dice en qué carpetas se permite escribir. Una ruta completa por línea; las líneas con `#` son comentarios. Por ejemplo:

```text
# Mis proyectos reales
C:\Users\ezead\OneDrive\Documentos\ARQ.ARG
```

Si su proyecto no está en una de esas carpetas, el script se niega y le dice qué agregar.

## Cada vez: abrir el proyecto

1. Guarde y cierre ese proyecto en cualquier otro Revit (dos Revit sobre el mismo archivo pisan los cambios).
2. Abra PowerShell 7 (`pwsh`) en la carpeta del repositorio y pegue (cambie la ruta por la de su proyecto):

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-open-project.ps1 -Path 'C:\Users\ezead\OneDrive\Documentos\ARQ.ARG\BIM-Agentic-Test.rvt'
```

3. El script compila el complemento y abre un Revit nuevo. Cuando aparezca el aviso de seguridad de **BIM Agentic OS Project** en esa ventana, haga clic en **Load Once** (Cargar una vez).
4. Cuando termina, el script muestra un bloque `=== Copie y pegue esto en el chat con Claude ===` con `pipe_name`, `document.path` y `document.unique_id`. Péguelo en el chat. Los mismos datos quedan en `.bim/open-project/latest.json`.

## Mientras trabaja

- Los cambios que hace Claude quedan en el modelo abierto pero **no se guardan solos**: guárdelos usted con **Ctrl+S** en ese Revit cuando le parezca bien.
- Puede seguir dibujando a mano en esa misma ventana.
- Al cerrar ese Revit el canal desaparece; para seguir otro día, vuelva a ejecutar el paso 2.

## Si algo quedó a medias

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-open-project.ps1 -Stop
```

Quita el manifiesto de prueba si quedó instalado y le dice si el Revit del proyecto sigue abierto. Nunca cierra Revit: ciérrelo usted (guardando antes si quiere).

## Qué rechaza el script (a propósito)

- Un archivo fuera de las carpetas habilitadas, un `.rfa`/`.rte` o una copia de respaldo (`Nombre.0001.rvt`).
- Un proyecto que parece abierto en otro Revit (el nombre aparece en el título de una ventana de Revit) o que otro programa tiene abierto.
- Un modelo compartido (workshared, con carpeta `Nombre_backup` al lado).
- Un enlace simbólico o junction entre la carpeta habilitada y el archivo.

## Para quien mantiene el repositorio

- El hijo recibe `BIM_AGENTIC_PROJECT_CONFIG` (nunca `BIM_AGENTIC_TEST_CONFIG`); con ninguna o con las dos variables el complemento no abre canal (`AddInGate.StartupMode`).
- `ProjectBootstrap` valida la configuración (`ProjectConfig`), vuelve a aplicar `OwnerProjectGuard` con las carpetas habilitadas del repositorio donde está el complemento copiado (`.bim/open-project/sessions/<utc>/addin`), abre el documento, comprueba ruta, que no sea de solo lectura, familia ni compartido, y recién entonces abre el canal y publica `project-result.json`.
- El manifiesto se instala sólo hasta que el hijo cargó el complemento y se quita enseguida.
- El servidor MCP que use Claude tiene que ser el de este repositorio (`scripts/install-mcp.ps1`): sólo así lee el mismo `.bim/revit-enabled-roots.txt`.

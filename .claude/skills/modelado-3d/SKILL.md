---
name: modelado-3d
description: Modelar un encargo en Revit con BIM Agentic OS (cabaña, ascensor, mueble, cualquier objeto) a partir de una ficha de entrada aprobada. Usar siempre que el dueño pida modelar, rehacer o corregir algo en Revit.
---

# Modelado 3D en Revit

La salida es siempre la misma: **un modelo BIM en Revit** (más adelante, otros programas). Lo que cambia es la
entrada. Cada encargo también mejora la herramienta: lo que falló o se aprendió vuelve al playbook y, si hace falta,
a un comando nuevo.

El modelo tiene que servir para documentar: plantas, cortes y contenido gráfico salen de él sin perder fidelidad.
Por eso **no se modela sin una ficha aprobada**. Con una ficha precisa (por ejemplo la ficha técnica de un
fabricante) el modelo sale bien a la primera. Con una idea vaga sale un esquema.

## 0 · Conexión

- La conexión con Revit son las herramientas `bim-agentic-os` (servidor MCP de esta carpeta, `.mcp.json`). Si no
  aparecen, pedile al dueño que corra `actualizar-todo.ps1` (o `mcp-update.ps1`) y reinicie Claude Code.
- Pipe de Revit: el último `.bim/open-project/sessions/*/project-result.json` (`bim-agentic-os-2025-<pid>`).
  Si cambió el pid, Revit se reabrió.
- Solo se escribe en copias de prueba y carpetas habilitadas (`.bim/revit-enabled-roots.txt`: `ARQ.ARG` y la raíz de encargos).
  Nunca el modelo de producción. Guardá con `document.save` al cerrar cada fase.

## 1 · Ficha de entrada (antes de tocar Revit)

1. Carpeta del encargo: `<raíz de encargos>\<encargo>\` con `entrada\` (lo que subió el dueño), `ficha.md`,
   `bitacora.md` y `salida\` (RVT, imágenes, exportaciones). La raíz es la de la oficina virtual
   (Modelado 3D → Encargos) y se lee de `.bim/modelado-3d-root.txt`; si no existe, preguntala. Esa raíz tiene que
   estar en `.bim/revit-enabled-roots.txt`.
2. Copiá la plantilla que corresponda de `fichas/` (`ascensor.md`, `arquitectura.md`; si no hay, partí de
   `arquitectura.md`) y llenala con lo que dé el dueño: fichas técnicas, planos, PDFs, croquis, fotos.
3. Si hay ficha de fabricante, cada medida lleva su fuente (página / plano). Lo que falte va a "Para confirmar".
4. Si hay imágenes de referencia, replicalas fielmente: no reinterpretes. Si una referencia no se puede construir
   con los comandos disponibles, decilo antes y proponé la aproximación.
5. Mandá la ficha (texto o una lámina simple) y **esperá el "aprobado"**. Corregir papel es barato; corregir
   Revit no.

Reglas de diseño del dueño (arquitectura): planta simple y ordenada (grilla, cuadrado, círculo; nada de trapecios
ni diagonales en planta); la complejidad va en volumen, cubierta, sustracciones y materiales; muros gruesos;
encuentros resueltos (alero, cumbrera, zócalo, esquinas, cambios de material).

### Según la entrada

| Entrada | Cómo se lee | Precisión |
|---|---|---|
| PDF / lámina técnica (fabricante, planos) | Leer texto y rasterizar páginas; tomar cotas, materiales, cortes; citar página | Alta |
| CAD (DXF/DWG; DWG se pide en DXF) | Leer capas, entidades y cotas con un script; cada archivo = una vista (planta, corte, vista) | Alta |
| Imágenes / renders (varias del mismo objeto) | Mirar todas; identificar partes, proporciones y materiales; estimar medidas desde una cota conocida o la que dé el prompt; marcar todo como estimación | Media/baja |
| Croquis a mano | Como imagen, más el criterio de grilla del dueño | Media |
| Solo texto (prompt) | Ficha con supuestos explícitos para aprobar | Baja |

El prompt del dueño siempre aclara qué es y qué importa. Si las entradas se contradicen, manda la más precisa
(lámina técnica > CAD > imagen > texto), salvo que el prompt diga otra cosa.

## 2 · Plan de modelado

Traducí la ficha a coordenadas: origen del encargo (lejos de otros modelos del archivo), grilla, niveles, tipos con
capas reales (muro, piso, cubierta), vértices de cubierta con cota, aberturas con posición a lo largo del muro.
Calculá con un script (Python) en vez de a mano; guardá los números en la bitácora.

## 3 · Modelado por lotes

- Una fase = un `command.batch` (todo o nada): tipos → muros y pisos → cubiertas → perfiles y aberturas →
  equipamiento → vistas. Pocas llamadas grandes gastan mucho menos que muchas chicas.
- Antes de inventar, leé `docs/revit-playbook.md`: ahí están las trampas conocidas (uniones de muros que corren los
  extremos, tipos de fundación estructurales, orden de capas, perfiles de muro, pliegues de cubierta).
- Consultas: pedí solo lo necesario (`limit`, `parameters`); evitá listados enormes.

## 4 · Verificación y entrega

- Vistas mínimas: 3D desde la orientación que muestra el concepto, 3D opuesta, corte por el elemento principal,
  planta. Nombralas `<encargo> ...`.
- Exportá las imágenes, **miralas vos primero** y compará contra la ficha y las referencias. Corregí lo que no
  coincida antes de mostrar.
- Entregá: imágenes + qué quedó, qué no, y qué falta confirmar. Sin relato de pasos.

## 5 · Registro

- `bitacora.md` del encargo: decisiones, coordenadas, ids de vistas, pendientes.
- Toda técnica o error nuevo de Revit va a `docs/revit-playbook.md` (eso es lo que hace que el próximo encargo salga
  mejor). Si falta una capacidad de la herramienta, anotala en "Pending capabilities" del playbook.

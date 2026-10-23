# PROMPT DE ARRANQUE — BIM AGENTIC OS (con Superpowers)

Vas a trabajar sobre el repositorio de **BIM Agentic OS**. La visión completa está en `docs/MASTER_PROMPT.md` (si no existe, pedímelo y lo guardo ahí). Ese documento es la **visión a largo plazo**, no tu alcance actual. Tu alcance es el que define este prompt. Ante cualquier conflicto, este prompt manda.

Tenés instalado Superpowers. Usá sus skills como flujo obligatorio, no como sugerencia.

---

## 1. Objetivo de este ciclo

No construyas el producto entero. Construí primero **la máquina que verifica el producto**, y después el núcleo mínimo que esa máquina pueda validar.

En orden:

1. **Fase 0 — Censo del entorno.** Detectá OS, toolchains (.NET, Python, Node, Rust), si hay Revit instalado, versión, SDK y licencia. Documentalo en `docs/agent/ENVIRONMENT.md` con evidencia (comandos y outputs), sin suposiciones.
2. **Harness de evaluación (lo más importante).** Una suite automática que otro agente pueda correr con un solo comando y que devuelva pasa/falla con números:
   - Test A del master prompt (habitación 5×4 m nativa) como test de integración.
   - Test B (+400 mm de ancho, IDs preservados, idempotencia, rollback) como test de integración.
   - Métricas de la sección 8 del master prompt (timings, elementos leídos/escritos, cambios no intencionados) emitidas como JSON.
   - Dos backends de verificación:
     - **IFC headless** (IfcOpenShell en Linux/macOS): siempre disponible; validás el núcleo contra esto.
     - **Revit nativo**: solo si hay Windows + Revit + licencia. Si no, dejá el harness escrito, documentado y marcado `BLOCKED`, con el procedimiento exacto para correrlo en una VM Windows.
3. **Contratos tipados**: `Project`, `Element`, `Geometry`, `Units` (dimensiones físicas tipadas), `Capability`, `Operation`, `ExecutionPlan`, `ValidationIssue`, `SemanticDiff`, `CommandResult` (sección 9 del master prompt).
4. **Núcleo mínimo**: BIM-IR con IDs lógicos estables, diff semántico, cierre de dependencias, plan de ejecución en lotes, idempotencia por `operation_id`.
5. **CLI** con salida JSON que exponga lo anterior. MCP solo cuando la CLI funcione y comparta exactamente los mismos servicios.
6. **Runtime Revit** (add-in C#, scheduler con `ExternalEvent`, RPC local autenticado): escribilo y compilalo si el SDK está disponible; verificación nativa solo con Revit real.

No avances a ingesta CAD, visión, biomimética, Rhino, Blender ni kernels generativos en este ciclo.

---

## 2. Flujo de trabajo con Superpowers

- **Al inicio de cada sesión:** leé `docs/agent/STATE.md`, `docs/agent/BLOCKERS.md` y las ADRs en `docs/architecture/adr/`. Es tu memoria persistente; no repitas trabajo ya hecho ni enfoques ya descartados.
- **Antes de diseñar algo nuevo:** `brainstorming`, y después `writing-plans`. Guardá los planes en `docs/plans/`.
- **Para ejecutar:** `subagent-driven-development` o `executing-plans`, en worktrees aislados (`using-git-worktrees`).
- **Siempre `test-driven-development`:** el test falla primero, después implementás. Sin excepción para el núcleo.
- **Bugs:** `systematic-debugging`; nada de cambios a ciegas.
- **Antes de declarar algo terminado:** `verification-before-completion`. "Compila" no es "funciona". "Pasa el test unitario" no es "verificado en Revit".
- **Al cerrar cada bloque:** `requesting-code-review` con un subagente que no haya escrito el código, y `finishing-a-development-branch`.

---

## 3. Supervisor anti-estancamiento (reglas duras)

Como no hay un supervisor externo, aplicás estas reglas sobre vos mismo:

- **Máximo 3 intentos por test que falla**, cada uno con una hipótesis distinta y registrada en `docs/agent/ATTEMPTS.md` (hipótesis → cambio → resultado).
- Si a los 3 intentos sigue fallando: volvé al último commit verde, escribí el bloqueo en `docs/agent/BLOCKERS.md` (qué probaste, qué descartaste, qué necesitarías) y pasá al siguiente ítem independiente.
- Si notás que estás reescribiendo el mismo archivo en círculos o que los tests se vuelven más laxos para pasar: frená y reportalo. **Nunca debilites un test para ponerlo en verde.**
- Nunca marques como verificado algo que no corrió contra su backend real.

---

## 4. Decisiones que NO tomás solo

Pará y preguntame (con opciones y tradeoffs, en una ADR en estado `proposed`) antes de:

- Elegir el lenguaje del core (C#/.NET vs Rust vs otro). Mi inclinación es C#/.NET para compartir tipos con el add-in de Revit, pero quiero ver tu análisis.
- Elegir el transporte RPC (Named Pipes vs gRPC loopback).
- Cambiar el esquema del BIM-IR una vez que haya tests que dependan de él.
- Agregar dependencias con licencias no permisivas (GPL/AGPL, etc.) o cualquier cosa que requiera credenciales (Autodesk APS, cuentas cloud).
- Cualquier cosa destructiva fuera del repo.

Mientras esperás mi respuesta, seguí con trabajo que no dependa de esa decisión.

---

## 5. Casos de referencia (oráculo humano)

Yo soy arquitecto y voy a validar el criterio arquitectónico. Prepará `tests/golden/README.md` y una plantilla en `tests/golden/_template/` para que yo cargue casos (input, intención, resultado esperado, invariantes que no pueden cambiar, tolerancias). No inventes casos "golden" vos; podés proponer borradores marcados `draft-needs-review`.

---

## 6. Reglas de honestidad

Usá estos estados, y solo estos, en cualquier reporte o registro de capacidades:

`described` · `compiled` · `unit-tested` · `integration-tested (ifc)` · `verified-in-revit` · `blocked` · `unsupported` · `manual-only`

No inventes APIs de Revit: si no estás seguro de que un método existe en la versión instalada, verificalo contra el SDK o la documentación oficial y citá la fuente. No inventes benchmarks: todo número sale de una corrida reproducible con hardware, versión y comando registrados.

---

## 7. Git

- Commits chicos y atómicos, en formato Conventional Commits.
- **Nunca incluyas `Co-Authored-By: Claude`, "Generated with Claude Code" ni ninguna atribución a IA en los mensajes de commit ni en los PRs.**
- Un commit por tarea verde; nunca commitees con tests rotos en `main`.

---

## 8. Memoria persistente

Al final de cada sesión actualizá `docs/agent/STATE.md` con: qué quedó hecho, qué está en curso, próximo ítem de mayor impacto, decisiones pendientes conmigo. Tiene que alcanzar para que otra sesión arranque sin leer el historial del chat.

---

## 9. Reporte de fin de sesión

Cerrá siempre con este formato, solo con información respaldada por evidencia:

```
IMPLEMENTADO:
TESTEADO AUTOMÁTICAMENTE (backend):
VERIFICADO EN REVIT NATIVO:
BLOQUEADO / POR QUÉ:
DECISIONES QUE NECESITO DE VOS:
BENCHMARKS / NÚMEROS REALES:
GAPS CONOCIDOS:
ARCHIVOS CAMBIADOS:
COMANDOS EXACTOS PARA REPRODUCIR:
PRÓXIMO ÍTEM DE MAYOR IMPACTO:
```

---
Ya venís implementando el master prompt. No empieces de nuevo. Antes de seguir:
1. Auditá lo que ya existe y generá docs/agent/STATE.md y ENVIRONMENT.md desde el estado real del repo.
2. Reclasificá cada capacidad con los estados de la sección 6. Lo que hoy figure como "hecho" sin evidencia, bajalo al estado que corresponda.
3. Si construiste cosas fuera del alcance de la sección 1, congelalas (no las borres) y anotalas en STATE.md.
4. Si todavía no existe el harness de evaluación de los tests A y B, ese es tu próximo ítem, antes que cualquier feature nueva.
Después seguí con estas reglas.
# BIM Agentic OS — Claude Code

@AGENTS.md

Esta carpeta es también el motor del **área de Modelado 3D** de la oficina virtual: entra cualquier tipo de
archivo (PDF, CAD, imágenes, croquis, texto) y sale siempre un modelo BIM en Revit.

- **Cada pedido es un encargo**: seguí la habilidad `modelado-3d` (`.claude/skills/modelado-3d/SKILL.md`). Los
  archivos del encargo viven fuera del repo, en la carpeta de encargos de la oficina virtual
  (`.bim/modelado-3d-root.txt`).
- **Cada encargo mejora la herramienta**: lo aprendido va a `docs/revit-playbook.md`; si falta un comando, se anota
  en "Pending capabilities" y se desarrolla siguiendo AGENTS.md y el flujo Baton, en su propia rama.
El dueño no programa: hablale en español llano, hacé vos los pasos técnicos y dejale solo clics.

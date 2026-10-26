# PROMPT DE LANZAMIENTO — Etapa 2 (Foundry + Arc)

> Igual que `prompts/LAUNCH-DEMO.md` del repo principal (`azure-sre-agent-customer-demo`), pero para esta demo complementaria. Úsalo justo antes de mostrarle esto al cliente — normalmente **inmediatamente después** de correr el `LAUNCH-DEMO.md` del repo principal, ya que ambas demos comparten las mismas 2 VMs.

## Prompt a usar

```
Necesito dejar lista la demo de la Etapa 2 (jorgedvp/azure-sre-agent-foundry-arc)
para mostrarla hoy. Haz esto sin pedirme pasos intermedios:

1. Verifica que vm-onprem-sql y vm-onprem-iis (rg-sre-customer-demo) estén
   encendidas y Arc en estado "Connected" — si ya ejecutaste el LAUNCH-DEMO.md
   del repo principal, probablemente ya está listo; si no, enciéndelas y espera
   a que Arc reconecte en ambas.

2. Escala el Container App (ca-arc-foundry-copilot, rg-sre-foundry-arc-poc)
   a min-replicas=1 si está en modo ahorro (scale-to-zero), para evitar el
   cold-start durante la demo en vivo.

3. Verifica /api/health del Container App y confirma que responde 200.

4. Dame la URL pública del Container App y confírmame que las 4 pruebas
   (inventario, OS, DB, IIS) deberían funcionar — no hace falta que las
   ejecutes tú, solo confirma que el ambiente está listo.
```

## Lo único que Jorge hace manualmente durante la demo en vivo

1. **Abrir la URL** del Container App en el navegador (o usar el chip de sugerencia si prefieres no escribir).
2. **Pegar el texto exacto** de cada paso de `docs/GUIDE.md` en el chat.
3. **Para el paso de base de datos**, pegar la contraseña SA real que te dé el asistente (la misma que usas en el Escenario 3 de la demo principal).

Nada de aprobaciones aquí — esta demo es de **solo lectura por diseño**, no hay pasos de "Approve action" como en la demo principal.

## Al finalizar

```
Termina la demo de la Etapa 2: escala el Container App de vuelta a
min-replicas=0 para no generar costo, y si no hay otra demo pronto,
desasigna también las VMs del repo principal.
```

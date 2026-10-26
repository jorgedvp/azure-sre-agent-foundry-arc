# Azure SRE Agent — Etapa 2: Agente custom en Microsoft Foundry sobre el mismo inventario Arc

> **Esto es complementario, no un reemplazo.** El demo original (`jorgedvp/azure-sre-agent-customer-demo`) sigue funcionando exactamente igual — este repo prueba que el mismo inventario de Azure Arc puede ser consumido por un "cerebro" distinto (un agente custom en Microsoft Foundry) para clientes que necesiten más personalización de la que ofrece el producto gestionado Azure SRE Agent.

## La pregunta que esto responde

> "Si todo funcionó usando SRE Agent, y un cliente quiere algo más personalizado a sus necesidades, ¿podría también hacerlo funcionar conectando Arc contra agentes en Foundry?"

**Sí.** Este repo es la prueba de concepto funcionando en vivo: un agente construido sobre Microsoft Foundry (modelo `gpt-4o`, con function calling) que lee e investiga el **mismo servidor Arc** (`vm-onprem-sql`) usado en la demo original — sin que el demo original se haya tocado ni modificado en absoluto.

## Qué NO se tocó del ambiente original

- `rg-sre-customer-demo` — mismo resource group, mismas 2 VMs, mismo SRE Agent (`sre-customer-demo`), misma configuración de Defender/Monitor.
- El único cambio ahí es **aditivo**: se agregaron 3 asignaciones de rol (Reader, Azure Connected Machine Resource Administrator, Log Analytics Reader) a una **identidad administrada nueva**, dedicada exclusivamente a esta Etapa 2. Ningún permiso existente del SRE Agent fue modificado o removido.
- Los 4 escenarios de la demo original (`docs/GUIDE.md` del repo `azure-sre-agent-customer-demo`) siguen funcionando exactamente igual que antes.

## Arquitectura

```
Frontend de chat (HTML/JS servido por la misma API — sin build step)
        │
        ▼
.NET API (Azure Container Apps) — identidad administrada dedicada
        │
        ├─► Microsoft Foundry (gpt-4o, function calling) — autenticación vía Entra ID
        │
        └─► Herramientas (tools) que llaman directo a:
              • Azure Resource Manager (Microsoft.HybridCompute) — listar/inspeccionar Arc
              • Azure Connected Machine Run Command — SOLO diagnósticos de lectura
                (bloqueo de comandos de escritura a nivel de aplicación, documentado abajo)
              • Log Analytics — consultas KQL de solo lectura
              • GitHub API — crear Issues etiquetados [Foundry POC] (nunca se mezclan
                con los Issues/PR reales del SRE Agent)
                        │
                        ▼
              Azure Arc — EXACTAMENTE el mismo inventario de vm-onprem-sql / vm-onprem-iis
```

## Límite de seguridad de esta prueba de concepto (importante)

Azure RBAC sobre Arc no tiene un rol granular de "solo diagnóstico" — el rol **Azure Connected Machine Resource Administrator** que necesita este agente para poder ejecutar cualquier comando es el mismo que usa el SRE Agent, y otorga el mismo alcance (root en Linux / SYSTEM en Windows) una vez que el comando corre. Esta es la misma observación documentada en `docs/ARCHITECTURE.md` del demo original: **el RBAC controla quién puede actuar, no qué puede hacer la acción una vez que corre.**

Por eso, en esta prueba de concepto el límite de seguridad se aplica **a nivel de aplicación**: el backend revisa cada script antes de ejecutarlo contra una lista de patrones bloqueados (`KILL`, `DROP`, `REVOKE`, `Stop-Service`, `rm -rf`, etc.) y **rechaza cualquier comando que modifique estado**, permitiendo solo diagnósticos de lectura (estado de servicios, uso de disco, procesos, etc.). Una versión productiva agregaría aquí el mismo paso de aprobación humana que ya usa Azure SRE Agent.

## Qué puedes mostrarle al cliente con esto

1. **Preguntar en el chat custom**: "Lista los servidores Arc conectados" → el agente Foundry devuelve el inventario real (mismo que ven en el SRE Agent).
2. **Pedir un diagnóstico de solo lectura**: "Revisa el uso de disco en vm-onprem-sql" → ejecuta un comando real vía Arc Run Command y devuelve el resultado.
3. **Intentar pedir algo destructivo** (opcional, para mostrar el guardrail): "Reinicia el servicio SQL Server" → el agente explica que esta prueba de concepto bloquea comandos de escritura por diseño.
4. **Documentar un hallazgo**: pedirle que abra un GitHub Issue con lo que encontró — aparece en el mismo repo del demo original, pero claramente etiquetado `foundry-poc` para no confundirse con el trabajo del SRE Agent.

## Despliegue

Ver `docs/DEPLOY.md` para el detalle completo de recursos creados y cómo reproducir este ambiente.

## Diseño original de esta fase

Ver `azure-sre-agent-customer-demo/docs/STAGE2-FOUNDRY-PLAN.md` para el documento de alcance original que dio origen a esta construcción.

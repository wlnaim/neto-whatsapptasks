# Referencia técnica

Detalles de funcionamiento de WhatsApp Tareas. Para instalar y empezar, ve al [README](../README.md); para ejemplos de uso, a [EJEMPLOS](EJEMPLOS.md).

## Datos y configuración

- La URL base por defecto es `http://localhost:8080`. El envío es `POST /message/sendText/{instancia}`, encabezado `apikey`, cuerpo JSON con `number` y `text`.
- La clave de Evolution se guarda protegida con Windows DPAPI para el usuario actual. La de Anthropic se lee desde su archivo `.txt` cada vez que se necesita.
- Los datos (tareas, historial) están en `%LOCALAPPDATA%\WhatsAppTasks\WhatsAppTasks.db`.
- Para una prueba aislada: `WhatsAppTasks.App.exe --show --data-dir=C:\ruta\de\pruebas`.
- Solo se permite una instancia del programa a la vez.

## Tareas

Se usa la zona horaria local de Windows. Editar y eliminar piden confirmación. Eliminar quita la tarea definitivamente; las ejecuciones ya hechas permanecen en el historial hasta su vencimiento normal.

| Calendario | Comportamiento |
| --- | --- |
| Una sola vez / Fecha futura | Una fecha y una hora concretas. |
| Diariamente / Todos los días | Todos los días a la hora indicada. |
| Cada semana en estos días | Los días de la semana seleccionados. |
| Cada mes | El día del mes de la fecha inicial; si ese día no existe, el último día del mes. |
| Una semana sí y una no | Cada dos semanas desde la fecha inicial. |
| Cada N semanas | Cada N semanas desde la fecha inicial, conservando su día de la semana. |

- La aplicación y el equipo deben estar activos. Al abrir después de un cierre o volver de suspensión, **los envíos vencidos se omiten**: las tareas recurrentes pasan a su siguiente fecha y las de una sola vez quedan vencidas.
- No se instala ningún servicio ni se modifica el inicio automático de Windows.
- Los mensajes se dividen en fragmentos de hasta 300 caracteres (sin perder contenido) con una pausa de 5 segundos entre fragmentos.
- La redacción con IA empieza al ejecutarse la tarea y puede añadir unos segundos de espera.
- Si un envío falla, queda registrado con el número de partes enviadas. No hay reintento automático para evitar duplicados.

## Agente Claude Code

Este tipo de tarea ejecuta `claude -p --output-format json` como proceso hijo, dentro de la **carpeta de trabajo** indicada. El prompt va por entrada estándar y el texto resultante se envía por WhatsApp con el flujo normal (división en partes, historial y reintento manual; el reintento solo reenvía el mensaje guardado, no vuelve a ejecutar el agente).

- Se usa `%USERPROFILE%\.local\bin\claude.exe` si existe; si no, `claude.exe` del PATH.
- **Herramientas permitidas:** se pasan a `--allowedTools`. Vacío = solo los permisos por defecto del modo `-p`.
- **Timeout:** de 1 a 120 minutos (10 por defecto). Al vencer se termina el árbol de procesos y la ejecución queda fallida sin enviar nada.
- Nunca se usa `--dangerously-skip-permissions`.

Riesgo: las herramientas permitidas (por ejemplo `Bash` o `PowerShell`) se ejecutan sin confirmación en tu equipo y con tus credenciales, y lo que el agente responda se envía por WhatsApp. Concede solo lo necesario.

## Historial

- La pestaña inicial muestra las últimas 48 horas, de más reciente a más antigua. Doble clic en una ejecución para ver el mensaje completo, destinatario, tiempos, resultado y detalle de las partes.
- Un resultado **Exitosa** significa que Evolution aceptó las solicitudes; no confirma lectura ni entrega final en WhatsApp.
- Los registros con más de siete días se eliminan automáticamente.
- Si Windows o la aplicación se cierran durante un envío puede quedar un resultado incierto; no se reenvía automáticamente.
- **Reintentar** (clic derecho sobre un envío fallido, parcial o cancelado): conserva destinatario y mensaje originales, envía solo las partes pendientes y deja cada intento como una fila nueva vinculada al original. Los intentos simultáneos de la misma cadena se bloquean. Si la IA falló antes de producir texto, no hay nada que reenviar.
- **HTTP 400 / exists: false:** Evolution no reconoce ese destinatario como disponible en WhatsApp. Revisa código de país y número.
- Los errores de conexión pueden dejar una entrega sin confirmar: revisa WhatsApp antes de reintentar para no duplicar una parte que el servidor haya aceptado sin responder.

## Compilar y validar

Requiere el SDK de .NET 10. El ejecutable publicado incluye el runtime de Windows x64 (quien solo lo usa no necesita instalar .NET, pero sí el SDK para compilarlo).

```powershell
dotnet build WhatsAppTasks.slnx -c Release
dotnet test WhatsAppTasks.slnx -c Release
./Publish.ps1
```

## Referencias

- [Evolution API](https://doc.evolution-api.com)
- [Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)
- [Modelos de Anthropic](https://platform.claude.com/docs/en/models/overview)

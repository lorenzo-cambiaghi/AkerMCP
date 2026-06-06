# AkerMCP — Improvement Plan

Based on the analysis of a real Claude Code session debugging a Voxel GI pipeline, this document details three improvements to the `game-engine` MCP server.

---

## 1. Multi-Client Named Pipe (Connessioni Multiple)

### Problema

Antigravity e Claude Code lanciano ciascuno il proprio `AkerMcp.Server.exe`. Il plugin Unity accetta **una sola connessione** alla volta (`maxInstances: 1` in `EnginePluginBase.cs` riga 95). Il primo server che si connette occupa l'unico slot; il secondo va in timeout con `"No engine connected"`.

### Analisi del Codice Attuale

```
EnginePluginBase.cs — RunPipeServer() (riga 84-136)
│
├── Crea NamedPipeServerStream(pipeName, InOut, maxInstances: 1, ...)   ← BLOCCO
├── WaitForConnectionAsync()       ← accetta UN client
├── HandleConnection(channel, ct)  ← loop sincrono request/response
│   └── (quando il client disconnette, il loop esce)
└── finally: dispose pipe e channel, poi ricomincia il while
```

- `IpcRequestHandler` è un'istanza **singola** creata in `Start()` — è **stateless** rispetto alla connessione (non tiene stato per-client). Tutte le operazioni vengono eseguite sul main thread di Unity via `_dispatcher.RunOnMainThread()`, che è già thread-safe (coda + `TaskCompletionSource`).
- `IpcChannel` (in `Shared/Ipc/IpcChannel.cs`) avvolge un singolo `Stream` con `SemaphoreSlim` per lettura/scrittura — è thread-safe per una coppia reader/writer ma non è condiviso tra connessioni.
- Lo `Stop()` chiude `_pipeServer` (campo singolo) e `_channel` (campo singolo) per sbloccare il loop.

### Piano di Modifica

#### [MODIFY] `Client/EnginePluginBase.cs`

**Cambio 1 — maxInstances:**
```diff
- new NamedPipeServerStream(Config.PipeName!, PipeDirection.InOut, 1, ...)
+ new NamedPipeServerStream(Config.PipeName!, PipeDirection.InOut,
+     NamedPipeServerStream.MaxAllowedServerInstances, ...)
```

**Cambio 2 — Accept loop multi-connessione:**

Il loop attuale è sequenziale: accetta → gestisce → dispose → ripete. Deve diventare: accetta → lancia gestione in background → ripete immediatamente.

```csharp
// Stato: lista dei client attivi per cleanup
private readonly List<Task> _activeConnections = new List<Task>();
private readonly object _connectionsLock = new object();

private async Task RunPipeServer(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        NamedPipeServerStream? pipeServer = null;
        try
        {
            pipeServer = new NamedPipeServerStream(
                Config.PipeName!,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            Log("Waiting for MCP server connection...");
            await pipeServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
            Log("MCP server connected.");

            // Passa proprietà al task di gestione e rimetti subito in ascolto
            var connectedPipe = pipeServer;
            pipeServer = null; // impedisce il dispose nel finally

            var connectionTask = Task.Run(() =>
                HandleSingleConnection(connectedPipe, ct));

            lock (_connectionsLock)
            {
                // Rimuovi task completati
                _activeConnections.RemoveAll(t => t.IsCompleted);
                _activeConnections.Add(connectionTask);
            }
        }
        catch (OperationCanceledException) { break; }
        catch (ObjectDisposedException)
        {
            if (!_running || ct.IsCancellationRequested) break;
        }
        catch (Exception ex)
        {
            if (!_running || ct.IsCancellationRequested) break;
            LogError($"Pipe server error: {ex.Message}");
            try { await Task.Delay(1000, ct); } catch { break; }
        }
        finally
        {
            // Se pipeServer non è null, vuol dire che non siamo arrivati
            // al punto di passarlo al task → dispose sicuro
            try { pipeServer?.Dispose(); } catch { }
        }
    }
}

private async Task HandleSingleConnection(
    NamedPipeServerStream pipe, CancellationToken ct)
{
    IpcChannel? channel = null;
    try
    {
        channel = new IpcChannel(pipe);
        Log($"Client connected (total: {ActiveConnectionCount})");

        while (!ct.IsCancellationRequested)
        {
            var request = await channel.ReceiveRequest(ct);
            var response = await _handler!.HandleRequest(request, ct);
            await channel.SendResponse(response, ct);
        }
    }
    catch (EndOfStreamException)
    {
        Log("MCP server client disconnected.");
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        if (_running)
            LogError($"Client connection error: {ex.Message}");
    }
    finally
    {
        try { channel?.Dispose(); } catch { }
        try { pipe.Dispose(); } catch { }
        Log($"Client disconnected (remaining: {ActiveConnectionCount - 1})");
    }
}
```

**Cambio 3 — Stop() aggiornato:**
```csharp
public void Stop()
{
    if (!_running) return;
    _running = false;

    try { _cts?.Cancel(); } catch { }

    // Aspetta che tutte le connessioni attive si chiudano
    Task[] connections;
    lock (_connectionsLock)
    {
        connections = _activeConnections.ToArray();
        _activeConnections.Clear();
    }
    try { Task.WaitAll(connections, TimeSpan.FromSeconds(2)); } catch { }

    try { _pipeServerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    try { _discovery?.Dispose(); } catch { }

    Log("AkerMcp plugin stopped.");
}
```

**Proprietà helper:**
```csharp
private int ActiveConnectionCount
{
    get { lock (_connectionsLock) return _activeConnections.Count(t => !t.IsCompleted); }
}

public bool IsRunning => _running;
public string? CurrentPipeName => _discovery?.PipeName;
```

### Thread Safety

L'`IpcRequestHandler` è **già thread-safe**: ogni richiesta è indipendente e il lavoro reale viene serializzato tramite `_dispatcher.RunOnMainThread()` (coda con lock). Due richieste simultanee da client diversi semplicemente entrano nella coda del main thread e vengono eseguite una alla volta. Nessuna modifica necessaria a `IpcRequestHandler`.

---

## 2. Robustezza della Reflection (`get_property` / `inspect`)

### Problema

Il tool `get_property` crasha quando una property di Unity lancia un'eccezione durante `PropertyInfo.GetValue()`. Esempi tipici:
- `Light.shadowRadius` → `NotSupportedException` se il rendering path non è HDRP
- Property marcate `[Obsolete]` che lanciano al primo accesso
- `MeshFilter.mesh` → crea un'istanza e logga un warning in edit mode
- Property URP che richiedono un certo stato del pipeline

### Analisi del Codice Attuale

Il flusso di `get_property`:

```
IpcRequestHandler.HandleGetProperty()            ← riga 178, ha try-catch globale
  └── node.GetProperty(propertyPath)              ← UnitySceneNode.cs riga 75
      ├── TryResolveOnComponent(component, path)  ← controlla solo se il NOME esiste (metadata)
      └── _resolver.Resolve(component, path)      ← PropertyPathResolver.cs riga 19
          └── prop.GetValue(current)              ← riga 42, NESSUN try-catch! 💥
```

Il catch globale in `HandleRequest()` (riga 99-102) intercetta l'eccezione e restituisce un errore MCP, ma il messaggio è uno stack trace grezzo e l'intera operazione fallisce — anche se la property esiste su un altro componente senza problemi.

Confronto: `ReflectionInspector.SafeGetValue()` (riga 136-150) **HA** un try-catch e restituisce `"[error reading value]"`. Ma `PropertyPathResolver` no.

### Piano di Modifica

#### [MODIFY] `Shared/Reflection/PropertyPathResolver.cs`

**Cambio 1 — Protezione in `Resolve()`** (riga 40-44):
```diff
  var prop = _cache.GetProperty(type, segment);
  if (prop != null && prop.CanRead)
  {
-     current = prop.GetValue(current);
-     continue;
+     try
+     {
+         current = prop.GetValue(current);
+         continue;
+     }
+     catch (Exception ex)
+     {
+         throw new PropertyPathException(
+             $"Property '{segment}' on {type.Name} threw on read: " +
+             $"{(ex.InnerException ?? ex).GetType().Name}: " +
+             $"{(ex.InnerException ?? ex).Message}");
+     }
  }
```

Stessa protezione in `ResolveSingle()` (riga 113-115):
```diff
  var prop = _cache.GetProperty(type, segment);
  if (prop != null && prop.CanRead)
-     return prop.GetValue(current);
+  {
+     try { return prop.GetValue(current); }
+     catch (Exception ex)
+     {
+         throw new PropertyPathException(
+             $"Property '{segment}' on {type.Name} threw on read: " +
+             $"{(ex.InnerException ?? ex).GetType().Name}: " +
+             $"{(ex.InnerException ?? ex).Message}");
+     }
+  }
```

Questo trasforma un'eccezione non gestita in un `PropertyPathException` con un messaggio chiaro. Il catch globale in `IpcRequestHandler` la trasforma in una risposta MCP leggibile dall'AI.

#### [MODIFY] `UnityTestProject/Assets/AkerMcp/UnitySceneNode.cs`

**Cambio 2 — Fallthrough tra componenti in `GetProperty()`** (riga 89-98):

Attualmente, se il primo componente che ha una property con quel nome lancia, tutto il tool fallisce. Deve provare il prossimo componente:

```diff
  // Try each component
  foreach (var component in _go.GetComponents<Component>())
  {
      if (component == null || component is Transform) continue;
      if (TryResolveOnComponent(component, propertyPath))
-         return _resolver.Resolve(component, propertyPath);
+     {
+         try { return _resolver.Resolve(component, propertyPath); }
+         catch (PropertyPathException) { continue; } // try next component
+     }
  }
```

In questo modo, se `Light.color` lancia (improbabile, ma possibile), il resolver prova il prossimo componente che ha una property `color`.

---

## 3. Estensione del Contesto Roslyn (Assembly Loading)

### Problema

Il `DynamicEvaluatorV2` carica un set **hardcodato** di assembly Unity. Qualsiasi modulo non nella lista è invisibile a Roslyn:
- **URP/HDRP** (`UnityEngine.Rendering.Universal`, `UnityEngine.Rendering.HighDefinition`)
- **UI** (`UnityEngine.UI`, `Unity.TextMeshPro`)
- **Input System**, **Cinemachine**, **NavMesh**, **Terrain**, **ParticleSystem**, **Timeline**
- Qualsiasi package UPM installato dall'utente

Nella sessione analizzata, Claude ha dovuto usare reflection manuale per leggere i settaggi di `UniversalRenderPipelineAsset` perché l'assembly URP non era nel contesto Roslyn.

### Analisi del Codice Attuale

`DynamicEvaluatorV2.BuildScriptOptions()` (riga 159-216):

```csharp
// Hardcoded: ~18 typeof() references
AddAssemblySafe(assemblies, typeof(GameObject));    // CoreModule
AddAssemblySafe(assemblies, typeof(Rigidbody));     // PhysicsModule
// ... 16 altri ...

// Scan parziale: solo Assembly-CSharp e Assembly-CSharp-Editor
foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
{
    if (asm.IsDynamic) continue;
    var name = asm.GetName().Name ?? "";
    if (name == "Assembly-CSharp" || name == "Assembly-CSharp-Editor")
        assemblies.Add(asm);
}
```

**Nota importante**: il campo `_state` (riga 37) non è mai usato. Il codice crea sempre compilazioni fresche — le variabili NON persistono tra chiamate `execute` successive. La documentazione nel `ToolRegistry` (che dice "state persists between calls") è **errata** per V2.

### Piano di Modifica

#### [MODIFY] `UnityTestProject/Assets/AkerMcp/Editor/DynamicEvaluatorV2.cs`

**Cambio 1 — Sostituire lista hardcoded con scan completo dell'AppDomain** (riga 159-216):

```csharp
private ScriptOptions BuildScriptOptions()
{
    var imports = new List<string>
    {
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Text",
        "UnityEngine",
        "UnityEditor"
    };

    // Include ALL non-dynamic assemblies currently loaded in the AppDomain.
    // This automatically picks up URP, HDRP, Input System, TextMeshPro,
    // Cinemachine, user scripts, and any other UPM package — without
    // maintaining a hardcoded list.
    var assemblies = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic)
        .Where(a =>
        {
            try { _ = a.Location; return !string.IsNullOrEmpty(a.Location); }
            catch { return false; }
        })
        .Distinct()
        .ToArray();

    return ScriptOptions.Default
        .WithReferences(assemblies)
        .WithImports(imports)
        .WithAllowUnsafe(false);
}
```

Questo è più semplice, più robusto e copre automaticamente qualsiasi pacchetto Unity installato. Il filtro `!IsDynamic` e il check su `Location` escludono gli assembly emessi da Roslyn stesso e quelli senza file fisico (che `MetadataReference.CreateFromAssembly` non supporta).

**Cambio 2 — Rimuovere il campo `_state` inutilizzato** (riga 37):

```diff
- private ScriptState<object>? _state;
```

E il metodo `ResetState()` (riga 245-248):
```diff
- public void ResetState()
- {
-     _state = null;
- }
```

**Cambio 3 — Aggiungere `using` comuni allo wrapper** (riga 94-110):

Aggiungere namespace URP/rendering al wrapper del codice utente, così non serve scriverli a mano:

```diff
  var syntaxTree = CSharpSyntaxTree.ParseText($@"
      using System;
      using System.Collections.Generic;
      using System.Linq;
      using System.Text;
      using UnityEngine;
      using UnityEditor;
      using AkerMcp.Unity;
+     using UnityEngine.Rendering;
```

Non aggiungiamo `using UnityEngine.Rendering.Universal` nel wrapper perché non tutti i progetti usano URP — se l'assembly non è caricata, il `using` genererebbe un errore di compilazione. Basta che l'assembly sia nei references: l'AI può scrivere `using UnityEngine.Rendering.Universal;` nel suo script se serve.

---

## 4. Documentazione — Fix tool description `execute`

### Problema

La description del tool `execute` in `ToolRegistry.cs` dice:
> "The script state persists between calls — variables defined in one execute call are available in the next."

Questo è **falso** per `DynamicEvaluatorV2`, che crea una `CSharpCompilation` fresca ad ogni chiamata. L'AI si aspetta di poter definire una variabile e riusarla nella chiamata successiva, ma non funziona.

#### [MODIFY] `Server/ToolRegistry.cs`

Trovare e rimuovere il claim sulla persistenza dello stato, oppure sostituirlo con:
> "Each script execution is independent — variables do not persist between calls."

---

## 5. Auto-Restart dopo Domain Reload

### Problema

Quando Unity ricompila gli script (domain reload), il plugin si ferma e **non riparte**. Il server MCP perde la connessione e il retry-loop non trova più il lock file (cancellato da `Stop()`). L'utente deve cliccare manualmente "Start" nella finestra AkerMcp dopo ogni ricompilazione — un'interruzione continua nel flusso di lavoro con l'AI.

### Analisi del Codice Attuale

La catena di eventi durante un domain reload:

```
Unity ricompila script
  → AssemblyReloadEvents.beforeAssemblyReload      (UnityMcpLifecycle.cs:11)
    → StopIfRunning()                               (UnityMcpLifecycle.cs:14)
      → UnityMcpPlugin.Stop()                       (UnityMcpPlugin.cs:58)
        → _dispatcher.Unregister()                   ← EditorApplication.update sganciato
        → base.Stop()
          → _cts.Cancel()                             ← pipe server task cancellato
          → _pipeServer.Close()                       ← pipe chiusa
          → _discovery.Dispose()                      ← LOCK FILE CANCELLATO
        → _instance = null                            ← singleton distrutto

--- Domain Reload (tutti i campi static reset a null/default) ---

Unity ricarica gli assembly
  → [InitializeOnLoad] UnityMcpLifecycle ctor        (UnityMcpLifecycle.cs:8)
    → ri-registra eventi quitting + beforeAssemblyReload
    → MA non chiama Start()!                          ← nessuno riavvia il plugin
```

**Risultato**: il plugin resta morto. Il lock file non esiste più. Il server MCP non trova la pipe durante il retry-loop e resta disconnesso finché l'utente non clicca "Start" manualmente.

### Piano di Modifica

La chiave è `SessionState`: un'API Unity che persiste durante i domain reload ma non tra sessioni dell'Editor. Perfetta per ricordare che il plugin era attivo e riavviarlo automaticamente.

#### [MODIFY] `UnityTestProject/Assets/AkerMcp/Editor/UnityMcpLifecycle.cs`

Trasformare la classe da "solo cleanup" a "cleanup + auto-restart":

```csharp
using UnityEditor;

namespace AkerMcp.Unity.Editor
{
    [InitializeOnLoad]
    internal static class UnityMcpLifecycle
    {
        private const string WasRunningKey = "AkerMcp_WasRunning";

        static UnityMcpLifecycle()
        {
            EditorApplication.quitting += OnQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;
        }

        private static void OnQuitting()
        {
            // Quit definitivo: ferma il plugin e NON salvare il flag,
            // così alla prossima apertura dell'Editor non parte da solo.
            SessionState.EraseBool(WasRunningKey);
            if (UnityMcpPlugin.IsRunning)
                UnityMcpPlugin.Instance.Stop();
        }

        private static void OnBeforeReload()
        {
            // Salva lo stato "era attivo" PRIMA di fermare il plugin
            if (UnityMcpPlugin.IsRunning)
            {
                SessionState.SetBool(WasRunningKey, true);
                UnityMcpPlugin.Instance.Stop();
            }
        }

        private static void OnAfterReload()
        {
            // Se era attivo prima del reload, E l'utente ha l'opzione attiva, riavvia
            bool autoRestartEnabled = EditorPrefs.GetBool("AkerMcp_AutoRestartEnabled", true);
            if (SessionState.GetBool(WasRunningKey, false) && autoRestartEnabled)
            {
                UnityMcpPlugin.Instance.Start();
                UnityEngine.Debug.Log("[AkerMcp] Auto-restarted after domain reload.");
            }
        }
    }
}
```

#### [MODIFY] `UnityTestProject/Assets/AkerMcp/Editor/McpEditorWindow.cs` — Toggle UI

Aggiungere un checkbox per lasciare all'utente il controllo su questa funzionalità:

```csharp
    // ... dentro OnGUI() ...

    EditorGUILayout.Space(10);
    
    // Toggle Auto-Restart
    bool autoRestart = EditorPrefs.GetBool("AkerMcp_AutoRestartEnabled", true);
    bool newAutoRestart = EditorGUILayout.ToggleLeft("Auto-restart after domain reload", autoRestart);
    if (newAutoRestart != autoRestart)
    {
        EditorPrefs.SetBool("AkerMcp_AutoRestartEnabled", newAutoRestart);
    }
    
    EditorGUILayout.Space(10);
```

**Perché `SessionState` e non `EditorPrefs` per il flag `WasRunning`**: `EditorPrefs` persiste su disco tra sessioni dell'Editor. Se l'Editor crasha o viene chiuso con il plugin attivo, alla prossima apertura partirebbe automaticamente — comportamento non desiderato. `SessionState` vive solo per la durata della sessione dell'Editor: se chiudi e riapri Unity, il plugin resta fermo. Il flag `AutoRestartEnabled` invece è una preferenza dell'utente e va salvato in `EditorPrefs`.

#### [MODIFY] `Client/EnginePluginBase.cs` — `Start()` idempotente

Attualmente `Start()` controlla `_running` per evitare doppie partenze. Dopo un domain reload, `_running` è `false` (campo non statico → l'istanza viene ricreata dal singleton lazy). Nessuna modifica necessaria: il nuovo `Instance` creato dal lazy init avrà `_running = false` e `Start()` funzionerà.

#### Lock File: stessa pipe name dopo reload

Il `PluginDiscovery` genera il nome della pipe basandosi sul PID del processo (`aker-mcp-unity-{pid}`). Dato che il PID di Unity non cambia durante un domain reload (è lo stesso processo), il nuovo lock file avrà **lo stesso nome** del precedente. Il server MCP che sta facendo retry-loop lo troverà e si riconnetterà automaticamente alla nuova pipe.

**Flusso dopo il fix:**
```
Unity ricompila
  → OnBeforeReload()
    → SessionState.SetBool("AkerMcp_WasRunning", true)
    → Stop() → pipe chiusa, lock file cancellato

--- Domain Reload ---

  → OnAfterReload()
    → SessionState.GetBool("AkerMcp_WasRunning") = true
    → Start()
      → nuova pipe con lo stesso nome (stesso PID)
      → nuovo lock file scritto
  
  → Server MCP retry-loop (ogni 2-10s)
    → trova il lock file → si connette alla pipe → ✅ ripristinato
```

Tempo di interruzione totale: tempo del domain reload + max 10s di backoff del server retry.

---

## Ordine di Implementazione

| # | Modifica | File | Rischio | Priorità |
|---|----------|------|---------|----------|
| 1 | Robustezza Reflection | `PropertyPathResolver.cs`, `UnitySceneNode.cs` | Basso | Alta |
| 2 | Multi-client pipe | `EnginePluginBase.cs` | Medio | Alta |
| 3 | Auto-restart domain reload | `UnityMcpLifecycle.cs` | Basso | Alta |
| 4 | Roslyn assembly scan | `DynamicEvaluatorV2.cs` | Basso | Media |
| 5 | Fix tool description | `ToolRegistry.cs` | Zero | Bassa |

---

## Verifica

1. **Reflection**: Eseguire `get_property` su una `Light` di Unity (es. `get_property {object_path: "/Directional Light", property_path: "shadowStrength"}`). Deve restituire il valore senza crash. Per una property che lancia, deve restituire un errore MCP leggibile, non uno stack trace.

2. **Multi-client**: Lanciare Antigravity e Claude Code contemporaneamente con lo stesso MCP config. Entrambi devono poter eseguire `inspect {target: "/"}` senza timeout.

3. **Auto-restart**: Con il plugin attivo, modificare un file `.cs` qualsiasi nel progetto e salvare → Unity ricompila → verificare nella finestra AkerMcp che lo Status torni **Running** automaticamente senza intervento manuale. Il server MCP deve riconnettersi entro ~10 secondi.

4. **Roslyn**: Eseguire via `execute`:
   ```csharp
   var urpAsset = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
   return urpAsset != null ? $"URP: {urpAsset.name}" : "No URP asset";
   ```
   Deve compilare senza errori (attualmente fallisce con `CS0234: type or namespace 'Universal' does not exist`).

5. **Tool description**: Verificare che la description di `execute` non menzioni la persistenza dello stato.


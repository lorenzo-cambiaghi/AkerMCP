# Implementazione Roslyn Dynamic Evaluator

Abilitare l'esecuzione di codice C# dinamico in Unity tramite il protocollo MCP per permettere automazioni procedurali e manipolazioni complesse della scena e degli asset.

## User Review Required

> [!IMPORTANT]
> L'integrazione di Roslyn aggiungerà circa 10-15MB di DLL alla cartella `Plugins`. Questo è necessario per avere il compilatore C# disponibile a runtime/editor.
> Il codice eseguito tramite `execute` avrà gli stessi permessi dell'utente nell'Editor di Unity (può creare, eliminare e modificare file).

## Proposed Changes

### [Component: Client (Unity Integration)]

#### [NEW] [DynamicEvaluator.cs](file:///c:/prjUnity/aker-mcp/Client/DynamicEvaluator.cs)
Creazione del motore di esecuzione basato su `Microsoft.CodeAnalysis.CSharp.Scripting`.
- Configurazione dei riferimenti (UnityEngine, UnityEditor, etc.).
- Gestione dello stato e delle variabili globali.
- Cattura degli output e degli errori.

#### [MODIFY] [IpcRequestHandler.cs](file:///c:/prjUnity/aker-mcp/Client/IpcRequestHandler.cs)
- Sostituzione dello stub `HandleExecute` con la chiamata al `DynamicEvaluator`.
- Integrazione con il `UnityMainThreadDispatcher` per garantire l'esecuzione sicura sulle API di Unity.

### [Component: Shared (Core Infrastructure)]

#### [MODIFY] [IpcConstants.cs](file:///c:/prjUnity/aker-mcp/Shared/Ipc/IpcConstants.cs)
- Assicurarsi che `execute` sia registrato correttamente (già presente, ma da validare).

## Verification Plan

### Automated Tests
1. **Simple Print**: Eseguire `Debug.Log("Hello")` e verificare la comparsa in console.
2. **Object Creation**: Eseguire `new GameObject("Test")` e verificare la presenza nella scena.
3. **Property Access**: Eseguire `return Selection.activeGameObject?.name` e verificare che il nome venga restituito correttamente via MCP.

### Manual Verification
- Chiedere all'IA di generare una struttura procedurale (es. una linea di cubi) e verificare il risultato visivo in Unity.

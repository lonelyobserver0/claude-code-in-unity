# Claude Code ↔ Unity Editor Bridge

Un ponte MCP (Model Context Protocol) che permette a **Claude Code** di pilotare l'**Editor di Unity** in tempo reale: creare GameObject, modificare componenti, gestire scene, eseguire voci di menu, controllare il Play Mode e leggere i log della Console.

---

## Architettura

```
┌──────────────────┐    stdio     ┌────────────────────────┐    HTTP loopback     ┌─────────────────────┐
│   Claude Code    │ ───MCP────▶  │  unity_mcp_server.py   │ ───POST + token ───▶ │   MCPBridge.cs      │
│   (CLI / IDE)    │              │   (FastMCP, Python)    │                      │  (Unity Editor)     │
└──────────────────┘              └────────────────────────┘                      └─────────────────────┘
                                                                                          │
                                                                                          ▼
                                                                                   UnityEditor API
                                                                                   (main thread)
```

- **`MCPBridge.cs`** — script Editor Unity. Espone un `HttpListener` su `127.0.0.1:8080` protetto da token. Le richieste vengono accodate ed eseguite sul **main thread** dell'Editor.
- **`unity_mcp_server.py`** — server MCP (FastMCP) che traduce le tool call di Claude Code in chiamate HTTP verso Unity.
- **`claude_code_config.json`** — registra il server MCP nel config di Claude Code.

---

## Prerequisiti

| Componente | Versione consigliata | Note |
|------------|----------------------|------|
| Unity Editor | 2021.3 LTS o successiva | Newtonsoft Json (`com.unity.nuget.newtonsoft-json`) richiesto |
| Python | 3.10+ | tipi generic come `dict[str, Any]` e `\| None` |
| Claude Code | ultima versione | https://claude.com/claude-code |
| Pacchetti Python | `mcp`, `httpx` | `pip install mcp httpx` |

---

## Installazione

### 1. Lato Unity

1. Copia **`MCPBridge.cs`** in qualsiasi cartella `Editor/` del tuo progetto Unity (es. `Assets/Editor/MCPBridge.cs`).
2. Verifica che il pacchetto **Newtonsoft Json** sia installato:
   - `Window → Package Manager → +  → Add package by name…` → `com.unity.nuget.newtonsoft-json`.
3. Dopo la compilazione apri il pannello: **`Tools → MCP Bridge → Control Panel`**.

### 2. Lato Python

```bash
pip install mcp httpx
```

Posiziona `unity_mcp_server.py` dove preferisci (consigliato un path stabile, es. `~/scripts/unity_mcp_server.py`).

### 3. Registra il server in Claude Code

Apri (o crea) il config MCP di Claude Code e aggiungi la voce contenuta in **`claude_code_config.json`**, sostituendo i due placeholder:

```json
{
  "mcpServers": {
    "unity": {
      "command": "python",
      "args": ["/PATH/ASSOLUTO/unity_mcp_server.py"],
      "env": {
        "UNITY_MCP_URL":   "http://127.0.0.1:8080",
        "UNITY_MCP_TOKEN": "INCOLLA_QUI_IL_TOKEN_DAL_PANNELLO_UNITY",
        "UNITY_MCP_TIMEOUT": "10.0"
      }
    }
  }
}
```

> Il path effettivo del config dipende dalla piattaforma — vedi la documentazione di Claude Code (`claude mcp` CLI o file `~/.claude/...`).

---

## Come usarlo (workflow operativo)

> **Questa è la sequenza minima per avere Claude Code che parla con Unity.** Seguila in ordine la prima volta.

### Step 1 — Avvia il bridge in Unity

1. Apri il progetto Unity.
2. Vai su **`Tools → MCP Bridge → Control Panel`**.
3. (Opzionale) Imposta una porta diversa se 8080 è occupata.
4. Clicca **`Start`**.
5. **Copia il token** mostrato nel campo *"Auth token"*.

Vedrai nella Console: `[MCP] Listening on http://127.0.0.1:8080 (token saved in EditorPrefs)`.

### Step 2 — Incolla il token nel config di Claude Code

Apri il config MCP, sostituisci `INCOLLA_QUI_IL_TOKEN_DAL_PANNELLO_UNITY` con il token copiato e l'URL del path Python. Salva.

### Step 3 — Riavvia Claude Code

Affinché Claude Code carichi il nuovo MCP server.

### Step 4 — Verifica la connessione

In una sessione Claude Code chiedi:

> *"Fai ping al server Unity"*

Risposta attesa: `{"ok": true}`.

A questo punto puoi chiedere a Claude Code cose come:

- *"Crea un cubo rosso chiamato Player a posizione (0, 1, 0)"*
- *"Aggiungi un Rigidbody a Player con massa 2"*
- *"Lista tutti i GameObject root della scena"*
- *"Metti il progetto in Play"*
- *"Mostrami gli ultimi errori della console"*
- *"Salva la scena come Assets/Scenes/Test.unity"*

### Step 5 — Riapertura sessioni successive

- **Auto-start:** spunta *"Auto-start on load"* nel pannello → il bridge parte automaticamente all'apertura del progetto.
- Il **token** è persistente in `EditorPrefs` finché non lo rigeneri.

---

## Comandi MCP disponibili

| Categoria | Tool MCP | Descrizione |
|-----------|----------|-------------|
| **Diagnostica** | `ping` | Verifica connessione e token |
| **Oggetti** | `create_object(type, name)` | Crea primitiva (`Cube`, `Sphere`, `Plane`, `Cylinder`, `Capsule`, `Quad`, `Empty`) |
| | `delete_object(object_id)` | Elimina (undoable) |
| | `instantiate_prefab(path, name?)` | Istanzia un prefab dall'AssetDatabase |
| | `find_object(name?, path?)` | Cerca per nome (ricorsivo) o per path gerarchico |
| | `list_scene_objects()` | Elenca i root della scena attiva |
| | `get_object_info(object_id)` | Info: transform, tag, layer, componenti |
| | `get_children(object_id)` | Figli diretti |
| **Transform** | `move_object(object_id, position?, rotation?, scale?)` | Posizione/rotazione/scala (Vector3 come `{x,y,z}`) |
| | `set_parent(object_id, parent_id?, world_position_stays?)` | Reparent (None per root) |
| | `set_active(object_id, active)` | Enable/disable |
| | `set_tag(object_id, tag)` | Tag (deve esistere in TagManager) |
| | `set_layer(object_id, layer)` | Int 0–31 o nome layer |
| **Componenti** | `add_component(object_id, component)` | Per nome tipo (cross-assembly: anche MonoBehaviour custom) |
| | `remove_component(object_id, component)` | (Transform escluso) |
| | `set_component_property(id, component, property, value)` | Field/Property pubblica via reflection |
| **Scene** | `save_scene(path?)` | Salva (eventuale save-as) |
| | `open_scene(path, mode?)` | `Single` / `Additive` / `AdditiveWithoutLoading` |
| | `new_scene(setup?)` | `DefaultGameObjects` / `EmptyScene` |
| | `get_scene_info()` | Nome, path, dirty, root count, build index |
| **Editor UX** | `select_object(object_id, frame?)` | Seleziona + ping + (opz.) frame in Scene view |
| | `execute_menu_item(menu_path)` | **Esegue qualsiasi voce di menu Unity** |
| **Play mode** | `set_play_mode(state)` | `play` / `stop` / `pause` / `unpause` |
| | `get_play_mode()` | Flag: playing, paused, compiling, updating |
| **Console** | `get_console_logs(limit?, severity?)` | Buffer circolare 1000 entries (`Log`/`Warning`/`Error`/`Exception`/`Assert`) |
| | `clear_console_logs()` | Svuota il buffer del bridge |

---

## Sicurezza

Il bridge è progettato per **uso locale**, ma applica già misure difensive:

- **Bind solo su `127.0.0.1`** — non raggiungibile dalla LAN.
- **Token obbligatorio** (`X-MCP-Token` header) generato con `RandomNumberGenerator` (32 byte base64-url).
- **DNS-rebinding guard** — controllo dell'header `Host` per accettare solo `127.0.0.1`/`localhost`.
- **Timeout main-thread** (10 s) per evitare deadlock sull'Editor.

> ⚠️ **Non esporre la porta su rete pubblica.** Il bridge dà accesso totale all'Editor (incluso `execute_menu_item`, che può eseguire qualsiasi azione disponibile da menu).

Se sospetti che il token sia compromesso → pannello → **`Regenerate Token`** → aggiorna il config di Claude Code.

---

## Configurazione avanzata

### Variabili d'ambiente (lato Python)

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `UNITY_MCP_URL` | `http://127.0.0.1:8080` | Base URL del bridge Unity |
| `UNITY_MCP_TOKEN` | *(richiesto)* | Token copiato dal pannello Unity |
| `UNITY_MCP_TIMEOUT` | `10.0` | Timeout per request in secondi |

### EditorPrefs (lato Unity)

| Chiave | Tipo | Default |
|--------|------|---------|
| `MCPBridge.AutoStart` | bool | `false` |
| `MCPBridge.Port` | int | `8080` |
| `MCPBridge.Token` | string | *(generato al primo avvio)* |

### Più progetti Unity in parallelo

Avvia ogni Editor su una porta differente e dà a ciascuno un'entry separata in `mcpServers` (`unity_a`, `unity_b`, …) con `UNITY_MCP_URL` e `UNITY_MCP_TOKEN` propri.

---

## Troubleshooting

| Sintomo | Causa probabile | Soluzione |
|---------|-----------------|-----------|
| `Unauthorized` (401) | Token mismatch | Ricopia il token dal pannello e riavvia Claude Code |
| `Connection failed` | Bridge non in ascolto | Premi **Start** nel pannello |
| `Unity main-thread timeout` (504) | Editor bloccato (compile/import) | Aspetta che finisca, poi ritenta |
| `Component type 'X' not found` | Tipo non in nessun assembly caricato | Verifica che lo script esista e sia compilato senza errori |
| Cambiamenti non visibili in Hierarchy | Editor non in focus | Le modifiche ci sono — `Ctrl+Z`/`Ctrl+S` funzionano normalmente |
| Errori dopo assembly reload | Listener fermato volontariamente | Normale: `beforeAssemblyReload` chiude il server; ripartirà se *Auto-start* è attivo |

Per debug rapido, dalla console Claude Code:

```
ping → deve rispondere {"ok": true}
get_play_mode → conferma che l'Editor risponde
get_console_logs(severity="Error") → ultimi errori catturati
```

---

## Limitazioni note

- **Tag**: `set_tag` richiede che il tag sia già definito in *Project Settings → Tags and Layers*.
- **`set_component_property`** opera solo su **field/property pubblici**. Per proprietà `[SerializeField] private`, esponi un setter pubblico o usa `SerializedObject` (non ancora supportato dal bridge).
- **`open_scene`** non chiede di salvare la scena corrente: eventuali modifiche non salvate vengono **scartate**.
- Il buffer di log è **in-memory** e non sopravvive ad assembly reload o riavvio dell'Editor.

---

## Struttura del repository

```
claude-code-in-unity/
├── MCPBridge.cs              # Script Editor Unity (HttpListener + endpoint)
├── unity_mcp_server.py       # Server MCP Python (FastMCP)
├── claude_code_config.json   # Esempio di registrazione MCP
└── README.md                 # Questo file
```

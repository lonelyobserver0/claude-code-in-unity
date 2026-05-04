# AI ↔ Unity Editor Bridge

Un ponte che permette a tool AI (**Claude Code**, **ChatGPT / Codex CLI**, **Cline**, **Aider**, …) di pilotare l'**Editor di Unity** in tempo reale: creare GameObject, modificare componenti, gestire scene, eseguire voci di menu, controllare il Play Mode e leggere i log della Console.

Due interfacce sopra lo stesso bridge HTTP:

- **MCP server** (`unity_mcp_server.py`) — per qualsiasi client MCP-compatibile (Claude Code, Codex CLI, Cline, ChatGPT desktop, …).
- **CLI diretto** (`unity_cli.py`) — per tool che non parlano MCP ma possono eseguire comandi shell (Aider via `/run`, Makefile, script).

---

## Architettura

```
┌──────────────────┐    stdio     ┌────────────────────────┐
│  Client MCP      │ ───MCP────▶  │  unity_mcp_server.py   │ ─┐
│ (Claude / Codex  │              │   (FastMCP, Python)    │  │
│  / Cline / …)    │              └────────────────────────┘  │  HTTP loopback   ┌─────────────────────┐
└──────────────────┘                                           ├──POST + token ─▶ │   MCPBridge.cs      │
                                                               │                  │  (Unity Editor)     │
┌──────────────────┐              ┌────────────────────────┐  │                  └─────────────────────┘
│ Shell / Aider /  │ ───exec────▶ │     unity_cli.py       │ ─┘                          │
│ Makefile / curl  │              │   (argparse, Python)   │                             ▼
└──────────────────┘              └────────────────────────┘                       UnityEditor API
                                                                                   (main thread)
```

- **`MCPBridge.cs`** — script Editor Unity. Espone un `HttpListener` su `127.0.0.1:8080` protetto da token. Le richieste vengono accodate ed eseguite sul **main thread** dell'Editor.
- **`unity_mcp_server.py`** — server MCP (FastMCP) che traduce tool call MCP in chiamate HTTP verso Unity.
- **`unity_cli.py`** — CLI diretto sul bridge HTTP per chi non usa MCP. Stessa env config del server MCP.
- **`claude_code_config.json`** — esempio di registrazione MCP (formato condiviso da più client).

---

## Prerequisiti

| Componente | Versione consigliata | Note |
|------------|----------------------|------|
| Unity Editor | 2021.3 LTS o successiva | Newtonsoft Json (`com.unity.nuget.newtonsoft-json`) richiesto |
| Python | 3.10+ | tipi generic come `dict[str, Any]` e `\| None` |
| Client AI | Claude Code / Codex CLI / Cline / Aider / … | vedi sezione *Integrazione client* |
| Pacchetti Python | `mcp`, `httpx` | `pip install mcp httpx` (per il solo CLI basta `httpx`) |

---

## Installazione

### 1. Lato Unity

1. Copia **`MCPBridge.cs`** in qualsiasi cartella `Editor/` del tuo progetto Unity (es. `Assets/Editor/MCPBridge.cs`).
2. Verifica che il pacchetto **Newtonsoft Json** sia installato:
   - `Window → Package Manager → +  → Add package by name…` → `com.unity.nuget.newtonsoft-json`.
3. Dopo la compilazione apri il pannello: **`Tools → MCP Bridge → Control Panel`**.

### 2. Lato Python

```bash
pip install mcp httpx        # per il server MCP
# oppure: pip install httpx  # per il solo CLI (unity_cli.py)
```

Posiziona `unity_mcp_server.py` e/o `unity_cli.py` dove preferisci (consigliato un path stabile, es. `~/scripts/`).

### 3. Configurazione client

La registrazione del server MCP è praticamente identica fra i client (è il [formato standard MCP](https://modelcontextprotocol.io)). Vedi la sezione [**Integrazione client**](#integrazione-client) sotto per i dettagli per Claude Code, ChatGPT/Codex CLI, Cline e Aider.

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

### Step 2 — Incolla il token nel config del tuo client

Apri il config MCP del client che usi (vedi [Integrazione client](#integrazione-client)), incolla il token al posto del placeholder e indica il path assoluto a `unity_mcp_server.py`. Salva.

> Per Aider e altri tool senza MCP, esporta `UNITY_MCP_TOKEN` come env var e usa `unity_cli.py` — vedi [Aider / shell tools](#aider--shell-tools-via-cli-diretto).

### Step 3 — Riavvia il client

Affinché ricarichi il nuovo MCP server.

### Step 4 — Verifica la connessione

Chiedi al tuo client AI:

> *"Fai ping al server Unity"*

Risposta attesa: `{"ok": true}`. (Equivalente da shell: `python unity_cli.py ping`.)

A questo punto puoi chiedere cose come:

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

## Integrazione client

Lo stesso `unity_mcp_server.py` funziona con qualsiasi client MCP. Cambia solo *dove* incolli il blocco di config. Per i client che non parlano MCP, usa `unity_cli.py` come comando shell.

### Claude Code

File: `~/.claude.json` (o tramite CLI `claude mcp add`).

```json
{
  "mcpServers": {
    "unity": {
      "command": "python",
      "args": ["/PATH/ASSOLUTO/unity_mcp_server.py"],
      "env": {
        "UNITY_MCP_URL":     "http://127.0.0.1:8080",
        "UNITY_MCP_TOKEN":   "INCOLLA_QUI_IL_TOKEN_DAL_PANNELLO_UNITY",
        "UNITY_MCP_TIMEOUT": "10.0"
      }
    }
  }
}
```

### ChatGPT Codex CLI

Codex CLI legge `~/.codex/config.toml`. Aggiungi:

```toml
[mcp_servers.unity]
command = "python"
args    = ["/PATH/ASSOLUTO/unity_mcp_server.py"]
env     = { UNITY_MCP_URL = "http://127.0.0.1:8080", UNITY_MCP_TOKEN = "INCOLLA_TOKEN", UNITY_MCP_TIMEOUT = "10.0" }
```

Nella sessione Codex puoi poi invocare i tool `unity.*` (`unity.ping`, `unity.create_object`, …).

### Cline (cline-cli / VS Code)

Cline gestisce i server MCP nel file `cline_mcp_settings.json` (raggiungibile da *MCP Servers → Edit Configuration*). Stesso schema di Claude Code:

```json
{
  "mcpServers": {
    "unity": {
      "command": "python",
      "args": ["/PATH/ASSOLUTO/unity_mcp_server.py"],
      "env": {
        "UNITY_MCP_URL":   "http://127.0.0.1:8080",
        "UNITY_MCP_TOKEN": "INCOLLA_TOKEN"
      },
      "disabled": false,
      "autoApprove": []
    }
  }
}
```

### ChatGPT (desktop / web — connettori MCP)

ChatGPT supporta i server MCP come *connectors*. Per un server stdio locale come questo serve di solito un wrapper HTTP/SSE: in alternativa, usa direttamente il CLI nei tool *Code Interpreter* o inquadra le chiamate via `unity_cli.py`.

### Aider / shell tools (via CLI diretto)

Aider non supporta MCP, ma può eseguire comandi shell con `/run`. Esporta una volta le env var nella sessione e poi usa `unity_cli.py`:

```bash
export UNITY_MCP_URL=http://127.0.0.1:8080
export UNITY_MCP_TOKEN=...incollato...
aider
```

Dentro Aider:

```
/run python /PATH/ASSOLUTO/unity_cli.py ping
/run python /PATH/ASSOLUTO/unity_cli.py create_object --type Cube --name Player
/run python /PATH/ASSOLUTO/unity_cli.py set_transform 12345 --position 0,1,0
/run python /PATH/ASSOLUTO/unity_cli.py get_console_logs --severity Error --limit 20
```

Lo stesso CLI è utilizzabile da qualsiasi tool con esecuzione di shell (Makefile, script CI, ricette di task runner). Vedi `python unity_cli.py --help` per la lista completa dei subcomandi. Per endpoint nuovi/non ancora wrappati c'è la fallback `call`:

```bash
python unity_cli.py call /create_object --json '{"type":"Sphere","name":"Ball"}'
```

L'exit code è `0` su successo, `1` se la risposta contiene `error`, `2` su errori di trasporto — comodo da usare in script.

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
├── unity_mcp_server.py       # Server MCP Python (FastMCP) per client MCP
├── unity_cli.py              # CLI diretto sul bridge HTTP per tool senza MCP
├── claude_code_config.json   # Esempio di registrazione MCP (formato condiviso)
└── README.md                 # Questo file
```

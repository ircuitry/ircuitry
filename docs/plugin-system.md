# ircuitry Plugins - design

Status: **draft for review** (no code yet). Author: design pass before build.

## 1. What we're building

A **Plugin** is a third thing you can author in ircuitry, alongside *bots* and *nodes*:

- A **bot** is a node graph that runs IRC logic inside its own tab.
- A **node** (`.ircnode`) is a reusable graph baked into one palette node.
- A **plugin** (`.ircplugin`) is a node graph that **extends ircuitry itself** - it hooks the app's own chrome (menus, toolbar, panels, right-click) and reacts to app events, running **globally across every tab with no tab of its own**.

You build a plugin in the normal node editor using a new family of **app nodes**, then **Bundle as Plugin** to get a portable `.ircplugin` that others can install, enable, and disable.

### Goals
- Author the app's UI from the graph: add menu items, a toolbar button, a side panel, right-click entries.
- React to app events (app start, tab switched, bot started/stopped, a contributed item clicked).
- Act on the app: toasts, dialogs, switch tabs, start/stop bots, and edit the *open bot's* graph.
- Run global + always-on once enabled; survive tab switches; no tab required.
- Be safe: same trust-card install + capability scan as community nodes, plus app-level permissions.

### Non-goals (v1)
- Replacing core chrome (we *add* to it, not rewrite it).
- Native code plugins / DLLs (plugins are graphs; code runs in the existing code-node sandbox).
- Theming (already a separate `.ircnode`/theme path).

---

## 2. The core gap, and the core idea

**Today every running graph is owned by a `BotRuntime`, which is owned by a `Bot` tab, driven by `ServerConn`s.** `ServerConn` *is* the `IRuntimeSink` - the "effect escape hatch" a node calls to act on the world (`Privmsg`, `UiWindow`, `GetState/SetState`, ...). There is **no app-global graph**. (See `BotRuntime.cs`, `ServerConn.cs`, `IRuntimeSink.cs`.)

The whole plugin system falls out of one move: add a **fourth runtime layer** and a **second sink**.

```
Bot tab        -> BotRuntime -> ServerConn (IRuntimeSink: IRC + UI-via-child-window)
Plugin (new)   -> PluginHost -> AppSink    (IRuntimeSink: app chrome + UI-in-process)
```

- **`PluginHost`** - one per enabled plugin. Owns a frozen copy of the plugin graph + its own persistent state dict (exactly like `BotRuntime` owns `_runGraph` + `_state`). Reuses `GraphExecutor` unchanged.
- **`AppSink : IRuntimeSink`** - implemented by the app shell (`AppModel`/`MainScreen`), not by `ServerConn`. Its job: route a node's effects into the app's chrome instead of into IRC. Because nodes only ever talk to `IRuntimeSink` + `INodeContext`, **GraphExecutor, the node model, and even the existing UI nodes work unchanged** - they just hit a different sink.

That's the leverage: we don't fork the executor, we add a sink.

---

## 3. The hook + trigger model

The app surfaces use the same shape the UI window subsystem already uses (`ui.button` registers a control; `ui.on` fires when it's used). We mirror it for the app:

- **Register on load.** When a plugin is enabled (and on app start), `PluginHost` fires the `app.start` trigger family. The plugin's setup flow runs **action** nodes (`Add Menu Item`, `Add Toolbar Button`, `Add Panel`, `Add Context Item`, `Add Command`) which call `AppSink` to **register a contribution** (id + label + icon + where).
- **Fire on activate.** When the user clicks a contributed item, the app fires the `app` trigger family with vars (`{app_event}` = `menu|toolbar|command|context|panel`, `{app_id}` = the contribution id, plus context like `{app_node}` for a node context item). An **`On App Event`** trigger node (filter by kind/id) catches it and runs the rest of the plugin's flow.

So a "Reverse selected node's colour" plugin is literally:

```
app.start --> app.context (id="rev", label="Reverse colour", on=node)
app.on (event=context, id="rev") --> app.graph.setparam(...) --> app.toast("done")
```

This is identical in spirit to `event.start -> ui.button` / `ui.on -> ...` that the demos already use, so it'll feel familiar and reuse the same teaching.

---

## 4. The four surfaces -> real injection points

The chrome already has clean, list-shaped extension points. Each surface becomes: a registry the `AppSink` writes into, and a few lines where the existing draw/click code merges plugin contributions.

| Surface | Today | Plugin node | Where it merges in |
|---|---|---|---|
| **Menu / commands** | `CtxItem` struct + `List<CtxItem> _ctxItems`, built in `OpenFileMenu`/`OpenMoreMenu`, drawn by `DrawContextMenu`, dispatched via `Do()` | `app.menu` (action) + `app.on(event=menu)` | a "Plugins" submenu in `OpenMoreMenu` that lists registered `app.menu` items; also feed `BuildCommands()` so they're searchable in the Cmd+K palette |
| **Toolbar button** | `DrawTitlebar` carves buttons right-to-left via the `Btn()` lambda + `IconBtn`/`IconPad` primitives | `app.toolbar` (action) + `app.on(event=toolbar)` | in `DrawTitlebar`, after the built-in cluster, loop the registered toolbar items, carve space with the same `Btn()`/`IconBtn`, hit-test, fire `app` on click |
| **Side panel** | `DockManager` + `Panel{Id,Dock,Size,Visible,Content}`; `_dock.Add(...)`, draw into `.Content` | `app.panel` (action) - registers a dock panel whose **content is a UiScene** | register `_dock.Add(new Panel{Id="plugin:<id>", ...})`; render the plugin's UiScene into `.Content` (see Section 6) |
| **Right-click** | same `CtxItem`/`_ctxItems`, built in `OpenContextMenu(onNode)` | `app.context` (action, `on=node\|canvas`) + `app.on(event=context)` | in `OpenContextMenu`, after the built-ins, append registered context items whose `on` matches; `Do()` fires `app` with `{app_node}` set |
| *(bonus)* **Command palette** | `Cmd{Icon,Label,Hint,Do}` from `BuildCommands()`, fuzzy-filtered | every `app.menu`/`app.command` auto-becomes a `Cmd` | one merge in `BuildCommands()` - free, since menu items already carry icon+label+action |

All of these reuse the existing `Renderer`/`Ui` widgets, so contributed items look native automatically.

**Note on tabs:** tabs *are* bots, so plugins can't add tabs - but they can decorate them (a badge) and add tab right-click items later. Out of scope for v1.

---

## 5. The four powers -> app-API nodes

Beyond every existing node (which a plugin can also use), plugins get new **app** nodes whose `Exec` calls `AppSink`:

**Dialogs & toasts**
- `app.toast` -> `AppSink.Toast(msg, kind)` -> `MainScreen.PushToast(...)` (already public).
- `app.dialog` -> render a UiScene as a modal overlay (Section 6); optional buttons.
- `app.confirm` -> a yes/no that **blocks the run** until answered, exactly like the existing IRC `AwaitApproval()` human-gate pattern, then pulses `yes`/`no`.

**Its own window / panel** - *reuses the UI subsystem we already built.* The plugin builds content with the existing `ui.panel`/`ui.text`/`ui.button`/`ui.slider`/`ui.mesh`/... nodes targeting a window id. The only difference is the sink: under `AppSink`, `UiWindow`/`UiUpsert`/`UiMesh`/... render **in-process** (into a dock panel from `app.panel`, or a modal overlay from `app.dialog`) instead of spawning a child OS-window host. Same nodes, same `UiScene`, same `UiWindowScreen` draw + hit-test + `ui.on` events. (This is the biggest reuse in the whole design.)

**App state & navigation**
- `app.info` (pure) -> active bot name/id, tab count, running state, app version, current selection - from `AppModel.ActiveBot` etc.
- `app.nav` -> switch tab, open a settings/manager pane.
- `app.bot` -> start/stop/restart a named (or the active) bot.

**Act on the open bot** (the powerful, careful one)
- `app.graph.read` (pure) -> the active bot's graph as JSON (or queryable).
- `app.graph.add` / `app.graph.setparam` / `app.graph.connect` / `app.graph.remove` -> mutate the active bot's editor graph (`Bot.Graph`), marshalled onto the UI thread (Section 8), then `MarkDirty()`.

---

## 6. In-app UI rendering (panels & dialogs)

A plugin panel/dialog is a **`UiScene`** (the exact data the window-host already renders). We add an in-process renderer path:

- `AppSink.UiUpsert/UiMesh/UiAnimate/...` build/maintain a `UiScene` keyed by window id, just like `ServerConn` does - but **don't** spawn a `UiHost` child process.
- Each frame, `MainScreen` draws the plugin's `UiScene` into either:
  - a `DockManager.Panel.Content` rect (for `app.panel`), or
  - a centred modal overlay (for `app.dialog`),
  using a shared `UiSceneRenderer` (the 2D/3D draw + input from `UiWindowScreen`, refactored to accept a target rect instead of a full window).
- Input + `ui.on` events flow back through `AppSink.OnUiEvent -> FireFamily("ui", ...)` into the plugin's host, identical to the window path.

Result: everything we built for OS-window UI (multiline fields, sliders, live slider events, 3D scenes, the demos) works **inside ircuitry** for plugin panels with zero new UI primitives.

---

## 7. New nodes (catalog additions)

A new **App** category (or fold into UI). Triggers in **bold**.

| Node | Kind | Purpose |
|---|---|---|
| **`app.start`** | trigger | fires when the plugin is enabled / app starts - the "register your hooks" entry point |
| **`app.on`** | trigger | fires on a contributed item's activation; filter by `event` (menu/toolbar/command/context) + `id`; outputs `{app_id}`, `{app_node}` |
| **`app.event`** | trigger | app lifecycle: `tab-switched`, `bot-started`, `bot-stopped`, `workspace-saved` |
| `app.menu` | action | register a menu item (+ auto command-palette entry) |
| `app.toolbar` | action | register a toolbar button |
| `app.context` | action | register a right-click item (`on=node\|canvas`) |
| `app.panel` | action | register a dockable side panel (content = a UiScene) |
| `app.command` | action | register a pure command-palette command |
| `app.toast` | action | a toast notification |
| `app.dialog` | action | open a UiScene as a modal |
| `app.confirm` | action | blocking yes/no -> `yes`/`no` exec outs |
| `app.info` | data | read app/active-bot state |
| `app.nav` | action | switch tab / open a pane |
| `app.bot` | action | start/stop/restart a bot |
| `app.graph.*` | data/action | read + mutate the active bot's graph |

Registration is the same pattern as every node: a `NodeDef` with `Exec` (+ `TriggerEvent` for triggers). New trigger families (`app`, `app.event`) just need (1) `FireFamily("app", vars)` called from the app shell, (2) the `NodeDef`s. (Per `NodeCatalog.cs` + `FireFamily` in `ServerConn` - we add the same call site in `PluginHost`.)

---

## 8. Authoring, bundling, lifecycle

**Authoring ("convert a workflow", per your pick).** You build a plugin in a normal bot tab using `app.*` nodes. A `Bundle as Plugin...` action (More menu + Cmd+K) validates that the graph has at least one `app.*` hook, collects metadata (name, version, icon, declared permissions), and writes a **`.ircplugin`**.

**`.ircplugin` format** - a superset of `.ircbot`:
```json
{
  "format": "ircuitry.plugin.v1",
  "name": "Graph Tidy",
  "version": "1.0.0",
  "icon": "broom",
  "description": "Adds a 'Tidy + colour-code' command.",
  "permissions": ["menu", "command", "edit-graph", "toast"],
  "nodes": [...], "connections": [...]
}
```

**Install.** Opens the existing **trust card** (`DrawInstallModal`) - reuse `Capabilities.Scan` to flag code/network/secret nodes, **plus** an app-permission section derived from the `app.*` nodes used (e.g. *"Edits your bots' graphs", "Adds menu items", "Controls bots"*). The graph-editing permission is highlighted as the scariest. Double-click already routes `.ircplugin` here once we add it to the file-association list (we just shipped that machinery).

**Storage.** Installed plugins live in `~/ircuitry/plugins/*.ircplugin` (mirrors `~/ircuitry/nodes/`), with an enabled/disabled flag in the workspace (a `plugins[]` block in `workspace.ircuitry`, next to `bots[]`/`groups[]`).

**Manager.** A **Plugins** pane (modal or a built-in dock panel): list installed plugins, enable/disable toggles, show permissions, uninstall, "open source in a tab" to edit. Enabling spins up a `PluginHost`; disabling tears it down + removes its contributions.

**Lifecycle.** On app start, each enabled plugin's `PluginHost` starts and fires `app.start` (registers hooks). On disable/uninstall/reload, the host stops and all its registered contributions are removed from the registries (so menus/panels/buttons vanish cleanly).

---

## 9. Threading & safety

- `PluginHost` runs flows on a worker pool like `BotRuntime` (off the UI thread).
- **Registries** (menu/toolbar/panel/context/command contributions) are read by the UI thread each frame and written by plugin flows -> a lock or a concurrent snapshot, same discipline as `ServerConn`'s `_uiGate`.
- **`app.graph.*` mutations** of `Bot.Graph` and any chrome drawing must be **marshalled to the UI thread** (a main-thread action queue drained in `Update`), because the editor graph isn't thread-safe.
- **Crash isolation:** a throwing plugin flow is caught per-run (executor already wraps `Exec` in try/catch) and surfaced as a toast/log, never taking down the app.
- **Trust:** plugins can edit your bots + run code, so install is gated by the trust card, and the manager shows exactly what each plugin may do. A "panic: disable all plugins" toggle.

---

## 10. Build plan (vertical slice first)

Each phase is shippable and testable headlessly (sink fakes + selftests, like the UI subsystem).

1. **Runtime + menu loop (the slice).** `PluginHost` + `AppSink` (toast + menu only); `app.start`, `app.menu`, `app.on`, `app.toast`; `.ircplugin` format + `Bundle as Plugin` + a minimal Plugins manager + install via trust card. End-to-end: author -> bundle -> install -> a menu item appears -> clicking it toasts. Selftest the register/fire loop with a fake `AppSink`.
2. **Toolbar + right-click + command palette.** `app.toolbar`, `app.context`, `app.command`; merge into `DrawTitlebar`, `OpenContextMenu`, `BuildCommands`.
3. **In-app UI: panels & dialogs.** Refactor `UiWindowScreen` draw into a `UiSceneRenderer(targetRect)`; `app.panel` (dock) + `app.dialog` (modal) rendering the existing `ui.*` scenes in-process. This is the biggest piece and unlocks rich plugins.
4. **App power: act-on-bot + nav + state.** `app.info`, `app.nav`, `app.bot`, `app.graph.*` with main-thread marshalling; permission surfacing for graph edits.
5. **Polish.** Manager UX, enable/disable persistence, panic switch, docs + 2-3 example plugins (e.g. "Tidy + recolour", "Quick-connect server", "Word-count panel").

---

## 11. Decisions (locked)

1. **Category:** its own **App / Plugins** node category. ✅ (added: `NodeCategory.App`, `plugin` accent.)
2. **Panel content:** reuse the `ui.*` nodes rendered into the panel (Phase 3). Row-helpers may come later.
3. **Edit-graph scope:** the **active bot only** in v1 (by id).
4. **Install:** auto-enable on install (after the trust card).
5. **Naming:** `.ircplugin` / "Plugin".
6. **Model:** one graph = one plugin (it can host many menus/panels/flows). Confirmed.

## 12. Status

**Phase 1: COMPLETE** - a real, end-to-end loop (author → bundle → install → menu item appears → click runs your flow).

- **Nodes + category:** `App / Plugins` category; `app.start`, `app.menu`, `app.on`, `app.toast`.
- **Runtime:** `PluginHost` (a plugin graph run under an `AppSink : IRuntimeSink`) + `PluginManager` (installed plugins, live hosts, the contribution registry) + the `.ircplugin` bundle format (`PluginBundle`, permissions derived from the `app.*` nodes used).
- **Chrome:** `MainScreen` is the `IAppHost` (toasts via `PushToast`); the More menu merges plugin menu items + **Bundle this as a plugin…** + a **Plugins…** manager (list / uninstall); installed plugins auto-load + enable on app start.
- **Distribution:** `.ircplugin` joins the file-association machinery (double-click installs; `OpenFile` routes it) on Linux/Windows/macOS + the `.deb`/AppImage/`.app` packaging.
- **Tests:** `PluginLoopTest` (node-level register→fire) + `PluginManagerTest` (manager enable→activate→disable + `.ircplugin` round-trip with derived permissions). Full selftest green. Sample: `~/greeter.ircplugin`.

**Next (Phase 2+):** toolbar + right-click + command-palette hooks; in-app panels & dialogs (the `ui.*`-into-a-dock-rect renderer); act-on-active-bot + nav + state; trust-card install (`Capabilities.Scan` + the app-permissions, currently shown in a toast) and a richer manager.

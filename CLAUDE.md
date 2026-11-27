# CLAUDE.md: PostgreSQL Explorer for SSMS 22

Context for AI coding assistants (and humans) working on this repo. The README is for users; this file is for whoever changes the code.

## What this is

An SSMS 22 VSIX extension (C#, .NET Framework 4.8, WinForms hosted in VS tool windows) that adds PostgreSQL to SSMS: Registered Servers, nodes injected into SSMS's own Object Explorer, a query window, multi-server (group) queries, and importers for pgAdmin / DataGrip / DBeaver.

## Build, install, release

```powershell
.\build.ps1             # Release build -> src\SsmsPgExplorer\bin\Release\SsmsPgExplorer.vsix
.\build.ps1 -Install    # build + install via SSMS's VSIXInstaller (SSMS must be closed)
.\build.ps1 -Uninstall
```

- MSBuild comes from VS 2022/2026 or Build Tools; `build.ps1` finds it with `vswhere`. The VS SDK comes from NuGet (`Microsoft.VSSDK.BuildTools`), so the VSSDK workload is not needed.
- **Compiling needs a local SSMS 22 install.** The csproj references `SqlWorkbench.Interfaces.dll`, `ObjectExplorer.dll` and `sqlmgmt.dll` from `$(SsmsIdeDir)` (default `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`) with `Private=False`. They are not redistributable, so they are not committed or shipped. Override with `/p:SsmsIdeDir=...`.
  - That's why there is no CI yet: hosted runners have no SSMS. The options are to install SSMS on a Windows runner, or to commit small reference-only stubs of those three assemblies.
- Version lives in **three places**; bump them together: `source.extension.vsixmanifest` (`Identity Version`), `source.extension.cs` (`Version`), `Properties/AssemblyInfo.cs`.
- `source.extension.cs` and `VSCommandTable.cs` are **hand-maintained**. The VS custom tools (VsixManifestGenerator / VsctGenerator) only run inside the IDE, so keep them in sync with the manifest and `.vsct` by hand.
- Release: bump the version, then `build.ps1`, commit, push, then:
  `gh release create vX.Y.Z src\SsmsPgExplorer\bin\Release\SsmsPgExplorer.vsix --title ... --notes-file ... [--prerelease]`
- SSMS 22 has **no Manage Extensions dialog**. Install and uninstall go through `VSIXInstaller.exe` in the SSMS IDE folder. Extension id: `SsmsPgExplorer.7C2E4F19-6B3A-4D8E-A1F0-9E5B3C7D2A64`.
- Installed location (per user): `%LOCALAPPDATA%\Microsoft\SSMS\22.0_<hash>\Extensions\<random>\`. Check that the package loaded with `Ssms.exe /log`, then look for `SsmsPgExplorerPackage` in `%APPDATA%\Microsoft\SSMS\22.0_<hash>\ActivityLog.xml`.

## Layout

| Path | What |
|---|---|
| `SsmsPgExplorerPackage.cs` | `ToolkitPackage`. Autoloads on `ShellInitialized` (background), registers commands and tool windows, starts the Object Explorer bridge. |
| `VSCommandTable.vsct` | Tools-menu group + View placement. `IDG_VS_VIEW_DEV_WINDOWS` exists in SSMS; `IDG_VS_VIEW_BROWSERS` does not compile. |
| `Commands/` | Show Registered Servers, New Query, Re-attach Object Explorer. |
| `Model/` | `ServerRegistration` (DataContract, **no secrets**), `ServerStore` (`%APPDATA%\SsmsPgExplorer\registered-servers.json` + `.bak`). |
| `Connection/` | `ConnectionFactory` (Npgsql, read-only guardrail, auth resolution, in-memory caches), `CredentialStore` (Windows Credential Manager P/Invoke), `AwsCli` (shells out to `aws.exe`), `PgCatalog` (pg_catalog queries for the tree). |
| `ObjectExplorer/` | `ObjectExplorerBridge` (finds the OE tree), `PgNode` (base `HierarchyTreeNode`), `PgNodes` (server / folder / db / schema / relation / function / leaf), `Icons` (drawn at runtime). |
| `RegisteredServers/` | Tool window, tree control, registration dialog, password prompt, import menu wiring. |
| `Query/` | Multi-instance query tool window, `QueryControl` (editor / results / messages / status, single + multi-server), `QueryRunner` (execution + `Combine` for merged group results). |
| `Import/` | `Importers` (pgAdmin `servers.json`, DataGrip `.idea/dataSources(.local).xml`, DBeaver `data-sources*.json`, merge/upsert), `ImportDialog` (preview, nothing ticked by default, filter, highlighted-rows fallback). |
| `Scripting/` | `ScriptingOptions` (SSMS option names/defaults, JSON in `%APPDATA%`; `[OnDeserializing]` re-applies defaults for missing keys) + dialog, `TableScripter` (CREATE / DROP / DROP+CREATE from pg_catalog deparsers; sets `search_path = pg_catalog` so references come out schema-qualified). |
| `Activity/` | `ActivityWindow` (multi-instance document), `ActivityControl` (`pg_stat_activity` + `pg_blocking_pids`, auto refresh, filter, Cancel / Kill via `pg_cancel_backend` / `pg_terminate_backend` with confirmation + log). |
| `Plans/` | `PlanModel` (`PlanParser.Parse` of `EXPLAIN (FORMAT JSON)`: exclusive cost / time, row-estimate factor, warnings; `ToText`), `PlanGraph` (custom-painted SSMS-style graph: root left, arrows sized by rows, hit-test, Ctrl+wheel zoom), `PlanView` (statement picker, Properties, Text / JSON, Open in PEV2). |
| `Query/SqlSplitter.cs` | Top-level `;` splitter (quotes, `E''`, `$tag$`, nested comments), `IsExplainable`, `MayWrite`. Used by plans; plain Execute still sends the whole batch. |
| `UI/` | `Theme` (VS color tokens -> WinForms, custom flat `ToolStripRenderer`), `ClickThroughToolStrip`. |
| `Diagnostics/Log.cs` | `%APPDATA%\SsmsPgExplorer\log.txt` (rolls at 1 MB). Never logs secrets or query results. |

## Object Explorer integration (the fragile part)

SSMS has **no public API for third-party server types**. `ConnectionDialogServerType` is a closed enum, so Postgres can't go in the Connect dialog. What we do instead:

1. `Package.GetGlobalService(typeof(IObjectExplorerService))` returns **`ObjectExplorerServiceDisp`**, an automation proxy. The real `ObjectExplorerService` (in `Microsoft.SqlServer.Management.SqlStudio.Explorer.dll`) is in its private `owner` field.
2. The service's `objectExplorerTree` field is **lazy and usually null**. Read the `Tree` property, which resolves or creates the OE window. Fallback: `GetObjectExplorerWindowFrame()` -> `VSFPROPID_DocView` -> `ObjectExplorerToolWindow.Control`. Last resort: a bounded field-graph walk. `ResolveTree` reports which steps failed in its error message.
3. The tree is `ObjectExplorerControl : LazyTreeView : ThemedTreeView : TreeView` (WinForms). **`LazyTreeView` is internal**, so hold it as `TreeView` and call `AddRootNode(TreeNode)`, `AddNode(TreeNode, TreeNode[])` and `AddIconToImageMap(TreeNode)` by reflection.
4. Nodes subclass the public `HierarchyTreeNode` (`SqlWorkbench.Interfaces`). Its abstract members are `Icon`, `SelectedIcon`, `State`, `ShowPolicyHealthState`. Nodes implement `INodeWithMenu` (`ShowContextMenu(Point)`, `DoDefaultAction()`). Fallback handlers are hooked on the tree's `BeforeExpand` / `NodeMouseClick` in case OE ignores our interfaces. A 400 ms debounce stops the menu from showing twice.
5. `LazyNode.Expandable = true` adds SSMS's own "expanding..." placeholder; don't add a second one. Children load off the UI thread (`JoinableTaskFactory`) and are swapped in on the UI thread.
6. The startup poll (every 2 s, about 2 min) calls `TryAttach(allowCreate: false)` so it never forces the OE window open. User actions use `allowCreate: true`.

To re-probe SSMS internals after an SSMS update, load the IDE assemblies by reflection in **Windows PowerShell 5.1** (`powershell.exe`, .NET Framework), not pwsh 7. Use an `AssemblyResolve` handler that indexes the IDE folder. Guard it against recursion and skip `*.resources`, or it stack-overflows.

## Gotchas learned the hard way

- **WinForms ToolStrip inside a WPF-hosted tool window drops the first click** (`WM_MOUSEACTIVATE` -> `MA_ACTIVATEANDEAT`). Always use `UI.ClickThroughToolStrip`.
- **Npgsql 8.0.x is the last line with `netstandard2.0`** (it runs on .NET Framework 4.8). Npgsql 9+ won't load. `SSMS.exe.config` already binding-redirects System.Text.Json, Channels, DiagnosticSource, Logging.Abstractions, etc. to SSMS's own 10.x copies, so don't ship conflicting redirects.
- `Community.VisualStudio.Toolkit.17` is referenced with `ExcludeAssets="Runtime"`, but its DLL still ends up in the VSIX. Check the VSIX zip contents after dependency changes.
- `MessageBox` is ambiguous (WinForms vs `Community.VisualStudio.Toolkit.MessageBox`). Alias it: `using MessageBox = System.Windows.Forms.MessageBox;`.
- `await TaskScheduler.Default` needs `using Microsoft.VisualStudio.Threading;`.
- Code-built forms don't auto-scale. Size dialogs with `DeviceDpi / 96f` and use `AutoSize` layouts, or buttons get clipped at 150% DPI.
- `ServerStore.Load()` **throws** on an unreadable file and never returns empty. Returning empty would let the next save wipe every registration. Saves keep `registered-servers.json.bak`, and duplicate ids get re-keyed on load.
- Query sessions are **unpooled**, one per server per query window, so `SET` and temp tables persist. Catalog/tree queries use pooled connections. Pooled connections are `DISCARD ALL`'d on return, so the read-only `SET SESSION CHARACTERISTICS` runs on every open.
- `QueryRunner` sets `AllResultTypesAreUnknown = true`, so every value comes back as text (psql-like, no type-mapping failures).
- Keyboard accelerators (F5 / Ctrl+E) may be claimed by the SSMS shell before the hosted control sees them. The Execute button is the reliable path.

## Security rules (keep these)

- **No secrets on disk** except a Windows Credential Manager entry the user opted into (`SsmsPgExplorer/<registration id>`). The JSON store and exports never contain passwords.
- AWS secret values and IAM tokens are held in memory only (10-minute cache). AWS access always goes through the user's own AWS CLI / SSO profiles. No AWS SDK, no stored keys.
- **Importers never read or decrypt stored passwords** from other tools. That includes DBeaver's encrypted credentials. They read connection definitions only.
- Sessions are **read-only by default**. Writes need both the registration's *Allow write sessions* flag and the query window toggle, with a confirmation, and every execution confirms in multi-server mode.
- The log never contains passwords, tokens, secret values or result data.
- Keep the repo **employer-neutral**: no company names, internal hostnames, account ids or team-specific process in code, docs, screenshots or commit messages. Use placeholders such as `111111111111`, `prod-readonly` and `my-org`.

## Open issues / next steps

- **"Registrations disappear" = AppData redirection, not a store bug.** If SSMS is started from a packaged (MSIX) app, such as some terminals or AI-assistant desktop apps, Windows redirects its `%APPDATA%` writes into that app's `%LOCALAPPDATA%\Packages\<app>\LocalCache\Roaming\...`. That SSMS and an SSMS started from the Start menu then see different `registered-servers.json` files. Diagnose with `fsutil hardlink list "%APPDATA%\SsmsPgExplorer\registered-servers.json"` (a `...\Packages\...\LocalCache\...` path confirms it). Start SSMS normally when testing.
- Tested end to end: importing from DataGrip (including AWS Secrets Manager data sources over SSO) and DBeaver, the import dialog's selection and filter, Object Explorer attach via the service proxy, Execute buttons, group move / rename / delete, and multi-server queries.
- Editor: plain `TextBox`. A real editor (syntax highlighting, IntelliSense) would be the biggest UX win.
- Ideas: plans in multi-server windows, plan comparison, bulk registration from Secrets Manager, CI (see Build), `.editorconfig` support for Format SQL.

## Conventions

- Conventional commits (`feat:`, `fix(object-explorer):`, `docs:` ...).
- Everything that touches SSMS internals fails soft: log, show a clear message, and keep Registered Servers and query windows working.
- Every UI event handler catches and surfaces exceptions (Messages tab or a MessageBox, plus the log). Never swallow them silently.

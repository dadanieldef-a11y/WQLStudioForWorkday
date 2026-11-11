# WQL Studio - C# / ASP.NET Core + Blazor WebAssembly

A C# build of the "Toad for Workday Extend" WQL workbench (originally a Next.js /
React / TypeScript app). Browse Workday Extend business objects, write and run
WQL as the logged-in user, drill into references, and Insert / Update / Delete
custom-BO records via the WCP Extend App REST API.

## Architecture

An **ASP.NET Core (.NET 8) hosted Blazor WebAssembly** solution. The Workday
access token never reaches the browser: it's held server-side in the ASP.NET
session (the analogue of the original's iron-session), and only the server's
`/api/*` proxy attaches it as a Bearer token. The WASM client only ever calls
same-origin `/api/*`.

Three projects under `src/`:

- **ExtendExplorer.Shared** - wire DTOs shared by server and client (camelCase JSON).
- **ExtendExplorer.Server** - the BFF + static host. OAuth (Authorization Code +
  PKCE), the session store, and the Workday proxy controllers (`/api/auth/*`,
  `/api/workday/*`). Serves the Blazor client.
- **ExtendExplorer.Client** - the Blazor WASM UI: schema tree, Monaco-based WQL
  editor, results grid, and the CRUD dialogs.

Tenant "connections" (endpoints, API client id + secret, tenant alias, BO
company id, API base) live in the browser's `localStorage`, exactly like the
original - this is a single-user developer tool, run locally.

## Prerequisites

- .NET 8 SDK (to build; the published exe is self-contained)
- A Workday tenant with an API client registered for Authorization Code + PKCE
  (S256), with the redirect URI `http://localhost:3000/api/auth/callback`.
  When a client secret is set on the connection the token call authenticates
  with HTTP Basic (confidential client); otherwise `client_id` goes in the body.

## Run

```bash
cd src/ExtendExplorer.Server
dotnet run
```

Then open <http://localhost:3000>. The `http` launch profile binds port 3000 to
match the OAuth redirect URI. Add a connection on the Connect screen, make it
active, and click **Connect** to start the OAuth flow.

## Notes

- **Monaco** is loaded on demand from the jsDelivr CDN via its AMD loader
  (`wwwroot/js/monaco-wql.js`), with the custom `wql` language, the
  `wql-graphite` theme, and FROM-scoped field completions. If the CDN can't be
  reached, the editor degrades to a plain `<textarea>` so the workbench still
  works.
- **Write modes** (Insert / Update / Delete) are restricted to this tenant's
  custom BOs - those whose name carries the `_{boCompanyId}_` marker. Set a BO
  Company ID on the connection to enable them.
- WQL filters instance/reference fields with `in (<id>)`; `=` is only for
  Boolean / Date / Numeric / Text. Drill-down re-queries the same source filtered
  to the clicked instance, keeping the select list.
- Insert auto-drops read-only / derived fields (Workday error code `A13`) and
  retries, so cloning a row into a new record "just works".
- Bulk writes use the WCP App API's native bulk endpoints (`?bulk=true`,
  100 records per call, body `{ "data": [ … ] }`): Bulk insert / Import
  (POST), Bulk edit (PATCH) and Delete (DELETE with `{ "id": wid }` entries).
  Results are still reported per record.

Things to know :

- **Port 3000 is fixed on purpose(unless someone wants it changed).** OAuth only returns to the redirect URI
  registered on the Workday API client - `http://localhost:3000/api/auth/callback`
  - so the exe binds `http://localhost:3000` (set in `Program.cs`, since
  `launchSettings.json` is a dev-only file and isn't in the published build).
  Every user runs on their own machine's localhost:3000, so the one
  registered loopback URI works for all of them. If port 3000 is already in use
  on a machine, the app won't start on the expected URL and login will fail.
- **SmartScreen.** The exe is unsigned, so Windows Defender SmartScreen will warn
  "unrecognized app" on first run - it runs if they click *More info → Run
  anyway*. A code-signing certificate (ideally one you already own) removes
  this.
- **Offline networks.** Monaco loads from a CDN; on a locked-down network the
  editor falls back to a plain textarea. Self-host Monaco (see the Monaco note
  above) if that matters.
- Publish to the clean `releases\win-x64` folder (the script does this) so stale
  copies never end up in the zip.

## Layout

```
ExtendExplorer.sln
src/
  ExtendExplorer.Shared/     Dtos.cs
  ExtendExplorer.Server/     Program.cs, Controllers/, Services/
  ExtendExplorer.Client/
    Program.cs, App.razor, Pages/
    Components/   Explorer (orchestrator), SchemaTree, QueryEditor, ResultsGrid,
                  Toolbar, ModeActionBar, Pager, and the write/export dialogs
    Services/     ApiClient, WqlMetadata, WqlQuery, WqlEdit, WqlFormat,
                  BoFields, Connections, PaneLayout, Interop, Wcp
    wwwroot/
```

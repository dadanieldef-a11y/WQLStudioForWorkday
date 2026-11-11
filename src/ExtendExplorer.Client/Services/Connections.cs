using System.Text.Json;

namespace ExtendExplorer.Client.Services;

// A saved tenant connection — the C# port of the Connection interface in
// lib/connections.ts. All values are non-secret except ClientSecret, which is
// stored in the browser like the rest (a deliberate dev-tool trade-off). The
// list lives only in this browser (localStorage); it does not sync across
// devices. JSON is camelCase to match the original localStorage shape.
public sealed class Connection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string AuthorizeEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string WqlBase { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TenantAlias { get; set; } = "";
    public string BoCompanyId { get; set; } = "";
    public string ApiBase { get; set; } = "";

    public Connection Clone() => (Connection)MemberwiseClone();

    // True only when every field is filled — the same bar as saving a connection.
    // Guards both Save (the editor) and Connect (the Connect screen) against an
    // incomplete connection, e.g. one imported from a partial settings file.
    public bool IsComplete() =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(AuthorizeEndpoint)
        && !string.IsNullOrWhiteSpace(TokenEndpoint)
        && !string.IsNullOrWhiteSpace(WqlBase)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(TenantAlias)
        && !string.IsNullOrWhiteSpace(BoCompanyId)
        && !string.IsNullOrWhiteSpace(ApiBase);
}

// Toad-style saved-connection store, persisted in the browser via Interop. The
// C# port of lib/connections.ts.
public sealed class Connections
{
    private const string Key = "wd_explorer_connections";
    private const string ActiveKey = "wd_explorer_active_connection";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Interop _interop;

    public Connections(Interop interop) => _interop = interop;

    private static string NewId()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rand = Guid.NewGuid().ToString("N")[..6];
        return $"conn_{Convert.ToString(ms, 16)}_{rand}";
    }

    public async Task<List<Connection>> ListAsync()
    {
        try
        {
            var raw = await _interop.LocalGetAsync(Key);
            if (string.IsNullOrEmpty(raw)) return new();
            var arr = JsonSerializer.Deserialize<List<Connection>>(raw, JsonOpts);
            return arr ?? new();
        }
        catch
        {
            return new();
        }
    }

    private async Task PersistAsync(List<Connection> list) =>
        await _interop.LocalSetAsync(Key, JsonSerializer.Serialize(list, JsonOpts));

    public Task<string?> GetActiveIdAsync() => _interop.LocalGetAsync(ActiveKey).AsTask();

    public async Task SetActiveIdAsync(string? id)
    {
        if (!string.IsNullOrEmpty(id)) await _interop.LocalSetAsync(ActiveKey, id);
        else await _interop.LocalRemoveAsync(ActiveKey);
    }

    // The active connection, falling back to the first if the active id is stale
    // or unset (so there's always a sensible default to connect with).
    public async Task<Connection?> GetActiveConnectionAsync()
    {
        var list = await ListAsync();
        if (list.Count == 0) return null;
        var id = await GetActiveIdAsync();
        return list.FirstOrDefault(c => c.Id == id) ?? list[0];
    }

    // Create or update. A connection without an id is created (and made active
    // if it's the first one); with an id, the matching entry is replaced.
    public async Task<Connection> SaveAsync(Connection input)
    {
        var list = await ListAsync();
        if (!string.IsNullOrEmpty(input.Id))
        {
            var next = list.Select(c =>
            {
                if (c.Id != input.Id) return c;
                var merged = input.Clone();
                merged.Id = c.Id;
                return merged;
            }).ToList();
            await PersistAsync(next);
            return next.First(c => c.Id == input.Id);
        }

        var created = input.Clone();
        created.Id = NewId();
        var appended = new List<Connection>(list) { created };
        await PersistAsync(appended);
        if (appended.Count == 1) await SetActiveIdAsync(created.Id);
        return created;
    }

    public async Task<Connection?> DuplicateAsync(string id)
    {
        var list = await ListAsync();
        var src = list.FirstOrDefault(c => c.Id == id);
        if (src is null) return null;
        var copy = src.Clone();
        copy.Id = NewId();
        copy.Name = $"{src.Name} (copy)";
        var appended = new List<Connection>(list) { copy };
        await PersistAsync(appended);
        return copy;
    }

    public async Task DeleteAsync(string id)
    {
        var list = await ListAsync();
        var next = list.Where(c => c.Id != id).ToList();
        await PersistAsync(next);
        if (await GetActiveIdAsync() == id)
            await SetActiveIdAsync(next.Count > 0 ? next[0].Id : null);
    }

    // ── Export / import (the Connect screen's settings file) ─────────────
    // The file is the wd-explorer-connections envelope: every saved connection
    // (secrets and all) plus the active id, pretty-printed. Same shape the web
    // app produces, so files move between the two.
    public async Task<string> ExportJsonAsync()
    {
        var list = await ListAsync();
        var activeId = await GetActiveIdAsync();
        var envelope = new
        {
            format = "wd-explorer-connections",
            version = 1,
            exportedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            activeId,
            connections = list,
        };
        return JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    // Merge a settings file into the saved list (by id: same id replaces, new id
    // appends), then activate the file's active connection if it's present.
    // Returns (imported = valid entries in the file, saved = entries written).
    public async Task<(int Imported, int Saved)> ImportJsonAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Not a settings file.");
        if (root.TryGetProperty("format", out var fmt)
            && fmt.ValueKind == JsonValueKind.String
            && fmt.GetString() != "wd-explorer-connections")
            throw new InvalidDataException("Unrecognized settings file.");
        if (!root.TryGetProperty("connections", out var connsEl)
            || connsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("No connections found in the file.");

        var incoming = new List<Connection>();
        foreach (var el in connsEl.EnumerateArray())
        {
            Connection? c;
            try { c = el.Deserialize<Connection>(JsonOpts); }
            catch { continue; }
            if (c is null || string.IsNullOrWhiteSpace(c.Name)) continue;
            if (string.IsNullOrWhiteSpace(c.Id)) c.Id = NewId();
            incoming.Add(c);
        }
        var imported = incoming.Count;

        var list = await ListAsync();
        var byId = list.ToDictionary(c => c.Id, c => c);
        var order = list.Select(c => c.Id).ToList();
        foreach (var c in incoming)
        {
            if (!byId.ContainsKey(c.Id)) order.Add(c.Id);
            byId[c.Id] = c;
        }
        var merged = order.Select(id => byId[id]).ToList();
        await PersistAsync(merged);

        var fileActive = root.TryGetProperty("activeId", out var a)
            && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;
        if (!string.IsNullOrEmpty(fileActive) && merged.Any(c => c.Id == fileActive))
            await SetActiveIdAsync(fileActive);
        else if (string.IsNullOrEmpty(await GetActiveIdAsync()) && merged.Count > 0)
            await SetActiveIdAsync(merged[0].Id);

        return (imported, incoming.Count);
    }
}

// Small browser-side helpers the Blazor client calls through Services/Interop.cs.
// These cover the DOM-only things the original React app did inline:
// localStorage (saved connections + schema-pane width), window.confirm, and a
// client-side CSV blob download (with a UTF-8 BOM so Excel opens it correctly).
window.appInterop = {
  localGet: function (key) {
    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  localSet: function (key, value) {
    try {
      window.localStorage.setItem(key, value);
    } catch {
      /* storage disabled / quota — best effort */
    }
  },
  localRemove: function (key) {
    try {
      window.localStorage.removeItem(key);
    } catch {
      /* ignore */
    }
  },
  confirm: function (message) {
    return window.confirm(message);
  },
  innerWidth: function () {
    return window.innerWidth;
  },
  focusById: function (id) {
    const el = document.getElementById(id);
    if (el) el.focus();
  },
  // The header "select all" checkbox shows a dash when only some rows on the
  // page are selected. `indeterminate` isn't settable through HTML attributes,
  // so the grid sets it here by element id after each render.
  setIndeterminate: function (id, value) {
    const el = document.getElementById(id);
    if (el) el.indeterminate = !!value;
  },
  // Download a text file (no BOM), e.g. the connections settings JSON.
  downloadText: function (filename, text, mime) {
    const blob = new Blob([text], { type: mime || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  },
  // Open a native "Save As" dialog so the user picks the name and location (and
  // can overwrite an existing file), via the File System Access API. Falls back
  // to a plain download where that API isn't available (e.g. Firefox/Safari).
  // Returns true if saved, false if the user cancelled the dialog.
  saveTextAs: async function (suggestedName, text, mime) {
    if (window.showSaveFilePicker) {
      try {
        const handle = await window.showSaveFilePicker({
          suggestedName: suggestedName,
          types: [
            {
              description: "WQL query",
              accept: { "text/plain": [".wql", ".txt", ".sql"] },
            },
          ],
        });
        const writable = await handle.createWritable();
        await writable.write(text);
        await writable.close();
        return true;
      } catch (e) {
        if (e && e.name === "AbortError") return false; // user cancelled
        // any other error (permission, etc.) falls through to the download
      }
    }
    this.downloadText(suggestedName, text, mime || "text/plain;charset=utf-8");
    return true;
  },
  // Trigger a client-side download of a CSV string. Prefix a BOM so Excel opens
  // UTF-8 correctly — matches downloadCsv() in the original lib/wql-format.ts.
  downloadCsv: function (filename, csv) {
    const blob = new Blob(["\uFEFF" + csv], {
      type: "text/csv;charset=utf-8;",
    });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  },
};

// Monaco + the "wql" language, ported from lib/monaco-wql.ts. In the original
// Next.js app Monaco was an npm dependency wired through @monaco-editor/react;
// here the Blazor client has no bundler, so Monaco is loaded on demand from a
// CDN via its AMD loader, and a small window.wqlEditor API bridges create/get/
// set/insert/focus/dispose to Services/Interop.cs.
//
// Keyword/operator set matches the WQL syntax docs — deliberately NOT ANSI SQL.
// WQL has no LIKE/BETWEEN/NULL; it uses contains / startswith / endswith /
// is empty / in, and filters instances with `in (...)`.
(function () {
  "use strict";

  const MONACO_VS =
    "https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs";

  const WQL_KEYWORDS = [
    "PARAMETERS", "SELECT", "FROM", "WHERE", "ON", "GROUP", "BY", "HAVING",
    "ORDER", "ASC", "DESC", "LIMIT", "AND", "OR", "NOT", "IN", "AS", "DISTINCT",
  ];
  const WQL_OPERATORS = [
    "in", "not in", "contains", "not contains", "startswith", "endswith",
    "is empty", "is not empty",
  ];
  const WQL_OPERATOR_WORDS = ["contains", "startswith", "endswith", "is", "empty"];
  const WQL_FUNCTIONS = ["AVG", "COUNT", "MIN", "MAX", "SUM"];

  let monacoLoad = null;
  let languageRegistered = false;
  let completionRegistered = false;
  const editors = Object.create(null);

  // Monaco spawns a web worker for its language services. Loaded cross-origin
  // from a CDN, the browser blocks a direct worker URL, which leaves the suggest
  // widget (autocomplete) stuck on "Loading…" even though the editor renders and
  // types fine. This trampoline hands Monaco a blob: worker (page origin, so its
  // cross-origin importScripts to the CDN is allowed) that loads the real worker.
  if (!self.MonacoEnvironment) {
    self.MonacoEnvironment = {
      getWorkerUrl: function () {
        const code =
          "self.MonacoEnvironment = { baseUrl: '" + MONACO_VS + "/' };\n" +
          "importScripts('" + MONACO_VS + "/base/worker/workerMain.js');";
        return URL.createObjectURL(
          new Blob([code], { type: "application/javascript" })
        );
      },
    };
  }

  // ── Monaco loader (AMD) ────────────────────────────────────────────────
  function loadMonaco() {
    if (window.monaco && window.monaco.editor) return Promise.resolve(window.monaco);
    if (monacoLoad) return monacoLoad;
    monacoLoad = new Promise((resolve, reject) => {
      const loader = document.createElement("script");
      loader.src = MONACO_VS + "/loader.js";
      loader.onload = () => {
        try {
          window.require.config({ paths: { vs: MONACO_VS } });
          window.require(["vs/editor/editor.main"], () => resolve(window.monaco));
        } catch (e) {
          reject(e);
        }
      };
      loader.onerror = () => reject(new Error("Failed to load Monaco loader"));
      document.body.appendChild(loader);
    });
    return monacoLoad;
  }

  // ── language + theme (port of registerWql) ─────────────────────────────
  function registerWql(monaco) {
    if (!languageRegistered) {
      monaco.languages.register({ id: "wql" });
      languageRegistered = true;
    }

    monaco.languages.setMonarchTokensProvider("wql", {
      ignoreCase: true,
      keywords: WQL_KEYWORDS,
      operatorWords: WQL_OPERATOR_WORDS,
      functions: WQL_FUNCTIONS,
      tokenizer: {
        root: [
          [/--.*$/, "comment"],
          [/\/\*/, "comment", "@blockComment"],
          [/'([^'\\]|\\.)*'/, "string"],
          [/"([^"\\]|\\.)*"/, "string"],
          [/\b\d+(\.\d+)?\b/, "number"],
          [/[{}]/, "delimiter.curly"],
          [
            /[a-zA-Z_]\w*/,
            {
              cases: {
                "@keywords": "keyword",
                "@operatorWords": "operator",
                "@functions": "predefined",
                "@default": "identifier",
              },
            },
          ],
          [/[()[\],.;]/, "delimiter"],
          [/[=<>!]+/, "operator"],
        ],
        blockComment: [
          [/[^/*]+/, "comment"],
          [/\*\//, "comment", "@pop"],
          [/[/*]/, "comment"],
        ],
      },
    });

    // WQL itself has no comments; these are editor-only (stripped before the
    // query is sent). Enables Ctrl+/ (line) and Shift+Alt+A (block) toggling.
    monaco.languages.setLanguageConfiguration("wql", {
      comments: { lineComment: "--", blockComment: ["/*", "*/"] },
      brackets: [["(", ")"], ["[", "]"], ["{", "}"]],
      autoClosingPairs: [
        { open: "(", close: ")" },
        { open: "[", close: "]" },
        { open: "{", close: "}" },
        { open: "'", close: "'" },
      ],
      surroundingPairs: [
        { open: "(", close: ")" },
        { open: "[", close: "]" },
        { open: "{", close: "}" },
        { open: "'", close: "'" },
      ],
    });

    monaco.editor.defineTheme("wql-graphite", {
      base: "vs-dark",
      inherit: true,
      rules: [
        { token: "keyword", foreground: "6cb4ff", fontStyle: "bold" },
        { token: "predefined", foreground: "c79bff" },
        { token: "string", foreground: "7fd39b" },
        { token: "number", foreground: "f0a862" },
        { token: "comment", foreground: "5c6a7d", fontStyle: "italic" },
        { token: "identifier", foreground: "dbe3ec" },
        { token: "operator", foreground: "9fb0c3" },
        { token: "delimiter", foreground: "8a97a8" },
        { token: "delimiter.curly", foreground: "f0a862" },
      ],
      colors: {
        "editor.background": "#12171e",
        "editor.foreground": "#dbe3ec",
        "editorLineNumber.foreground": "#3a4658",
        "editorLineNumber.activeForeground": "#8a97a8",
        "editor.selectionBackground": "#264056",
        "editor.lineHighlightBackground": "#171e28",
        "editorCursor.foreground": "#4aa8ff",
        "editorIndentGuide.background1": "#20293580",
        "editorSuggestWidget.background": "#161c24",
        "editorSuggestWidget.border": "#26313f",
        "editorSuggestWidget.foreground": "#dbe3ec",
        "editorSuggestWidget.selectedBackground": "#243244",
        "editorSuggestWidget.selectedForeground": "#ffffff",
        "editorSuggestWidget.highlightForeground": "#4aa8ff",
        "editorSuggestWidget.focusHighlightForeground": "#6cb4ff",
      },
    });
  }

  // ── metadata for completions (its own tiny cache over /api/*) ───────────
  let monaco_ = null; // set once Monaco has loaded (used by the window.wqlEditor API)
  let dsCache = null;
  let dsInFlight = null;
  const fieldCache = Object.create(null);
  const fieldInFlight = Object.create(null);
  // Same first batch the schema tree loads. Huge BOs (allWorkers has thousands
  // of fields) are 100 fields per Workday call, so fetching everything for
  // completion would mean dozens of slow calls on every new FROM source.
  const FIELD_LIMIT = 2000;

  // The schema tree loads data sources + fields through the C# app; it primes
  // these caches via window.wqlEditor.setDataSources/setFields, so completion is
  // usually instant and never fetches. Only if a source hasn't been loaded yet
  // do we fetch it here — with NO timeout, because the first data-source
  // describe on a large tenant can legitimately take a while, and aborting it
  // just leaves completion with no data.
  function fetchJson(url) {
    return fetch(url, { headers: { Accept: "application/json" } });
  }

  // Each keystroke can ask for completions, so a fetch already in flight is
  // shared rather than started again. A failed fetch is forgotten so the next
  // request retries it.
  function fetchDataSources() {
    if (dsCache) return Promise.resolve(dsCache);
    if (!dsInFlight) {
      dsInFlight = (async () => {
        const res = await fetchJson("/api/workday/datasources");
        if (!res.ok) throw new Error("datasources " + res.status);
        const json = await res.json();
        dsCache = json.dataSources || [];
        return dsCache;
      })().finally(() => { dsInFlight = null; });
    }
    return dsInFlight;
  }

  function fetchFields(id) {
    if (fieldCache[id]) return Promise.resolve(fieldCache[id]);
    if (!fieldInFlight[id]) {
      fieldInFlight[id] = (async () => {
        const res = await fetchJson(
          "/api/workday/datasources/" + encodeURIComponent(id) +
            "/fields?offset=0&limit=" + FIELD_LIMIT,
        );
        if (!res.ok) throw new Error("fields " + res.status);
        const json = await res.json();
        // The tree may have primed a fuller list while this was in flight.
        if (!fieldCache[id]) fieldCache[id] = json.fields || [];
        return fieldCache[id];
      })().finally(() => { delete fieldInFlight[id]; });
    }
    return fieldInFlight[id];
  }

  function aliasesInFrom(text) {
    const found = new Set();
    const re = /\bfrom\s+([a-zA-Z_]\w*)/gi;
    let m;
    while ((m = re.exec(text))) found.add(m[1]);
    return [...found];
  }

  // ── statements: the one under the cursor is what Run executes ──────────
  // A file can hold several queries, each ended by ";". A ";" inside a
  // 'string', a "quoted name" or a -- / /* */ comment doesn't end anything.
  // Returns [{ start, end }] offsets; `end` is just after the ";" (or the end
  // of the text), so the segments cover the whole text with no gaps.
  function splitStatements(text) {
    const segs = [];
    let start = 0;
    let i = 0;
    const n = text.length;
    while (i < n) {
      const c = text[i];
      const next = i + 1 < n ? text[i + 1] : "";
      if (c === "'" || c === '"') {
        // quoted: '' / "" inside is an escaped quote
        i++;
        while (i < n) {
          if (text[i] === c) {
            if (i + 1 < n && text[i + 1] === c) { i += 2; continue; }
            break;
          }
          i++;
        }
        i++;
        continue;
      }
      if (c === "-" && next === "-") {
        while (i < n && text[i] !== "\n") i++;
        continue;
      }
      if (c === "/" && next === "*") {
        i += 2;
        while (i < n && !(text[i] === "*" && text[i + 1] === "/")) i++;
        i += 2;
        continue;
      }
      if (c === ";") {
        segs.push({ start: start, end: i + 1 });
        start = i + 1;
      }
      i++;
    }
    if (start < n || segs.length === 0) segs.push({ start: start, end: n });
    return segs;
  }

  // The query text within a segment: past leading whitespace and comments
  // (a header comment belongs to the query below it), and before the ending
  // ";" and trailing whitespace. codeStart === codeEnd means "nothing to run".
  function codeBounds(text, seg) {
    let s = seg.start;
    while (s < seg.end) {
      if (/\s/.test(text[s])) { s++; continue; }
      if (text[s] === "-" && text[s + 1] === "-") {
        while (s < seg.end && text[s] !== "\n") s++;
        continue;
      }
      if (text[s] === "/" && text[s + 1] === "*") {
        const close = text.indexOf("*/", s + 2);
        s = close < 0 || close + 2 > seg.end ? seg.end : close + 2;
        continue;
      }
      break;
    }
    // End just after the last character of query text, ignoring the ending
    // ";" and any comments after the query (so a trailing "-- note" stays out).
    let e = s;
    let i = s;
    const stop = text[seg.end - 1] === ";" ? seg.end - 1 : seg.end;
    while (i < stop) {
      const c = text[i];
      if (c === "'" || c === '"') {
        i++;
        while (i < stop) {
          if (text[i] === c) {
            if (text[i + 1] === c) { i += 2; continue; }
            break;
          }
          i++;
        }
        i++;
        e = Math.min(i, stop);
        continue;
      }
      if (c === "-" && text[i + 1] === "-") {
        while (i < stop && text[i] !== "\n") i++;
        continue;
      }
      if (c === "/" && text[i + 1] === "*") {
        const close = text.indexOf("*/", i + 2);
        i = close < 0 || close + 2 > stop ? stop : close + 2;
        continue;
      }
      if (!/\s/.test(c)) e = i + 1;
      i++;
    }
    return { codeStart: s, codeEnd: Math.max(s, e) };
  }

  // Which statement the cursor means:
  //  - the segment containing it (so a header comment runs the query below);
  //  - just after a ";", or on a comment after it on the same line, the
  //    query that ";" ended;
  //  - on blank space with no query (e.g. after the last one), the nearest
  //    query before it, else after it.
  function statementAt(text, offset) {
    const segs = splitStatements(text).map(function (g) {
      return Object.assign(g, codeBounds(text, g));
    });
    let k = segs.findIndex(function (g) { return offset >= g.start && offset < g.end; });
    if (k < 0) k = segs.length - 1;
    const lead = text.slice(segs[k].start, offset);
    // Still on the ";" line, with only spaces or a trailing comment before the
    // cursor: that belongs to the query the ";" ended.
    // (Not when a query follows the cursor on that line: "a; |SELECT 2" runs SELECT 2.)
    const eol = text.indexOf("\n", offset);
    const rest = text.slice(offset, eol < 0 ? text.length : eol);
    // A cursor touching the ";" always means the query it ends.
    if (k > 0 && text[segs[k].start - 1] === ";"
        && (offset === segs[k].start
            || (!lead.includes("\n")
                && /^[ \t]*(--.*|\/\*.*)?$/.test(lead)
                && (/^[ \t]*(--.*|\/\*.*)?$/.test(rest) || /(--|\/\*)/.test(lead))))) k--;
    const has = function (g) { return g.codeEnd > g.codeStart; };
    if (!has(segs[k])) {
      let j = k - 1;
      while (j >= 0 && !has(segs[j])) j--;
      if (j < 0) { j = k + 1; while (j < segs.length && !has(segs[j])) j++; }
      if (j >= 0 && j < segs.length) k = j;
    }
    return segs[k];
  }

  // Text to add as new statement(s): trimmed, and ended with ";" if its last
  // statement has none (added right after the query, so a trailing comment
  // can't swallow it).
  function terminate(text) {
    let t = (text || "").replace(/\s+$/, "");
    const segs = splitStatements(t).map(function (g) { return Object.assign(g, codeBounds(t, g)); });
    const last = segs[segs.length - 1];
    if (last && last.codeEnd > last.codeStart && t[last.end - 1] !== ";")
      t = t.slice(0, last.codeEnd) + ";" + t.slice(last.codeEnd);
    return t;
  }

  // Where a new statement goes: after the statement the cursor is in — after its
  // ";" and any comment on that line — adding the ";" (at semiAt) if it has
  // none. Returns { at, replace, prefix, semiAt }: replace `replace` characters
  // at offset `at` (spaces before a query sharing the line) with
  // `prefix + newText`, and insert ";" at semiAt (-1 = not needed).
  function insertionPoint(text, offset) {
    const g = statementAt(text, offset);
    if (!g || g.codeEnd <= g.codeStart)
      return { at: text.length, replace: 0, prefix: text.trim() ? "\n\n" : "", semiAt: -1 };
    const semiAt = text[g.end - 1] === ";" ? -1 : g.codeEnd;
    const from = semiAt < 0 ? g.end : g.codeEnd;
    const eol = text.indexOf("\n", from);
    const lineEnd = eol < 0 ? text.length : eol;
    const rest = text.slice(from, lineEnd);
    // Only spaces or a comment left on the line: go to the end of the line.
    if (/^[ \t]*(--.*|\/\*.*\*\/[ \t]*)?$/.test(rest))
      return { at: lineEnd, replace: 0, prefix: "\n\n", semiAt: semiAt };
    // Another query follows on the same line: insert before it, eating the
    // spaces in between.
    const ws = /^[ \t]*/.exec(rest)[0].length;
    return { at: from, replace: ws, prefix: "\n\n", semiAt: semiAt };
  }

  // Blank line between an inserted statement and whatever follows it.
  function separatorAfter(text, at) {
    const after = text.slice(at);
    if (/^\S/.test(after)) return "\n\n";
    if (!after.trim()) return "";
    if (after.startsWith("\n\n") || after.startsWith("\r\n\r\n")) return "";
    if (after.startsWith("\n") || after.startsWith("\r\n")) return "\n";
    return "\n\n";
  }

  // Briefly light up the text that's about to run.
  function flashRange(editor, monaco, range) {
    if (!range || range.isEmpty()) return;
    const decos = editor.deltaDecorations([], [
      { range: range, options: { className: "wql-run-flash", stickiness: 1 } },
    ]);
    setTimeout(function () { try { editor.deltaDecorations(decos, []); } catch { /* disposed */ } }, 700);
  }

  // ── completion provider (port of createCompletionProvider) ──────────────
  function createCompletionProvider(monaco) {
    if (completionRegistered) return;
    completionRegistered = true;

    monaco.languages.registerCompletionItemProvider("wql", {
      triggerCharacters: [" ", ".", ",", "{"],
      async provideCompletionItems(model, position) {
        const K = monaco.languages.CompletionItemKind;
        const word = model.getWordUntilPosition(position);
        const range = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn,
        };
        const textUntil = model
          .getValueInRange({
            startLineNumber: 1,
            startColumn: 1,
            endLineNumber: position.lineNumber,
            endColumn: position.column,
          })
          .toLowerCase();

        const suggestions = [];

        let dataSources = [];
        try {
          dataSources = await fetchDataSources();
        } catch {
          /* offline / not authed — fall back to keywords only */
        }

        // Right after FROM → only data sources make sense.
        if (/\bfrom\s+[a-zA-Z_]*$/i.test(textUntil)) {
          for (const ds of dataSources) {
            suggestions.push({
              label: ds.alias,
              kind: K.Class,
              detail: ds.descriptor,
              insertText: ds.alias,
              range,
            });
          }
          return { suggestions };
        }

        // Otherwise: fields of the queried source(s) FIRST, fetched on demand.
        const aliasToId = new Map(
          dataSources.map((d) => [d.alias.toLowerCase(), d.id]),
        );
        const activeAliases = aliasesInFrom(model.getValue());
        const seenField = new Set();
        for (const alias of activeAliases) {
          const id = aliasToId.get(alias.toLowerCase());
          if (!id) continue;
          let fields = [];
          try {
            fields = await fetchFields(id);
          } catch {
            continue;
          }
          for (const f of fields) {
            if (seenField.has(f.alias)) continue;
            seenField.add(f.alias);
            const isRef =
              f.type === "Single Instance" || f.type === "Multi Instance";
            suggestions.push({
              label: f.alias,
              kind: isRef ? K.Reference : K.Field,
              detail: f.type + " · " + f.descriptor,
              insertText: f.alias,
              sortText: "0" + f.alias,
              range,
            });
          }
        }

        for (const fn of WQL_FUNCTIONS) {
          suggestions.push({
            label: fn, kind: K.Function, insertText: fn + "()",
            sortText: "1" + fn, range,
          });
        }
        for (const ds of dataSources) {
          suggestions.push({
            label: ds.alias, kind: K.Class, detail: ds.descriptor,
            insertText: ds.alias, sortText: "2" + ds.alias, range,
          });
        }
        for (const kw of WQL_KEYWORDS) {
          suggestions.push({
            label: kw, kind: K.Keyword, insertText: kw,
            sortText: "3" + kw, range,
          });
        }
        for (const op of WQL_OPERATORS) {
          suggestions.push({
            label: op, kind: K.Operator, insertText: op,
            sortText: "3" + op, range,
          });
        }
        return { suggestions };
      },
    });
  }

  // ── public API used from Blazor (Services/Interop.cs) ───────────────────
  window.wqlEditor = {
    // The C# app primes completion metadata with what the schema tree already
    // loaded, so autocomplete is instant and doesn't re-fetch (matches how the
    // original app shared one cache between the tree and the editor).
    setDataSources: function (list) {
      if (Array.isArray(list)) dsCache = list;
    },
    setFields: function (dataSourceId, list) {
      if (dataSourceId && Array.isArray(list)) fieldCache[dataSourceId] = list;
    },
    // Returns true once Monaco is loaded and the editor has mounted. Throws /
    // rejects if the CDN can't be reached, so the C# side falls back to a
    // plain <textarea>.
    create: async function (elementId, initialValue, dotnetRef) {
      const monaco = await loadMonaco();
      monaco_ = monaco;
      registerWql(monaco);
      createCompletionProvider(monaco);
      const el = document.getElementById(elementId);
      if (!el) throw new Error("editor host not found: " + elementId);
      if (editors[elementId]) {
        editors[elementId].dispose();
        delete editors[elementId];
      }
      const editor = monaco.editor.create(el, {
        value: initialValue || "",
        language: "wql",
        theme: "wql-graphite",
        fontSize: 13,
        fontFamily:
          "'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, monospace",
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        lineNumbers: "on",
        renderLineHighlight: "line",
        padding: { top: 12, bottom: 12 },
        automaticLayout: true,
        tabSize: 2,
        wordWrap: "on",
        suggestOnTriggerCharacters: true,
        // Our schema/keyword completions run on the main thread; word-based
        // suggestions run in the web worker. Turning them off means the suggest
        // widget never waits on the worker, so completions can't hang on it.
        wordBasedSuggestions: "off",
      });
      editors[elementId] = editor;

      editor.onDidChangeModelContent(() => {
        dotnetRef.invokeMethodAsync("OnValueChanged", editor.getValue());
      });
      // Ctrl/Cmd+Enter runs the query — the muscle memory every query tool has.
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => {
        dotnetRef.invokeMethodAsync("OnRunInvoked");
      });
      return true;
    },
    getValue: function (elementId) {
      const e = editors[elementId];
      return e ? e.getValue() : "";
    },
    // What Run should execute: the selection if there is one, otherwise the
    // statement around the cursor. Returns the text, plus the offsets of the
    // part a column sort / drill-down may later rewrite in place (so other
    // statements in the editor are left alone). The chosen text flashes.
    getRunText: function (elementId) {
      const e = editors[elementId];
      if (!e) return null;
      const model = e.getModel();
      const text = model.getValue();
      const sel = e.getSelection();
      if (sel && !sel.isEmpty()) {
        const a = model.getOffsetAt(sel.getStartPosition());
        const b = model.getOffsetAt(sel.getEndPosition());
        flashRange(e, monaco_, sel);
        return { text: text.slice(a, b), codeStart: a, codeEnd: b };
      }
      const offset = model.getOffsetAt(e.getPosition());
      const g = statementAt(text, offset);
      if (!g || g.codeEnd <= g.codeStart) return { text: "", codeStart: 0, codeEnd: 0 };
      const range = monaco_.Range.fromPositions(
        model.getPositionAt(g.codeStart), model.getPositionAt(g.codeEnd));
      flashRange(e, monaco_, range);
      return { text: text.slice(g.start, g.end), codeStart: g.codeStart, codeEnd: g.codeEnd };
    },
    // Add text as a new statement after the one the cursor is in (or fill the
    // editor if it's empty), as one undoable edit. Puts the cursor `cursor`
    // characters into the new text, scrolls it into view and returns its offset.
    insertStatement: function (elementId, value, cursor) {
      const e = editors[elementId];
      if (!e) return -1;
      const model = e.getModel();
      const doc = model.getValue();
      const add = terminate(value);
      const edits = [];
      let start;
      if (!doc.trim()) {
        edits.push({ range: model.getFullModelRange(), text: add });
        start = 0;
      } else {
        const p = insertionPoint(doc, model.getOffsetAt(e.getPosition()));
        const suffix = separatorAfter(doc, p.at + p.replace);
        if (p.semiAt >= 0) {
          const sp = model.getPositionAt(p.semiAt);
          edits.push({ range: monaco_.Range.fromPositions(sp, sp), text: ";" });
        }
        edits.push({
          range: monaco_.Range.fromPositions(model.getPositionAt(p.at), model.getPositionAt(p.at + p.replace)),
          text: p.prefix + add + suffix,
        });
        // Offsets after the edits: the ";" (if any) comes before the insertion.
        start = p.at + (p.semiAt >= 0 ? 1 : 0) + p.prefix.length;
      }
      e.pushUndoStop();
      e.executeEdits("insert-statement", edits);
      e.pushUndoStop();
      const pos = model.getPositionAt(start + Math.max(0, Math.min(cursor || 0, add.length)));
      e.setPosition(pos);
      e.revealPositionInCenterIfOutsideViewport(pos);
      e.focus();
      return start;
    },
    // Replace text between two offsets as one undoable edit (Ctrl+Z restores).
    replaceRange: function (elementId, start, end, value) {
      const e = editors[elementId];
      if (!e) return;
      const model = e.getModel();
      const range = monaco_.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(end));
      e.pushUndoStop();
      e.executeEdits("statement-edit", [{ range: range, text: value || "", forceMoveMarkers: true }]);
      e.pushUndoStop();
    },
    setValue: function (elementId, value) {
      const e = editors[elementId];
      if (e && e.getValue() !== value) e.setValue(value || "");
    },
    setPosition: function (elementId, line, column) {
      const e = editors[elementId];
      if (e) {
        e.setPosition({ lineNumber: line, column: column });
        e.focus();
      }
    },
    insertAtCursor: function (elementId, text) {
      const e = editors[elementId];
      if (!e) return;
      const sel = e.getSelection();
      e.executeEdits("schema-insert", [
        { range: sel, text: text, forceMoveMarkers: true },
      ]);
      e.focus();
    },
    focus: function (elementId) {
      const e = editors[elementId];
      if (e) e.focus();
    },
    dispose: function (elementId) {
      const e = editors[elementId];
      if (e) {
        e.dispose();
        delete editors[elementId];
      }
    },
  };
})();

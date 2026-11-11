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
  let dsCache = null;
  const fieldCache = Object.create(null);

  // The schema tree loads data sources + fields through the C# app; it primes
  // these caches via window.wqlEditor.setDataSources/setFields, so completion is
  // usually instant and never fetches. Only if a source hasn't been loaded yet
  // do we fetch it here — with NO timeout, because the first data-source
  // describe on a large tenant can legitimately take a while, and aborting it
  // just leaves completion with no data.
  function fetchJson(url) {
    return fetch(url, { headers: { Accept: "application/json" } });
  }

  async function fetchDataSources() {
    if (dsCache) return dsCache;
    const res = await fetchJson("/api/workday/datasources");
    if (!res.ok) throw new Error("datasources " + res.status);
    const json = await res.json();
    dsCache = json.dataSources || [];
    return dsCache;
  }

  async function fetchFields(id) {
    if (fieldCache[id]) return fieldCache[id];
    const res = await fetchJson(
      "/api/workday/datasources/" + encodeURIComponent(id) + "/fields",
    );
    if (!res.ok) throw new Error("fields " + res.status);
    const json = await res.json();
    fieldCache[id] = json.fields || [];
    return fieldCache[id];
  }

  function aliasesInFrom(text) {
    const found = new Set();
    const re = /\bfrom\s+([a-zA-Z_]\w*)/gi;
    let m;
    while ((m = re.exec(text))) found.add(m[1]);
    return [...found];
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

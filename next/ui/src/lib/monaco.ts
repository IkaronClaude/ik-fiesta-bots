// Monaco setup: local workers (no CDN), Lua + YAML highlighting, and the Lua language services fed from the
// API manifest (C# helpers: completion, hover docs, signature help) and from the user's own libraries.
import 'monaco-editor/esm/vs/editor/edcore.main.js';
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api';
import 'monaco-editor/esm/vs/basic-languages/lua/lua.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/yaml/yaml.contribution.js';
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import type { ApiManifest, ManifestFunction } from '../api/types';
import { moduleExports } from './luaLint';

(self as any).MonacoEnvironment = { getWorker: () => new EditorWorker() };

export { monaco };

interface LuaContext {
  manifest: ApiManifest | null;
  /** library path -> source (user/<owner>/..., lib/..., brain/<brain>/...) */
  libraries: Record<string, string>;
}
const ctx: LuaContext = { manifest: null, libraries: {} };
export function setLuaManifest(m: ApiManifest | null) { ctx.manifest = m; }
export function setLuaLibraries(libs: Record<string, string>) { ctx.libraries = { ...ctx.libraries, ...libs }; }

const BUILTIN_GLOBALS = ['bb', 'require', 'pairs', 'ipairs', 'type', 'tostring', 'tonumber', 'math', 'string', 'table', 'select', 'error', 'pcall', 'next'];

function costTag(f: ManifestFunction) { return `${f.cost}${f.alloc && f.alloc !== '0' ? `, alloc ${f.alloc}` : ''}`; }
function fnDoc(f: ManifestFunction): monaco.IMarkdownString {
  const lines = [`\`\`\`lua\n${f.signature}${f.returns ? ' -> ' + f.returns : ''}\n\`\`\``, `*${costTag(f)}*${f.resource ? ` · claims **${f.resource}**` : ''}${f.aliasOf ? ` · alias of \`${f.aliasOf}\`` : ''}`];
  if (f.doc) lines.push(f.doc);
  lines.push('_C# helper (API manifest)_');
  return { value: lines.join('\n\n') };
}

/** Map a local alias (`local my = require "user/x/y"`) to the library path. */
function requireAliases(text: string): Map<string, string> {
  const m = new Map<string, string>();
  for (const x of text.matchAll(/local\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*require\s*\(?\s*["']([^"']+)["']/g)) m.set(x[1], x[2]);
  return m;
}

/** Brain-private module short names that state functions use (`survival.recoverTick`). */
let brainModuleShortNames: Record<string, string> = {};
export function setBrainModules(paths: string[]) {
  brainModuleShortNames = Object.fromEntries(paths.map(p => [p.split('/').pop()!, p]));
}

function libExports(path: string) { const src = ctx.libraries[path]; return src ? moduleExports(src) : []; }

let registered = false;
export function registerLua() {
  if (registered) return; registered = true;

  monaco.editor.defineTheme('botui-dark', {
    base: 'vs-dark', inherit: true,
    rules: [{ token: 'comment', foreground: '6f7f86', fontStyle: 'italic' }, { token: 'keyword', foreground: 'd7a1ff' }, { token: 'string', foreground: 'c8d98a' }, { token: 'number', foreground: 'f2b36d' }],
    colors: { 'editor.background': '#12181b', 'editorGutter.background': '#12181b', 'editor.lineHighlightBackground': '#1a2226', 'editorLineNumber.foreground': '#3e4d53' },
  });

  monaco.languages.registerCompletionItemProvider('lua', {
    triggerCharacters: ['.', '"', "'", '/'],
    provideCompletionItems(model, position) {
      const line = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
      const word = model.getWordUntilPosition(position);
      const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
      const K = monaco.languages.CompletionItemKind;
      const m = ctx.manifest;
      const suggestions: monaco.languages.CompletionItem[] = [];
      // require "path
      const req = /require\s*\(?\s*["']([^"']*)$/.exec(line);
      if (req) {
        const start = position.column - req[1].length;
        for (const p of Object.keys(ctx.libraries)) suggestions.push({ label: p, kind: K.Module, insertText: p, range: new monaco.Range(position.lineNumber, start, position.lineNumber, position.column), detail: 'library' });
        return { suggestions };
      }
      const member = /([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_0-9]*)$/.exec(line);
      if (member) {
        const base = member[1];
        if (m) {
          for (const f of m.functions.filter(f => f.module === base)) {
            suggestions.push({ label: f.name, kind: K.Function, detail: `${f.signature} -> ${f.returns || '-'}`, documentation: fnDoc(f),
              insertText: f.params && f.params.length ? `${f.name}($0)` : `${f.name}()`, insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range,
              sortText: '0' + f.name });
          }
          for (const [c, v] of Object.entries(m.constants?.[base] || {})) suggestions.push({ label: c, kind: K.Constant, detail: `${base}.${c} = ${v}`, insertText: c, range, sortText: '1' + c });
        }
        if (base === 'bb') for (const s of ['bot', 'layer', 'state']) suggestions.push({ label: s, kind: K.Variable, insertText: s, range, detail: `blackboard ${s} scope` });
        const alias = requireAliases(model.getValue()).get(base);
        const libPath = alias || brainModuleShortNames[base];
        if (libPath) for (const e of libExports(libPath)) suggestions.push({ label: e.name, kind: K.Method, detail: `${libPath}: ${e.name}(${e.params})`,
          documentation: { value: (e.doc ? e.doc + '\n\n' : '') + `_user helper (${libPath})_` }, insertText: e.params ? `${e.name}($0)` : `${e.name}()`,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range });
        return { suggestions };
      }
      if (m) {
        const mods = [...new Set(m.functions.map(f => f.module))].filter(x => x !== '_G');
        for (const mod of mods) suggestions.push({ label: mod, kind: K.Module, insertText: mod, range, detail: `C# module (${m.functions.filter(f => f.module === mod).length} functions)` });
        for (const f of m.functions.filter(f => f.module === '_G')) suggestions.push({ label: f.name, kind: K.Function, insertText: `${f.name}($0)`, insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range, detail: f.signature, documentation: fnDoc(f) });
      }
      for (const g of BUILTIN_GLOBALS) suggestions.push({ label: g, kind: K.Keyword, insertText: g, range, sortText: '9' + g });
      for (const [short, p] of Object.entries(brainModuleShortNames)) suggestions.push({ label: short, kind: K.Module, insertText: short, range, detail: p });
      return { suggestions };
    },
  });

  monaco.languages.registerHoverProvider('lua', {
    provideHover(model, position) {
      const line = model.getLineContent(position.lineNumber);
      const word = model.getWordAtPosition(position); if (!word) return null;
      const before = line.slice(0, word.startColumn - 1);
      const mm = /([A-Za-z_][A-Za-z0-9_]*)\.$/.exec(before);
      const m = ctx.manifest;
      const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
      if (mm && m) {
        const f = m.functions.find(f => f.module === mm[1] && f.name === word.word);
        if (f) return { range, contents: [fnDoc(f)] };
        const c = m.constants?.[mm[1]]?.[word.word];
        if (c !== undefined) return { range, contents: [{ value: `\`${mm[1]}.${word.word}\` = ${c}` }] };
        const libPath = requireAliases(model.getValue()).get(mm[1]) || brainModuleShortNames[mm[1]];
        const e = libPath && libExports(libPath).find(x => x.name === word.word);
        if (e) return { range, contents: [{ value: `\`\`\`lua\n${word.word}(${e.params})\n\`\`\`` }, { value: (e.doc || '') + `\n\n_user helper (${libPath})_` }] };
      }
      if (m) {
        const g = m.functions.find(f => f.module === '_G' && f.name === word.word);
        if (g) return { range, contents: [fnDoc(g)] };
        const modFns = m.functions.filter(f => f.module === word.word);
        if (modFns.length) return { range, contents: [{ value: `**${word.word}**: C# module, ${modFns.length} functions` }] };
      }
      return null;
    },
  });

  monaco.languages.registerSignatureHelpProvider('lua', {
    signatureHelpTriggerCharacters: ['(', ','],
    provideSignatureHelp(model, position) {
      const text = model.getValueInRange(new monaco.Range(position.lineNumber, 1, position.lineNumber, position.column));
      let depth = 0, commas = 0, i = text.length - 1;
      for (; i >= 0; i--) {
        const ch = text[i];
        if (ch === ')') depth++;
        else if (ch === '(') { if (depth === 0) break; depth--; }
        else if (ch === ',' && depth === 0) commas++;
      }
      if (i < 0) return null;
      const name = /([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*$/.exec(text.slice(0, i))?.[1];
      if (!name || !ctx.manifest) return null;
      const [a, b] = name.includes('.') ? name.split('.') : ['_G', name];
      const f = ctx.manifest.functions.find(f => f.module === a && f.name === b);
      if (!f) return null;
      return { value: { activeSignature: 0, activeParameter: commas,
        signatures: [{ label: f.signature, documentation: f.doc, parameters: (f.params || []).map(p => ({ label: p })) }] }, dispose() {} };
    },
  });
}

import { useEffect, useRef } from 'react';
import { monaco, registerLua } from '../lib/monaco';
import { lintLuaSource } from '../lib/luaLint';
import { useApp } from '../lib/app';
import type { LintResult } from '../api/types';

interface Props {
  value: string;
  onChange?: (v: string) => void;
  language?: 'lua' | 'yaml' | 'plaintext';
  /** Lua expression (`when`, `progress`), linted as `return <expr>` */
  expression?: boolean;
  height?: number | string;
  readOnly?: boolean;
  /** extra markers (host lint results for this source) */
  markers?: LintResult[];
  /** called with client-side issues after each lint pass */
  onIssues?: (n: { errors: number; warnings: number }) => void;
  compact?: boolean;
  ariaLabel?: string;
  /** reveal + highlight this line */
  revealLine?: number;
}

export function CodeEditor({ value, onChange, language = 'lua', expression, height = 300, readOnly, markers, onIssues, compact, ariaLabel, revealLine }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const ed = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const changeRef = useRef(onChange); changeRef.current = onChange;
  const issuesRef = useRef(onIssues); issuesRef.current = onIssues;
  const markersRef = useRef(markers); markersRef.current = markers;
  const { manifest } = useApp();
  const manifestRef = useRef(manifest); manifestRef.current = manifest;
  const settingValue = useRef(false);

  const lint = () => {
    const e = ed.current; if (!e) return;
    const model = e.getModel(); if (!model) return;
    const ms: monaco.editor.IMarkerData[] = [];
    if (language === 'lua') {
      for (const i of lintLuaSource(model.getValue(), manifestRef.current, { expression })) {
        ms.push({ severity: i.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning, message: `${i.message} (${i.code})`,
          startLineNumber: i.line, endLineNumber: i.line, startColumn: i.column + 1, endColumn: i.endColumn ? i.endColumn + 1 : model.getLineMaxColumn(i.line) });
      }
    }
    for (const l of markersRef.current || []) {
      const line = Math.max(1, Math.min(model.getLineCount(), l.line || 1));
      ms.push({ severity: l.severity === 'error' ? monaco.MarkerSeverity.Error : l.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Info,
        message: `${l.message} (${l.code}, host lint)`, startLineNumber: line, endLineNumber: line, startColumn: (l.column ?? 0) + 1, endColumn: model.getLineMaxColumn(line) });
    }
    monaco.editor.setModelMarkers(model, 'botui', ms);
    issuesRef.current?.({ errors: ms.filter(m => m.severity === monaco.MarkerSeverity.Error).length, warnings: ms.filter(m => m.severity === monaco.MarkerSeverity.Warning).length });
  };

  useEffect(() => {
    registerLua();
    const e = monaco.editor.create(host.current!, {
      value, language, theme: 'botui-dark', automaticLayout: true, readOnly, minimap: { enabled: false },
      fontSize: 13, fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace", scrollBeyondLastLine: false, tabSize: 2,
      lineNumbers: compact ? 'off' : 'on', glyphMargin: !compact, folding: !compact, lineDecorationsWidth: compact ? 4 : 10,
      renderLineHighlight: compact ? 'none' : 'line', wordWrap: compact ? 'on' : 'off', ariaLabel,
      scrollbar: compact ? { vertical: 'auto', horizontal: 'hidden', alwaysConsumeMouseWheel: false } : { alwaysConsumeMouseWheel: false },
      overviewRulerLanes: compact ? 0 : 3, fixedOverflowWidgets: true, quickSuggestions: { other: true, strings: true, comments: false },
    });
    ed.current = e;
    let t: number | undefined;
    const sub = e.onDidChangeModelContent(() => {
      if (!settingValue.current) changeRef.current?.(e.getValue());
      window.clearTimeout(t); t = window.setTimeout(lint, 250);
    });
    lint();
    return () => { sub.dispose(); window.clearTimeout(t); e.getModel()?.dispose(); e.dispose(); ed.current = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [language, expression]);

  useEffect(() => {
    const e = ed.current; if (!e) return;
    if (e.getValue() !== value) {
      settingValue.current = true;
      const sel = e.getSelection();
      e.executeEdits('external', [{ range: e.getModel()!.getFullModelRange(), text: value }]);
      e.pushUndoStop();
      if (sel) e.setSelection(sel);
      settingValue.current = false;
    }
  }, [value]);
  useEffect(() => { ed.current?.updateOptions({ readOnly }); }, [readOnly]);
  useEffect(() => { lint(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [markers, manifest]);
  useEffect(() => {
    if (revealLine && ed.current) {
      ed.current.revealLineInCenter(revealLine);
      ed.current.setPosition({ lineNumber: revealLine, column: 1 });
      ed.current.focus();
    }
  }, [revealLine]);

  return <div className={'code-editor' + (compact ? ' compact' : '')} ref={host} style={{ height }} />;
}

export function DiffView({ original, modified, language = 'lua', height = 420 }: { original: string; modified: string; language?: string; height?: number | string }) {
  const host = useRef<HTMLDivElement>(null);
  const ed = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  useEffect(() => {
    registerLua();
    const e = monaco.editor.createDiffEditor(host.current!, { theme: 'botui-dark', automaticLayout: true, readOnly: true, renderSideBySide: true,
      minimap: { enabled: false }, fontSize: 12, scrollBeyondLastLine: false, originalEditable: false, renderOverviewRuler: false });
    ed.current = e;
    return () => { const m = e.getModel(); e.dispose(); m?.original.dispose(); m?.modified.dispose(); };
  }, []);
  useEffect(() => {
    const e = ed.current; if (!e) return;
    const old = e.getModel();
    e.setModel({ original: monaco.editor.createModel(original, language), modified: monaco.editor.createModel(modified, language) });
    old?.original.dispose(); old?.modified.dispose();
  }, [original, modified, language]);
  return <div className="code-editor" ref={host} style={{ height }} />;
}

declare module 'luaparse' {
  const luaparse: { parse(code: string, options?: Record<string, unknown>): any };
  export default luaparse;
}
declare module 'monaco-editor/esm/vs/editor/edcore.main.js';
declare module 'monaco-editor/esm/vs/basic-languages/lua/lua.contribution.js';
declare module 'monaco-editor/esm/vs/basic-languages/yaml/yaml.contribution.js';

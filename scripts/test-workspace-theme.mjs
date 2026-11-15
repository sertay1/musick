import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from '../src/MusicBot.Web/clientapp/node_modules/typescript/lib/typescript.js';

const source = readFileSync(new URL('../src/MusicBot.Web/clientapp/src/hooks/useTheme.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;

function mount(saved, dark) {
  let state, effects = [], cleanup = [];
  const storage = new Map(saved ? [['musicbot-theme', saved]] : []);
  const listeners = new Set(), calls = [];
  const media = { matches: dark, addEventListener: (_, fn) => listeners.add(fn), removeEventListener: (_, fn) => listeners.delete(fn) };
  const attributes = {};
  const context = {
    exports: {}, window: { matchMedia: () => media },
    document: { documentElement: { setAttribute: (key, value) => attributes[key] = value } },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    require: id => id === 'react' ? {
      useState: init => { if (state === undefined) state = init(); return [state, value => state = value]; },
      useEffect: fn => effects.push(fn),
    } : { api: { setDesktopTheme: theme => { calls.push(theme); return Promise.resolve(); } } },
  };
  vm.runInNewContext(compiled, context);
  const render = () => {
    cleanup.forEach(fn => fn?.()); effects = [];
    const result = context.exports.useTheme();
    cleanup = effects.map(fn => fn());
    return result;
  };
  return { render, attributes, calls, storage, system: value => { media.matches = value; [...listeners].forEach(fn => fn()); }, unmount: () => cleanup.forEach(fn => fn?.()), listeners };
}

const auto = mount(null, true);
assert.equal(auto.render().theme, 'dark');
auto.system(false);
assert.equal(auto.render().theme, 'light');
assert.equal(auto.attributes['data-theme'], 'light');
assert.equal(auto.storage.size, 0, 'OS preference must not become a saved user override');
auto.render().toggle();
assert.equal(auto.render().theme, 'dark');
assert.equal(auto.storage.get('musicbot-theme'), 'dark');
auto.system(false);
assert.equal(auto.render().theme, 'dark', 'Explicit selection wins over OS');
auto.unmount();
assert.equal(auto.listeners.size, 0);
assert.equal(mount('light', true).render().theme, 'light');
const invalid = mount('obsolete', true);
invalid.render(); invalid.system(false);
assert.equal(invalid.render().theme, 'light', 'Invalid old storage must not disable OS changes');
console.log('Theme checks passed: system changes, persistence, explicit choice, invalid storage and listener cleanup.');

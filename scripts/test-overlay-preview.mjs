import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const origin = 'http://127.0.0.1:3050';
const parent = {};
const listeners = {};
const artworkProperties = new Map();
const context = vm.createContext({
  URL, URLSearchParams,
  location: { search: '?preview=1', href: origin + '/overlays/now-playing/index.html', origin },
  parent,
  document: { documentElement: { style: { setProperty: (key,value) => artworkProperties.set(key,value), removeProperty: key => artworkProperties.delete(key) } } },
  window: { MusickOverlayPreferences: { keepLast: true }, addEventListener: (name, callback) => { listeners[name] = callback; } },
});
const source = await readFile(new URL('../src/MusicBot.Web/clientapp/public/overlays/shared/signalr-client.js', import.meta.url), 'utf8');
const Connection = vm.runInContext(source + '\nOverlayConnection', context);
const connection = Object.create(Connection.prototype);
connection.handlers = {};
connection._lastNowPlaying = null;
connection._showBanner = () => {};
let snapshot;
connection.on('nowplaying:updated', () => {});
connection.on('queue:updated', value => { snapshot = value; });
await connection.start();
const demoTitle = snapshot.nowPlaying.item.song.title;
const song = { spotifyUri: 'spotify:track:current', title: 'Live Spotify', artist: 'Artist', durationMs: 200000 };
const next = { id: 'next', song: { ...song, spotifyUri: 'youtube:next', title: 'Live YouTube' }, requestedBy: 'viewer', platform: 'kick' };
const send = state => listeners.message({ origin, source: parent, data: { type: 'musick:overlay-preview-state', state } });
send({ nowPlaying: { spotifyTrack: song, item: null, isPlaying: true, progressMs: 123000 }, upcoming: [next] });
assert.equal(snapshot.nowPlaying.spotifyTrack.title, 'Live Spotify');
assert.equal(snapshot.nowPlaying.progressMs, 123000);
assert.equal(snapshot.upcoming[0].requestedBy, 'viewer');
send({ nowPlaying: { spotifyTrack: null, item: next, isPlaying: false, progressMs: 45000 }, upcoming: [] });
assert.equal(snapshot.nowPlaying.item.song.title, 'Live YouTube');
assert.equal(snapshot.nowPlaying.isPlaying, false);
send({ nowPlaying: { spotifyTrack: null, item: null, isPlaying: false, progressMs: 0 }, upcoming: [next] });
assert.equal(snapshot.nowPlaying.item, null, 'A queue-only snapshot must not inherit a sample or previous song');
assert.equal(snapshot.nowPlaying.spotifyTrack, null);
assert.equal(snapshot.upcoming.length, 1);
send(null);
assert.equal(snapshot.nowPlaying.item.song.title, demoTitle, 'No live state returns to sample data');
const unchanged = snapshot;
listeners.message({ origin: 'https://foreign.example', source: parent, data: { type: 'musick:overlay-preview-state', state: {} } });
listeners.message({ origin, source: {}, data: { type: 'musick:overlay-preview-state', state: {} } });
assert.equal(snapshot, unchanged, 'Only the same-origin parent may replace preview data');
console.log('Overlay preview passed: Spotify, YouTube, pause/progress, queue-only, sample fallback and message origin checks.');

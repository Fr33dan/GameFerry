// GameFerry in the browser: choose games and a card, then copy. The engine does the work in a worker.
import { Engine } from './engine.js';
import { gamesRoot, isGamesFolder, listGames, removeIfEmpty } from './card.js';
import * as store from './store.js';
import { Estimate, formatDuration, formatSize } from './estimate.js';
import { coverImage, showCover } from './covers.js';

const IMAGE = /\.(7z|iso|wbfs)$/i;

/** Files read at once. Over a network share, and above all over a VPN, reading a file is mostly waiting for replies. */
const READ_AT_ONCE = 6;

/** A folder with more games than this starts with none ticked: a whole library is rarely what is wanted. */
const TICK_UP_TO = 30;

/** @param {string} id @returns {any} */
const $ = (id) => document.getElementById(id);

/**
 * @typedef {object} Item
 * @property {string} name
 * @property {FileSystemFileHandle} handle
 * @property {File | null} file   Opened when the game is read.
 * @property {import('./engine.js').Probe | null} probe
 * @property {{size: number, lastModified: number} | null} known
 *   The file as it was when last read, in an earlier visit. Its game shows straight away; the file is checked later.
 * @property {Promise<void> | null} reading
 * @property {boolean} tick       Whether to tick the game once it turns out to be copyable.
 * @property {boolean} selected
 * @property {string} status
 * @property {string} state   waiting | reading | ready | busy | done | failed | skip
 */

const state = {
  /** @type {Item[]} */ items: [],
  /** @type {Item[]} Games whose files haven't been opened yet this visit. */ toRead: [],
  /** @type {string | null} The chosen folder as messages name it ("the drive" for a whole drive). */ folderName: null,
  folderIsDrive: false,
  /** @type {number | null} Games found so far while a folder is being listed. */ looking: null,
  subfolders: 0,
  /** @type {string | null} */ sourceProblem: null,
  listing: 0,
  /** @type {string[]} Words typed in the search box, in lower case. */ filter: [],
  /** @type {FileSystemDirectoryHandle | null} */ card: null,
  /** @type {import('./card.js').CardGame[]} */ cardGames: [],
  copying: false,
  cancelled: false,
  /** Whether the lists show covers from GameTDB; remembered in this browser. */ showCovers: readSetting('covers') !== 'off',
};

// The folder APIs only exist on secure pages (https, or localhost); on plain http they are missing in any browser.
const supported = window.isSecureContext && 'showDirectoryPicker' in window && 'createWritable' in (globalThis.FileSystemFileHandle?.prototype ?? {});
if (!supported) {
  if (window.isSecureContext) {
    $('unsupported').hidden = false;
  } else {
    $('insecure-origin').textContent = location.origin;
    $('insecure').hidden = false;
  }
  for (const button of document.querySelectorAll('button')) /** @type {HTMLButtonElement} */ (button).disabled = true;
  throw new Error('This browser cannot write to folders.');
}

const engine = new Engine();
engine.ready.catch((error) => setStatus(`The engine could not start: ${error.message}`));

// ---------------------------------------------------------------------------------------------------------------
// Games

const SOURCE_HINT = $('source-hint').textContent;

$('pick-source').addEventListener('click', async () => {
  const handle = await pick(() => window.showDirectoryPicker({ id: 'gameferry-games', mode: 'read' }));
  if (!handle) return;
  await store.save('source', handle);
  $('reconnect-source').hidden = true;
  await loadSource(handle);
});

$('pick-files').addEventListener('click', async () => {
  const handles = await pick(() => window.showOpenFilePicker({ id: 'gameferry-games', multiple: true }));
  if (!handles) return;
  state.folderName = null;
  state.folderIsDrive = false;
  await addGames(handles, true);
});

/**
 * Lists a folder's disc images, then reads them in the background. Over a network share even the listing takes a
 * while, so the hint counts what has turned up.
 * @param {FileSystemDirectoryHandle} folder
 */
async function loadSource(folder) {
  const listing = ++state.listing;
  state.items = [];
  state.toRead = [];
  state.folderIsDrive = isDrive(folder);
  state.folderName = state.folderIsDrive ? 'the drive' : folder.name;
  state.looking = 0;
  state.subfolders = 0;
  state.sourceProblem = null;
  renderGames();

  /** @type {FileSystemFileHandle[]} */
  const found = [];
  let shown = performance.now();
  try {
    for await (const entry of folder.values()) {
      if (listing !== state.listing) return;
      if (entry.kind === 'directory') state.subfolders++;
      else if (IMAGE.test(entry.name)) found.push(/** @type {FileSystemFileHandle} */ (entry));
      if (performance.now() - shown > 200) {
        shown = performance.now();
        state.looking = found.length;
        summarize();
      }
    }
  } catch (error) {
    if (listing === state.listing) state.sourceProblem = `Couldn't read ${folder.name}: ${explain(error)}`;
  }

  if (listing !== state.listing) return;
  state.looking = null;
  found.sort((a, b) => a.name.localeCompare(b.name));
  await addGames(found, found.length <= TICK_UP_TO);
}

/**
 * Adds games to the list, showing straight away those remembered from an earlier visit, and starts reading the rest.
 * @param {FileSystemFileHandle[]} handles
 * @param {boolean} tick Whether to tick the games that turn out to be copyable.
 */
async function addGames(handles, tick) {
  const listing = state.listing;
  const names = new Set(state.items.map((i) => i.name));
  /** @type {Item[]} */
  const added = [];
  for (const handle of handles) {
    if (names.has(handle.name)) continue;
    names.add(handle.name);
    added.push({ name: handle.name, handle, file: null, probe: null, known: null, reading: null, tick, selected: false, status: 'Not read yet', state: 'waiting' });
  }

  const remembered = await store.loadMany(added.map((i) => `game:${i.name}`));
  if (listing !== state.listing) return;
  added.forEach((item, i) => {
    const record = remembered[i];
    if (!record) return;
    item.known = { size: record.size, lastModified: record.lastModified };
    item.probe = record.probe;
    classify(item);
  });

  state.items.push(...added);
  state.toRead.push(...added);
  renderGames();
  readMore();
}

let reading = 0;

/** Keeps READ_AT_ONCE files being read. */
function readMore() {
  while (reading < READ_AT_ONCE) {
    const item = nextToRead();
    if (!item) return;
    reading++;
    startRead(item).finally(() => {
      reading--;
      readMore();
    });
  }
}

/**
 * Takes the next game to read off the list: new games the search shows first, then other new games, and last the
 * remembered ones, which are only checked.
 */
function nextToRead() {
  const list = state.toRead;
  let index = list.findIndex((i) => !i.probe && matches(i));
  if (index < 0) index = list.findIndex((i) => !i.probe);
  if (index < 0 && list.length > 0) index = 0;
  return index < 0 ? null : list.splice(index, 1)[0];
}

/** @param {Item} item */
function startRead(item) {
  item.reading ??= read(item);
  return item.reading;
}

/**
 * Opens a game's file and identifies it, unless it is as it was when last read: the answer is remembered, because a
 * big library takes a while to read through and its files rarely change.
 * @param {Item} item
 */
async function read(item) {
  const known = item.known;
  if (!known) {
    item.state = 'reading';
    item.status = 'Reading…';
    fillRow(item);
  }
  try {
    const file = await item.handle.getFile();
    item.file = file;
    if (known && known.size === file.size && known.lastModified === file.lastModified) return;
    item.probe = await engine.probe(file);
    await store.save(`game:${item.name}`, { size: file.size, lastModified: file.lastModified, probe: item.probe });
  } catch (error) {
    item.probe = { file: item.name, problem: explain(error) };
  }
  classify(item);
  fillRow(item);
  scheduleSummary();
}

/**
 * Works out what a game's row should say, given what is on the card. A copy's outcome stays on its row until another
 * card is chosen.
 * @param {Item} item
 * @param {boolean} [fresh] True when the card has just been chosen.
 */
function classify(item, fresh = false) {
  const probe = item.probe;
  if (!probe || item.state === 'busy' || (!fresh && (item.state === 'done' || item.state === 'failed'))) return;
  const firstTime = item.state === 'waiting' || item.state === 'reading';
  if (probe.problem) {
    item.status = probe.problem;
    item.state = 'skip';
    item.selected = false;
  } else if (state.cardGames.some((g) => g.id6 === probe.id6)) {
    item.status = 'Already on the card';
    item.state = 'skip';
    item.selected = false;
  } else {
    item.status = 'Ready';
    item.state = 'ready';
    if (firstTime) item.selected = item.tick;
  }
}

/** @param {Item} item */
const canCopy = (item) => !state.copying && (item.state === 'ready' || item.state === 'failed');

/** @param {Item} item */
function title(item) {
  return item.probe?.discTitle || item.probe?.fileTitle || item.name.replace(/(\.nkit)?\.(7z|iso|wbfs)$/i, '');
}

/**
 * True if every word typed in the search box is in the game's file name or title, or starts its ID (most IDs end in
 * 01, so a "1" anywhere in them would match nearly everything).
 * @param {Item} item
 */
function matches(item) {
  if (state.filter.length === 0) return true;
  const text = `${item.name} ${item.probe?.discTitle ?? ''}`.toLowerCase();
  const id = item.probe?.id6?.toLowerCase() ?? '';
  return state.filter.every((word) => text.includes(word) || id.startsWith(word));
}

/** @type {WeakMap<Item, HTMLTableRowElement>} */
const rows = new WeakMap();
const noMatch = emptyRow(5, '');

/** Rebuilds the game list when games come or go; a change to one game goes through fillRow. */
function renderGames() {
  const body = $('games').tBodies[0];
  body.replaceChildren(...state.items.map((item) => fillRow(item, rows.get(item) ?? makeRow(item))));
  if (state.items.length === 0) {
    body.append(emptyRow(5, state.looking !== null ? `Looking through ${state.folderName}…` : 'Choose the folder your games are in, or add files.'));
  }
  body.append(noMatch);
  summarize();
}

/** Brings every row up to date, after something that changes them all: a copy starting or ending, another card. */
function refreshGames() {
  for (const item of state.items) fillRow(item);
  summarize();
}

/** @param {Item} item */
function makeRow(item) {
  const row = document.createElement('tr');
  const box = document.createElement('input');
  box.type = 'checkbox';
  box.addEventListener('change', () => {
    item.selected = box.checked;
    summarize();
  });
  cell(row, '', 'check').append(box);
  const sub = document.createElement('span');
  sub.className = 'sub';
  const titles = document.createElement('div');
  titles.append(document.createElement('span'), sub);
  cell(row, '').append(gameBox(coverImage(), titles));
  cell(row, '');
  cell(row, '', 'number');
  cell(row, '');
  rows.set(item, row);
  return row;
}

/**
 * @param {Item} item
 * @param {HTMLTableRowElement | undefined} [row]
 * @returns {HTMLTableRowElement}
 */
function fillRow(item, row = rows.get(item)) {
  if (!row) return /** @type {any} */ (row);
  const [check, name, id, size, status] = row.cells;
  const box = /** @type {HTMLInputElement} */ (check.firstChild);
  box.checked = item.selected;
  box.disabled = !canCopy(item);
  box.setAttribute('aria-label', `Copy ${title(item)}`);
  const [heading, sub] = name.querySelectorAll('span');
  heading.textContent = title(item);
  sub.textContent = item.name;
  if (state.showCovers) showCover(/** @type {HTMLImageElement} */ (name.querySelector('img')), item.probe?.problem ? null : item.probe?.id6);
  id.textContent = item.probe?.id6 ?? '';
  size.textContent = item.probe?.estimatedBytes ? `≈ ${formatSize(item.probe.estimatedBytes)}` : item.file ? formatSize(item.file.size) : '';
  status.textContent = item.status;
  status.className = `state-${item.state}`;
  row.hidden = !matches(item);
  return row;
}

let summaryPending = false;

/** Updates the counts once per frame at most, however many files are read in between. */
function scheduleSummary() {
  if (summaryPending) return;
  summaryPending = true;
  requestAnimationFrame(() => {
    summaryPending = false;
    summarize();
  });
}

/** Updates what depends on the whole list: the hint and reading progress, the search count, select-all and Copy. */
function summarize() {
  const total = state.items.length;
  const read = state.items.filter((i) => i.probe).length;
  $('source-hint').textContent = sourceHint(total, read);

  const progress = $('read-progress');
  const looking = state.looking !== null;
  progress.hidden = !looking && read === total;
  progress.classList.toggle('indeterminate', looking);
  $('read-bar').style.width = looking ? '' : `${((read / Math.max(1, total)) * 100).toFixed(1)}%`;
  if (looking) progress.removeAttribute('aria-valuenow');
  else progress.setAttribute('aria-valuenow', String(Math.round((read / Math.max(1, total)) * 100)));

  const shown = state.items.filter(matches);
  $('list-tools').hidden = total === 0;
  $('filter-count').textContent = state.filter.length ? `${shown.length.toLocaleString()} of ${total.toLocaleString()} shown` : '';
  noMatch.hidden = total === 0 || shown.length > 0;
  noMatch.cells[0].textContent = `No games match “${$('filter').value.trim()}”.`;

  const choosable = shown.filter(canCopy);
  const all = /** @type {HTMLInputElement} */ ($('select-all'));
  all.checked = choosable.length > 0 && choosable.every((i) => i.selected);
  all.disabled = choosable.length === 0;
  updateStart();
}

/** What the hint under the Games heading says. @param {number} total @param {number} read */
function sourceHint(total, read) {
  const where = state.folderName ? ` ${state.folderIsDrive ? 'on' : 'in'} ${state.folderName}` : '';
  if (state.sourceProblem) return state.sourceProblem;
  if (state.looking !== null) return `Looking through ${state.folderName}… ${count(state.looking, 'game file')} so far.`;
  if (total === 0) {
    if (!state.folderName) return SOURCE_HINT;
    const inside = state.subfolders
      ? ` Only files directly in the folder you choose are listed, not those in the ${count(state.subfolders, 'folder')} inside it.`
      : '';
    return `No .7z, .iso or .wbfs files${where}.${inside}`;
  }
  if (read < total) {
    return `Reading ${read.toLocaleString()} of ${count(total, 'file')}${where}… This can take a while over a network; what is read is remembered for next time.`;
  }

  let ready = 0;
  let copied = 0;
  let onCard = 0;
  let problems = 0;
  for (const item of state.items) {
    if (item.probe?.problem) problems++;
    else if (item.state === 'done') copied++;
    else if (item.state === 'skip') onCard++;
    else ready++;
  }
  const parts = [];
  if (ready) parts.push(`${ready.toLocaleString()} ready to copy`);
  if (copied) parts.push(`${copied.toLocaleString()} copied`);
  if (onCard) parts.push(`${onCard.toLocaleString()} already on the card`);
  if (problems) parts.push(`${problems.toLocaleString()} can't be copied`);
  const tick = ready && !state.items.some((i) => i.selected) ? ' Tick the games to copy.' : '';
  return `${count(total, 'file')}${where}: ${parts.join(', ')}.${tick}`;
}

$('select-all').addEventListener('change', (/** @type {Event} */ event) => {
  const checked = /** @type {HTMLInputElement} */ (event.target).checked;
  for (const item of state.items) {
    if (!canCopy(item) || !matches(item)) continue;
    item.selected = checked;
    fillRow(item);
  }
  summarize();
});

$('filter').addEventListener('input', (/** @type {Event} */ event) => {
  state.filter = /** @type {HTMLInputElement} */ (event.target).value.toLowerCase().split(/\s+/).filter(Boolean);
  for (const item of state.items) {
    const row = rows.get(item);
    if (row) row.hidden = !matches(item);
  }
  summarize();
});

// ---------------------------------------------------------------------------------------------------------------
// Card

$('pick-card').addEventListener('click', async () => {
  const handle = await pick(() => window.showDirectoryPicker({ id: 'gameferry-card', mode: 'readwrite' }));
  if (!handle) return;
  await store.save('card', handle);
  $('reconnect-card').hidden = true;
  await loadCard(handle);
});

/**
 * @param {FileSystemDirectoryHandle} handle
 * @param {boolean} [fresh] False when reading the same card again after a copy, so the copy's outcome stays shown.
 */
async function loadCard(handle, fresh = true) {
  state.card = handle;
  $('card-hint').textContent = isGamesFolder(handle) ? `Games go straight into ${handle.name}.`
    : isDrive(handle) ? "Games go in the card's wbfs folder."
    : `Games go in ${handle.name}/wbfs.`;
  try {
    const root = await gamesRoot(handle, false);
    state.cardGames = root ? await listGames(root) : [];
  } catch (error) {
    state.cardGames = [];
    setStatus(`Couldn't read the card: ${/** @type {Error} */ (error).message}`);
  }

  for (const item of state.items) classify(item, fresh);
  renderCard();
  refreshGames();
  await checkKeys();
}

function renderCard() {
  const body = $('card-games').tBodies[0];
  body.replaceChildren();
  if (!state.card) body.append(emptyRow(3, 'No card chosen yet.'));
  else if (state.cardGames.length === 0) body.append(emptyRow(3, 'No games on this card yet.'));
  for (const game of state.cardGames) {
    const row = document.createElement('tr');
    const image = coverImage();
    if (state.showCovers) showCover(image, game.id6);
    const heading = document.createElement('span');
    heading.textContent = game.title || game.folder;
    cell(row, '').append(gameBox(image, heading));
    cell(row, game.id6);
    cell(row, formatSize(game.bytes), 'number');
    body.append(row);
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Covers

function applyCovers() {
  document.body.classList.toggle('no-covers', !state.showCovers);
  $('toggle-covers').textContent = state.showCovers ? 'Hide covers' : 'Show covers';
}

$('toggle-covers').addEventListener('click', () => {
  state.showCovers = !state.showCovers;
  writeSetting('covers', state.showCovers ? 'on' : 'off');
  applyCovers();
  if (state.showCovers) {
    refreshGames();
    renderCard();
  }
});

// ---------------------------------------------------------------------------------------------------------------
// Keys (only for builds made without the Wii common key)

async function checkKeys() {
  const about = await engine.about().catch(() => null);
  if (!about || about.builtInKeys) return;
  if (!about.hasKeys && state.card) {
    // BootMii writes keys.bin next to its NAND backup, at the card's top level.
    try {
      const file = await (await state.card.getFileHandle('keys.bin')).getFile();
      const result = await engine.useKeys(file);
      if (result.ok) {
        $('keys-status').textContent = `Using keys.bin from ${isDrive(state.card) ? 'the card' : state.card.name}.`;
        return;
      }
    } catch {
      // Not there: ask for it.
    }
  }
  $('keys').hidden = about.hasKeys;
}

$('pick-keys').addEventListener('click', async () => {
  const handles = await pick(() => window.showOpenFilePicker({ id: 'gameferry-keys' }));
  if (!handles) return;
  const result = await engine.useKeys(await handles[0].getFile());
  $('keys-status').textContent = result.ok ? 'Key loaded.' : result.error ?? 'That file was not accepted.';
  if (result.ok) $('keys').hidden = true;
});

// ---------------------------------------------------------------------------------------------------------------
// Copying

const CHOOSE_CARD = 'Choose the card to copy to.';

function updateStart() {
  const count = state.items.filter((i) => i.selected).length;
  const start = /** @type {HTMLButtonElement} */ ($('start'));
  start.disabled = state.copying || count === 0 || !state.card;
  start.textContent = count > 0 ? `Copy ${count} game${count === 1 ? '' : 's'}` : 'Copy';
  if (state.copying) return;
  if (count > 0 && !state.card) setStatus(CHOOSE_CARD);
  else if ($('status').textContent === CHOOSE_CARD) setStatus('');
}

$('start').addEventListener('click', () => copySelected().catch((error) => {
  log(`Stopped: ${error.message}`);
  setStatus(`Stopped: ${error.message}`);
}));

$('cancel').addEventListener('click', () => {
  state.cancelled = true;
  engine.cancel();
  setStatus('Cancelling…');
});

async function copySelected() {
  const queue = state.items.filter((i) => i.selected && canCopy(i));
  if (queue.length === 0 || !state.card) return;

  // A folder chosen in an earlier visit may need its permission confirmed again.
  // @ts-ignore: requestPermission is Chromium's File System Access extension.
  if ((await state.card.requestPermission({ mode: 'readwrite' })) !== 'granted') {
    setStatus('GameFerry needs permission to write to the card.');
    return;
  }

  state.copying = true;
  state.cancelled = false;
  $('cancel').hidden = false;
  refreshGames();

  const estimate = new Estimate(queue.map((i) => i.probe?.imageBytes ?? i.file?.size ?? 0));
  const root = /** @type {FileSystemDirectoryHandle} */ (await gamesRoot(state.card, true));
  const partitions = /** @type {HTMLInputElement} */ ($('game-only')).checked ? 'GameOnly' : 'RemoveUpdate';
  let copied = 0;
  let failed = 0;

  for (let index = 0; index < queue.length && !state.cancelled; index++) {
    const item = queue[index];
    // A game remembered from an earlier visit may not have had its file opened yet, and the file may have changed.
    if (!item.file) await startRead(item);
    if (!item.file || (item.state !== 'ready' && item.state !== 'failed')) {
      failed++;
      estimate.finish(index);
      log(`${title(item)}: not copied: ${item.status}`);
      continue;
    }

    const probe = /** @type {import('./engine.js').Probe} */ (item.probe);
    const file = /** @type {File} */ (item.file);
    const folderName = /** @type {string} */ (probe.folder);
    const name = title(item);
    item.status = 'Copying…';
    item.state = 'busy';
    fillRow(item);
    log(`${name}: copying to ${root.name}/${folderName}`);

    const existed = await root.getDirectoryHandle(folderName).then(() => true, () => false);
    const folder = await root.getDirectoryHandle(folderName, { create: true });
    const started = performance.now();
    engine.onProgress = (source, written) => {
      estimate.report(index, source);
      showProgress(estimate, index, queue.length, name, source / Math.max(1, probe.imageBytes ?? file.size), written);
    };
    engine.onSaving = (fileName, done, total) => {
      setStatus(`${name}: the browser couldn't finish saving ${fileName}, so it is being saved again from what was written, ${Math.floor((done / Math.max(1, total)) * 100)}%`);
    };

    /** @type {import('./engine.js').TransferResult} */
    let result;
    try {
      result = await engine.transfer(file, folder, /** @type {string} */ (probe.id6), { partitions });
    } catch (error) {
      result = { error: /** @type {Error} */ (error).message };
    }

    estimate.finish(index);
    const seconds = (performance.now() - started) / 1000;
    if (result.ok && result.crcMatches !== false && !result.unsaved?.length) {
      copied++;
      item.status = `Copied, ${formatSize(result.outputBytes ?? 0)}`;
      item.state = 'done';
      item.selected = false;
      const checked = result.groupsChecked
        ? `; ${result.groupsChecked - (result.groupsFailing ?? 0)} of ${result.groupsChecked} rebuilt groups match the disc's own hashes`
        : '';
      log(`  done in ${formatDuration(seconds)}: ${formatSize(result.outputBytes ?? 0)} in ${result.parts} file(s)${checked}`);
      for (const warning of result.warnings ?? []) log(`  note: ${warning}`);
      for (const again of result.savedAgain ?? []) {
        log(`  note: the browser couldn't finish saving ${again.name} (${again.problem}), so it was saved again from what had been written.`);
      }
      if (result.groupsFailing) log(`  warning: ${result.groupsFailing} group(s) did not match the disc's hashes; the source may be damaged.`);
    } else if (result.unsaved?.length) {
      // Everything was written, but the browser couldn't give a file its name, even on a second try.
      failed++;
      item.state = 'failed';
      const where = `${root.name}/${folderName}`;
      /** @param {{name: string, swap: string | null, emptyLeft?: boolean}} u */
      const fix = (u) => `${u.emptyLeft ? `delete the empty ${u.name}, then ` : ''}rename ${u.swap} to ${u.name}`;
      const rescue = result.unsaved.filter((u) => u.swap);
      item.status = rescue.length
        ? `Not saved: in ${where}, ${rescue.map(fix).join(' and ')} before closing this page, or copy the game again`
        : `Failed: the browser couldn't save ${result.unsaved.map((u) => u.name).join(' and ')}`;
      for (const u of result.unsaved) {
        log(`  The whole game was written, but the browser couldn't save ${u.name}: ${u.problem}.`);
        if (u.swap) log(`  Its data is in ${where}/${u.swap}. To keep it, ${fix(u)} before closing this page; or copy the game again.`);
      }
    } else {
      // A cancelled game stays ticked, ready for the next copy.
      if (!result.cancelled) failed++;
      item.state = result.cancelled ? 'ready' : 'failed';
      item.status = result.cancelled ? 'Cancelled'
        : result.error ? `Failed: ${result.error}`
        : `Failed: the source's checksum is ${result.crc}, not ${result.expectedCrc}; the file is damaged`;
      log(`  ${item.status}`);
      if (!existed) await removeIfEmpty(root, folderName);
    }
    fillRow(item);
  }

  engine.onProgress = null;
  engine.onSaving = null;
  state.copying = false;
  $('cancel').hidden = true;
  /** @type {HTMLElement} */ ($('progress-bar')).style.width = `${failed || state.cancelled ? estimate.fraction * 100 : 100}%`;
  const summary = `Copied ${copied} of ${queue.length} game(s)${failed ? `, ${failed} failed` : ''} in ${formatDuration(estimate.elapsed)}.`;
  setStatus(state.cancelled ? `Cancelled. ${summary}` : summary);
  log(summary);
  await loadCard(state.card, false);
}

/**
 * @param {Estimate} estimate @param {number} index @param {number} count @param {string} title
 * @param {number} fraction @param {number} written
 */
function showProgress(estimate, index, count, title, fraction, written) {
  /** @type {HTMLElement} */ ($('progress-bar')).style.width = `${(estimate.fraction * 100).toFixed(1)}%`;
  document.querySelector('.copy .progress')?.setAttribute('aria-valuenow', String(Math.round(estimate.fraction * 100)));
  const speed = estimate.speed;
  const left = estimate.remaining;
  const parts = [
    `${count > 1 ? `Game ${index + 1} of ${count}: ` : ''}${title}, ${Math.min(100, Math.floor(fraction * 100))}%`,
    `${formatSize(written)} written`,
  ];
  if (speed) parts.push(`reading ${formatSize(speed)}/s`);
  if (left !== null && left > 0) parts.push(`about ${formatDuration(left)} left${count > 1 ? ` for all ${count}` : ''}`);
  setStatus(parts.join(' · '));
}

// ---------------------------------------------------------------------------------------------------------------
// Helpers

/** Runs a picker, treating the user closing it as "nothing chosen". @template T @param {() => Promise<T>} show */
async function pick(show) {
  try {
    return await show();
  } catch (error) {
    if (/** @type {DOMException} */ (error).name !== 'AbortError') setStatus(String(/** @type {Error} */ (error).message));
    return null;
  }
}

/** A file error in words; the browser's own messages are written for developers. @param {unknown} error */
function explain(error) {
  const name = /** @type {Error} */ (error)?.name;
  if (name === 'NotFoundError') return 'The file is no longer there.';
  if (name === 'NotReadableError') return "The file couldn't be read: it may have changed, or the network share may have dropped.";
  if (name === 'NotAllowedError') return 'GameFerry no longer has permission to read it. Choose the folder again.';
  return String(/** @type {Error} */ (error)?.message ?? error);
}

/**
 * True for the top folder of a drive. Browsers never say where a chosen folder is, not even its drive letter, and a
 * drive's top folder has no name of its own (Chrome gives "\"), so messages call it the card or the drive instead.
 * @param {FileSystemDirectoryHandle} handle
 */
function isDrive(handle) {
  return /^[\\/]?$/.test(handle.name);
}

/** "1 file", "1,456 files". @param {number} n @param {string} noun */
function count(n, noun) {
  return `${n.toLocaleString()} ${noun}${n === 1 ? '' : 's'}`;
}

/** @param {HTMLTableRowElement} row @param {string} text @param {string} [className] */
function cell(row, text, className) {
  const td = document.createElement('td');
  td.textContent = text;
  if (className) td.className = className;
  row.append(td);
  return td;
}

/** A game's cover beside its title. @param {HTMLImageElement} image @param {HTMLElement} titles */
function gameBox(image, titles) {
  const box = document.createElement('div');
  box.className = 'game';
  box.append(image, titles);
  return box;
}

/**
 * A preference kept in this browser. Storage can be refused (private windows, blocked site data); the page then just
 * uses its defaults.
 * @param {string} name
 */
function readSetting(name) {
  try {
    return localStorage.getItem(`gameferry-${name}`);
  } catch {
    return null;
  }
}

/** @param {string} name @param {string} value */
function writeSetting(name, value) {
  try {
    localStorage.setItem(`gameferry-${name}`, value);
  } catch {
    // Not kept: the default comes back next visit.
  }
}

/** @param {number} columns @param {string} text */
function emptyRow(columns, text) {
  const row = document.createElement('tr');
  row.className = 'empty';
  const td = document.createElement('td');
  td.colSpan = columns;
  td.textContent = text;
  row.append(td);
  return row;
}

/** @param {string} text */
function setStatus(text) {
  $('status').textContent = text;
}

/** @param {string} line */
function log(line) {
  const box = $('log');
  box.textContent += `${line}\n`;
  box.scrollTop = box.scrollHeight;
}

/**
 * Offers a folder remembered from an earlier visit. Chromium may keep the permission, or want a click to give it back.
 * @param {string} key @param {'read' | 'readwrite'} mode @param {string} buttonId @param {(h: FileSystemDirectoryHandle) => Promise<void>} use
 * @param {string} drive What to call the folder if it is a whole drive, e.g. "card".
 */
async function restore(key, mode, buttonId, use, drive) {
  const saved = await store.remembered(key, mode);
  if (!saved) return;
  if (saved.granted) {
    await use(saved.handle);
    return;
  }

  const button = $(buttonId);
  button.textContent = `Use ${isDrive(saved.handle) ? `the same ${drive}` : saved.handle.name} again`;
  button.hidden = false;
  button.onclick = async () => {
    // @ts-ignore: requestPermission is Chromium's File System Access extension.
    if ((await saved.handle.requestPermission({ mode })) !== 'granted') return;
    button.hidden = true;
    await use(saved.handle);
  };
}

applyCovers();
renderGames();
renderCard();
await restore('card', 'readwrite', 'reconnect-card', loadCard, 'card');
await restore('source', 'read', 'reconnect-source', loadSource, 'drive');
if ('serviceWorker' in navigator && location.protocol === 'https:') navigator.serviceWorker.register('sw.js').catch(() => {});

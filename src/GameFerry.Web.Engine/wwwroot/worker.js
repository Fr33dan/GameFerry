// GameFerry's engine worker: hosts the .NET runtime, reads the chosen files synchronously (FileReaderSync only exists
// in workers) and writes the WBFS parts into the game's folder on the card.
//
// Messages in:  { id, kind: 'about' }
//               { id, kind: 'probe', file }
//               { id, kind: 'keys', file }
//               { id, kind: 'transfer', file, folder: FileSystemDirectoryHandle, baseName, options }
//               { kind: 'cancel' }
// Messages out: { id, result } or { id, error, name }; during a transfer { kind: 'progress', source, written },
//               { kind: 'finishing', name, size } as each file is closed (see finish), and after closing failed,
//               { kind: 'renaming', name, attempt, of, pause } before each try at renaming its swap file (pause 0
//               once the try itself starts; see saveAgain).
import { dotnet } from './_framework/dotnet.js';

/** How much of each end of a file a probe reads ahead: a 7z's table of contents and an image's header fit easily. */
const READ_AHEAD = 16 << 10;

const reader = new FileReaderSync();
/** @type {Map<number, {file: File, ahead: {start: number, bytes: Uint8Array}[]}>} */
const files = new Map();
let nextFileId = 1;
let target = null;

function addFile(file, ahead = []) {
  const id = nextFileId++;
  files.set(id, { file, ahead });
  return id;
}

/**
 * Reads the parts of a file that identifying it needs, its start and (for a 7z archive) its end, both at once and
 * without blocking the worker, so the page can have several files on the way: over a network share, and above all
 * over a VPN, waiting for each read costs far more than the reading.
 * @param {File} file
 */
function readAhead(file) {
  const ranges = [[0, Math.min(file.size, READ_AHEAD)]];
  if (/\.7z$/i.test(file.name) && file.size > READ_AHEAD) ranges.push([Math.max(READ_AHEAD, file.size - READ_AHEAD), file.size]);
  return Promise.all(ranges.map(async ([start, end]) => ({ start, bytes: new Uint8Array(await file.slice(start, end).arrayBuffer()) })));
}

async function openPart(part) {
  const name = part === 0 ? `${target.baseName}.wbfs` : `${target.baseName}.wbf${part}`;
  const handle = await target.folder.getFileHandle(name, { create: true });
  target.names[part] = name;
  return handle.createWritable({ keepExistingData: false });
}

const imports = {
  readFile(fileId, offset, view) {
    const { file, ahead } = files.get(fileId);
    const end = Math.min(file.size, offset + view.length);
    if (end <= offset) return 0;
    const held = ahead.find((range) => offset >= range.start && end <= range.start + range.bytes.length);
    if (held) {
      view.set(held.bytes.subarray(offset - held.start, end - held.start));
      return end - offset;
    }
    const bytes = new Uint8Array(reader.readAsArrayBuffer(file.slice(offset, end)));
    view.set(bytes);
    return bytes.length;
  },
  writeChunk(part, offset, view) {
    // Copied now: the engine reuses its buffer as soon as this returns.
    const data = view.slice();
    target.parts[part] ??= openPart(part);
    target.sizes[part] = Math.max(target.sizes[part] ?? 0, offset + data.length);
    return target.parts[part].then((writable) => writable.write({ type: 'write', position: offset, data }));
  },
  progress(source, written) {
    postMessage({ kind: 'progress', source, written });
  },
};

const runtime = await dotnet.create();
runtime.setModuleImports('gameferry', imports);
const engine = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).GameFerry.Web.Engine.WebEngine;

/** Discards every part and removes the files the copy created. */
async function discardParts() {
  for (const settled of await Promise.allSettled(target.parts.filter(Boolean))) {
    if (settled.status === 'fulfilled') await settled.value.abort().catch(() => {});
  }
  for (const name of target.names.filter(Boolean)) await target.folder.removeEntry(name).catch(() => {});
}

/**
 * Closes every part, which is when Chrome gives the file its real name: until then the data is in a swap file beside
 * it (name.crswap). That last step can fail on its own, when something else has the file open at that moment, so a
 * part that fails is renamed from its swap file afterwards (see saveAgain).
 * @returns {Promise<{savedAgain: object[], unsaved: object[]}>}
 */
async function commitParts() {
  const savedAgain = [];
  const unsaved = [];
  for (const [part, opening] of target.parts.entries()) {
    if (!opening) continue;
    const writable = await opening;
    const name = target.names[part];
    const started = performance.now();
    try {
      await finish(writable, name, target.sizes[part]);
    } catch (error) {
      const problem = `${describe(error)} (after ${since(started)} s)`;
      const outcome = await saveAgain(name, target.sizes[part], describe(error));
      if (outcome.saved) savedAgain.push({ name, problem, tries: outcome.tries });
      else unsaved.push({ name, problem, tries: outcome.tries, swap: outcome.swap, emptyLeft: outcome.emptyLeft });
    }
  }
  return { savedAgain, unsaved };
}

/**
 * Closes a written file, which is when Chrome reads the whole file back (a Safe Browsing check) and then renames its
 * swap file. That rename now and then fails; saveAgain copes with it. The page is told, as the status would otherwise
 * sit at 100% for minutes.
 * @param {FileSystemWritableFileStream} writable @param {string} name @param {number} size
 */
async function finish(writable, name, size) {
  postMessage({ kind: 'finishing', name, size });
  await writable.close();
}

/** How long to wait before each try at renaming a swap file, in seconds. */
const RENAME_PAUSES = [5, 30, 120];

/** Errors after which another try can't help: the swap file is gone, or the browser won't rename files here. */
const RENAME_STOPS = new Set(['NotFoundError', 'NotAllowedError', 'SecurityError', 'TypeError', 'NotSupportedError']);

/**
 * Saves a part after closing it failed. Closing is when Chrome reads the file back and renames its swap file
 * (name.crswap) onto the real name. When that rename fails, Chrome drops the writable and tries to delete the swap
 * file, which survives only because something else, most likely antivirus, still has it open; renaming it by hand
 * later works.
 *
 * So the swap file is renamed here, after a pause for whatever has it to let go, and again after longer pauses if need
 * be. Each try costs Chrome one more read of the file, but nothing is written again. Every try is reported, with how
 * long it took and why it failed, for the log.
 * @param {string} name @param {number} size @param {string} closing How closing failed.
 * @returns {Promise<{saved: boolean, swap: string | null, tries: {pause: number, seconds: number, problem?: string, same?: boolean}[], emptyLeft?: boolean}>}
 */
async function saveAgain(name, size, closing) {
  const folder = target.folder;
  const found = await findSwap(folder, name, size).catch(() => null);
  if (!found) {
    // Nothing to rename, so the empty file the copy started with would only look like a broken game.
    await clearEmpty(folder, name);
    return { saved: false, swap: null, tries: [] };
  }
  const tries = [];
  for (const [i, pause] of RENAME_PAUSES.entries()) {
    postMessage({ kind: 'renaming', name, attempt: i + 1, of: RENAME_PAUSES.length, pause });
    await new Promise((resolve) => setTimeout(resolve, pause * 1000));
    postMessage({ kind: 'renaming', name, attempt: i + 1, of: RENAME_PAUSES.length, pause: 0 });
    const started = performance.now();
    try {
      // The empty file the copy started with: out of the way now, so that renaming by hand works if this doesn't.
      await clearEmpty(folder, name);
      // Into the folder's handle, not move(name): Chrome checks a plain rename against the file's path, and when the
      // card was picked as a whole drive that check never reaches the drive's permission, so it is refused.
      // @ts-ignore: move() is Chromium's.
      await found.handle.move(folder, name);
      tries.push({ pause, seconds: since(started) });
      return { saved: true, swap: found.entry, tries };
    } catch (error) {
      const problem = describe(error);
      tries.push({ pause, seconds: since(started), problem, same: problem === closing });
      if (RENAME_STOPS.has(error?.name)) break;
    }
  }
  return { saved: false, swap: found.entry, tries, emptyLeft: !(await clearEmpty(folder, name)) };
}

/** The swap file holding a part: the right size, and the newest if an earlier copy left one too. */
async function findSwap(folder, name, size) {
  const pattern = new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(\\.\\d+)?\\.crswap$`);
  let newest = null;
  for await (const [entry, handle] of folder.entries()) {
    if (handle.kind !== 'file' || !pattern.test(entry)) continue;
    const file = await /** @type {FileSystemFileHandle} */ (handle).getFile();
    if (file.size === size && (!newest || file.lastModified >= newest.file.lastModified)) newest = { entry, handle, file };
  }
  return newest;
}

/** Removes the file of that name if it is empty. Returns whether no empty file of that name is left. */
async function clearEmpty(folder, name) {
  const file = await folder.getFileHandle(name).then((handle) => handle.getFile()).catch(() => null);
  if (file?.size !== 0) return true;
  return folder.removeEntry(name).then(() => true, () => false);
}

/** Whole seconds since a performance.now() time. */
function since(started) {
  return Math.round((performance.now() - started) / 1000);
}

/** @param {any} error */
function describe(error) {
  return `${error?.name ?? 'Error'}: ${String(error?.message ?? error).replace(/\.$/, '')}`;
}

async function transfer(request) {
  target = { folder: request.folder, baseName: request.baseName, parts: [], names: [], sizes: [] };
  const fileId = addFile(request.file);
  try {
    const result = JSON.parse(await engine.Transfer(fileId, request.file.size, request.file.name, JSON.stringify(request.options ?? {})));
    if (result.ok === true && result.crcMatches !== false) {
      const { savedAgain, unsaved } = await commitParts();
      if (savedAgain.length) result.savedAgain = savedAgain;
      if (unsaved.length) result.unsaved = unsaved;
    } else {
      await discardParts();
    }
    return result;
  } catch (error) {
    await discardParts();
    throw error;
  } finally {
    files.delete(fileId);
    target = null;
  }
}

onmessage = async (event) => {
  const request = event.data;
  if (request.kind === 'cancel') {
    engine.Cancel();
    return;
  }

  try {
    let result;
    if (request.kind === 'about') {
      result = JSON.parse(engine.About());
    } else if (request.kind === 'probe') {
      const fileId = addFile(request.file, await readAhead(request.file));
      try {
        result = JSON.parse(engine.Probe(fileId, request.file.size, request.file.name));
      } finally {
        files.delete(fileId);
      }
    } else if (request.kind === 'keys') {
      const fileId = addFile(request.file);
      try {
        result = JSON.parse(engine.UseKeysBin(fileId, request.file.size));
      } finally {
        files.delete(fileId);
      }
    } else if (request.kind === 'transfer') {
      result = await transfer(request);
    } else {
      throw new Error(`unknown request ${request.kind}`);
    }

    postMessage({ id: request.id, result });
  } catch (error) {
    postMessage({ id: request.id, error: String(error?.message ?? error), name: error?.name });
  }
};

postMessage({ kind: 'ready' });

// GameFerry's engine worker: hosts the .NET runtime, reads the chosen files synchronously (FileReaderSync only exists
// in workers) and writes the WBFS parts into the game's folder on the card.
//
// Messages in:  { id, kind: 'about' }
//               { id, kind: 'probe', file }
//               { id, kind: 'keys', file }
//               { id, kind: 'transfer', file, folder: FileSystemDirectoryHandle, baseName, options }
//               { kind: 'cancel' }
// Messages out: { id, result } or { id, error, name }; during a transfer { kind: 'progress', source, written },
//               { kind: 'finishing', name, size } as each file is closed (see finish), and
//               { kind: 'saving', name, done, total } while a file is saved again (see saveAgain).
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
 * it (name.crswap). That last step can fail on its own, for instance if something else has the file open at that
 * moment, so a part that fails is saved again from its swap file (see saveAgain).
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
      const problem = `${describe(error)} (after ${Math.round((performance.now() - started) / 1000)} s)`;
      const outcome = await saveAgain(name, target.sizes[part], writable);
      if (outcome.saved) savedAgain.push({ name, problem, leftover: outcome.leftover });
      else unsaved.push({ name, swap: outcome.swap, emptyLeft: outcome.emptyLeft, problem: outcome.problem ? `${problem}; saving again: ${outcome.problem}` : problem });
    }
  }
  return { savedAgain, unsaved };
}

/**
 * Closes a written file, which is when Chrome reads the whole file back (a Safe Browsing check) and then renames its
 * swap file. That rename now and then fails, most often on big files; saveAgain copes with it. The page is told, as
 * the status would otherwise sit at 100% for minutes.
 * @param {FileSystemWritableFileStream} writable @param {string} name @param {number} size
 */
async function finish(writable, name, size) {
  postMessage({ kind: 'finishing', name, size });
  await writable.close();
}

/** Writables whose closing failed. They are kept while the page is open, because Chrome deletes their swap files when they go. */
const stranded = new Set();

/**
 * Saves a part again from the swap file Chrome wrote it to, after closing it failed: the swap file still holds every
 * byte, so it is copied under the real name through a fresh writable (whose own swap file is name.1.crswap). The
 * failed writable is kept until then, as Chrome deletes its swap file when it goes; if saving again fails too, it is
 * kept for good, so the swap file can be renamed by hand.
 * @param {string} name @param {number} size @param {FileSystemWritableFileStream} failed
 * @returns {Promise<{saved: boolean, swap: string | null, leftover?: string | null, emptyLeft?: boolean, problem?: string}>}
 */
async function saveAgain(name, size, failed) {
  stranded.add(failed);
  const folder = target.folder;
  let swap = null;
  try {
    // The swap file holding this part: the right size, and the newest if an earlier copy left one too.
    const swapName = new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(\\.\\d+)?\\.crswap$`);
    let newest = null;
    for await (const [entry, handle] of folder.entries()) {
      if (handle.kind !== 'file' || !swapName.test(entry)) continue;
      const file = await /** @type {FileSystemFileHandle} */ (handle).getFile();
      if (file.size === size && (!newest || file.lastModified >= newest.file.lastModified)) newest = { entry, file };
    }
    if (!newest) return { saved: false, swap: null, problem: 'the swap file could not be found' };
    swap = newest.entry;

    let done = 0;
    let shown = 0;
    const counting = new TransformStream({
      transform(chunk, controller) {
        done += chunk.length;
        if (done - shown >= 16 << 20 || done === size) {
          shown = done;
          postMessage({ kind: 'saving', name, done, total: size });
        }
        controller.enqueue(chunk);
      },
    });
    const writable = await (await folder.getFileHandle(name, { create: true })).createWritable({ keepExistingData: false });
    const started = performance.now();
    try {
      await newest.file.stream().pipeThrough(counting).pipeTo(writable, { preventClose: true });
      await finish(writable, name, size);
    } catch (error) {
      await writable.abort().catch(() => {});
      // A DOMException's message can't be changed, so the timing goes on a copy.
      const timed = new Error(`${String(error?.message ?? error).replace(/\.$/, '')} (after ${Math.round((performance.now() - started) / 1000)} s)`);
      timed.name = error?.name ?? 'Error';
      throw timed;
    }
    stranded.delete(failed);
    await failed.abort().catch(() => {});
    // The first try's swap file is no longer needed, but Chrome may not let it go until its writable does.
    const leftover = await folder.removeEntry(swap).then(() => null, (error) => (error?.name === 'NotFoundError' ? null : swap));
    return { saved: true, swap, leftover };
  } catch (error) {
    // The empty file made at the start would be in the way of renaming the swap file by hand. The browser may not
    // let it go while the failed writable lasts; then whoever renames the swap file has to delete it first.
    const empty = async () => (await folder.getFileHandle(name).then((handle) => handle.getFile(), () => null))?.size === 0;
    if (await empty()) await folder.removeEntry(name).catch(() => {});
    return { saved: false, swap, emptyLeft: await empty(), problem: describe(error) };
  }
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

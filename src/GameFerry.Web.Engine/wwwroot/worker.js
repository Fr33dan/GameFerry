// GameFerry's engine worker: hosts the .NET runtime, reads the chosen files synchronously (FileReaderSync only exists
// in workers) and writes the WBFS parts into the game's folder on the card.
//
// Messages in:  { id, kind: 'about' }
//               { id, kind: 'probe', file }
//               { id, kind: 'keys', file }
//               { id, kind: 'transfer', file, folder: FileSystemDirectoryHandle, baseName, options }
//               { kind: 'cancel' }
// Messages out: { id, result } or { id, error, name }, and { kind: 'progress', source, written } during a transfer.
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
    return target.parts[part].then((writable) => writable.write({ type: 'write', position: offset, data }));
  },
  progress(source, written) {
    postMessage({ kind: 'progress', source, written });
  },
};

const runtime = await dotnet.create();
runtime.setModuleImports('gameferry', imports);
const engine = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).GameFerry.Web.Engine.WebEngine;

/** Commits every part on success; otherwise discards them and removes the files the copy created. */
async function finishParts(commit) {
  const writables = await Promise.allSettled(target.parts.filter(Boolean));
  for (const settled of writables) {
    if (settled.status !== 'fulfilled') continue;
    if (commit) await settled.value.close();
    else await settled.value.abort().catch(() => {});
  }

  if (!commit) {
    for (const name of target.names.filter(Boolean))
      await target.folder.removeEntry(name).catch(() => {});
  }
}

async function transfer(request) {
  target = { folder: request.folder, baseName: request.baseName, parts: [], names: [] };
  const fileId = addFile(request.file);
  try {
    const result = JSON.parse(await engine.Transfer(fileId, request.file.size, request.file.name, JSON.stringify(request.options ?? {})));
    await finishParts(result.ok === true && result.crcMatches !== false);
    return result;
  } catch (error) {
    await finishParts(false);
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

// The card (or USB drive): where the games go and which ones are already there.

/**
 * @typedef {object} CardGame
 * @property {string} id6
 * @property {string} title  From the folder's name, or the disc's own title if the folder is named by its ID alone.
 * @property {string} folder
 * @property {number} bytes  All parts together.
 */

/** True if this folder is itself the games folder (USB loaders read a folder called "wbfs" at the drive root). */
export function isGamesFolder(/** @type {FileSystemDirectoryHandle} */ handle) {
  return handle.name.toLowerCase() === 'wbfs';
}

/**
 * The folder games go in: the chosen folder if it is a wbfs folder, otherwise the wbfs folder inside it.
 * @param {FileSystemDirectoryHandle} card
 * @param {boolean} create
 * @returns {Promise<FileSystemDirectoryHandle | null>}
 */
export async function gamesRoot(card, create) {
  if (isGamesFolder(card)) return card;
  for await (const [name, handle] of card.entries()) {
    if (handle.kind === 'directory' && name.toLowerCase() === 'wbfs') return /** @type {FileSystemDirectoryHandle} */ (handle);
  }
  return create ? card.getDirectoryHandle('wbfs', { create: true }) : null;
}

const decoder = new TextDecoder('latin1');

/**
 * Reads what a WBFS file says about its disc: the copy of the disc header at 0x200 holds the ID and title.
 * @param {File} file
 */
async function readWbfsHeader(file) {
  const bytes = new Uint8Array(await file.slice(0, 0x300).arrayBuffer());
  if (bytes.length < 0x300 || decoder.decode(bytes.subarray(0, 4)) !== 'WBFS') return null;
  const id6 = decoder.decode(bytes.subarray(0x200, 0x206));
  const titleBytes = bytes.subarray(0x220, 0x260);
  const end = titleBytes.indexOf(0);
  return { id6, title: decoder.decode(end >= 0 ? titleBytes.subarray(0, end) : titleBytes).trim() };
}

/**
 * The title in a game folder's name: "Title [GAMEID]" as GameFerry and Wii Backup Manager name them, or
 * "GAMEID_Title" as some older loaders did. Folder names usually beat the disc's own title, which is often a short
 * code ("Example Game Deluxe" rather than "EXAMPLE DX"). Empty when the folder is named by its ID alone.
 * @param {string} folder
 * @param {string} id6
 */
export function folderTitle(folder, id6) {
  let title = folder.trim();
  const tag = `[${id6}]`.toUpperCase();
  if (title.toUpperCase().endsWith(tag)) title = title.slice(0, -tag.length);
  else if (title.toUpperCase().startsWith(id6.toUpperCase())) title = title.slice(id6.length).replace(/^[\s_-]+/, '');
  return title.trim();
}

/**
 * The games on the card: each sub-folder of the games folder holding ID.wbfs (plus .wbf1... parts).
 * @param {FileSystemDirectoryHandle} root
 * @returns {Promise<CardGame[]>}
 */
export async function listGames(root) {
  /** @type {CardGame[]} */
  const games = [];
  for await (const [folderName, folder] of root.entries()) {
    if (folder.kind !== 'directory') continue;
    /** @type {Map<string, File>} */
    const files = new Map();
    for await (const [name, entry] of /** @type {FileSystemDirectoryHandle} */ (folder).entries()) {
      if (entry.kind === 'file') files.set(name.toLowerCase(), await /** @type {FileSystemFileHandle} */ (entry).getFile());
    }

    for (const [name, file] of files) {
      if (!name.endsWith('.wbfs')) continue;
      const header = await readWbfsHeader(file).catch(() => null);
      if (!header) continue;
      const base = name.slice(0, -5);
      let bytes = file.size;
      for (let part = 1; files.has(`${base}.wbf${part}`); part++) bytes += /** @type {File} */ (files.get(`${base}.wbf${part}`)).size;
      games.push({ id6: header.id6, title: folderTitle(folderName, header.id6) || header.title, folder: folderName, bytes });
    }
  }
  return games.sort((a, b) => a.title.localeCompare(b.title));
}

/**
 * Removes a game folder the copy created, if nothing is left in it.
 * @param {FileSystemDirectoryHandle} root
 * @param {string} name
 */
export async function removeIfEmpty(root, name) {
  try {
    const folder = await root.getDirectoryHandle(name);
    for await (const _ of folder.keys()) return;
    await root.removeEntry(name);
  } catch {
    // Already gone, or not empty: either way nothing to do.
  }
}

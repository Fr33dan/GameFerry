// Remembers the chosen folders and settings between visits, in this browser only (IndexedDB can hold folder handles).

const DB = 'gameferry';
const STORE = 'kv';

/** @type {Promise<IDBDatabase> | null} */
let connection = null;

/** Opens the database once and keeps it: reading a big library looks up every file in it. @returns {Promise<IDBDatabase>} */
function open() {
  if (!connection) {
    connection = new Promise((resolve, reject) => {
      const request = indexedDB.open(DB, 1);
      request.onupgradeneeded = () => request.result.createObjectStore(STORE);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    connection.catch(() => {
      connection = null; // try again next time
    });
  }
  return connection;
}

/** @param {string} key @returns {Promise<any>} */
export async function load(key) {
  try {
    const db = await open();
    return await new Promise((resolve, reject) => {
      const request = db.transaction(STORE).objectStore(STORE).get(key);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  } catch {
    return undefined;
  }
}

/** Several values in one go, undefined where there are none. @param {string[]} keys @returns {Promise<any[]>} */
export async function loadMany(keys) {
  try {
    const db = await open();
    return await new Promise((resolve, reject) => {
      const store = db.transaction(STORE).objectStore(STORE);
      const values = new Array(keys.length);
      let left = keys.length;
      if (left === 0) resolve(values);
      keys.forEach((key, i) => {
        const request = store.get(key);
        request.onsuccess = () => {
          values[i] = request.result;
          if (--left === 0) resolve(values);
        };
        request.onerror = () => reject(request.error);
      });
    });
  } catch {
    return new Array(keys.length);
  }
}

/** @param {string} key @param {any} value */
export async function save(key, value) {
  try {
    const db = await open();
    await new Promise((resolve, reject) => {
      const transaction = db.transaction(STORE, 'readwrite');
      transaction.objectStore(STORE).put(value, key);
      transaction.oncomplete = () => resolve(undefined);
      transaction.onerror = () => reject(transaction.error);
    });
  } catch {
    // Private windows can refuse storage; the folders then just have to be chosen again next time.
  }
}

/**
 * A remembered folder, if this browser still lets the page use it without asking.
 * @param {string} key
 * @param {'read' | 'readwrite'} mode
 * @returns {Promise<{handle: FileSystemDirectoryHandle, granted: boolean} | null>}
 */
export async function remembered(key, mode) {
  const handle = await load(key);
  if (!handle) return null;
  try {
    // @ts-ignore: queryPermission is Chromium's File System Access extension.
    return { handle, granted: (await handle.queryPermission({ mode })) === 'granted' };
  } catch {
    return null;
  }
}

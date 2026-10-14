// A promise-based client for the engine worker (worker.js, published with the WebAssembly engine).

/**
 * @typedef {object} Probe
 * @property {string} file
 * @property {string} [entry]      Name of the image inside an archive.
 * @property {number} [imageBytes] Size of the image (unpacked, for archives).
 * @property {string} [packing]
 * @property {string} [id6]
 * @property {string} [discTitle]
 * @property {string} [fileTitle]
 * @property {string} [kind]
 * @property {boolean} [nkit]
 * @property {string} [folder]     Folder name for the card: "Title [ID]".
 * @property {number} [estimatedBytes]
 * @property {string} [problem]    Why the file can't be copied, if it can't.
 */

/**
 * @typedef {object} TransferResult
 * @property {boolean} [ok]
 * @property {boolean} [cancelled]
 * @property {string} [error]
 * @property {number} [outputBytes]
 * @property {number} [parts]
 * @property {boolean} [crcMatches]
 * @property {string} [crc]
 * @property {string} [expectedCrc]
 * @property {number} [groupsChecked]
 * @property {number} [groupsFailing]
 * @property {string[]} [warnings]
 */

export class Engine {
  /** @param {string | URL} url */
  constructor(url = new URL('./worker.js', import.meta.url)) {
    this.worker = new Worker(url, { type: 'module' });
    this.nextId = 1;
    /** @type {Map<number, {resolve: (value: any) => void, reject: (error: Error) => void}>} */
    this.pending = new Map();
    /** @type {((source: number, written: number) => void) | null} */
    this.onProgress = null;

    /** @type {Promise<void>} Settles once the .NET runtime has loaded in the worker. */
    this.ready = new Promise((resolve, reject) => {
      this.worker.addEventListener('message', (event) => this.receive(event.data, resolve));
      this.worker.addEventListener('error', (event) => reject(new Error(event.message || 'The engine could not start.')));
    });
  }

  /** @param {any} message @param {() => void} ready */
  receive(message, ready) {
    if (message.kind === 'ready') {
      ready();
      return;
    }
    if (message.kind === 'progress') {
      this.onProgress?.(message.source, message.written);
      return;
    }
    const waiting = this.pending.get(message.id);
    if (!waiting) return;
    this.pending.delete(message.id);
    if (message.error) {
      // The name tells file errors apart, such as NotReadableError for a file that changed or a share that dropped.
      const error = new Error(message.error);
      if (message.name) error.name = message.name;
      waiting.reject(error);
    } else {
      waiting.resolve(message.result);
    }
  }

  /** @param {string} kind @param {object} payload */
  async call(kind, payload = {}) {
    await this.ready;
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.worker.postMessage({ id, kind, ...payload });
    });
  }

  /** @returns {Promise<{version: string, builtInKeys: boolean, hasKeys: boolean}>} */
  about() {
    return this.call('about');
  }

  /** @param {File} file @returns {Promise<Probe>} */
  probe(file) {
    return this.call('probe', { file });
  }

  /** @param {File} file @returns {Promise<{ok?: boolean, error?: string}>} */
  useKeys(file) {
    return this.call('keys', { file });
  }

  /**
   * Copies one game into `folder` as `baseName.wbfs` (and `.wbf1`... past the split size).
   * @param {File} file
   * @param {FileSystemDirectoryHandle} folder
   * @param {string} baseName
   * @param {object} options
   * @returns {Promise<TransferResult>}
   */
  transfer(file, folder, baseName, options) {
    return this.call('transfer', { file, folder, baseName, options });
  }

  cancel() {
    this.worker.postMessage({ kind: 'cancel' });
  }
}

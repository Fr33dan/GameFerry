// Lists of games: themed lists published with GameFerry, and lists people make themselves. A list only picks out games
// in the chosen folder; it holds titles and game IDs, nothing else.

/** Where the GameFerry lists are published: index.json names the list files, in the order they are offered. */
export const LISTS_URL = 'https://raw.githubusercontent.com/Fr33dan/GameFerry-lists/main/';

/** What the GameFerry lists are and how to add to them. */
export const LISTS_ABOUT = 'https://github.com/Fr33dan/GameFerry-lists';

/** The list format this page reads and writes. */
const FORMAT = 1;

/**
 * @typedef {object} ListGame
 * @property {string[]} ids  Game IDs or their starts: 3 characters match the game in every region, 6 one exact disc.
 * @property {string} title
 */

/**
 * @typedef {object} GameList
 * @property {string} id
 * @property {string} name
 * @property {string} description
 * @property {ListGame[]} games
 */

/**
 * Checks a list, from a file or from the GameFerry lists, and puts it in the shape the page uses. Throws an error that
 * says what is wrong.
 * @param {unknown} data The parsed JSON.
 * @returns {GameList}
 */
export function parseList(data) {
  if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error("it isn't a GameFerry list");
  const list = /** @type {Record<string, unknown>} */ (data);
  if (typeof list.format === 'number' && list.format > FORMAT) throw new Error('it was made by a newer GameFerry');
  if (!Array.isArray(list.games)) throw new Error('it has no games');
  if (list.games.length > 5000) throw new Error('it has more than 5,000 games');
  const games = list.games.map((game, i) => {
    const ids = [game?.id].flat().map((id) => (typeof id === 'string' ? id.trim().toUpperCase() : ''));
    if (ids.length === 0 || ids.length > 20 || !ids.every((id) => /^[A-Z0-9]{1,6}$/.test(id))) {
      throw new Error(`game ${i + 1} has no usable ID (IDs are up to six letters and digits)`);
    }
    const title = typeof game.title === 'string' && game.title.trim() ? game.title.trim().slice(0, 200) : ids[0];
    return { ids, title };
  });
  return {
    id: typeof list.id === 'string' && /^[\w-]{1,80}$/.test(list.id) ? list.id : '',
    name: typeof list.name === 'string' && list.name.trim() ? list.name.trim().slice(0, 80) : 'Untitled list',
    description: typeof list.description === 'string' ? list.description.trim().slice(0, 300) : '',
    games,
  };
}

/**
 * Fetches the GameFerry lists. Lists that can't be read are left out, as long as some can.
 * @param {string} base
 * @returns {Promise<GameList[]>}
 */
export async function fetchLists(base) {
  const get = async (/** @type {string} */ path) => {
    const response = await fetch(new URL(path, base), { cache: 'no-cache' });
    if (!response.ok) throw new Error(`${path}: ${response.status} ${response.statusText}`.trim());
    return response.json();
  };
  const index = await get('index.json');
  if (!Array.isArray(index?.lists)) throw new Error('index.json names no lists');
  const settled = await Promise.allSettled(index.lists.filter((p) => typeof p === 'string').map(async (path) => {
    const list = parseList(await get(path));
    list.id ||= path.replace(/^.*\/|\.json$/g, '');
    return list;
  }));
  const lists = settled.flatMap((s) => (s.status === 'fulfilled' ? [s.value] : []));
  if (lists.length === 0) throw new Error(settled.find((s) => s.status === 'rejected')?.reason?.message ?? 'no lists');
  return lists;
}

/**
 * Finds which of a list's games a disc is: the index of the first game whose ID it starts with, or -1.
 * @param {GameList} list
 * @returns {(id6: string) => number}
 */
export function matcher(list) {
  /** @type {Map<string, number>} */
  const index = new Map();
  list.games.forEach((game, i) => {
    for (const id of game.ids) if (!index.has(id)) index.set(id, i);
  });
  return (id6) => {
    for (let n = 1; n <= id6.length; n++) {
      const found = index.get(id6.slice(0, n));
      if (found !== undefined) return found;
    }
    return -1;
  };
}

/**
 * A list as a file in the GameFerry lists' format, one game per line, so it can be shared or added to them.
 * @param {GameList} list
 */
export function listFile(list) {
  const games = list.games.map((g) => `    ${JSON.stringify({ id: g.ids.length === 1 ? g.ids[0] : g.ids, title: g.title })}`);
  return `{\n  "format": ${FORMAT},\n  "id": ${JSON.stringify(list.id)},\n  "name": ${JSON.stringify(list.name)},\n`
    + `  "description": ${JSON.stringify(list.description)},\n  "games": [\n${games.join(',\n')}\n  ]\n}\n`;
}

/** A file name for a list: its name, without characters file systems refuse. @param {GameList} list */
export function listFileName(list) {
  return `${list.name.replace(/[<>:"/\\|?*\u0000-\u001f]+/g, ' ').replace(/\s+/g, ' ').trim() || 'list'}.json`;
}

import { appendFile, mkdir, realpath, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash, randomUUID } from 'node:crypto';

const artifacts = fileURLToPath(new URL('./artifacts/', import.meta.url));
const inside = (root, target) => {
  const relative = path.relative(root, target);
  return relative === '' || (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`));
};

function settle(value) {
  if (!Number.isInteger(value) || value < 0 || value > 500) throw new Error('settleMs must be an integer from 0 to 500.');
  return value;
}

/** Uses an initialized sky client and a Window returned by that client. */
export async function createComputerUseSession({ sky, window, evidenceDirectory, settleMs = 250, includeText = false }) {
  const defaultSettle = settle(settleMs);
  const root = await realpath(artifacts);
  const requested = path.resolve(evidenceDirectory);
  if (!inside(path.resolve(artifacts), requested)) throw new Error('evidenceDirectory must be inside tools/game-test/artifacts.');
  const evidence = await realpath(requested);
  if (!inside(root, evidence)) throw new Error('evidenceDirectory resolves outside tools/game-test/artifacts.');
  const directory = path.join(evidence, `ComputerUse-${randomUUID()}`);
  await mkdir(directory);
  const logPath = path.join(directory, 'Operations.jsonl');
  let state = null;
  let sequence = 0;
  let busy = false;
  const record = row => appendFile(logPath, `${JSON.stringify(row)}\n`, 'utf8');

  async function run(kind, args = {}) {
    if (busy) throw new Error('A Computer Use step is already running.');
    const delay = settle(args.settleMs ?? defaultSettle);
    const previous = state;
    if (kind !== 'observe' && !previous) throw new Error('Observe and inspect the current state before entering input.');
    let input = null;
    const previousScreenshotIds = previous?.screenshots?.map(item => item.id) ?? [];
    if (kind === 'click' || kind === 'scroll') {
      const screenshotId = args.screenshotId ?? previousScreenshotIds[0];
      if (!screenshotId || !previousScreenshotIds.includes(screenshotId)) throw new Error('screenshotId must belong to the latest state.');
      if (![args.x, args.y].every(Number.isFinite)) throw new Error('x and y must be coordinates from the latest screenshot.');
      input = { screenshotId, x: args.x, y: args.y };
      if (kind === 'scroll') {
        const scrollX = args.scrollX ?? 0;
        if (![scrollX, args.scrollY].every(Number.isFinite)) throw new Error('scrollX and scrollY must be finite numbers.');
        Object.assign(input, { scrollX, scrollY: args.scrollY });
      }
    } else if (kind === 'pressKey') {
      if (typeof args.key !== 'string' || !args.key.trim()) throw new Error('key must be a nonempty sky key name or chord.');
      input = { key: args.key };
    }
    busy = true;
    state = null;
    const step = ++sequence;
    const startedUtc = new Date().toISOString();
    const target = previous?.window ?? window;
    try {
      await record({ sequence: step, phase: 'started', utc: startedUtc, operation: kind, input, previousScreenshotIds, settleMs: delay });
      if (kind === 'click') await sky.click({ window: target, ...input });
      else if (kind === 'pressKey') await sky.press_key({ window: target, ...input });
      else if (kind === 'scroll') await sky.scroll({ window: target, ...input });
      if (delay) await new Promise(resolve => setTimeout(resolve, delay));
      const fresh = await sky.get_window_state({ window: target, include_screenshot: true, include_text: includeText });
      if (!fresh.screenshots?.length) throw new Error('sky returned no screenshot; observe again before entering input.');
      const pictures = [];
      for (const [index, screenshot] of fresh.screenshots.entries()) {
        const match = /^data:image\/(png|jpeg);base64,([\s\S]+)$/.exec(screenshot.url);
        if (!match) throw new Error('Expected a PNG or JPEG screenshot data URL from sky.');
        const bytes = Buffer.from(match[2], 'base64');
        const file = `${String(step).padStart(4, '0')}-${index}.${match[1] === 'jpeg' ? 'jpg' : 'png'}`;
        await writeFile(path.join(directory, file), bytes, { flag: 'wx' });
        pictures.push({ id: screenshot.id, file, sha256: createHash('sha256').update(bytes).digest('hex').toUpperCase(), bytes: bytes.length,
          width: screenshot.width, height: screenshot.height, originX: screenshot.originX, originY: screenshot.originY, zIndex: screenshot.zIndex });
      }
      await record({ sequence: step, phase: 'completed', utc: new Date().toISOString(), operation: kind,
        window: fresh.window, screenshots: pictures, accessibility: fresh.accessibility });
      state = fresh;
      window = fresh.window;
      return fresh;
    } catch (error) {
      await record({ sequence: step, phase: 'error', utc: new Date().toISOString(), operation: kind, error: String(error) });
      throw error;
    } finally {
      busy = false;
    }
  }

  return Object.freeze({
    directory,
    logPath,
    observe: (options = {}) => run('observe', options),
    click: options => run('click', options),
    pressKey: (key, options = {}) => run('pressKey', { ...options, key }),
    scroll: options => run('scroll', options),
  });
}

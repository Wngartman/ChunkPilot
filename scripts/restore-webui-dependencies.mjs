import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { pipeline } from 'node:stream/promises';
import { Readable } from 'node:stream';
import { basename, dirname, join, resolve, sep } from 'node:path';
import { spawnSync } from 'node:child_process';

const projectRoot = resolve(process.argv[2] || new URL('..', import.meta.url).pathname.replace(/^\/(?:[A-Za-z]:)/, value => value.slice(1)));
const webUi = join(projectRoot, 'src', 'ChunkPilot.WebUi');
const destination = resolve(process.argv[3] || join(projectRoot, 'temp', 'webui-manual', 'node_modules'));
if (!destination.toLowerCase().startsWith((projectRoot + sep).toLowerCase())) {
  throw new Error(`Dependency destination must remain inside the repository: ${destination}`);
}

const lock = JSON.parse(await readFile(join(webUi, 'package-lock.json'), 'utf8'));
const workerIndex = Number(process.argv[4] || 0);
const workerCount = Number(process.argv[5] || 1);
if (!Number.isInteger(workerIndex) || !Number.isInteger(workerCount) || workerIndex < 0 || workerCount < 1 || workerIndex >= workerCount)
  throw new Error('Worker index/count is invalid.');
const allPackages = Object.entries(lock.packages)
  .filter(([key, value]) => key.startsWith('node_modules/') && value.resolved)
  .filter(([, value]) => !Array.isArray(value.os) || value.os.includes('win32'))
  .filter(([, value]) => !Array.isArray(value.cpu) || value.cpu.includes('x64'))
  .sort(([left], [right]) => left.localeCompare(right));
const packages = allPackages.filter((_, index) => index % workerCount === workerIndex);
const cache = join(projectRoot, 'temp', 'webui-package-cache');
await mkdir(destination, { recursive: true });
await mkdir(cache, { recursive: true });

for (const [index, [key, descriptor]] of packages.entries()) {
  const relative = key.slice('node_modules/'.length);
  const target = join(destination, ...relative.split('/'));
  try {
    await stat(join(target, 'package.json'));
    console.log(`[${index + 1}/${packages.length}] present ${relative}`);
    continue;
  } catch { /* restore it */ }
  await mkdir(target, { recursive: true });
  const archive = join(cache, `${createHash('sha256').update(descriptor.resolved).digest('hex')}.tgz`);
  try {
    await stat(archive);
    console.log(`[${index + 1}/${packages.length}] cached ${relative}`);
  } catch {
    console.log(`[${index + 1}/${packages.length}] download ${relative}`);
    const response = await fetch(descriptor.resolved);
    if (!response.ok || !response.body) throw new Error(`Download failed (${response.status}) for ${relative}`);
    await pipeline(Readable.fromWeb(response.body), createWriteStream(archive));
  }
  const integrity = String(descriptor.integrity || '').split(/\s+/).find(value => value.startsWith('sha512-'));
  if (integrity) {
    const hash = createHash('sha512');
    await pipeline(createReadStream(archive), hash);
    const actual = hash.digest('base64');
    if (actual !== integrity.slice('sha512-'.length)) throw new Error(`Integrity check failed for ${relative}`);
  }
  const result = spawnSync('tar.exe', ['-xf', archive, '-C', target, '--strip-components=1'], { stdio: 'inherit' });
  if (result.status !== 0) throw new Error(`Could not extract ${relative}`);
}

const binRoot = join(destination, '.bin');
await mkdir(binRoot, { recursive: true });
for (const [key] of packages) {
  const relative = key.slice('node_modules/'.length);
  const manifestPath = join(destination, ...relative.split('/'), 'package.json');
  let manifest;
  try { manifest = JSON.parse(await readFile(manifestPath, 'utf8')); } catch { continue; }
  if (!manifest.bin) continue;
  const commands = typeof manifest.bin === 'string' ? { [manifest.name]: manifest.bin } : manifest.bin;
  for (const [command, executable] of Object.entries(commands)) {
    const pathFromBin = join('..', ...relative.split('/'), ...String(executable).split('/'));
    await writeFile(join(binRoot, `${command}.cmd`), `@ECHO OFF\r\nnode "%~dp0${pathFromBin}" %*\r\n`, 'utf8');
  }
}

if (workerCount === 1) {
  const lockHash = createHash('sha256').update(await readFile(join(webUi, 'package-lock.json'))).digest('hex');
  await writeFile(join(destination, '.chunkpilot-lock-sha256'), `${lockHash}\n`, 'utf8');
}

console.log(`WebUI dependency worker ${workerIndex + 1}/${workerCount} restored ${packages.length} integrity-checked packages at ${destination}`);

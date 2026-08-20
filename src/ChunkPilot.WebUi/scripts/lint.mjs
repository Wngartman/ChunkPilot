import { readdir, readFile } from 'node:fs/promises';
import { extname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../src/', import.meta.url));
const failures = [];

async function visit(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) await visit(path);
    else if (['.ts', '.tsx', '.css'].includes(extname(path))) inspect(path, await readFile(path, 'utf8'));
  }
}

function inspect(path, source) {
  const name = relative(root, path);
  const rules = [
    [/\beval\s*\(/g, 'eval is prohibited'],
    [/dangerouslySetInnerHTML/g, 'raw HTML injection is prohibited'],
    [/https?:\/\/(?!chunkpilot\.local)/g, 'remote runtime assets are prohibited'],
    [/from\s+['"](?:@mui|antd|bootstrap|framer-motion|react-icons)/g, 'unapproved UI or icon library']
  ];
  for (const [pattern, message] of rules) {
    if (pattern.test(source)) failures.push(`${name}: ${message}`);
  }
  if (path.endsWith('.tsx') && /#[0-9a-f]{3,8}\b/i.test(source))
    failures.push(`${name}: page-local color literal; use a design token`);
  if (path.endsWith('.tsx') && /\bAgent (?:ready|connected|disconnected|unavailable)\b/i.test(source))
    failures.push(`${name}: internal Agent status is prohibited in normal product copy`);
}

await visit(root);
if (failures.length) {
  console.error(failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log('WebUI source policy checks passed.');
}

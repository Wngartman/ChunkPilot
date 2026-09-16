export const memoryPresets = [1024, 2048, 3072, 4096, 6144, 8192, 10240, 12288, 16384, 20480, 24576] as const;

export interface MemoryParseResult {
  valid: boolean;
  mebibytes: number | null;
  error: string | null;
  normalized: string | null;
}

export function formatMemory(mebibytes: number): string {
  const gigabytes = mebibytes / 1024;
  return `${mebibytes.toLocaleString()} MB (${Number.isInteger(gigabytes) ? gigabytes : gigabytes.toFixed(2).replace(/0+$/, '').replace(/\.$/, '')} GB)`;
}

export function formatGigabytes(mebibytes: number, locale?: string): string {
  const gigabytes = mebibytes / 1024;
  for (let decimals = 0; decimals <= 4; decimals++) {
    const rounded = Number(gigabytes.toFixed(decimals));
    if (Math.round(rounded * 1024) === mebibytes)
      return new Intl.NumberFormat(locale, { useGrouping: false, maximumFractionDigits: decimals }).format(rounded);
  }
  return new Intl.NumberFormat(locale, { useGrouping: false, maximumFractionDigits: 10 }).format(gigabytes);
}

export function parseMemory(text: string, minimumMib = 512, maximumMib = 24 * 1024, locale?: string): MemoryParseResult {
  const input = text.trim();
  if (!input) return invalid('Enter a memory amount.');
  const match = /^(?<value>[+-]?(?:\d(?:[\d.,\u00a0\u202f ]*\d)?|[.,]\d+))\s*(?<unit>mib|mb|gib|gb)?(?:\s*\([^)]*\))?$/i.exec(input);
  if (!match?.groups) return invalid('Enter an amount such as 4096 MB, 4 GB, or 6.5 GB.');
  const unit = (match.groups.unit ?? 'mb').toLowerCase();
  const parts = new Intl.NumberFormat(locale).formatToParts(12345.6);
  const decimal = parts.find(part => part.type === 'decimal')?.value ?? '.';
  const group = parts.find(part => part.type === 'group')?.value;
  let number = match.groups.value;
  if (group && number.includes(group)) {
    // Decimal GB entry never accepts grouping: "1,5" must not become 15 GB.
    const grouped = new RegExp(`^[+-]?\\d{1,3}(?:${escapeRegex(group)}\\d{3})+(?:${escapeRegex(decimal)}\\d+)?$`);
    if (unit === 'gb' || unit === 'gib' || !grouped.test(number))
      return invalid('Use a decimal memory amount without thousands separators.');
    number = number.replaceAll(group, '');
  }
  const decimalNumber = new RegExp(`^[+-]?(?:\\d+(?:${escapeRegex(decimal)}\\d+)?|${escapeRegex(decimal)}\\d+)$`);
  if (!decimalNumber.test(number)) return invalid('Use the decimal separator for your region.');
  const amount = Number(number.replace(decimal, '.'));
  if (!Number.isFinite(amount) || amount <= 0) return invalid('Memory must be greater than zero.');
  const rawMib = unit === 'gb' || unit === 'gib' ? amount * 1024 : amount;
  const mebibytes = Math.round(rawMib);
  if (!Number.isSafeInteger(mebibytes)) return invalid('That memory amount is too large.');
  if (mebibytes < minimumMib) return invalid(`Memory must be at least ${formatMemory(minimumMib)}.`);
  if (mebibytes > maximumMib) return invalid(`Memory cannot exceed ${formatMemory(maximumMib)}.`);
  return { valid: true, mebibytes, error: null, normalized: formatMemory(mebibytes) };
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

export function hostMemoryWarning(mebibytes: number, hostTotalBytes: number | null | undefined): string | null {
  if (!hostTotalBytes || hostTotalBytes <= 0) return null;
  const totalMib = hostTotalBytes / 1024 / 1024;
  return mebibytes > totalMib * 0.75
    ? `This reserves more than 75% of this computer's ${formatMemory(Math.round(totalMib))} total memory. Leave room for Windows and players' client software.`
    : null;
}

function invalid(error: string): MemoryParseResult {
  return { valid: false, mebibytes: null, error, normalized: null };
}

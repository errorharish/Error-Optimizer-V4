// Shared heading casing: page/section/modal titles are authored in ALL CAPS across the app;
// the typography system shows them in Title Case while keeping known acronyms intact.
const ACRONYMS = new Set([
  "CPU", "GPU", "RAM", "SSD", "HDD", "NVME", "OS", "PC", "UI", "UX", "FPS", "DNS", "TCP", "IP",
  "USB", "BIOS", "UEFI", "HPET", "MSI", "API", "IO", "I/O", "DPC", "ISR", "QOS", "VRAM", "ADB",
  "HAGS", "NIC", "LAN", "WIFI", "SMB", "VPN", "XMP", "AMD", "NVIDIA", "BCD", "TRIM", "AI", "ID",
  "WPF", "GB", "MB", "KB", "TB", "MS", "HZ", "LDPLAYER", "MEMU", "VM", "VBS", "HVCI",
]);
const SMALL = new Set(["a", "an", "and", "as", "at", "by", "for", "in", "of", "on", "or", "the", "to", "vs", "via", "with"]);

function word(w: string, first: boolean): string {
  const core = w.replace(/[^A-Za-z0-9/]/g, "");
  if (!core) return w;
  if (/\d/.test(core) || ACRONYMS.has(core.toUpperCase())) return w;
  const lower = w.toLowerCase();
  if (!first && SMALL.has(core.toLowerCase())) return lower;
  return lower.replace(/[a-z]/, (c) => c.toUpperCase());
}

/** Title-cases an all-caps heading; mixed-case strings are returned unchanged. */
export function headingCase(text: string): string {
  if (!/[A-Z]/.test(text) || /[a-z]/.test(text)) return text;
  return text
    .split(/(\s+|-)/)
    .map((part, i) => (/^(\s+|-)$/.test(part) ? part : word(part, i === 0)))
    .join("");
}

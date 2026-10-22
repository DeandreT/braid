/** Presentation of metadata only. Never apply these formatters to payload values. */
export function formatByteCount(value: unknown): string {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0)
    return "Size unavailable";
  if (value < 1024) return `${value} ${value === 1 ? "byte" : "bytes"}`;
  const units = ["KiB", "MiB", "GiB", "TiB", "PiB"];
  let size = value / 1024,
    index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index++;
  }
  return `${new Intl.NumberFormat("en", { maximumFractionDigits: 1 }).format(size)} ${units[index]}`;
}

export function formatUtcTimestamp(value: unknown): string {
  if (typeof value !== "string") return "Time unavailable";
  const parts =
    /^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.\d{1,9})?(?:Z|[+-]\d\d:\d\d)$/.exec(
      value,
    );
  if (!parts) return "Time unavailable";
  const [year, month, day, hour, minute, second] = parts.slice(1).map(Number);
  const leap = year! % 4 === 0 && (year! % 100 !== 0 || year! % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (
    month! < 1 ||
    month! > 12 ||
    day! < 1 ||
    day! > days[month! - 1]! ||
    hour! > 23 ||
    minute! > 59 ||
    second! > 59
  )
    return "Time unavailable";
  const date = new Date(value);
  if (!Number.isFinite(date.valueOf())) return "Time unavailable";
  return (
    new Intl.DateTimeFormat("en", {
      timeZone: "UTC",
      year: "numeric",
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
      hourCycle: "h23",
    }).format(date) + " UTC"
  );
}

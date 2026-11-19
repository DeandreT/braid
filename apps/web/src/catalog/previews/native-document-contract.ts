const formats = [
  ["flextext", "FlexText", "text/plain", "txt"],
  [
    "xlsx",
    "Portable workbook",
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    "xlsx",
  ],
  ["protobuf", "Protocol Buffers", "application/x-protobuf", "pb"],
  ["xbrl", "XBRL instance", "application/xbrl+xml", "xbrl"],
] as const;
export const nativeFormats = formats.map(([key, label, media, extension]) => ({
  key,
  label,
  media,
  extension,
  profile: `braid.ferrule-${key}-preview.v1` as const,
  schema: `braid.${key}-settings.v1`,
}));
export const nativeProfile = (value: unknown) =>
  nativeFormats.find((f) => f.profile === value);
export const nativeMedia = (value: unknown) =>
  nativeFormats.find((f) => f.media === value);
export function nativeSettings(media: unknown, settings: unknown): boolean {
  const format = nativeMedia(media);
  if (
    !format ||
    !settings ||
    typeof settings !== "object" ||
    Array.isArray(settings)
  )
    return false;
  const s = settings as Record<string, unknown>;
  if (
    Object.keys(s).length !== 2 ||
    s["schema"] !== format.schema ||
    typeof s["optionsJson"] !== "string" ||
    new TextEncoder().encode(s["optionsJson"]).length > 16384
  )
    return false;
  try {
    const options: unknown = JSON.parse(s["optionsJson"]);
    return !!options && typeof options === "object" && !Array.isArray(options);
  } catch {
    return false;
  }
}

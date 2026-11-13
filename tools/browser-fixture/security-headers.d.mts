export const applicationCsp: string;
export const browserSecurityHeaders: Readonly<Record<string, string>>;
export function missingSecurityHeaders(
  headers: Record<string, string>,
  csp?: string,
): string[];

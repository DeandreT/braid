// Keep the independent browser expectation aligned with BrowserSecurityHeaders.cs.
export const applicationCsp =
  "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; worker-src 'self'; child-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
export const browserSecurityHeaders = Object.freeze({
  "Content-Security-Policy": applicationCsp,
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "no-referrer",
  "Cross-Origin-Opener-Policy": "same-origin",
  "Cross-Origin-Resource-Policy": "same-origin",
});

export function missingSecurityHeaders(headers, csp = applicationCsp) {
  const normalized = Object.fromEntries(
    Object.entries(headers).map(([name, value]) => [name.toLowerCase(), value]),
  );
  return Object.entries({
    ...browserSecurityHeaders,
    "Content-Security-Policy": csp,
  })
    .filter(([name, value]) => normalized[name.toLowerCase()] !== value)
    .map(([name]) => name);
}

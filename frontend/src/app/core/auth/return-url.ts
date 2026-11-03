/**
 * Accepts only same-app absolute paths ("/x"), never protocol-relative ("//host"), backslash or scheme URLs,
 * so a crafted `returnUrl` cannot send a freshly signed-in user to another origin.
 */
export function safeReturnUrl(value: string | null | undefined): string | null {
  if (!value || value.length > 2048 || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) return null;
  if (/[\u0000-\u001f\u007f\\]/.test(value)) return null;
  return value;
}

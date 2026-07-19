export async function api(path, options = {}) {
  const response = await fetch(path, { headers: { 'content-type':'application/json', ...(options.headers || {}) }, ...options });
  if (response.status === 204) return null;
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(body.detail || body.title || 'Request failed'), { code: body.code, status: response.status, body });
  return body;
}
export const newId = () => crypto.randomUUID();

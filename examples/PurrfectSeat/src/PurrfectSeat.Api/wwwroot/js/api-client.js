const browserBridge = async () => window.purrfectSeatWasmReady ? await window.purrfectSeatWasmReady : null;

export async function api(path, options = {}) {
  const bridge = await browserBridge();
  if (bridge) return bridge.request(path, options);
  const response = await fetch(path, { headers: { 'content-type':'application/json', ...(options.headers || {}) }, ...options });
  if (response.status === 204) return null;
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(body.detail || body.title || 'Request failed'), { code: body.code, status: response.status, body });
  return body;
}

export async function subscribe(path, onMessage, onState) {
  const bridge = await browserBridge();
  if (bridge) return bridge.subscribe(path, onMessage, onState);
  const source = new EventSource(path);
  source.addEventListener('availability', onMessage);
  source.onopen = () => onState?.('connected');
  source.onerror = () => onState?.('reconnecting');
  return () => source.close();
}
export const newId = () => crypto.randomUUID();

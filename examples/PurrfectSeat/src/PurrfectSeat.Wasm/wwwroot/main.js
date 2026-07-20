import { dotnet } from './_framework/dotnet.js';

export const ready = (async () => {
  const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
  const config = getConfig();
  const exports = await getAssemblyExports(config.mainAssemblyName);
  await runMain();
  const service = exports.PurrfectSeat?.Wasm?.Services?.WasmBookingBridge;
  if (!service || typeof service.ListPerformancesJson !== 'function') {
    const availableExports = Object.entries(exports)
      .map(([name, value]) => `${name}: ${Object.keys(value).join(', ')}`)
      .join('; ');
    const availableBridgeExports = service ? Object.keys(service).join(', ') : 'none';
    throw new Error(`PurrfectSeat browser bridge was not exported. Available exports: ${availableExports}; bridge exports: ${availableBridgeExports}`);
  }

  const parseResponse = text => JSON.parse(text);
  const success = response => response.statusCode >= 200 && response.statusCode < 300;
  const request = async (path, options = {}) => {
    const method = (options.method || 'GET').toUpperCase();
    const body = options.body || '{}';
    let response;
    if (method === 'GET' && path === '/performances') {
      return JSON.parse(await service.ListPerformancesJson());
    }

    const availability = path.match(/^\/performances\/([^/]+)\/availability$/);
    const hold = path.match(/^\/performances\/([^/]+)\/holds$/);
    const confirmation = path.match(/^\/performances\/([^/]+)\/holds\/([^/]+)\/confirm$/);
    const cancellation = path.match(/^\/performances\/([^/]+)\/holds\/([^/]+)$/);
    if (method === 'GET' && availability) response = parseResponse(await service.GetAvailabilityJson(availability[1]));
    else if (method === 'POST' && hold) response = parseResponse(await service.PlaceHoldJson(hold[1], body));
    else if (method === 'POST' && confirmation) response = parseResponse(await service.ConfirmHoldJson(confirmation[1], confirmation[2], body));
    else if (method === 'DELETE' && cancellation) response = parseResponse(await service.CancelHoldJson(cancellation[1], cancellation[2]));
    else throw new Error(`Unsupported browser-WASM API route: ${method} ${path}`);

    if (!success(response)) {
      throw Object.assign(new Error(response.message || 'Request failed'), { code: response.code, status: response.statusCode, body: response });
    }

    return response.value ?? null;
  };

  const bridge = {
    request,
    subscribe: (path, onMessage, onState) => {
      onState?.('connected');
      const poll = () => request(path).then(onMessage).catch(() => onState?.('reconnecting'));
      const timer = window.setInterval(poll, 1000);
      return () => window.clearInterval(timer);
    }
  };

  window.purrfectSeatWasm = bridge;
  return bridge;
})();

mergeInto(LibraryManager.library, {
  GuaUnityWebInstall: function (hostNamePointer, ownerIdPointer, timeoutMs) {
    const hostName = UTF8ToString(hostNamePointer);
    const ownerId = UTF8ToString(ownerIdPointer);
    const previous = globalThis.__guaUnityWebState;
    if (previous && typeof previous.uninstall === 'function') {
      previous.uninstall('engine_unsupported', 'The Unity WebGL Gua runtime was replaced.');
    }

    const pending = new Map();
    const callTimeoutMs = Number.isFinite(timeoutMs) && timeoutMs >= 0 ? timeoutMs : 5000;
    const state = { ownerId, pending, disposed: false, uninstall: null };

    const cancelHostCall = function (callId) {
      try { SendMessage(hostName, 'HandleWebCancellation', String(callId)); } catch (_error) { }
    };

    const takePending = function (callId) {
      const entry = pending.get(callId);
      if (!entry) return null;
      pending.delete(callId);
      clearTimeout(entry.timer);
      if (entry.signal) entry.signal.removeEventListener('abort', entry.aborted);
      return entry;
    };

    const rejectPending = function (code, message) {
      for (const callId of Array.from(pending.keys())) {
        const entry = takePending(callId);
        if (!entry) continue;
        cancelHostCall(callId);
        entry.reject({ code, message });
      }
    };

    const worldSelectorError = function (command) {
      if (!command || command.type !== 'query_world_objects') return null;
      const own = function (name) { return Object.prototype.hasOwnProperty.call(command, name); };
      const hasRelative = own('relativeToObjectId');
      const hasMaxDistance = own('maxDistance');
      const hasLimit = own('limit');
      if (hasRelative !== hasMaxDistance || (hasRelative && (typeof command.relativeToObjectId !== 'string' ||
          command.relativeToObjectId.length === 0 || typeof command.maxDistance !== 'number' ||
          !Number.isFinite(command.maxDistance) || command.maxDistance < 0)) ||
          (hasLimit && (!hasRelative || !Number.isInteger(command.limit) || command.limit < 1 || command.limit > 4294967295))) {
        return 'World spatial criteria require relativeToObjectId, a finite non-negative maxDistance, and an optional positive limit.';
      }
      const stateFields = ['stateKey', 'stateType', 'stateString', 'stateNumber', 'stateBool'];
      if (!stateFields.some(own)) return null;
      if (!own('stateKey') || typeof command.stateKey !== 'string' || command.stateKey.length === 0 ||
          !own('stateType') || !Number.isInteger(command.stateType) || command.stateType < 0 || command.stateType > 3) {
        return 'World state criteria require a non-empty stateKey and a valid stateType.';
      }
      const expectedValueField = command.stateType === 0 ? null
        : command.stateType === 1 ? 'stateString'
        : command.stateType === 2 ? 'stateNumber' : 'stateBool';
      for (const name of ['stateString', 'stateNumber', 'stateBool']) {
        if (own(name) !== (name === expectedValueField)) return 'World state criterion fields conflict with stateType.';
      }
      if (expectedValueField === 'stateString' && typeof command.stateString !== 'string') return 'stateString must be a string.';
      if (expectedValueField === 'stateNumber' && (typeof command.stateNumber !== 'number' || !Number.isFinite(command.stateNumber))) {
        return 'stateNumber must be a finite number.';
      }
      if (expectedValueField === 'stateBool' && typeof command.stateBool !== 'boolean') return 'stateBool must be a boolean.';
      return null;
    };

    state.uninstall = function (code, message) {
      if (state.disposed) return;
      state.disposed = true;
      rejectPending(code, message);
      if (globalThis.__guaUnityWebState !== state) return;
      delete globalThis.__guaUnityWebState;
      delete globalThis.__guaUnityWebPort;
      delete globalThis.__guaUnityWebResolveInternal;
    };

    globalThis.__guaUnityWebState = state;
    globalThis.__guaUnityWebResolveInternal = function (resolvedOwnerId, callId, payload, failed) {
      if (resolvedOwnerId !== ownerId || state.disposed) return;
      const entry = takePending(callId);
      if (!entry) return;
      try {
        const value = JSON.parse(payload);
        if (failed) entry.reject(value); else entry.resolve(value);
      } catch (_error) {
        entry.reject({ code: 'invalid_request', message: 'Unity WebGL returned malformed Gua JSON.' });
      }
    };
    globalThis.__guaUnityWebPort = {
      capabilities: ['observe_v1'],
      __guaOwnerId: ownerId,
      invoke(command, options) {
        if (state.disposed) return Promise.reject({ code: 'engine_unsupported', message: 'The Unity WebGL Gua runtime is unavailable.' });
        if (command.type === 'get_screenshot') return Promise.reject({ code: 'engine_unsupported', message: 'Unity WebGL screenshot readback is not enabled.' });
        const selectorError = worldSelectorError(command);
        if (selectorError) return Promise.reject({ code: 'invalid_request', message: selectorError });
        const signal = options && options.signal;
        const requestedTimeoutMs = options && options.timeoutMs;
        if (signal && signal.aborted) return Promise.reject({ code: 'aborted', message: 'The Unity WebGL Gua call was aborted.' });
        if (requestedTimeoutMs !== undefined && (!Number.isInteger(requestedTimeoutMs) || requestedTimeoutMs < 0 || requestedTimeoutMs > 2147483647)) {
          return Promise.reject({ code: 'invalid_request', message: 'Unity WebGL timeoutMs must be an integer from 0 to 2147483647.' });
        }
        const effectiveTimeoutMs = requestedTimeoutMs === undefined ? callTimeoutMs : requestedTimeoutMs;
        const callId = globalThis.__guaUnityWebNextCallId || 1;
        globalThis.__guaUnityWebNextCallId = callId + 1;
        return new Promise((resolve, reject) => {
          const timer = setTimeout(() => {
            const entry = takePending(callId);
            if (!entry) return;
            cancelHostCall(callId);
            entry.reject({ code: 'timeout', message: 'Timed out waiting for Unity WebGL host completion.' });
          }, effectiveTimeoutMs);
          const aborted = function () {
            const entry = takePending(callId);
            if (!entry) return;
            cancelHostCall(callId);
            entry.reject({ code: 'aborted', message: 'The Unity WebGL Gua call was aborted.' });
          };
          pending.set(callId, { resolve, reject, timer, signal, aborted });
          if (signal) signal.addEventListener('abort', aborted, { once: true });
          if (signal && signal.aborted) {
            aborted();
            return;
          }
          try {
            SendMessage(hostName, 'HandleWebRequest', JSON.stringify({ callId, command, commandFields: Object.keys(command) }));
          } catch (_error) {
            const entry = takePending(callId);
            if (entry) entry.reject({ code: 'engine_unsupported', message: `Unity WebGL could not reach the Gua runtime '${hostName}'.` });
          }
        });
      }
    };
  },
  GuaUnityWebUninstall: function (ownerIdPointer) {
    const ownerId = UTF8ToString(ownerIdPointer);
    const state = globalThis.__guaUnityWebState;
    if (state && state.ownerId === ownerId && typeof state.uninstall === 'function') {
      state.uninstall('engine_unsupported', 'The Unity WebGL Gua runtime was destroyed.');
    }
  },
  GuaUnityWebResolve: function (ownerIdPointer, callId, jsonPointer, failed) {
    const resolve = globalThis.__guaUnityWebResolveInternal;
    if (typeof resolve === 'function') resolve(UTF8ToString(ownerIdPointer), callId, UTF8ToString(jsonPointer), failed);
  }
});

extends RefCounted

# Same-page Godot Web Export port consumed by gua-webmcp. It deliberately has
# no WebSocket endpoint and uses the adapter's request-correlated event queue.

var adapter_ref: WeakRef
var get_tree_callback: JavaScriptObject
var get_world_tree_callback: JavaScriptObject
var query_world_callback: JavaScriptObject
var enqueue_callback: JavaScriptObject
var poll_callback: JavaScriptObject
var cancel_callback: JavaScriptObject
var get_game_input_capabilities_callback: JavaScriptObject
var get_game_input_actions_v2_callback: JavaScriptObject
var get_game_input_actions_callback: JavaScriptObject
var find_game_input_actions_v2_callback: JavaScriptObject
var find_game_input_actions_callback: JavaScriptObject
var get_game_input_state_callback: JavaScriptObject
var enqueue_game_input_callback: JavaScriptObject
var poll_game_input_callback: JavaScriptObject
var release_game_input_callback: JavaScriptObject
var bridge_owner_id := ""
var game_input_owner_id := 0
var attached := false
var observe_callback: JavaScriptObject
var release_observe_callback: JavaScriptObject
var observe_client_id := 0


func attach(gua_adapter: RefCounted) -> bool:
	detach()
	adapter_ref = weakref(gua_adapter)
	if not OS.has_feature("web"):
		return false
	var window := JavaScriptBridge.get_interface("window")
	if window == null:
		return false
	get_tree_callback = JavaScriptBridge.create_callback(_get_tree)
	get_world_tree_callback = JavaScriptBridge.create_callback(_get_world_tree)
	query_world_callback = JavaScriptBridge.create_callback(_query_world)
	enqueue_callback = JavaScriptBridge.create_callback(_enqueue_action)
	poll_callback = JavaScriptBridge.create_callback(_poll_action)
	cancel_callback = JavaScriptBridge.create_callback(_cancel_action)
	get_game_input_capabilities_callback = JavaScriptBridge.create_callback(_get_game_input_capabilities)
	get_game_input_actions_v2_callback = JavaScriptBridge.create_callback(_get_game_input_actions_v2)
	get_game_input_actions_callback = JavaScriptBridge.create_callback(_get_game_input_actions)
	find_game_input_actions_v2_callback = JavaScriptBridge.create_callback(_find_game_input_actions_v2)
	find_game_input_actions_callback = JavaScriptBridge.create_callback(_find_game_input_actions)
	get_game_input_state_callback = JavaScriptBridge.create_callback(_get_game_input_state)
	enqueue_game_input_callback = JavaScriptBridge.create_callback(_enqueue_game_input)
	poll_game_input_callback = JavaScriptBridge.create_callback(_poll_game_input)
	release_game_input_callback = JavaScriptBridge.create_callback(_release_game_input_owner)
	observe_callback = JavaScriptBridge.create_callback(_observe_command)
	release_observe_callback = JavaScriptBridge.create_callback(_release_observe_client)
	window.__guaGodotObserve = observe_callback
	window.__guaGodotReleaseObserve = release_observe_callback
	observe_client_id = gua_adapter.context.create_observe_client(1)
	window.__guaGodotGetTree = get_tree_callback
	window.__guaGodotGetWorldTree = get_world_tree_callback
	window.__guaGodotQueryWorld = query_world_callback
	window.__guaGodotEnqueueAction = enqueue_callback
	window.__guaGodotPollAction = poll_callback
	window.__guaGodotCancelAction = cancel_callback
	window.__guaGodotGetGameInputCapabilities = get_game_input_capabilities_callback
	window.__guaGodotGetGameInputActionsV2 = get_game_input_actions_v2_callback
	window.__guaGodotGetGameInputActions = get_game_input_actions_callback
	window.__guaGodotFindGameInputActionsV2 = find_game_input_actions_v2_callback
	window.__guaGodotFindGameInputActions = find_game_input_actions_callback
	window.__guaGodotGetGameInputState = get_game_input_state_callback
	window.__guaGodotEnqueueGameInput = enqueue_game_input_callback
	window.__guaGodotPollGameInput = poll_game_input_callback
	window.__guaGodotReleaseGameInput = release_game_input_callback
	game_input_owner_id = gua_adapter.create_game_input_owner() if not _game_input_capabilities(gua_adapter).is_empty() else 0
	bridge_owner_id = str(get_instance_id())
	JavaScriptBridge.eval("""
(() => {
  const engineError = (code, message) => Object.assign(new Error(message), { code });
  const previousPort = globalThis.__guaGodotWebPort;
  if (previousPort && typeof previousPort.__guaUninstall === 'function') previousPort.__guaUninstall();
  const getTree = globalThis.__guaGodotGetTree;
  const observe = globalThis.__guaGodotObserve;
  const releaseObserve = globalThis.__guaGodotReleaseObserve;
  const getWorldTree = globalThis.__guaGodotGetWorldTree;
  const queryWorld = globalThis.__guaGodotQueryWorld;
  const enqueueAction = globalThis.__guaGodotEnqueueAction;
  const pollAction = globalThis.__guaGodotPollAction;
  const cancelAction = globalThis.__guaGodotCancelAction;
  const getGameInputCapabilities = globalThis.__guaGodotGetGameInputCapabilities;
  const getGameInputActionsV2 = globalThis.__guaGodotGetGameInputActionsV2;
  const findGameInputActionsV2 = globalThis.__guaGodotFindGameInputActionsV2;
  const getGameInputActions = globalThis.__guaGodotGetGameInputActions;
  const findGameInputActions = globalThis.__guaGodotFindGameInputActions;
  const getGameInputState = globalThis.__guaGodotGetGameInputState;
  const enqueueGameInput = globalThis.__guaGodotEnqueueGameInput;
  const pollGameInput = globalThis.__guaGodotPollGameInput;
  const releaseGameInput = globalThis.__guaGodotReleaseGameInput;
  const callGodot = (callback, ...args) => {
    const response = {};
    callback(response, ...args);
    return response.value;
  };
  const pending = new Map();
  const pendingGameInputs = new Map();
  let disposed = false;
  const releasePendingGameInputs = (causeRequestId, causeError) => {
    try { callGodot(releaseGameInput, "1"); } catch (_) {}
    for (const [requestId, call] of [...pendingGameInputs.entries()]) {
      const error = requestId === causeRequestId
        ? causeError
        : engineError('aborted', 'The page-owned Godot game input session was released because another call ended.');
      call.fail(error);
    }
  };
  const port = {
    capabilities: ['observe_v1'],
    __guaOwnerId: "%s",
    __guaUninstall() {
      if (disposed) return;
      disposed = true;
      const error = engineError('engine_unsupported', 'The Godot Gua adapter is no longer available.');
      for (const call of pending.values()) call.cancelOrDrain(error);
      for (const call of [...pendingGameInputs.values()]) call.fail(error);
      try { callGodot(releaseGameInput); } catch (_) {}
      try { callGodot(releaseObserve); } catch (_) {}
    },
    async invoke(command, options) {
      if (disposed) throw engineError('engine_unsupported', 'The Godot Gua adapter is no longer available.');
      if (!command || typeof command.type !== 'string') throw engineError('invalid_request', 'Missing Gua in-page command.');
      if (['get_observe_snapshot', 'subscribe_observations', 'poll_observations', 'unsubscribe_observations'].includes(command.type)) {
        const needsId = command.type === 'poll_observations' || command.type === 'unsubscribe_observations';
        if (Object.keys(command).some(key => key !== 'type' && (!needsId || key !== 'subscriptionId')) ||
            (needsId && (!Number.isSafeInteger(command.subscriptionId) || command.subscriptionId <= 0))) throw engineError('invalid_request', 'Invalid Observe request.');
        if (options && options.signal && options.signal.aborted) throw engineError('aborted', 'Observe request aborted.');
        const result = JSON.parse(callGodot(observe, JSON.stringify(command)));
        if (result && result.code) throw engineError(result.code, 'Observe request rejected.');
        return result;
      }
      const signal = options && options.signal;
      const requestedTimeoutMs = options && options.timeoutMs;
      if (signal && signal.aborted) throw engineError('aborted', 'The Godot Gua call was aborted.');
      if (requestedTimeoutMs !== undefined && (!Number.isInteger(requestedTimeoutMs) || requestedTimeoutMs < 0 || requestedTimeoutMs > 2147483647)) {
        throw engineError('invalid_request', 'Godot Web timeoutMs must be an integer from 0 to 2147483647.');
      }
      if (command.type === 'get_ui_tree') {
        const tree = JSON.parse(callGodot(getTree));
        if (tree && tree.code) throw engineError(tree.code, tree.message || 'The Godot Gua adapter is unavailable.');
        return tree;
      }
      if (command.type === 'get_world_object_tree') {
        const tree = JSON.parse(callGodot(getWorldTree));
        if (tree && tree.code) throw engineError(tree.code, tree.message || 'The Godot Gua world adapter is unavailable.');
        return tree;
      }
      if (command.type === 'query_world_objects') {
        const result = JSON.parse(callGodot(queryWorld, JSON.stringify(command)));
        if (result && result.code) throw engineError(result.code, result.message || 'The Godot Gua world adapter is unavailable.');
        return result;
      }
      if (command.type === 'get_game_input_capabilities') return JSON.parse(callGodot(getGameInputCapabilities));
      if (command.type === 'get_game_input_actions_v2') {
        const result = JSON.parse(callGodot(getGameInputActionsV2));
        if (result && result.code) throw engineError(result.code, result.message || 'The Godot Gua adapter is unavailable.');
        return result;
      }
      if (command.type === 'find_game_input_actions_v2') {
        const result = JSON.parse(callGodot(findGameInputActionsV2, JSON.stringify(command)));
        if (result && result.code) throw engineError(result.code, result.message || 'Invalid game input selector.');
        return result;
      }
      if (command.type === 'get_game_input_actions') return JSON.parse(callGodot(getGameInputActions));
      if (command.type === 'find_game_input_actions') {
        const result = JSON.parse(callGodot(findGameInputActions, JSON.stringify(command)));
        if (result && result.code) throw engineError(result.code, result.message || 'Invalid game input selector.');
        return result;
      }
      if (command.type === 'get_game_input_state') return JSON.parse(callGodot(getGameInputState));
      if (command.type === 'perform_game_input') {
        const receipt = JSON.parse(callGodot(enqueueGameInput, JSON.stringify(command.request)));
        if (!receipt.requestId) throw engineError(receipt.code || 'invalid_request', receipt.message || 'Godot rejected the game input request.');
        return await new Promise((resolve, reject) => {
          const deadline = performance.now() + (requestedTimeoutMs === undefined ? 5000 : requestedTimeoutMs);
          let timer = 0;
          let settled = false;
          const finish = (settle) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            if (signal) signal.removeEventListener('abort', aborted);
            pendingGameInputs.delete(receipt.requestId);
            settle();
          };
          const call = { fail: (error) => finish(() => reject(error)) };
          const aborted = () => {
            releasePendingGameInputs(receipt.requestId, engineError('aborted', 'The Godot game input call was aborted and page-owned inputs were released.'));
          };
          const poll = () => {
            if (disposed) return finish(() => reject(engineError('engine_unsupported', 'The Godot Gua adapter is no longer available.')));
            try {
              const result = JSON.parse(callGodot(pollGameInput, String(receipt.requestId)));
              if (result && result.completed) return finish(() => resolve(result));
              if (result && result.code) return finish(() => reject(engineError(result.code, result.message || 'The Godot game input session is unavailable.')));
              if (performance.now() >= deadline) {
                return releasePendingGameInputs(receipt.requestId, engineError('timeout', 'Timed out waiting for Godot game input completion; page-owned inputs were released.'));
              }
              timer = setTimeout(poll, 0);
            } catch (_) {
              return finish(() => reject(engineError('engine_unsupported', 'The Godot Gua adapter is no longer available.')));
            }
          };
          pendingGameInputs.set(receipt.requestId, call);
          if (signal) signal.addEventListener('abort', aborted, { once: true });
          if (signal && signal.aborted) return aborted();
          poll();
        });
      }
      if (command.type !== 'perform_action') throw engineError('engine_unsupported', `Godot Web command is unsupported: ${command.type}`);
      const receipt = JSON.parse(callGodot(enqueueAction, JSON.stringify(command.request)));
      if (!receipt.requestId) throw engineError(receipt.code || 'invalid_request', receipt.message || 'Godot rejected the Gua action.');
      return await new Promise((resolve, reject) => {
        const deadline = performance.now() + (requestedTimeoutMs === undefined ? 5000 : requestedTimeoutMs);
        const call = { reject, timer: 0, signal, aborted: null, settled: false, discardResult: false, drainDeadline: 0, cancelOrDrain: null };
        const finish = (settle) => {
          clearTimeout(call.timer);
          if (call.signal) call.signal.removeEventListener('abort', call.aborted);
          pending.delete(receipt.requestId);
          if (call.settled) return;
          call.settled = true;
          settle();
        };
        const rejectWithoutDropping = (error) => {
          if (call.settled) return;
          call.settled = true;
          if (call.signal) call.signal.removeEventListener('abort', call.aborted);
          reject(error);
        };
        const readResult = () => {
          const result = JSON.parse(callGodot(pollAction, String(receipt.requestId)));
          if (result && result.code) throw engineError(result.code, result.message || 'The Godot Gua adapter is unavailable.');
          return result;
        };
        const schedulePoll = () => {
          clearTimeout(call.timer);
          call.timer = setTimeout(poll, call.discardResult ? 10 : 0);
        };
        const cancelOrDrain = (error) => {
          let cancellationResult;
          try { cancellationResult = Number(callGodot(cancelAction, String(receipt.requestId))); }
          catch (_) { cancellationResult = -1; }
          if (cancellationResult === 1) return finish(() => reject(error));
          if (cancellationResult === 0) {
            try { readResult(); } catch (_) {}
            return finish(() => reject(error));
          }
          call.discardResult = true;
          call.drainDeadline = performance.now() + 5000;
          rejectWithoutDropping(error);
          schedulePoll();
        };
        call.cancelOrDrain = cancelOrDrain;
        const aborted = () => cancelOrDrain(engineError('aborted', 'The Godot Gua call was aborted.'));
        call.aborted = aborted;
        pending.set(receipt.requestId, call);
        if (signal) signal.addEventListener('abort', aborted, { once: true });
        if (signal && signal.aborted) return aborted();
        function poll() {
          if (disposed && !call.discardResult) return finish(() => reject(engineError('engine_unsupported', 'The Godot Gua adapter is no longer available.')));
          try {
            const result = readResult();
            if (result) return finish(() => resolve(result));
            if (call.discardResult) {
              if (performance.now() >= call.drainDeadline) return finish(() => {});
              return schedulePoll();
            }
            if (performance.now() >= deadline) {
              return cancelOrDrain(engineError('timeout', 'Timed out waiting for Godot host completion.'));
            }
            schedulePoll();
          } catch (error) {
            return finish(() => reject(error && error.code ? error : engineError('engine_unsupported', 'The Godot Gua adapter is no longer available.')));
          }
        }
        poll();
      });
    }
  };
  globalThis.__guaGodotWebPort = port;
})();
""" % bridge_owner_id)
	attached = true
	return true


func detach() -> void:
	if not bridge_owner_id.is_empty() and OS.has_feature("web"):
		JavaScriptBridge.eval("""
(() => {
  const port = globalThis.__guaGodotWebPort;
  if (!port || port.__guaOwnerId !== "%s") return;
  port.__guaUninstall();
  delete globalThis.__guaGodotWebPort;
  delete globalThis.__guaGodotObserve;
          delete globalThis.__guaGodotReleaseObserve;
          delete globalThis.__guaGodotGetTree;
  delete globalThis.__guaGodotGetWorldTree;
  delete globalThis.__guaGodotQueryWorld;
  delete globalThis.__guaGodotEnqueueAction;
  delete globalThis.__guaGodotPollAction;
  delete globalThis.__guaGodotCancelAction;
  delete globalThis.__guaGodotGetGameInputCapabilities;
  delete globalThis.__guaGodotGetGameInputActions;
  delete globalThis.__guaGodotGetGameInputActionsV2;
  delete globalThis.__guaGodotFindGameInputActions;
  delete globalThis.__guaGodotFindGameInputActionsV2;
  delete globalThis.__guaGodotGetGameInputState;
  delete globalThis.__guaGodotEnqueueGameInput;
  delete globalThis.__guaGodotPollGameInput;
  delete globalThis.__guaGodotReleaseGameInput;
})();
""" % bridge_owner_id)
	_release_game_input_owner([])
	_release_observe_client([])
	observe_callback = null
	release_observe_callback = null
	bridge_owner_id = ""
	get_tree_callback = null
	get_world_tree_callback = null
	query_world_callback = null
	enqueue_callback = null
	poll_callback = null
	cancel_callback = null
	get_game_input_capabilities_callback = null
	get_game_input_actions_v2_callback = null
	get_game_input_actions_callback = null
	find_game_input_actions_v2_callback = null
	find_game_input_actions_callback = null
	get_game_input_state_callback = null
	enqueue_game_input_callback = null
	poll_game_input_callback = null
	release_game_input_callback = null
	adapter_ref = null
	attached = false


func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE and attached:
		detach()


func _release_observe_client(_arguments: Array) -> void:
	var adapter := _adapter()
	if adapter != null and observe_client_id != 0:
		adapter.context.release_observe_client(observe_client_id)
	observe_client_id = 0

func _observe_command(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null or observe_client_id == 0:
		_respond(arguments, '{"code":"engine_unsupported"}')
		return
	var command: Variant = JSON.parse_string(str(arguments[1]))
	if not command is Dictionary:
		_respond(arguments, '{"code":"invalid_request"}')
		return
	var operations := {"get_observe_snapshot": 1, "subscribe_observations": 2, "poll_observations": 3, "unsubscribe_observations": 4}
	var operation: int = operations.get(command.get("type", ""), 0)
	var subscription: Variant = command.get("subscriptionId", 0)
	if operation == 0 or not (subscription is int or subscription is float) or not is_finite(float(subscription)) or float(subscription) != floor(float(subscription)) or float(subscription) < 0 or float(subscription) > 9007199254740991:
		_respond(arguments, '{"code":"invalid_request"}')
		return
	var result: Dictionary = adapter.context.observe_command(observe_client_id, operation, int(subscription))
	if int(result.get("status", 5)) != 0:
		_respond(arguments, '{"code":"invalid_request"}')
	elif operation == 4:
		_respond(arguments, "null")
	elif operation == 2:
		_respond(arguments, '{"subscriptionId":%d,"snapshot":%s}' % [result.subscriptionId, result.json])
	else:
		_respond(arguments, result.json)

func _get_tree(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "The Godot Gua adapter is no longer available."}))
		return
	_respond(arguments, adapter.get_player_ui_tree_json())


func _get_world_tree(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "The Godot Gua adapter is no longer available."}))
		return
	_respond(arguments, adapter.get_player_world_object_tree_json())


func _query_world(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "The Godot Gua adapter is no longer available."}))
		return
	var source = JSON.parse_string(str(_request_argument(arguments)))
	if not source is Dictionary:
		_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "World query must be an object."}))
		return
	var command: Dictionary = source
	var selector := {
		"id": command.get("worldId", ""),
		"kind": command.get("kind", ""),
		"label": command.get("label", ""),
		"tag": command.get("tag", ""),
		"parent_id": command.get("parentId", ""),
		"direct_child": command.get("directChild", 0),
		"visible_to_player": command.get("visibleToPlayer", 0),
		"active": command.get("active", 0),
		"state_key": command.get("stateKey", ""),
		"state_type": command.get("stateType", 0),
		"state_string": command.get("stateString", ""),
		"state_number": command.get("stateNumber", 0.0),
		"state_bool": command.get("stateBool", false),
	}
	if command.has("relativeToObjectId"):
		selector["relative_to_object_id"] = command.get("relativeToObjectId")
	if command.has("maxDistance"):
		selector["max_distance"] = command.get("maxDistance")
	if command.has("limit"):
		var limit = _normalize_world_limit(command.get("limit"))
		if limit == null:
			_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "World query limit must be an integer from 1 through 4294967295."}))
			return
		selector["limit"] = limit
	_respond(arguments, adapter.query_player_world_objects_json(selector))


func _normalize_world_limit(value: Variant) -> Variant:
	if typeof(value) not in [TYPE_INT, TYPE_FLOAT]:
		return null
	var numeric := float(value)
	if not is_finite(numeric) or numeric < 1.0 or numeric > 4294967295.0 or numeric != floor(numeric):
		return null
	return int(numeric)


func _enqueue_action(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "The Godot Gua adapter is no longer available."}))
		return
	var source = JSON.parse_string(str(_request_argument(arguments)))
	if not source is Dictionary:
		_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "Action request must be an object."}))
		return
	var request: Dictionary = source
	var native_request := {
		"action": request.get("action", ""),
		"node_id": request.get("nodeId", ""),
		"value": request.get("value", ""),
		"bool_value": request.get("checked", false),
		"delta_x": request.get("deltaX", 0.0),
		"delta_y": request.get("deltaY", 0.0),
		"scroll_unit": request.get("scrollUnit", 0),
		"key": request.get("key", ""),
		"modifiers": request.get("modifiers", 0),
		"sensitive": request.get("sensitive", false),
	}
	var receipt: Dictionary = adapter.enqueue_player_action(native_request)
	if receipt.get("error_code", -1) != 0:
		var error_code := int(receipt.get("error_code", -1))
		_respond(arguments, JSON.stringify({"code": _web_error_code(error_code), "message": "Godot rejected the Gua action.", "hostError": error_code}))
		return
	_respond(arguments, JSON.stringify({"requestId": receipt.get("request_id", 0)}))


func _poll_action(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "The Godot Gua adapter is no longer available."}))
		return
	var request_id := int(str(_request_argument(arguments)))
	var result: Dictionary = adapter.poll_action_result(request_id)
	_respond(arguments, "null" if result.is_empty() else JSON.stringify(result))


func _cancel_action(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null:
		_respond(arguments, 0)
		return
	var request_id := int(str(_request_argument(arguments)))
	_respond(arguments, adapter.cancel_action_request(request_id))


func _game_input_capabilities(adapter: RefCounted) -> Array:
	var mask := int(adapter.get_game_input_capabilities(1))
	var result: Array = []
	if mask & 1: result.push_back("semantic_game_input_v1")
	if mask & 1: result.push_back("semantic_game_input_search_v1")
	if mask & 1 and adapter.has_method("supports_game_input_metadata") and adapter.supports_game_input_metadata(): result.push_back("semantic_game_input_metadata_v1")
	if mask & 2: result.push_back("raw_keyboard_input_v1")
	if mask & 4: result.push_back("raw_pointer_input_v1")
	if mask & 8: result.push_back("raw_gamepad_input_v1")
	if mask & 16: result.push_back("text_input_v1")
	if mask != 0: result.push_back("game_input_lease_v1")
	return result


func _get_game_input_capabilities(arguments: Array) -> void:
	var adapter := _adapter()
	_respond(arguments, JSON.stringify(_game_input_capabilities(adapter)) if adapter != null else "[]")


func _get_game_input_actions(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null or not "semantic_game_input_v1" in _game_input_capabilities(adapter):
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "Semantic game input is unavailable."}))
		return
	_respond(arguments, adapter.get_player_game_input_actions_json())


func _find_game_input_actions(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null or not "semantic_game_input_search_v1" in _game_input_capabilities(adapter):
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "Semantic game input search is unavailable."}))
		return
	var source = JSON.parse_string(str(_request_argument(arguments)))
	if not source is Dictionary:
		_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "Invalid game input selector."}))
		return
	var selector := {
		"id": source.get("id", ""), "query": source.get("query", ""), "value_type": source.get("valueType", ""),
		"active": source.get("active") if source.has("active") else null, "context": source.get("context", ""),
		"category": source.get("category", ""), "tags": source.get("tags", []), "limit": source.get("limit", 20),
	}
	if selector.active == null: selector.erase("active")
	_respond(arguments, adapter.find_game_input_actions_json(selector, 1))


func _get_game_input_actions_v2(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null or not "semantic_game_input_metadata_v1" in _game_input_capabilities(adapter):
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "Semantic game input is unavailable."}))
		return
	_respond(arguments, adapter.get_game_input_actions_json_v2(1))


func _find_game_input_actions_v2(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null or not "semantic_game_input_metadata_v1" in _game_input_capabilities(adapter):
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "Semantic game input search is unavailable."}))
		return
	var source = JSON.parse_string(str(_request_argument(arguments)))
	if not source is Dictionary:
		_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "Invalid game input selector."}))
		return
	var selector := {
		"id": source.get("id", ""), "query": source.get("query", ""), "value_type": source.get("valueType", ""),
		"active": source.get("active") if source.has("active") else null, "context": source.get("context", ""),
		"category": source.get("category", ""), "tags": source.get("tags", []), "limit": source.get("limit", 20),
	}
	if selector.active == null: selector.erase("active")
	_respond(arguments, adapter.find_game_input_actions_json_v2(selector, 1))


func _get_game_input_state(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter != null and game_input_owner_id == 0 and not _game_input_capabilities(adapter).is_empty():
		game_input_owner_id = adapter.create_game_input_owner()
	if adapter == null or game_input_owner_id == 0:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "Game input is unavailable."}))
		return
	_respond(arguments, adapter.get_game_input_state_json(game_input_owner_id))


func _enqueue_game_input(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "Game input is unavailable."}))
		return
	if game_input_owner_id == 0:
		game_input_owner_id = adapter.create_game_input_owner()
	var source = JSON.parse_string(str(_request_argument(arguments)))
	if not source is Dictionary:
		_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "Game input request must be an object."}))
		return
	var request: Dictionary = source
	if request.get("type", "") in ["press_game_input_action", "set_game_input_action"]:
		var action_map = JSON.parse_string(adapter.find_game_input_actions_json(
			{"id": request.get("actionId", ""), "limit": 1}, 1))
		if action_map is Dictionary:
			for action in action_map.get("actions", []):
				if action is Dictionary and action.get("id", "") == request.get("actionId", "") and action.get("requiresConfirmation", false) and not request.get("confirmed", false):
					_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "This game input action requires confirmed=true."}))
					return
	var native_request := _native_game_input_request(request)
	if native_request.is_empty():
		_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "Unknown game input request."}))
		return
	native_request["owner_id"] = game_input_owner_id
	native_request["observation_profile"] = 1
	var receipt: Dictionary = adapter.enqueue_game_input(native_request)
	if receipt.get("error_code", -1) != 0:
		_respond(arguments, JSON.stringify({"code": "invalid_request", "message": "Godot rejected the game input request.", "hostError": receipt.get("error_code", -1)}))
		return
	_respond(arguments, JSON.stringify({"requestId": receipt.get("request_id", 0)}))


func _native_game_input_request(request: Dictionary) -> Dictionary:
	var type := str(request.get("type", ""))
	var base := {"lease_ms": int(request.get("leaseMs", 5000)), "device_index": int(request.get("gamepadIndex", 0)),
		"sensitive": bool(request.get("sensitive", false)), "confirmed": bool(request.get("confirmed", false)),
		"x": float(request.get("x", 0.0)), "y": float(request.get("y", 0.0)), "value": null}
	match type:
		"press_game_input_action": return _merge_game_input(base, 1, 1, str(request.get("actionId", "")), true)
		"set_game_input_action": return _merge_game_input(base, 1, 2, str(request.get("actionId", "")), request.get("value"))
		"release_game_input_action": return _merge_game_input(base, 1, 3, str(request.get("actionId", "")), null)
		"release_all_game_inputs": return _merge_game_input(base, 6, 10, "", null)
		"key_down": return _merge_game_input(base, 2, 4, str(request.get("code", "")), null)
		"key_up": return _merge_game_input(base, 2, 5, str(request.get("code", "")), null)
		"press_physical_key": return _merge_game_input(base, 2, 1, str(request.get("code", "")), null)
		"pointer_move":
			var absolute: bool = request.get("mode", "") == "absolute"
			return _merge_game_input(base, 3, 6 if absolute else 7,
				"absolute:" + str(request.get("coordinateSpace", "viewport_pixels")) if absolute else "delta:", null)
		"pointer_button_down": return _merge_game_input(base, 3, 4, str(request.get("button", "")), null)
		"pointer_button_up": return _merge_game_input(base, 3, 5, str(request.get("button", "")), null)
		"pointer_wheel":
			base["x"] = float(request.get("deltaX", 0.0))
			base["y"] = float(request.get("deltaY", 0.0))
			return _merge_game_input(base, 3, 8, str(request.get("wheelUnit", "pixels")), null)
		"gamepad_button_down": return _merge_game_input(base, 4, 4, str(request.get("button", "")), null)
		"gamepad_button_up": return _merge_game_input(base, 4, 5, str(request.get("button", "")), null)
		"set_gamepad_axis": return _merge_game_input(base, 4, 2, str(request.get("axis", "")), request.get("value"))
		"reset_gamepad": return _merge_game_input(base, 4, 9, "", null)
		"text_input": return _merge_game_input(base, 5, 2, "", request.get("text", ""))
	return {}


func _merge_game_input(base: Dictionary, kind: int, operation: int, target: String, value: Variant) -> Dictionary:
	var result := base.duplicate()
	result.merge({"kind": kind, "operation": operation, "target": target, "value": value}, true)
	return result


func _poll_game_input(arguments: Array) -> void:
	var adapter := _adapter()
	if adapter == null or game_input_owner_id == 0:
		_respond(arguments, JSON.stringify({"code": "engine_unsupported", "message": "Game input is unavailable."}))
		return
	var request_id := int(str(_request_argument(arguments)))
	_respond(arguments, adapter.get_game_input_result_json(game_input_owner_id, request_id))


func _release_game_input_owner(arguments: Array) -> void:
	var adapter := _adapter()
	var owner_id := game_input_owner_id
	game_input_owner_id = 0
	if adapter == null or owner_id == 0:
		_respond(arguments, 0)
		return
	var released: bool = bool(adapter.release_game_input_owner(owner_id))
	if released and str(_request_argument(arguments)) == "1" and not _game_input_capabilities(adapter).is_empty():
		game_input_owner_id = adapter.create_game_input_owner()
	_respond(arguments, 1 if released else 0)


func _respond(arguments: Array, value: Variant) -> void:
	if arguments.is_empty() or not arguments[0] is JavaScriptObject:
		return
	var response: JavaScriptObject = arguments[0]
	response.value = value


func _request_argument(arguments: Array) -> Variant:
	return arguments[1] if arguments.size() > 1 else null


func _adapter() -> RefCounted:
	return adapter_ref.get_ref() if adapter_ref != null else null


func _web_error_code(error_code: int) -> String:
	match error_code:
		-2:
			return "node_not_found"
		-3:
			return "hidden"
		-4:
			return "disabled"
		-5:
			return "unsupported_action"
		_:
			return "invalid_request"

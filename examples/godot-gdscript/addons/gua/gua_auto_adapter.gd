class_name GuaAutoAdapter
extends RefCounted

signal clock_tick(delta: float)
signal game_input_action_changed(action_id: String, value: Variant)

const META_ID := "gua_id"
const META_SENSITIVE := "gua_sensitive"
const RESET_CLOCK_FLAG := 1 << 6
const RESET_DEFAULT_FLAGS := 207
const CLOCK_CALLBACK_LIMIT := 1000000
const CONTEXT_CLASS := "GuaContext"
const GDEXTENSION_RESOURCE := "res://addons/gua/gua.gdextension"
const REBUILD_COMMAND := "cmake --build --preset windows-msvc-debug --target gua-godot"
const REQUIRED_CONTEXT_METHODS := [
	"create_observe_owner", "destroy_observe_owner", "register_observe", "unregister_observe",
	"observe_registration_alive", "register_value_enum", "publish_observe_json",
	"create_observe_client", "release_observe_client", "observe_command",
	"begin_frame",
	"register_node",
	"register_node_v2",
	"end_frame",
	"get_ui_tree_json",
	"get_player_ui_tree_json",
	"get_version_json",
	"get_observation_profile",
	"set_screenshot",
	"get_screenshot_json",
	"consume_screenshot_request",
	"complete_screenshot_request",
	"enqueue_click",
	"consume_click_request",
	"emit_click",
	"poll_event",
	"enqueue_action",
	"enqueue_player_action",
	"cancel_action_request",
	"consume_action_request",
	"emit_action_result",
	"poll_event_v2",
	"poll_action_result",
	"get_context_status",
	"reset_context",
	"clock_install",
	"clock_pause",
	"clock_run_for",
	"clock_resume",
	"clock_advance",
	"get_clock",
	"consume_clock_step",
	"consume_clock_steps",
	"enable_virtual_clock_adapter",
	"publish_game_input_actions",
	"get_game_input_actions_json",
	"get_player_game_input_actions_json",
	"find_game_input_actions_json",
	"enable_game_input_adapter",
	"get_game_input_capabilities",
	"create_game_input_owner",
	"release_game_input_owner",
	"enqueue_game_input",
	"get_game_input_state_json",
	"get_game_input_result_json",
	"consume_game_input_request",
	"complete_game_input_request",
	"tick_game_input_leases",
	"begin_world_frame",
	"register_world_object",
	"end_world_frame",
	"abort_world_frame",
	"get_world_object_tree_json",
	"query_world_objects_json",
	"get_player_world_object_tree_json",
	"query_player_world_objects_json",
	"enable_world_object_tree_adapter",
	"start_inspector_bridge",
	"inspector_bridge_url",
]

var context: Object
var root: Control
var buttons_by_id: Dictionary = {}
var tabs_by_id: Dictionary = {}
var list_items_by_id: Dictionary = {}
var controls_by_id: Dictionary = {}
var connected_buttons: Dictionary = {}
var suppressed_clicks: Dictionary = {}
var gdextension_resource: Resource
var unavailable := false
var screenshot_capture_scheduled := false
var webmcp_bridge: RefCounted
var last_clock_ticks_ms := Time.get_ticks_msec()
var observed_clock_generation := -1
var clock_schedules: Array[Dictionary] = []
var next_clock_schedule_id := 1
var active_clock_schedule_id := 0
var active_clock_schedule_cancelled := false
var clock_run_active := false
var clock_run_callbacks_remaining := CLOCK_CALLBACK_LIMIT
var clock_run_generation := -1
var clock_execution_limit_reached := false
var game_input_values: Dictionary = {}
var semantic_values_by_owner: Dictionary = {}
var held_physical_keys: Dictionary = {}
var held_pointer_buttons: Dictionary = {}
var synthetic_pointer_position := Vector2.ZERO
var held_gamepad_buttons: Dictionary = {}
var held_gamepad_axes: Dictionary = {}
var game_input_sequence := 0
var raw_input_enabled := false
var semantic_input_enabled := false
var player_semantic_input_allowed := false
var player_raw_input_allowed := false
var last_game_input_ticks_ms := Time.get_ticks_msec()
var disposed := false
var observe_owners: Array[WeakRef] = []

func create_observe_owner(source: int, runtime_id: String = "", target_node: Node = null) -> RefCounted:
	if not _ensure_context():
		return null
	var id: int = context.create_observe_owner(source, runtime_id)
	if id == 0:
		return null
	var owner: RefCounted = preload("res://addons/gua/gua_observe_owner.gd").new()
	owner.context = context
	owner.owner_id = id
	owner.source = source
	owner.target = weakref(target_node) if target_node != null else null
	observe_owners.append(weakref(owner))
	return owner

func register_value_enum(enum_type: String, members: Array) -> int:
	return context.register_value_enum(enum_type, members) if _ensure_context() else 1

func _sample_observations(ui: bool) -> void:
	for reference: WeakRef in observe_owners.duplicate():
		var owner: RefCounted = reference.get_ref()
		if owner != null:
			owner.sample_frame(ui)
	observe_owners = observe_owners.filter(func(reference: WeakRef) -> bool: return reference.get_ref() != null)


func attach(root_control: Control) -> void:
	if disposed:
		push_error("GuaAutoAdapter.attach called after dispose.")
		return
	_detach_webmcp_bridge()
	root = root_control
	last_clock_ticks_ms = Time.get_ticks_msec()
	last_game_input_ticks_ms = last_clock_ticks_ms
	if _ensure_context() and OS.has_feature("web"):
		var bridge_script := load("res://addons/gua/gua_webmcp_bridge.gd")
		if bridge_script != null:
			webmcp_bridge = bridge_script.new()
			webmcp_bridge.attach(self)


func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE and not disposed:
		for reference: WeakRef in observe_owners:
			var owner: RefCounted = reference.get_ref()
			if owner != null:
				owner.dispose()
		observe_owners.clear()
		if webmcp_bridge != null:
			webmcp_bridge.detach()
		webmcp_bridge = null
		# RefCounted self method calls are invalid during PREDELETE. Pass state to
		# a static script call so the final held-input cleanup still runs.
		load("res://addons/gua/gua_auto_adapter.gd").release_injected_state(held_physical_keys, held_pointer_buttons, held_gamepad_buttons, held_gamepad_axes, synthetic_pointer_position, semantic_values_by_owner, game_input_values, Signal())


func _detach_webmcp_bridge() -> void:
	if webmcp_bridge != null and webmcp_bridge.has_method("detach"):
		webmcp_bridge.detach()
	webmcp_bridge = null


func dispose() -> void:
	for reference: WeakRef in observe_owners:
		var owner: RefCounted = reference.get_ref()
		if owner != null:
			owner.dispose()
	observe_owners.clear()
	if disposed:
		return
	disposed = true
	_detach_webmcp_bridge()
	_release_all_injected_inputs()
	_disconnect_buttons()
	for signal_name in [&"clock_tick", &"game_input_action_changed"]:
		for connection in get_signal_connection_list(signal_name):
			var callback: Callable = connection.get("callable", Callable())
			if callback.is_valid() and is_connected(signal_name, callback):
				disconnect(signal_name, callback)
	clock_schedules.clear()
	screenshot_capture_scheduled = false
	buttons_by_id.clear()
	tabs_by_id.clear()
	list_items_by_id.clear()
	controls_by_id.clear()
	suppressed_clicks.clear()
	root = null
	if context != null:
		context.stop_inspector_bridge()
	context = null
	gdextension_resource = null


func update(screen: String) -> void:
	if not _ensure_context():
		return
	var ticks_ms := Time.get_ticks_msec()
	var game_input_elapsed_ms := maxi(0, ticks_ms - last_game_input_ticks_ms)
	last_game_input_ticks_ms = ticks_ms
	context.tick_game_input_leases(game_input_elapsed_ms)
	var clock_status: Dictionary = context.get_clock()
	_bind_pending_clock_schedules(clock_status)
	var clock_installed := bool(clock_status.get("installed", false))
	var clock_generation := int(clock_status.get("generation", -1))
	if clock_installed and observed_clock_generation != clock_generation:
		last_clock_ticks_ms = ticks_ms
	observed_clock_generation = clock_generation if clock_installed else -1
	if clock_installed and clock_status.get("state", "running") == "running":
		context.clock_advance(maxi(0, ticks_ms - last_clock_ticks_ms))
	last_clock_ticks_ms = ticks_ms
	while true:
		var step: Dictionary = context.consume_clock_step()
		if step.is_empty():
			break
		var step_generation := int(step.get("generation", 0))
		if not clock_run_active or clock_run_generation != step_generation:
			clock_run_active = true
			clock_run_callbacks_remaining = CLOCK_CALLBACK_LIMIT
			clock_run_generation = step_generation
		clock_run_callbacks_remaining -= _drain_clock_schedules(
			float(context.get_clock().get("now_ms", 0.0)), step_generation, clock_run_callbacks_remaining)
		if clock_execution_limit_reached:
			push_error("Gua clock execution_limit")
			clock_execution_limit_reached = false
		if int(context.get_clock().get("generation", -1)) != step_generation:
			clock_run_active = false
			clock_run_callbacks_remaining = CLOCK_CALLBACK_LIMIT
			clock_run_generation = -1
			continue
		_dispatch_clock_tick(float(step.get("delta_ms", 0.0)) / 1000.0, step_generation)
		if bool(step.get("final", false)):
			clock_run_active = false
			clock_run_callbacks_remaining = CLOCK_CALLBACK_LIMIT
			clock_run_generation = -1

	if root == null:
		push_error("GuaAutoAdapter.update called before attach.")
		return

	context.begin_frame(screen)
	buttons_by_id.clear()
	tabs_by_id.clear()
	list_items_by_id.clear()
	controls_by_id.clear()
	_collect_control(root, "")
	_sample_observations(true)
	context.end_frame()
	_publish_world_frame(screen)
	_dispatch_click_requests()
	_dispatch_action_requests()
	_dispatch_game_input_requests()
	_schedule_screenshot_capture()


func configure_game_input_actions(input_context: String, actions: Array[Dictionary], allow_player_agents := false) -> bool:
	if not _ensure_context():
		return false
	var published := bool(context.publish_game_input_actions(input_context, actions))
	if published:
		semantic_input_enabled = true
		player_semantic_input_allowed = allow_player_agents
		_update_game_input_capabilities()
	return published


func enable_raw_input(allow_player_agents := false) -> bool:
	if not _ensure_context():
		return false
	raw_input_enabled = true
	player_raw_input_allowed = allow_player_agents
	_update_game_input_capabilities()
	return true


func _update_game_input_capabilities() -> void:
	var capabilities := (1 if semantic_input_enabled else 0) | (30 if raw_input_enabled else 0)
	var player_capabilities := (1 if semantic_input_enabled and player_semantic_input_allowed else 0) | \
		(30 if raw_input_enabled and player_raw_input_allowed else 0)
	context.enable_game_input_adapter(capabilities, player_capabilities)


func get_game_input_capabilities(observation_profile := 0) -> int:
	return int(context.get_game_input_capabilities(observation_profile)) if _ensure_context() else 0


func get_game_input_action_value(action_id: String, fallback: Variant = null) -> Variant:
	return game_input_values.get(action_id, fallback)


func _publish_world_frame(scene: String, report_errors: bool = true) -> void:
	if root == null or root.get_tree() == null or not context.begin_world_frame(scene):
		return
	var objects := root.get_tree().get_nodes_in_group(&"gua_world_object")
	objects.sort_custom(func(a: Node, b: Node) -> bool: return str(a.get_path()) < str(b.get_path()))
	for node: Node in objects:
		if not (node is Node2D or node is Node3D):
			continue
		if not node.has_meta(&"gua_world_id"):
			_report_world_frame_error("Gua world object requires gua_world_id metadata: %s" % node.get_path(), report_errors)
			context.abort_world_frame()
			return
		var object_id := str(node.get_meta(&"gua_world_id", ""))
		if object_id.is_empty():
			_report_world_frame_error("Gua world object requires a non-empty gua_world_id: %s" % node.get_path(), report_errors)
			context.abort_world_frame()
			return
		var parent_id := ""
		var ancestor := node.get_parent()
		while ancestor != null:
			if ancestor.is_in_group(&"gua_world_object") and ancestor.has_meta(&"gua_world_id"):
				var candidate_parent_id := str(ancestor.get_meta(&"gua_world_id", ""))
				if not candidate_parent_id.is_empty():
					parent_id = candidate_parent_id
					break
			ancestor = ancestor.get_parent()
		var position := Vector3.ZERO
		var space := "world2d"
		if node is Node3D:
			position = (node as Node3D).global_position
			space = "world3d"
		else:
			var position_2d := (node as Node2D).global_position
			position = Vector3(position_2d.x, position_2d.y, 0.0)
		var visible_to_player = node.get_meta(&"gua_world_visible_to_player", false)
		var active = node.get_meta(&"gua_world_active", true)
		if typeof(visible_to_player) != TYPE_BOOL or typeof(active) != TYPE_BOOL:
			_report_world_frame_error("Gua world visibility/active metadata must be boolean: %s" % object_id, report_errors)
			context.abort_world_frame()
			return
		var descriptor := {
			"id": object_id,
			"parent_id": parent_id,
			"kind": str(node.get_meta(&"gua_world_kind", "object")),
			"label": str(node.get_meta(&"gua_world_label", node.name)),
			"description": str(node.get_meta(&"gua_world_description", "")),
			"space": space,
			"position": position,
			"visible_to_player": visible_to_player,
			"active": active,
			"agent_exposure": str(node.get_meta(&"gua_agent_exposure", node.get_meta(&"gua_world_agent_exposure", "auto"))),
			"agent_field_rules": node.get_meta(&"gua_agent_field_rules", node.get_meta(&"gua_world_agent_field_rules", [])),
			"agent_allowed_actions": node.get_meta(&"gua_agent_allowed_actions", node.get_meta(&"gua_world_agent_allowed_actions", [])),
			"agent_allowed_actions_set": node.has_meta(&"gua_agent_allowed_actions") or node.has_meta(&"gua_world_agent_allowed_actions"),
			"tags": node.get_meta(&"gua_world_tags", []),
			"state": node.get_meta(&"gua_world_state", {}),
			"domain_id": str(node.get_meta(&"gua_world_domain_id", "")),
			"related_ui_node_id": str(node.get_meta(&"gua_world_related_ui_node_id", "")),
		}
		if not context.register_world_object(descriptor):
			_report_world_frame_error("Failed to register Gua world object: %s" % object_id, report_errors)
			context.abort_world_frame()
			return
	_sample_observations(false)
	if not context.end_world_frame():
		_report_world_frame_error("Gua world frame was rejected", report_errors)


func _report_world_frame_error(message: String, report_errors: bool) -> void:
	if report_errors:
		push_error(message)


func _dispatch_clock_tick(delta_seconds: float, step_generation: int) -> void:
	for connection in get_signal_connection_list(&"clock_tick"):
		if disposed or context == null:
			return
		if int(context.get_clock().get("generation", -1)) != step_generation:
			return
		var callback: Callable = connection.get("callable", Callable())
		if not callback.is_valid() or not clock_tick.is_connected(callback):
			continue
		var flags := int(connection.get("flags", 0))
		if flags & CONNECT_ONE_SHOT:
			clock_tick.disconnect(callback)
		if flags & CONNECT_DEFERRED:
			Callable(self, "_dispatch_deferred_clock_tick").bind(
				callback, delta_seconds, step_generation).call_deferred()
		else:
			callback.call(delta_seconds)


func _dispatch_deferred_clock_tick(callback: Callable, delta_seconds: float, step_generation: int) -> void:
	if disposed or context == null:
		return
	if int(context.get_clock().get("generation", -1)) == step_generation and callback.is_valid():
		callback.call(delta_seconds)


func capture_viewport_screenshot(image_override: Image = null) -> Dictionary:
	if not _ensure_context() or root == null:
		return {"ok": false, "error": "Gua adapter is not attached."}
	var image := image_override
	if image == null:
		image = root.get_viewport().get_texture().get_image()
	if image == null or image.is_empty():
		return {"ok": false, "error": "Godot viewport capture returned an empty image."}
	var png := image.save_png_to_buffer()
	context.set_screenshot("data:image/png;base64," + Marshalls.raw_to_base64(png), image.get_width(), image.get_height())
	return {"ok": true, "width": image.get_width(), "height": image.get_height()}


func _schedule_screenshot_capture() -> void:
	if disposed or context == null or screenshot_capture_scheduled:
		return
	var request: Dictionary = context.consume_screenshot_request()
	if request.is_empty():
		return
	if DisplayServer.get_name() == "headless":
		context.complete_screenshot_request({"request_id": request.get("request_id", 0), "unavailable": "headless"})
		return
	screenshot_capture_scheduled = true
	_capture_requested_screenshot.call_deferred(request)


func _capture_requested_screenshot(request: Dictionary) -> void:
	if disposed or context == null or not is_instance_valid(root):
		screenshot_capture_scheduled = false
		return
	while context.get_context_status().get("session_epoch", 0) == request.get("session_epoch", 0) and context.get_context_status().get("frame_sequence", 0) <= request.get("after_frame_sequence", 0):
		await root.get_tree().process_frame
		if disposed or context == null or not is_instance_valid(root):
			screenshot_capture_scheduled = false
			return
	if context.get_context_status().get("session_epoch", 0) != request.get("session_epoch", 0):
		context.complete_screenshot_request({"request_id": request.get("request_id", 0), "unavailable": "unsupported"})
		screenshot_capture_scheduled = false
		_schedule_screenshot_capture()
		return
	await RenderingServer.frame_post_draw
	if disposed or context == null or not is_instance_valid(root):
		screenshot_capture_scheduled = false
		return
	var result := {"request_id": request.get("request_id", 0)}
	if DisplayServer.get_name() == "headless":
		result["unavailable"] = "headless"
	else:
		var image := root.get_viewport().get_texture().get_image() if root != null else null
		if image == null or image.is_empty():
			result["unavailable"] = "rendering_disabled"
		else:
			var png := image.save_png_to_buffer()
			result["data_uri"] = "data:image/png;base64," + Marshalls.raw_to_base64(png)
			result["width"] = image.get_width()
			result["height"] = image.get_height()
	context.complete_screenshot_request(result)
	screenshot_capture_scheduled = false
	_schedule_screenshot_capture()


func start_inspector_bridge(port: int = 8765) -> bool:
	if not _ensure_context():
		return false

	return context.start_inspector_bridge(port)


func inspector_bridge_url() -> String:
	if not _ensure_context():
		return ""

	return context.inspector_bridge_url()


func get_ui_tree_json() -> String:
	if not _ensure_context():
		return ""

	return context.get_ui_tree_json()


func get_version_json() -> String:
	if not _ensure_context():
		return "{}"
	return context.get_version_json()


func get_game_input_actions_json() -> String:
	if not _ensure_context():
		return "{}"
	return context.get_game_input_actions_json()


func get_player_game_input_actions_json() -> String:
	return context.get_player_game_input_actions_json() if _ensure_context() else "{}"


func find_game_input_actions_json(selector: Dictionary, observation_profile := 0) -> String:
	return context.find_game_input_actions_json(selector, observation_profile) if _ensure_context() else "{}"


func create_game_input_owner() -> int:
	return int(context.create_game_input_owner()) if _ensure_context() else 0


func release_game_input_owner(owner_id: int) -> bool:
	return context.release_game_input_owner(owner_id) if _ensure_context() else false


func enqueue_game_input(request: Dictionary) -> Dictionary:
	return context.enqueue_game_input(request) if _ensure_context() else {"error_code": -1, "request_id": 0}


func get_game_input_state_json(owner_id: int) -> String:
	return context.get_game_input_state_json(owner_id) if _ensure_context() else "{}"


func get_game_input_result_json(owner_id: int, request_id: int) -> String:
	return context.get_game_input_result_json(owner_id, request_id) if _ensure_context() else "{}"


func get_player_ui_tree_json() -> String:
	if not _ensure_context():
		return ""

	return context.get_player_ui_tree_json()


func get_world_object_tree_json() -> String:
	if not _ensure_context():
		return ""
	return context.get_world_object_tree_json()


func query_world_objects_json(selector: Dictionary) -> String:
	if not _ensure_context():
		return ""
	return context.query_world_objects_json(selector)


func get_player_world_object_tree_json() -> String:
	if not _ensure_context():
		return ""
	return context.get_player_world_object_tree_json()


func query_player_world_objects_json(selector: Dictionary) -> String:
	if not _ensure_context():
		return ""
	return context.query_player_world_objects_json(selector)


func enqueue_click(id: String) -> bool:
	if not _ensure_context():
		return false

	return context.enqueue_click(id)


func poll_event() -> Dictionary:
	if not _ensure_context():
		return {}

	return context.poll_event()


func enqueue_action(request: Dictionary) -> Dictionary:
	if not _ensure_context():
		return {"error_code": -1, "request_id": 0}
	return context.enqueue_action(request)


func enqueue_player_action(request: Dictionary) -> Dictionary:
	if not _ensure_context():
		return {"error_code": -1, "request_id": 0}
	return context.enqueue_player_action(request)


func cancel_action_request(request_id: int) -> int:
	if not _ensure_context():
		return 0
	return context.cancel_action_request(request_id)


func poll_event_v2() -> Dictionary:
	if not _ensure_context():
		return {}
	return context.poll_event_v2()


func poll_action_result(request_id: int) -> Dictionary:
	if not _ensure_context():
		return {}
	return context.poll_action_result(request_id)


func get_context_status() -> Dictionary:
	if not _ensure_context():
		return {}
	return context.get_context_status()


func clock_install(initial_time_ms: float = 0.0, step_ms: float = 1000.0 / 60.0) -> Dictionary:
	if not _ensure_context():
		return {}
	var result: Dictionary = context.clock_install(initial_time_ms, step_ms)
	if int(result.get("result", 0)) == 1:
		last_clock_ticks_ms = Time.get_ticks_msec()
		observed_clock_generation = int(result.get("generation", context.get_clock().get("generation", -1)))
	return result

func clock_pause() -> Dictionary:
	return context.clock_pause() if _ensure_context() else {}

func clock_run_for(duration_ms: float, step_ms = null) -> Dictionary:
	return context.clock_run_for(duration_ms, step_ms) if _ensure_context() else {}

func clock_resume() -> Dictionary:
	return context.clock_resume() if _ensure_context() else {}

func get_clock() -> Dictionary:
	return context.get_clock() if _ensure_context() else {}

func clock_schedule(delay_ms: float, callback: Callable, interval_ms: float = 0.0) -> int:
	if not is_finite(delay_ms) or not is_finite(interval_ms) or delay_ms < 0.0 or interval_ms < 0.0 or not callback.is_valid():
		return 0
	var status := get_clock()
	var bind_on_install := not bool(status.get("installed", false))
	var now_ms := float(status.get("now_ms", 0.0))
	var due_ms := delay_ms if bind_on_install else now_ms + delay_ms
	if not bind_on_install and not _clock_schedule_deadline_is_representable(now_ms, delay_ms, due_ms, interval_ms):
		return 0
	var schedule_id := next_clock_schedule_id
	next_clock_schedule_id += 1
	clock_schedules.append({
		"id": schedule_id,
		"generation": int(status.get("generation", 0)),
		"due_ms": due_ms,
		"bind_on_install": bind_on_install,
		"interval_ms": interval_ms,
		"callback": callback,
	})
	return schedule_id

func clock_cancel(schedule_id: int) -> void:
	clock_schedules = clock_schedules.filter(func(item: Dictionary) -> bool: return int(item.id) != schedule_id)
	if active_clock_schedule_id == schedule_id:
		active_clock_schedule_cancelled = true

func _bind_pending_clock_schedules(status: Dictionary) -> void:
	if not bool(status.get("installed", false)):
		var generation := int(status.get("generation", 0))
		clock_schedules = clock_schedules.filter(func(item: Dictionary) -> bool:
			return int(item.get("generation", -1)) == generation)
		return
	var generation := int(status.get("generation", 0))
	var now_ms := float(status.get("now_ms", 0.0))
	var retained_schedules: Array[Dictionary] = []
	for item: Dictionary in clock_schedules:
		if bool(item.get("bind_on_install", false)) and generation == int(item.get("generation", 0)) + 1:
			var delay_ms := float(item.get("due_ms", 0.0))
			var due_ms := now_ms + delay_ms
			if not _clock_schedule_deadline_is_representable(
				now_ms, delay_ms, due_ms, float(item.get("interval_ms", 0.0))):
				continue
			item.generation = generation
			item.due_ms = due_ms
			item.bind_on_install = false
		retained_schedules.append(item)
	clock_schedules = retained_schedules

func _drain_clock_schedules(now_ms: float, generation: int, callback_budget: int) -> int:
	clock_schedules = clock_schedules.filter(func(item: Dictionary) -> bool: return int(item.get("generation", 0)) == generation)
	clock_schedules.sort_custom(func(a: Dictionary, b: Dictionary) -> bool:
		return float(a.due_ms) < float(b.due_ms) or (float(a.due_ms) == float(b.due_ms) and int(a.id) < int(b.id)))
	var callbacks := 0
	while not clock_schedules.is_empty() and float(clock_schedules[0].due_ms) <= now_ms:
		if callbacks >= callback_budget:
			clock_execution_limit_reached = true
			clock_schedules.clear()
			active_clock_schedule_id = 0
			active_clock_schedule_cancelled = false
			return callbacks
		callbacks += 1
		var item: Dictionary = clock_schedules.pop_front()
		active_clock_schedule_id = int(item.id)
		active_clock_schedule_cancelled = false
		(item.callback as Callable).call()
		var current_generation := int(context.get_clock().get("generation", -1))
		if current_generation != generation:
			clock_schedules = clock_schedules.filter(func(candidate: Dictionary) -> bool:
				return int(candidate.get("generation", -1)) == current_generation)
			active_clock_schedule_id = 0
			active_clock_schedule_cancelled = false
			return callbacks
		if float(item.interval_ms) > 0.0 and not active_clock_schedule_cancelled:
			var previous_due_ms := float(item.due_ms)
			var next_due_ms := previous_due_ms + float(item.interval_ms)
			if is_finite(next_due_ms) and next_due_ms > previous_due_ms:
				item.due_ms = next_due_ms
				clock_schedules.append(item)
		clock_schedules.sort_custom(func(a: Dictionary, b: Dictionary) -> bool:
			return float(a.due_ms) < float(b.due_ms) or (float(a.due_ms) == float(b.due_ms) and int(a.id) < int(b.id)))
		active_clock_schedule_id = 0
		active_clock_schedule_cancelled = false
	return callbacks


func _clock_schedule_deadline_is_representable(
	now_ms: float, delay_ms: float, due_ms: float, interval_ms: float) -> bool:
	if not is_finite(due_ms) or (delay_ms > 0.0 and due_ms <= now_ms):
		return false
	if interval_ms > 0.0:
		var next_due_ms := due_ms + interval_ms
		if not is_finite(next_due_ms) or next_due_ms <= due_ms:
			return false
	return true


func reset_context(options: Dictionary = {}) -> Dictionary:
	if not _ensure_context():
		return {"result": -1}
	var resolved := options.duplicate()
	if not resolved.has("expected_session_epoch"):
		resolved["expected_session_epoch"] = context.get_context_status().get("session_epoch", 0)
	var report: Dictionary = context.reset_context(resolved)
	if report.get("result", -1) == 1:
		_disconnect_buttons()
		buttons_by_id.clear()
		tabs_by_id.clear()
		list_items_by_id.clear()
		controls_by_id.clear()
		suppressed_clicks.clear()
		if (int(resolved.get("flags", RESET_DEFAULT_FLAGS)) & RESET_CLOCK_FLAG) != 0:
			last_clock_ticks_ms = Time.get_ticks_msec()
			observed_clock_generation = -1
			clock_schedules.clear()
			active_clock_schedule_id = 0
			active_clock_schedule_cancelled = false
			clock_run_active = false
			clock_run_callbacks_remaining = CLOCK_CALLBACK_LIMIT
			clock_run_generation = -1
			clock_execution_limit_reached = false
	return report


func _ensure_context() -> bool:
	if disposed:
		return false
	if context != null:
		return not unavailable
	if unavailable:
		return false

	if gdextension_resource == null:
		gdextension_resource = load(GDEXTENSION_RESOURCE)
		if gdextension_resource == null:
			_mark_unavailable(
				"Failed to load %s. Ensure the Gua addon files are installed and rebuild the Godot GDExtension DLL with: %s"
				% [GDEXTENSION_RESOURCE, REBUILD_COMMAND]
			)
			return false

	if not ClassDB.class_exists(CONTEXT_CLASS) or not ClassDB.can_instantiate(CONTEXT_CLASS):
		_mark_unavailable(
			"%s is not available. Ensure addons/gua/gua.gdextension is enabled and rebuild the Godot GDExtension DLL with: %s"
			% [CONTEXT_CLASS, REBUILD_COMMAND]
		)
		return false

	context = ClassDB.instantiate(CONTEXT_CLASS)
	if context == null:
		_mark_unavailable(
			"Failed to instantiate %s. Ensure addons/gua/gua.gdextension loaded successfully and rebuild with: %s"
			% [CONTEXT_CLASS, REBUILD_COMMAND]
		)
		return false

	var missing_methods := _missing_context_methods(context)
	if not missing_methods.is_empty():
		_mark_unavailable(
			"%s is missing required method '%s'. The vendored gua_godot Windows debug DLL is stale. Rebuild it with: %s"
			% [CONTEXT_CLASS, missing_methods[0], REBUILD_COMMAND]
		)
		return false

	var version: Variant = JSON.parse_string(context.get_version_json())
	if not version is Dictionary or not (version as Dictionary).get("capabilities", []).has("agent_projection_v1"):
		_mark_unavailable(
			"%s does not advertise agent_projection_v1. The vendored gua_godot Windows debug DLL is stale. Rebuild it with: %s"
			% [CONTEXT_CLASS, REBUILD_COMMAND]
		)
		return false

	context.enable_virtual_clock_adapter()
	context.enable_world_object_tree_adapter()

	return true


func _missing_context_methods(candidate: Object) -> Array:
	var missing_methods := []
	for method in REQUIRED_CONTEXT_METHODS:
		if not candidate.has_method(method):
			missing_methods.append(method)
	return missing_methods


func _mark_unavailable(message: String) -> void:
	unavailable = true
	context = null
	push_error(message)


func _collect_control(control: Control, parent_id: String) -> void:
	var id := _control_id(control)
	var role := _control_role(control)
	var label := _control_label(control)
	var descriptor := {
		"id": id,
		"role": role,
		"label": label,
		"bounds": Rect2(control.global_position, control.size),
		"visible": control.is_visible_in_tree(),
		"enabled": _control_enabled(control),
		"focused": _control_focused(control),
		"agent_exposure": str(control.get_meta(&"gua_agent_exposure", "auto")),
		"agent_field_rules": control.get_meta(&"gua_agent_field_rules", []),
		"agent_allowed_actions": control.get_meta(&"gua_agent_allowed_actions", []),
		"agent_allowed_actions_set": control.has_meta(&"gua_agent_allowed_actions"),
	}
	if not parent_id.is_empty():
		descriptor["parent_id"] = parent_id
	var text: Variant = _control_text(control)
	if text != null:
		descriptor["text"] = text
	var value: Variant = _control_value(control)
	if value != null:
		descriptor["value"] = value
	if control is CheckBox:
		descriptor["checked"] = (control as CheckBox).button_pressed
	if control is LineEdit:
		var line := control as LineEdit
		descriptor["caret_position"] = line.caret_column
		descriptor["selection_start"] = line.get_selection_from_column() if line.has_selection() else line.caret_column
		descriptor["selection_end"] = line.get_selection_to_column() if line.has_selection() else line.caret_column
	elif control is TextEdit:
		var edit := control as TextEdit
		descriptor["caret_position"] = edit.get_caret_column()
		descriptor["selection_start"] = edit.get_selection_from_column() if edit.has_selection() else edit.get_caret_column()
		descriptor["selection_end"] = edit.get_selection_to_column() if edit.has_selection() else edit.get_caret_column()
	if control is ScrollContainer:
		var scroll := control as ScrollContainer
		descriptor["scroll_x"] = scroll.scroll_horizontal
		descriptor["scroll_y"] = scroll.scroll_vertical
		descriptor["scroll_max_x"] = scroll.get_h_scroll_bar().max_value
		descriptor["scroll_max_y"] = scroll.get_v_scroll_bar().max_value
	if control is Range:
		var range := control as Range
		if not (control.has_meta(META_SENSITIVE) and control.get_meta(META_SENSITIVE)):
			descriptor["range_value"] = range.value
		descriptor["range_min"] = range.min_value
		descriptor["range_max"] = range.max_value
	if control is OptionButton:
		descriptor["selected_index"] = (control as OptionButton).selected
	elif control is ItemList:
		var selected := (control as ItemList).get_selected_items()
		descriptor["selected_index"] = selected[0] if not selected.is_empty() else -1
	elif control is TabContainer:
		descriptor["selected_index"] = (control as TabContainer).current_tab
	context.register_node_v2(descriptor)
	controls_by_id[id] = control

	if control is BaseButton:
		buttons_by_id[id] = control
		_connect_button(control as BaseButton, id)
	if control is ItemList:
		_collect_item_list_items(control as ItemList, id)
	if control is TabContainer:
		_collect_tab_items(control as TabContainer, id)

	for child in control.get_children():
		if child is Control:
			_collect_control(child as Control, id)


func _collect_item_list_items(item_list: ItemList, parent_id: String) -> void:
	for index in range(item_list.item_count):
		var label := item_list.get_item_text(index)
		var id := "%s$item:%d" % [parent_id, index]
		list_items_by_id[id] = {"list": item_list, "index": index}
		context.register_node_v2({
			"id": id,
			"parent_id": parent_id,
			"role": "listitem",
			"label": label,
			"text": label,
			"bounds": Rect2(item_list.global_position, item_list.size),
			"visible": item_list.is_visible_in_tree(),
			"enabled": not item_list.is_item_disabled(index),
			"selected": item_list.is_selected(index),
			"agent_exposure": str(item_list.get_meta(&"gua_agent_exposure", "auto")),
			"agent_field_rules": item_list.get_meta(&"gua_agent_field_rules", []),
			"agent_allowed_actions": item_list.get_meta(&"gua_agent_allowed_actions", []),
			"agent_allowed_actions_set": item_list.has_meta(&"gua_agent_allowed_actions"),
		})


func _collect_tab_items(tab_container: TabContainer, parent_id: String) -> void:
	for index in range(tab_container.get_tab_count()):
		var label := tab_container.get_tab_title(index)
		var id := "%s$tab:%d" % [parent_id, index]
		tabs_by_id[id] = {"container": tab_container, "index": index}
		context.register_node_v2({
			"id": id,
			"parent_id": parent_id,
			"role": "tab",
			"label": label,
			"text": label,
			"bounds": Rect2(tab_container.global_position, tab_container.size),
			"visible": tab_container.is_visible_in_tree(),
			"enabled": not tab_container.is_tab_disabled(index),
			"selected": tab_container.current_tab == index,
			"agent_exposure": str(tab_container.get_meta(&"gua_agent_exposure", "auto")),
			"agent_field_rules": tab_container.get_meta(&"gua_agent_field_rules", []),
			"agent_allowed_actions": tab_container.get_meta(&"gua_agent_allowed_actions", []),
			"agent_allowed_actions_set": tab_container.has_meta(&"gua_agent_allowed_actions"),
		})


func _dispatch_click_requests() -> void:
	for id in buttons_by_id.keys():
		var button := buttons_by_id[id] as BaseButton
		while true:
			var request: Dictionary = context.consume_action_request("click", id)
			if request.is_empty():
				break
			var error_code := -2 if not _agent_action_allowed(button, "click", request) else (-3 if not button.is_visible_in_tree() else (-4 if button.disabled else 0))
			if error_code != 0:
				_emit_click_result(request, id, error_code)
				continue

			var group := button.button_group
			if button.toggle_mode and not (button.button_pressed and group != null and not group.allow_unpress):
				button.button_pressed = not button.button_pressed
			suppressed_clicks[id] = true
			button.emit_signal("pressed")
			_emit_click_result(request, id, 0)

	for id in tabs_by_id.keys():
		var target: Dictionary = tabs_by_id[id]
		var tab_container := target["container"] as TabContainer
		var index := int(target["index"])
		while true:
			var request: Dictionary = context.consume_action_request("click", id)
			if request.is_empty():
				break
			var error_code := -2 if not _agent_action_allowed(tab_container, "click", request) else (-3 if not tab_container.is_visible_in_tree() else (-4 if tab_container.is_tab_disabled(index) else 0))
			if error_code != 0:
				_emit_click_result(request, id, error_code)
				continue

			tab_container.current_tab = index
			_emit_click_result(request, id, 0)


func _emit_click_result(request: Dictionary, id: String, error_code: int) -> void:
	context.emit_action_result({
		"request_id": request.get("request_id", 0),
		"action": "click",
		"node_id": id,
		"succeeded": error_code == 0,
		"error_code": error_code,
	})


func _dispatch_action_requests() -> void:
	for id in controls_by_id.keys():
		var control := controls_by_id[id] as Control
		for action in ["focus", "set_value", "set_checked", "select", "scroll", "press_key"]:
			while true:
				var request: Dictionary = context.consume_action_request(action, id)
				if request.is_empty():
					break
				var error_code := _apply_action(control, action, request)
				context.emit_action_result({
					"request_id": request.get("request_id", 0),
					"action": action,
					"node_id": id,
					"succeeded": error_code == 0,
					"error_code": error_code,
					"value": request.get("value", ""),
					"sensitive": request.get("sensitive", false),
				})
	for id in list_items_by_id.keys():
		_dispatch_derived_select_requests(id, list_items_by_id[id])
	for id in tabs_by_id.keys():
		_dispatch_derived_select_requests(id, tabs_by_id[id])
	while true:
		var request: Dictionary = context.consume_action_request("press_key", "")
		if request.is_empty():
			break
		var focused := root.get_viewport().gui_get_focus_owner()
		var error_code := _apply_action(focused, "press_key", request) if focused is Control else -2
		context.emit_action_result({
			"request_id": request.get("request_id", 0), "action": "press_key", "node_id": "",
			"succeeded": error_code == 0, "error_code": error_code,
		})


func _agent_action_allowed(target: Node, action: String, request: Dictionary = {}) -> bool:
	if int(request.get("observation_profile", context.get_observation_profile())) != 1:
		return true
	var current: Node = target
	var target_node := true
	while current != null:
		if str(current.get_meta(&"gua_agent_exposure", "auto")) == "private":
			return false
		if target_node and current.has_meta(&"gua_agent_allowed_actions"):
			var allowed: Array = current.get_meta(&"gua_agent_allowed_actions", [])
			if not allowed.has(action):
				return false
		target_node = false
		current = current.get_parent()
	return true


func _apply_action(control: Control, action: String, request: Dictionary) -> int:
	if not _agent_action_allowed(control, action, request):
		return -2
	if not control.is_visible_in_tree():
		return -3
	if not _control_enabled(control):
		return -4
	match action:
		"focus":
			if control.focus_mode == Control.FOCUS_NONE:
				return -5
			var focus_target: Control = (control as SpinBox).get_line_edit() if control is SpinBox else control
			if focus_target.focus_mode == Control.FOCUS_NONE:
				return -5
			focus_target.grab_focus()
			if not focus_target.has_focus():
				return -5
		"set_value":
			var value = request.get("value", "")
			if control is LineEdit:
				(control as LineEdit).text = value
			elif control is TextEdit:
				(control as TextEdit).text = value
			elif control is Range and str(value).is_valid_float():
				(control as Range).value = float(value)
			else:
				return -6
			if request.get("sensitive", false):
				control.set_meta(META_SENSITIVE, true)
		"set_checked":
			if control is BaseButton:
				(control as BaseButton).button_pressed = request.get("bool_value", false)
			else:
				return -5
		"select":
			if not _select_value(control, str(request.get("value", ""))):
				return -6
		"scroll":
			var scroll_unit := int(request.get("scroll_unit", 0))
			if scroll_unit != 0 and scroll_unit != 1:
				return -6
			var delta_x := float(request.get("delta_x", 0.0))
			var delta_y := float(request.get("delta_y", 0.0))
			if scroll_unit == 1:
				delta_x *= _semantic_scroll_extent(control, true)
				delta_y *= _semantic_scroll_extent(control, false)
			if control is ScrollContainer:
				var scroll := control as ScrollContainer
				scroll.scroll_horizontal += int(round(delta_x))
				scroll.scroll_vertical += int(round(delta_y))
			elif control is ItemList:
				var item_list := control as ItemList
				item_list.get_h_scroll_bar().value += delta_x
				item_list.get_v_scroll_bar().value += delta_y
			else:
				return -5
		"press_key":
			if control.focus_mode == Control.FOCUS_NONE:
				return -5
			if not control.has_focus():
				control.grab_focus()
			if not control.has_focus():
				return -5
			var event := InputEventKey.new()
			event.keycode = OS.find_keycode_from_string(str(request.get("key", "")))
			if event.keycode == KEY_NONE:
				return -6
			var modifiers := int(request.get("modifiers", 0))
			event.shift_pressed = (modifiers & 1) != 0
			event.alt_pressed = (modifiers & 2) != 0
			event.ctrl_pressed = (modifiers & 4) != 0
			event.meta_pressed = (modifiers & 8) != 0
			event.pressed = true
			control.get_viewport().push_input(event, true)
			var release := event.duplicate() as InputEventKey
			release.pressed = false
			control.get_viewport().push_input(release, true)
		_:
			return -5
	return 0


func _dispatch_game_input_requests() -> void:
	while true:
		var request: Dictionary = context.consume_game_input_request()
		if request.is_empty():
			break
		var error_code := _apply_game_input(request)
		context.complete_game_input_request({
			"request_id": request.get("request_id", 0),
			"succeeded": error_code == 0,
			"error_code": error_code,
		})


func _apply_game_input(request: Dictionary) -> int:
	var kind := int(request.get("kind", 0))
	var operation := int(request.get("operation", 0))
	var target := str(request.get("target", ""))
	var owner_id := int(request.get("owner_id", 0))
	if kind == 6 or operation == 10:
		_release_owner_injected_inputs(owner_id)
		return 0
	if kind == 1:
		var value = JSON.parse_string(str(request.get("value_json", "null")))
		if operation == 1:
			_pulse_semantic_input(target, true)
		elif operation == 2:
			if value is Dictionary and value.has("x") and value.has("y"):
				value = Vector2(float(value.x), float(value.y))
			if value is String:
				_pulse_semantic_input(target, value)
			else:
				_set_semantic_input_owner(owner_id, target, value)
		elif operation == 3:
			_release_semantic_input_owner(owner_id, target)
		else:
			return -4
		return 0
	if kind == 2:
		var keycode := _keycode_from_w3c(target)
		var key_location := _key_location_from_w3c(target)
		if keycode == KEY_NONE:
			return -6
		if operation == 1:
			var restore_pressed := _has_physical_key_holder(target)
			if restore_pressed:
				_inject_key(keycode, false, key_location)
			_inject_key(keycode, true, key_location)
			_inject_key(keycode, false, key_location)
			if restore_pressed:
				_inject_key(keycode, true, key_location)
		elif operation == 4:
			held_physical_keys["%d:%s" % [owner_id, target]] = {"owner": owner_id, "target": target, "keycode": keycode, "location": key_location}
			_inject_key(keycode, true, key_location)
		elif operation in [3, 5]:
			held_physical_keys.erase("%d:%s" % [owner_id, target])
			_inject_key(keycode, _has_physical_key_holder(target), key_location)
		else:
			return -4
		return 0
	if kind == 3:
		if operation in [6, 7]:
			var motion := InputEventMouseMotion.new()
			var coordinates := target.split(":")
			var point := Vector2(float(request.get("x", 0.0)), float(request.get("y", 0.0)))
			if operation == 6:
				if coordinates.size() > 1 and coordinates[1] == "viewport_normalized" and root != null:
					point *= root.get_viewport().get_visible_rect().size
				synthetic_pointer_position = point
			else:
				motion.relative = point
				synthetic_pointer_position += point
			motion.position = synthetic_pointer_position
			motion.global_position = synthetic_pointer_position
			Input.parse_input_event(motion)
			return 0
		if operation == 8:
			var horizontal := float(request.get("x", 0.0))
			var vertical := float(request.get("y", 0.0))
			if horizontal != 0.0:
				_inject_wheel(MOUSE_BUTTON_WHEEL_RIGHT if horizontal > 0.0 else MOUSE_BUTTON_WHEEL_LEFT, absf(horizontal))
			if vertical != 0.0:
				_inject_wheel(MOUSE_BUTTON_WHEEL_DOWN if vertical > 0.0 else MOUSE_BUTTON_WHEEL_UP, absf(vertical))
			return 0
		var button := _mouse_button(target)
		if button == MOUSE_BUTTON_NONE:
			return -6
		var pressed := operation == 4
		if operation not in [3, 4, 5]:
			return -4
		if pressed:
			held_pointer_buttons["%d:%s" % [owner_id, target]] = {"owner": owner_id, "target": target, "button": button}
		else:
			held_pointer_buttons.erase("%d:%s" % [owner_id, target])
		var mouse := InputEventMouseButton.new()
		mouse.button_index = button
		mouse.pressed = _has_pointer_button_holder(target)
		mouse.position = synthetic_pointer_position
		mouse.global_position = synthetic_pointer_position
		Input.parse_input_event(mouse)
		return 0
	if kind == 4:
		var device := int(request.get("device_index", 0))
		if operation == 9:
			_release_owner_gamepad(owner_id, device)
			return 0
		if operation == 2:
			var axis := _gamepad_axis(target)
			if axis < 0 or target in ["left_trigger", "right_trigger"]:
				return -6
			var value := clampf(float(JSON.parse_string(str(request.get("value_json", "0")))), -1.0, 1.0)
			_set_owner_gamepad_axis(owner_id, device, target, axis, value)
			return 0
		if operation == 3 and _gamepad_axis(target) >= 0:
			_release_owner_gamepad_axis(owner_id, device, target)
			return 0
		if target in ["left_trigger", "right_trigger"]:
			if operation == 4:
				_set_owner_gamepad_axis(owner_id, device, target, _gamepad_axis(target), 1.0)
			elif operation == 5:
				_release_owner_gamepad_axis(owner_id, device, target)
			else:
				return -4
			return 0
		var button_index := _gamepad_button(target)
		if button_index < 0:
			return -6
		var pressed := operation == 4
		if operation not in [3, 4, 5]:
			return -4
		var button_key := "%d:%d:%s" % [owner_id, device, target]
		if pressed:
			held_gamepad_buttons[button_key] = {"owner": owner_id, "device": device, "target": target, "button": button_index}
		else:
			held_gamepad_buttons.erase(button_key)
		var joy := InputEventJoypadButton.new()
		joy.device = device
		joy.button_index = button_index
		joy.pressed = _has_gamepad_button_holder(device, target)
		Input.parse_input_event(joy)
		return 0
	if kind == 5:
		var text = JSON.parse_string(str(request.get("value_json", "\"\"")))
		if not text is String:
			return -6
		for index in text.length():
			var character := InputEventKey.new()
			character.unicode = text.unicode_at(index)
			character.pressed = true
			Input.parse_input_event(character)
		return 0
	return -4


func _set_semantic_input(action_id: String, value: Variant) -> void:
	game_input_values[action_id] = value
	game_input_action_changed.emit(action_id, value)


func _neutral_semantic_input(value: Variant) -> Variant:
	if value is Vector2:
		return Vector2.ZERO
	if value is float or value is int:
		return 0.0
	if value is String:
		return ""
	return false


func _pulse_semantic_input(action_id: String, value: Variant) -> void:
	var previous = game_input_values.get(action_id, _neutral_semantic_input(value))
	_set_semantic_input(action_id, value)
	_set_semantic_input(action_id, previous)


func _set_semantic_input_owner(owner_id: int, action_id: String, value: Variant) -> void:
	if not semantic_values_by_owner.has(action_id):
		semantic_values_by_owner[action_id] = {}
	game_input_sequence += 1
	var owners: Dictionary = semantic_values_by_owner[action_id]
	owners[owner_id] = {"value": value, "sequence": game_input_sequence}
	_set_semantic_input(action_id, value)


func _release_semantic_input_owner(owner_id: int, action_id: String) -> void:
	if not semantic_values_by_owner.has(action_id):
		return
	var owners: Dictionary = semantic_values_by_owner[action_id]
	owners.erase(owner_id)
	var sequence := -1
	var value = _neutral_semantic_input(game_input_values.get(action_id, false))
	for owned in owners.values():
		if int(owned.get("sequence", -1)) > sequence:
			sequence = int(owned.get("sequence", -1))
			value = owned.get("value")
	if owners.is_empty():
		semantic_values_by_owner.erase(action_id)
	_set_semantic_input(action_id, value)


func _inject_key(keycode: Key, pressed: bool, location: KeyLocation = KEY_LOCATION_UNSPECIFIED) -> void:
	var event := InputEventKey.new()
	event.physical_keycode = keycode
	event.keycode = keycode
	event.location = location
	event.pressed = pressed
	Input.parse_input_event(event)


func _has_physical_key_holder(target: String) -> bool:
	for held in held_physical_keys.values():
		if str(held.get("target", "")) == target:
			return true
	return false


func _has_pointer_button_holder(target: String) -> bool:
	for held in held_pointer_buttons.values():
		if str(held.get("target", "")) == target:
			return true
	return false


func _has_gamepad_button_holder(device: int, target: String) -> bool:
	for held in held_gamepad_buttons.values():
		if int(held.get("device", -1)) == device and str(held.get("target", "")) == target:
			return true
	return false


func _keycode_from_w3c(code: String) -> Key:
	if code.begins_with("Key") and code.length() == 4:
		return OS.find_keycode_from_string(code.substr(3, 1))
	if code.begins_with("Digit") and code.length() == 6:
		return OS.find_keycode_from_string(code.substr(5, 1))
	if code.begins_with("F") and code.substr(1).is_valid_int():
		var function_index := int(code.substr(1))
		if function_index >= 1 and function_index <= 24:
			return KEY_F1 + function_index - 1
	if code.begins_with("Numpad") and code.length() == 7 and code.substr(6, 1).is_valid_int():
		return KEY_KP_0 + int(code.substr(6, 1))
	var names := {
		"ArrowUp": KEY_UP, "ArrowDown": KEY_DOWN, "ArrowLeft": KEY_LEFT, "ArrowRight": KEY_RIGHT,
		"Space": KEY_SPACE, "Enter": KEY_ENTER, "Escape": KEY_ESCAPE, "Tab": KEY_TAB,
		"ShiftLeft": KEY_SHIFT, "ShiftRight": KEY_SHIFT, "ControlLeft": KEY_CTRL,
		"ControlRight": KEY_CTRL, "AltLeft": KEY_ALT, "AltRight": KEY_ALT,
		"MetaLeft": KEY_META, "MetaRight": KEY_META, "Backquote": KEY_QUOTELEFT,
		"Backslash": KEY_BACKSLASH, "Backspace": KEY_BACKSPACE, "BracketLeft": KEY_BRACKETLEFT,
		"BracketRight": KEY_BRACKETRIGHT, "CapsLock": KEY_CAPSLOCK, "Comma": KEY_COMMA,
		"ContextMenu": KEY_MENU, "Delete": KEY_DELETE, "End": KEY_END, "Equal": KEY_EQUAL,
		"Home": KEY_HOME, "Insert": KEY_INSERT, "Minus": KEY_MINUS, "NumLock": KEY_NUMLOCK,
		"PageDown": KEY_PAGEDOWN, "PageUp": KEY_PAGEUP, "Pause": KEY_PAUSE, "Period": KEY_PERIOD,
		"Quote": KEY_APOSTROPHE, "ScrollLock": KEY_SCROLLLOCK, "Semicolon": KEY_SEMICOLON,
		"Slash": KEY_SLASH, "PrintScreen": KEY_PRINT, "NumpadAdd": KEY_KP_ADD,
		"NumpadDecimal": KEY_KP_PERIOD, "NumpadDivide": KEY_KP_DIVIDE, "NumpadEnter": KEY_KP_ENTER,
		"NumpadMultiply": KEY_KP_MULTIPLY, "NumpadSubtract": KEY_KP_SUBTRACT,
	}
	return names.get(code, KEY_NONE)


func _key_location_from_w3c(code: String) -> KeyLocation:
	if code in ["ShiftLeft", "ControlLeft", "AltLeft", "MetaLeft"]:
		return KEY_LOCATION_LEFT
	if code in ["ShiftRight", "ControlRight", "AltRight", "MetaRight"]:
		return KEY_LOCATION_RIGHT
	return KEY_LOCATION_UNSPECIFIED


func _mouse_button(name: String) -> MouseButton:
	return {
		"primary": MOUSE_BUTTON_LEFT, "secondary": MOUSE_BUTTON_RIGHT, "auxiliary": MOUSE_BUTTON_MIDDLE,
		"back": MOUSE_BUTTON_XBUTTON1, "forward": MOUSE_BUTTON_XBUTTON2,
	}.get(name, MOUSE_BUTTON_NONE)


func _inject_wheel(button: MouseButton, factor: float) -> void:
	var wheel := InputEventMouseButton.new()
	wheel.button_index = button
	wheel.pressed = true
	wheel.factor = factor
	wheel.position = synthetic_pointer_position
	wheel.global_position = synthetic_pointer_position
	Input.parse_input_event(wheel)


func _gamepad_button(name: String) -> int:
	return {
		"south": JOY_BUTTON_A, "east": JOY_BUTTON_B, "west": JOY_BUTTON_X, "north": JOY_BUTTON_Y,
		"back": JOY_BUTTON_BACK, "start": JOY_BUTTON_START,
		"left_stick": JOY_BUTTON_LEFT_STICK, "right_stick": JOY_BUTTON_RIGHT_STICK,
		"left_shoulder": JOY_BUTTON_LEFT_SHOULDER, "right_shoulder": JOY_BUTTON_RIGHT_SHOULDER,
		"dpad_up": JOY_BUTTON_DPAD_UP, "dpad_down": JOY_BUTTON_DPAD_DOWN,
		"dpad_left": JOY_BUTTON_DPAD_LEFT, "dpad_right": JOY_BUTTON_DPAD_RIGHT,
	}.get(name, -1)


func _gamepad_axis(name: String) -> int:
	return {
		"left_stick_x": JOY_AXIS_LEFT_X, "left_stick_y": JOY_AXIS_LEFT_Y,
		"right_stick_x": JOY_AXIS_RIGHT_X, "right_stick_y": JOY_AXIS_RIGHT_Y,
		"left_trigger": JOY_AXIS_TRIGGER_LEFT, "right_trigger": JOY_AXIS_TRIGGER_RIGHT,
	}.get(name, -1)


func _set_owner_gamepad_axis(owner_id: int, device: int, target: String, axis: int, value: float) -> void:
	game_input_sequence += 1
	var axis_key := "%d:%d:%s" % [owner_id, device, target]
	held_gamepad_axes[axis_key] = {"owner": owner_id, "device": device, "target": target, "axis": axis, "value": value, "sequence": game_input_sequence}
	var motion := InputEventJoypadMotion.new()
	motion.device = device
	motion.axis = axis
	motion.axis_value = value
	Input.parse_input_event(motion)


func _release_gamepad(device: int) -> void:
	for key in held_gamepad_buttons.keys():
		var held: Dictionary = held_gamepad_buttons[key]
		if int(held.device) != device:
			continue
		var event := InputEventJoypadButton.new()
		event.device = device
		event.button_index = int(held.button)
		event.pressed = false
		Input.parse_input_event(event)
		held_gamepad_buttons.erase(key)
	for key in held_gamepad_axes.keys():
		var held: Dictionary = held_gamepad_axes[key]
		if int(held.device) != device:
			continue
		var event := InputEventJoypadMotion.new()
		event.device = device
		event.axis = int(held.axis)
		event.axis_value = 0.0
		Input.parse_input_event(event)
		held_gamepad_axes.erase(key)


func _release_owner_gamepad_axis(owner_id: int, device: int, target: String) -> void:
	held_gamepad_axes.erase("%d:%d:%s" % [owner_id, device, target])
	var selected_value := 0.0
	var selected_sequence := -1
	var axis := _gamepad_axis(target)
	for held in held_gamepad_axes.values():
		if int(held.get("device", -1)) != device or str(held.get("target", "")) != target:
			continue
		if int(held.get("sequence", -1)) > selected_sequence:
			selected_sequence = int(held.get("sequence", -1))
			selected_value = float(held.get("value", 0.0))
	var event := InputEventJoypadMotion.new()
	event.device = device
	event.axis = axis
	event.axis_value = selected_value
	Input.parse_input_event(event)


func _release_owner_gamepad(owner_id: int, device: int) -> void:
	for key in held_gamepad_buttons.keys():
		var held: Dictionary = held_gamepad_buttons[key]
		if int(held.get("owner", 0)) != owner_id or int(held.get("device", -1)) != device:
			continue
		var target := str(held.get("target", ""))
		var button := int(held.get("button", -1))
		held_gamepad_buttons.erase(key)
		var event := InputEventJoypadButton.new()
		event.device = device
		event.button_index = button
		event.pressed = _has_gamepad_button_holder(device, target)
		Input.parse_input_event(event)
	for key in held_gamepad_axes.keys():
		var held: Dictionary = held_gamepad_axes[key]
		if int(held.get("owner", 0)) == owner_id and int(held.get("device", -1)) == device:
			_release_owner_gamepad_axis(owner_id, device, str(held.get("target", "")))


func _release_owner_injected_inputs(owner_id: int) -> void:
	for key in held_physical_keys.keys():
		var held: Dictionary = held_physical_keys[key]
		if int(held.get("owner", 0)) != owner_id:
			continue
		var target := str(held.get("target", ""))
		var keycode: Key = int(held.get("keycode", KEY_NONE))
		var location: KeyLocation = int(held.get("location", KEY_LOCATION_UNSPECIFIED))
		held_physical_keys.erase(key)
		_inject_key(keycode, _has_physical_key_holder(target), location)
	for key in held_pointer_buttons.keys():
		var held: Dictionary = held_pointer_buttons[key]
		if int(held.get("owner", 0)) != owner_id:
			continue
		var target := str(held.get("target", ""))
		var button: MouseButton = int(held.get("button", MOUSE_BUTTON_NONE))
		held_pointer_buttons.erase(key)
		var event := InputEventMouseButton.new()
		event.button_index = button
		event.pressed = _has_pointer_button_holder(target)
		event.position = synthetic_pointer_position
		event.global_position = synthetic_pointer_position
		Input.parse_input_event(event)
	for device in range(4):
		_release_owner_gamepad(owner_id, device)
	for action_id in semantic_values_by_owner.keys():
		_release_semantic_input_owner(owner_id, str(action_id))


func _release_all_injected_inputs() -> void:
	release_injected_state(held_physical_keys, held_pointer_buttons, held_gamepad_buttons, held_gamepad_axes, synthetic_pointer_position, semantic_values_by_owner, game_input_values, game_input_action_changed)


func _semantic_scroll_extent(control: Control, horizontal: bool) -> float:
	if control is ItemList:
		var item_list := control as ItemList
		if item_list.item_count > 0:
			var item_size := item_list.get_item_rect(0).size
			var item_extent := item_size.x if horizontal else item_size.y
			if item_extent > 0.0:
				return item_extent
		var list_bar: ScrollBar = item_list.get_h_scroll_bar() if horizontal else item_list.get_v_scroll_bar()
		if list_bar.custom_step > 0.0:
			return list_bar.custom_step
	elif control is ScrollContainer:
		var scroll := control as ScrollContainer
		var custom_step := scroll.scroll_horizontal_custom_step if horizontal else scroll.scroll_vertical_custom_step
		if custom_step > 0.0:
			return custom_step
		var scroll_bar: ScrollBar = scroll.get_h_scroll_bar() if horizontal else scroll.get_v_scroll_bar()
		if scroll_bar.custom_step > 0.0:
			return scroll_bar.custom_step
	return maxf(1.0, float(control.get_theme_default_font_size()))


func _dispatch_derived_select_requests(id: String, target: Dictionary) -> void:
	while true:
		var request: Dictionary = context.consume_action_request("select", id)
		if request.is_empty():
			break
		var error_code := _select_derived_item(target, request)
		context.emit_action_result({
			"request_id": request.get("request_id", 0),
			"action": "select",
			"node_id": id,
			"succeeded": error_code == 0,
			"error_code": error_code,
		})


func _select_derived_item(target: Dictionary, request: Dictionary) -> int:
	var index := int(target["index"])
	if target.has("list"):
		var item_list := target["list"] as ItemList
		if not _agent_action_allowed(item_list, "select", request):
			return -2
		if not item_list.is_visible_in_tree():
			return -3
		if item_list.is_item_disabled(index):
			return -4
		item_list.select(index)
		item_list.item_selected.emit(index)
		return 0
	var tab_container := target["container"] as TabContainer
	if not _agent_action_allowed(tab_container, "select", request):
		return -2
	if not tab_container.is_visible_in_tree():
		return -3
	if tab_container.is_tab_disabled(index):
		return -4
	tab_container.current_tab = index
	return 0


func _select_value(control: Control, value: String) -> bool:
	if control is OptionButton:
		var option := control as OptionButton
		for index in range(option.item_count):
			var semantic_value := str(option.get_item_metadata(index)) if option.get_item_metadata(index) != null else option.get_item_text(index)
			if semantic_value == value:
				option.select(index)
				option.item_selected.emit(index)
				return true
	if control is ItemList:
		var item_list := control as ItemList
		for index in range(item_list.item_count):
			if item_list.get_item_text(index) == value:
				item_list.select(index)
				item_list.item_selected.emit(index)
				return true
	if control is TabContainer:
		var tabs := control as TabContainer
		for index in range(tabs.get_tab_count()):
			if tabs.get_tab_title(index) == value:
				tabs.current_tab = index
				return true
	return false


func _connect_button(button: BaseButton, id: String) -> void:
	var instance_id := button.get_instance_id()
	if connected_buttons.has(instance_id):
		return

	var callback := _on_button_pressed.bind(id)
	connected_buttons[instance_id] = {"button": button, "callback": callback}
	button.pressed.connect(callback)


func _disconnect_buttons() -> void:
	for connection in connected_buttons.values():
		var button := connection.get("button") as BaseButton
		var callback: Callable = connection.get("callback", Callable())
		if is_instance_valid(button) and callback.is_valid() and button.pressed.is_connected(callback):
			button.pressed.disconnect(callback)
	connected_buttons.clear()


func _on_button_pressed(id: String) -> void:
	if suppressed_clicks.erase(id):
		return

	context.emit_click(id)


func _control_id(control: Control) -> String:
	if control.has_meta(META_ID):
		return str(control.get_meta(META_ID))
	if control == root:
		return "root"
	return str(root.get_path_to(control))


func _control_role(control: Control) -> String:
	if control is OptionButton:
		return "combobox"
	if control is ItemList:
		return "list"
	if control is TabContainer:
		return "tablist"
	if control is CheckBox:
		return "checkbox"
	if control is BaseButton:
		return "button"
	if control is Label:
		return "text"
	if control is LineEdit or control is TextEdit:
		return "textbox"
	if control is Slider:
		return "slider"
	if control is SpinBox:
		return "slider"
	if control is ScrollContainer:
		return "scrollarea"
	return "panel"


func _control_label(control: Control) -> String:
	if control.has_meta(META_SENSITIVE) and control.get_meta(META_SENSITIVE):
		return control.name
	if control is OptionButton:
		return control.name
	if control is BaseButton:
		return (control as BaseButton).text
	if control is Label:
		return (control as Label).text
	if control is LineEdit:
		return (control as LineEdit).text
	if control is TextEdit:
		return (control as TextEdit).text
	return control.name


func _control_text(control: Control) -> Variant:
	if control.has_meta(META_SENSITIVE) and control.get_meta(META_SENSITIVE):
		return null
	if control is BaseButton:
		return (control as BaseButton).text
	if control is Label:
		return (control as Label).text
	if control is LineEdit:
		return (control as LineEdit).text
	if control is TextEdit:
		return (control as TextEdit).text
	return null


func _control_value(control: Control) -> Variant:
	if control.has_meta(META_SENSITIVE) and control.get_meta(META_SENSITIVE):
		return null
	if control is OptionButton:
		var option := control as OptionButton
		return option.get_item_text(option.selected) if option.selected >= 0 else ""
	if control is LineEdit:
		return (control as LineEdit).text
	if control is TextEdit:
		return (control as TextEdit).text
	if control is Range:
		return str((control as Range).value)
	return null


func _control_enabled(control: Control) -> bool:
	if control is BaseButton:
		return not (control as BaseButton).disabled
	if control is LineEdit:
		return (control as LineEdit).editable
	if control is TextEdit:
		return (control as TextEdit).editable
	if control is SpinBox:
		return (control as SpinBox).editable
	if control is ItemList:
		return true
	if control is TabContainer:
		return true
	if control is Slider:
		return (control as Slider).editable
	return control.mouse_filter != Control.MOUSE_FILTER_IGNORE


func _control_focused(control: Control) -> bool:
	if control is SpinBox:
		return (control as SpinBox).get_line_edit().has_focus()
	return control.has_focus()

static func release_injected_state(keys: Dictionary, pointers: Dictionary, buttons: Dictionary, axes: Dictionary, position: Vector2, owners: Dictionary, values: Dictionary, changed: Signal) -> void:
	for held: Dictionary in keys.values():
		var event := InputEventKey.new()
		event.physical_keycode = int(held.get("keycode", KEY_NONE))
		event.keycode = event.physical_keycode
		event.location = int(held.get("location", KEY_LOCATION_UNSPECIFIED))
		event.pressed = false
		Input.parse_input_event(event)
	keys.clear()
	for held: Dictionary in pointers.values():
		var event := InputEventMouseButton.new()
		event.button_index = int(held.get("button", MOUSE_BUTTON_NONE))
		event.pressed = false
		event.position = position
		event.global_position = position
		Input.parse_input_event(event)
	pointers.clear()
	for held: Dictionary in buttons.values():
		var event := InputEventJoypadButton.new()
		event.device = int(held.device)
		event.button_index = int(held.button)
		event.pressed = false
		Input.parse_input_event(event)
	buttons.clear()
	for held: Dictionary in axes.values():
		var event := InputEventJoypadMotion.new()
		event.device = int(held.device)
		event.axis = int(held.axis)
		event.axis_value = 0.0
		Input.parse_input_event(event)
	axes.clear()
	owners.clear()
	for action_id: String in values.keys():
		var current: Variant = values[action_id]
		var neutral: Variant = Vector2.ZERO if current is Vector2 else 0.0 if current is float or current is int else "" if current is String else false
		values[action_id] = neutral
		if not changed.is_null():
			changed.emit(action_id, neutral)

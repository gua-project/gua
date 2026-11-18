extends Control

func _ready() -> void:
	_check(GuaGamePolicy.verified)
	if not OS.has_feature("editor"):
		_check(not DirAccess.dir_exists_absolute("res://addons/gua/.variants"))
		_check(not FileAccess.file_exists("res://addons/gua/gua_webmcp_bridge.gd"))
		_check(not DirAccess.dir_exists_absolute("res://addons/gua/editor"))
	var adapter := GuaAutoAdapter.new()
	adapter.attach(self)
	var started: bool = adapter.start_inspector_bridge(19469)
	_check(started == (GuaGamePolicy.REMOTE_MODE != "off"))
	var context: Object = adapter.context
	_check(context != null)
	context.begin_frame("game-owned-policy")
	context.register_node("local", "button", "Local node", Rect2(0, 0, 10, 10), true, true)
	context.register_node("secret", "button", "Hidden secret", Rect2(0, 0, 10, 10), false, true)
	context.end_frame()
	_check("Local node" in context.get_ui_tree_json())
	if GuaGamePolicy.REMOTE_MODE == "player":
		_check(context.get_observation_profile() == 1)
	if started:
		var socket := WebSocketPeer.new()
		_check(socket.connect_to_url("ws://127.0.0.1:19469") == OK)
		var deadline := Time.get_ticks_msec() + 5000
		while socket.get_ready_state() != WebSocketPeer.STATE_OPEN and Time.get_ticks_msec() < deadline:
			socket.poll()
			await get_tree().process_frame
		_check(socket.get_ready_state() == WebSocketPeer.STATE_OPEN)
		var tree: Dictionary = await _request(socket, 1, "get_ui_tree")
		_check(tree.get("ok") == true and "Local node" in JSON.stringify(tree))
		_check(("Hidden secret" in JSON.stringify(tree)) == (GuaGamePolicy.REMOTE_MODE != "player"))
		context.enable_virtual_clock_adapter()
		var clock: Dictionary = await _request(socket, 2, "clock_install", {"initialTimeMs":0, "stepMs":10})
		_check(clock.get("ok") == (GuaGamePolicy.REMOTE_MODE != "player"))
		var click: Dictionary = await _request(socket, 3, "click_node", {"nodeId":"local"})
		_check(click.get("ok") == true)
		var action: Dictionary = context.consume_action_request("click", "local")
		_check(not action.is_empty() and action.get("request_id", 0) != 0)
		context.emit_action_result({"request_id":action.request_id, "action":"click", "node_id":"local", "succeeded":true})
		socket.close()
	adapter.dispose()
	print("GAME_OWNED_GODOT_SUCCESS: ", GuaGamePolicy.REMOTE_MODE)
	get_tree().quit()

func _request(socket: WebSocketPeer, id: int, type: String, extra: Dictionary = {}) -> Dictionary:
	var message := {"id":id, "type":type}
	message.merge(extra)
	_check(socket.send_text(JSON.stringify(message)) == OK)
	var deadline := Time.get_ticks_msec() + 5000
	while Time.get_ticks_msec() < deadline:
		socket.poll()
		while socket.get_available_packet_count() > 0:
			var response: Variant = JSON.parse_string(socket.get_packet().get_string_from_utf8())
			if response is Dictionary and response.get("id") == id:
				return response
		await get_tree().process_frame
	_check(false, "Timed out waiting for the correlated wire response.")
	return {}

func _check(condition: bool, reason: String = "Godot game policy contract failed") -> void:
	# GDScript assert is removed from Release exports, including its side effects.
	# These conditions and wire operations must execute in the actual Release game.
	if not condition:
		push_error(reason)
		OS.kill(OS.get_process_id())


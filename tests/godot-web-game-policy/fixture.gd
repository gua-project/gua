extends Control
var adapter: RefCounted
var consumed := 0
var info: Dictionary
var mode := ""
var clock_allowed := false
var local_ui := false
var debug_denied := false
var fixture_ready := false

func _ready() -> void:
	var policy := get_node_or_null("/root/GuaGamePolicy")
	if policy == null or not policy.get("verified"):
		var details: Dictionary = {"present":policy != null}
		if policy != null:
			details["script"] = policy.get_script().resource_path
			details["constants"] = policy.get_script().get_script_constant_map()
			details["enter_method"] = policy.has_method("_enter_tree")
			details["inside_tree"] = policy.is_inside_tree()
			details["stage"] = policy.get("stage")
			details["host_web"] = OS.has_feature("web")
		push_error("Fixture did not reach verified startup: " + JSON.stringify(details))
		return
	mode = str(ProjectSettings.get_setting("gua/remote_mode", "off"))
	var button := Button.new()
	button.text = "Guarded button"
	button.set_meta("gua_id", "guarded")
	button.size = Vector2(200, 80)
	button.pressed.connect(func(): consumed += 1)
	add_child(button)
	adapter = load("res://addons/gua/gua_auto_adapter.gd").new()
	adapter.attach(self)
	adapter.update("guarded")
	info = JSON.parse_string(adapter.context.get_build_info_json())
	local_ui = "Guarded button" in str(adapter.context.get_ui_tree_json())
	var expected_ceiling := "none" if mode == "off" else ("player" if mode == "player" else "debug")
	if info.remoteEnabled != (mode != "off") or info.remotePermissionCeiling != expected_ceiling:
		push_error("Actual Web side module has the wrong compiled mode.")
		return
	# The native ceiling must reject a local API attempt as well as remote calls.
	if mode == "player":
		debug_denied = adapter.context.create_observe_client(0) == 0
		if not debug_denied:
			push_error("Player Debug escalation escaped.")
			return
	adapter.context.enable_virtual_clock_adapter()
	var clock: Dictionary = adapter.context.clock_install(0, 10)
	clock_allowed = int(clock.get("result", 0)) > 0
	if not clock_allowed:
		push_error("Game-owned local clock API was unexpectedly removed.")
		return
	fixture_ready = true
	_publish()

func _process(_delta: float) -> void:
	if fixture_ready:
		adapter.update("guarded")
		_publish()

func _publish() -> void:
	JavaScriptBridge.eval("globalThis.__guaGameFixture=" + JSON.stringify({"ready":true,"mode":mode,"consumed":consumed,"build":info,"debugDenied":debug_denied,"clockAllowed":clock_allowed,"localUi":local_ui}))

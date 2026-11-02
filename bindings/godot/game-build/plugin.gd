@tool
extends EditorPlugin

const SETTING := "gua/remote_mode"
const POLICY := "res://gua_generated/policy.gd"
const ROOT := "res://addons/gua/"
var _exporter: EditorExportPlugin
var _write_failed := false

func _enter_tree() -> void:
	if not ProjectSettings.has_setting(SETTING):
		ProjectSettings.set_setting(SETTING, "off")
	ProjectSettings.set_initial_value(SETTING, "off")
	ProjectSettings.add_property_info({"name":SETTING, "type":TYPE_STRING, "hint":PROPERTY_HINT_ENUM, "hint_string":"off,testing,player"})
	_exporter = preload("export.gd").new()
	_exporter.prepare = _prepare
	add_export_plugin(_exporter)
	_prepare()

func _exit_tree() -> void:
	remove_export_plugin(_exporter)

func _build() -> bool:
	return _prepare()

func _prepare() -> bool:
	_write_failed = false
	var mode: String = ProjectSettings.get_setting(SETTING, "off")
	if mode not in ["off", "testing", "player"]:
		push_error("Gua remote_mode must be off, testing, or player.")
		return false
	if ProjectSettings.has_setting("autoload/GuaGamePolicy") and ProjectSettings.get_setting("autoload/GuaGamePolicy") != "*" + POLICY:
		push_error("GuaGamePolicy autoload is already assigned to another script; game verification cannot be installed.")
		return false
	if FileAccess.file_exists(ROOT + "gua.gdextension"):
		push_error("A legacy auto-loaded Gua addon remains. Install the game-build SDK into a clean addon folder.")
		return false
	var package := ROOT + ".variants/" + mode + "/"
	var manifest: Variant = JSON.parse_string(FileAccess.get_file_as_string(package + "gua-build-manifest.json"))
	if not manifest is Dictionary or not manifest.get("build") is Dictionary:
		push_error("The provider SDK is missing the selected prebuilt Gua mode.")
		return false
	var build: Dictionary = manifest.build
	var ceiling := "none" if mode == "off" else ("player" if mode == "player" else "debug")
	if build.get("remoteEnabled") != (mode != "off") or build.get("remotePermissionCeiling") != ceiling or build.get("configuration") != "Release":
		push_error("Gua provider mode does not match the game setting.")
		return false
	var expected_flavors: Array = ["shipping"] if mode == "off" else (["ai-player"] if mode == "player" else ["development", "qa"])
	if build.get("flavor") not in expected_flavors:
		return false
	var native := ""
	var native_hash := ""
	var entries: Dictionary = {}
	for entry: Dictionary in manifest.files:
		var path: String = entry.path
		if entries.has(path) or path.is_absolute_path() or ".." in path.split("/") or "\\" in path:
			return false
		entries[path] = entry.sha256
		if path.begins_with("addons/gua/bin/") and path.ends_with(".windows.release.x86_64.dll"):
			if native != "":
				return false
			native = path
			native_hash = entry.sha256
	if native == "" or FileAccess.get_sha256(package + native) != native_hash:
		push_error("Missing or modified provider GDExtension.")
		return false
	DirAccess.make_dir_recursive_absolute("res://gua-native")
	DirAccess.make_dir_recursive_absolute("res://gua_generated/native")
	_write("res://gua-native/.gdignore", "")
	_write("res://gua_generated/native/.gdignore", "")
	if DirAccess.copy_absolute(package + native, "res://gua-native/gua.dll") != OK:
		return false
	var descriptor := "[configuration]\nentry_symbol=\"gua_godot_library_init\"\ncompatibility_minimum=\"4.7\"\n[libraries]\nwindows.x86_64=\"res://gua-native/gua.dll\"\n"
	_write("res://gua_generated/native/gua.gdextension", descriptor)
	var guard := FileAccess.get_file_as_string(ROOT + "editor/policy.gd.txt")
	guard = guard.replace("/*MODE*/", JSON.stringify(mode)).replace("/*HASH*/", JSON.stringify(native_hash))
	guard = guard.replace("/*SOURCE*/", JSON.stringify(build.sourceCommit)).replace("/*BUILD*/", JSON.stringify(build.buildId))
	_write(POLICY, guard)
	for name: String in ["gua_auto_adapter.gd", "gua_observe_owner.gd", "gua_spatial.gd"]:
		var path := "addons/gua/" + name
		if not entries.has(path) or FileAccess.get_sha256(package + path) != entries[path]:
			return false
		var source := FileAccess.get_file_as_string(package + path).replace("\r\n", "\n")
		if name == "gua_auto_adapter.gd":
			source = _guard_adapter(source, mode)
			if source == "":
				return false
		_write(ROOT + name, source)
	if not ProjectSettings.has_setting("autoload/GuaGamePolicy"):
		add_autoload_singleton("GuaGamePolicy", POLICY)
	var first := ProjectSettings.get_order("autoload/GuaGamePolicy")
	for property: Dictionary in ProjectSettings.get_property_list():
		if String(property.name).begins_with("autoload/"):
			first = mini(first, ProjectSettings.get_order(property.name))
	ProjectSettings.set_order("autoload/GuaGamePolicy", first - 1)
	return not _write_failed and ProjectSettings.save() == OK

func _guard_adapter(source: String, mode: String) -> String:
	var marker := "func _ensure_context() -> bool:\n"
	if source.count(marker) != 1:
		return ""
	var policy_check := "\tvar tree := Engine.get_main_loop() as SceneTree\n\tvar policy := tree.root.get_node_or_null(\"GuaGamePolicy\") if tree != null else null\n\tif policy == null or not policy.verified:\n\t\treturn false\n"
	source = source.replace(marker, marker + policy_check)
	source = source.replace("res://addons/gua/gua.gdextension", "res://gua_generated/native/gua.gdextension")
	# This native Windows SDK has no Web bootstrap. Web needs its own verified host.
	var web := RegEx.new()
	web.compile('(?ms)^\tif _ensure_context\\(\\) and OS.has_feature\\("web"\\).*?\t\t\twebmcp_bridge.attach\\(self\\)\n')
	source = web.sub(source, "", true)
	if mode == "off":
		source = source.replace("return context.start_inspector_bridge(port)", "return false # Remote start is absent from this game build.")
	return source

func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file != null:
		file.store_string(text)
		_write_failed = _write_failed or file.get_error() != OK
	else:
		_write_failed = true

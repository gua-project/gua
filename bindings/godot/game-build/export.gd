@tool
extends EditorExportPlugin
var prepare: Callable
var _failed := false
var _original_autoload: Variant
var _original_order := 0
var _failed_target := ""

func _get_name() -> String:
	return "GuaGameOwnedPolicy"

func _export_begin(features: PackedStringArray, _debug: bool, _path: String, _flags: int) -> void:
	_failed = false
	if not features.has("windows") or not features.has("x86_64") or not prepare.call():
		_failed = true
		_failed_target = _path if _path.is_absolute_path() else ProjectSettings.globalize_path(_path if _path.begins_with("res://") else "res://" + _path)
		# Windows exports stage the executable at <name>.tmp and only promote it
		# on success. Its matching final .exe is the explicitly selected product.
		if features.has("windows") and _failed_target.to_lower().ends_with(".tmp"):
			_failed_target = _failed_target.get_basename() + ".exe"
		get_export_platform().add_message(EditorExportPlatform.EXPORT_MESSAGE_ERROR, "Gua game build", "The selected Gua game SDK requires Windows x64 and successful game-owned policy generation.")
		# The exporter can still create files after an error. Never package a stale
		# previously enabled policy, even when the destination has old native files.
		var refused := "extends Node\nconst REMOTE_MODE = \"invalid\"\nvar verified = false\nfunc _enter_tree():\n\tpush_error(\"Gua build policy generation failed; game startup refused.\")\n\tOS.kill(OS.get_process_id())\n"
		# Use a new resource path: an old policy.gd.remap can otherwise redirect
		# the loader to a previously compiled policy despite replacing raw source.
		add_file("res://gua_generated/refused_build.gd", refused.to_utf8_buffer(), false)
		_original_autoload = ProjectSettings.get_setting("autoload/GuaGamePolicy", null)
		_original_order = ProjectSettings.get_order("autoload/GuaGamePolicy") if _original_autoload != null else 0
		ProjectSettings.set_setting("autoload/GuaGamePolicy", "*res://gua_generated/refused_build.gd")
		var first := ProjectSettings.get_order("autoload/GuaGamePolicy")
		for property: Dictionary in ProjectSettings.get_property_list():
			if String(property.name).begins_with("autoload/"):
				first = mini(first, ProjectSettings.get_order(property.name))
		ProjectSettings.set_order("autoload/GuaGamePolicy", first - 1)
		# The Windows exporter propagates an unreadable shared-object input as
		# a build error. add_message alone does not change its CLI exit status.
		add_shared_object("res://gua_generated/BUILD_REFUSED_no_valid_policy.dll", PackedStringArray(), "gua-build-refused")
		return
	add_shared_object("res://gua-native/gua.dll", PackedStringArray(["windows", "x86_64"]), "gua-native")
	var descriptor := "res://gua_generated/native/gua.gdextension"
	add_file(descriptor, FileAccess.get_file_as_bytes(descriptor), false)
	add_file("res://gua_generated/policy.gd", FileAccess.get_file_as_bytes("res://gua_generated/policy.gd"), false)

func _export_end() -> void:
	if _failed:
		ProjectSettings.set_setting("autoload/GuaGamePolicy", _original_autoload)
		if _original_autoload != null:
			ProjectSettings.set_order("autoload/GuaGamePolicy", _original_order)
		# Remove only the explicitly selected Windows build products, never user
		# data or the output directory. A failed OFF build must not leave an old
		# enabled EXE/PCK at the same output name.
		if _failed_target.to_lower().ends_with(".exe"):
			for product: String in [_failed_target, _failed_target.get_basename() + ".pck"]:
				if FileAccess.file_exists(product) and DirAccess.remove_absolute(product) != OK:
					get_export_platform().add_message(EditorExportPlatform.EXPORT_MESSAGE_ERROR, "Gua game build", "Failed output could not be removed; do not use the previous build.")

func _export_file(path: String, _type: String, _features: PackedStringArray) -> void:
	if _failed and path == "res://gua_generated/policy.gd":
		skip()
		return
	if path.begins_with("res://addons/gua/.variants/") or path.begins_with("res://addons/gua/editor/") or path == "res://addons/gua/plugin.cfg" or path.ends_with("gua_webmcp_bridge.gd"):
		skip()

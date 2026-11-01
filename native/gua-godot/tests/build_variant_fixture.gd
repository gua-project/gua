extends Node

func _ready() -> void:
    var expected: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://expected.json"))
    var extension: Resource = load("res://addons/gua/gua.gdextension")
    if extension == null or not ClassDB.class_exists("GuaContext"):
        fail("GuaContext was not registered by the packaged extension")
        return
    var context: RefCounted = ClassDB.instantiate("GuaContext")
    var info: Dictionary = JSON.parse_string(str(context.call("get_build_info_json")))
    if info.get("flavor") != expected.flavor or info.get("remoteEnabled") != expected.remote:
        fail("Packaged Godot build identity mismatch")
        return
    context.call("begin_frame", "fixture")
    context.call("register_node", "public", "button", "Public", Rect2(0, 0, 10, 10), true, true)
    context.call("register_node", "secret", "button", "Hidden secret", Rect2(0, 0, 10, 10), false, true)
    context.call("end_frame")
    var tree: String = str(context.call("get_ui_tree_json"))
    if not tree.contains("Public") or tree.contains("Hidden secret") == (expected.flavor == "ai-player"):
        fail("Godot legacy read ignored the compiled Player ceiling")
        return
    if expected.flavor == "ai-player" and int(context.call("create_observe_client", 0)) != 0:
        fail("Godot binding created a Debug observation client")
        return
    if ResourceLoader.exists("res://addons/gua/gua_webmcp_bridge.gd") != bool(expected.remote):
        fail("Packaged WebMCP resource availability does not match the variant")
        return
    if bool(context.call("start_inspector_bridge", 19463)) != bool(expected.remote):
        fail("Godot embedded runtime transport availability mismatch")
        return
    context.call("stop_inspector_bridge")
    context = null
    var report: FileAccess = FileAccess.open("user://gua-variant-result.json", FileAccess.WRITE)
    if report == null:
        fail("Cannot save the actual consumer result")
        return
    report.store_string(JSON.stringify({"passed": true, "flavor": expected.flavor, "remote": expected.remote}))
    report.close()
    print("GUA_VARIANT_GODOT_PASS ", expected.flavor)
    get_tree().quit(0)

func fail(message: String) -> void:
    push_error(message)
    get_tree().quit(1)

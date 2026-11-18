@tool
extends EditorPlugin
# Runs outside the collectible C# game assembly. Uses the normal Editor build
# button; requesting an unload from that game's own C# stack would hold it alive.
var elapsed: float = 0
var invoked: bool = false
var reopened: bool = false
func _process(delta: float) -> void:
    elapsed += delta
    if OS.get_environment("GUA_EDITOR_FIXTURE_ACTION") == "rebuild":
        if elapsed > 5 and not invoked:
            invoked = true
            var file := FileAccess.open("res://Main.cs", FileAccess.READ_WRITE)
            file.seek_end()
            file.store_string("\n// Owned normal Editor rebuild fixture.\n")
            file.close()
            var button := find_build(get_tree().root)
            if button:
                print("GUA_EDITOR_NORMAL_BUILD_TRIGGER")
                button.emit_signal("pressed")
            else:
                push_error("Owned C# build button not found")
        if elapsed > 23 and not reopened:
            reopened = true
            EditorInterface.reload_scene_from_path("res://Main.tscn")
    if elapsed > 38:
        print("GUA_EDITOR_NORMAL_QUIT")
        get_tree().quit(0)
func find_build(node: Node) -> Button:
    if node is Button and node.shortcut:
        for event in node.shortcut.events:
            if event is InputEventKey and event.keycode == KEY_B and event.alt_pressed:
                return node
    for child in node.get_children():
        var found := find_build(child)
        if found:
            return found
    return null

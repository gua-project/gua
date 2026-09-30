extends Node

var adapter: RefCounted
var owners: Array[RefCounted] = []
var phase := ["First"]

func _ready() -> void:
	call_deferred("_start")

func _start() -> void:
	var screen := Control.new()
	add_child(screen)
	var button := Button.new()
	button.name = "advance"
	button.text = "Advance"
	button.size = Vector2(100, 50)
	button.pressed.connect(func() -> void: phase[0] = "Second")
	screen.add_child(button)
	var enemy := Node2D.new()
	enemy.position = Vector2(2, 3)
	enemy.add_to_group(&"gua_world_object")
	enemy.set_meta(&"gua_world_id", "enemy-1")
	enemy.set_meta(&"gua_world_visible_to_player", true)
	screen.add_child(enemy)
	adapter = preload("res://addons/gua/gua_auto_adapter.gd").new()
	adapter.attach(screen)
	adapter.update("observe")
	adapter.register_value_enum("game.Phase", ["First", "Second"])
	var owner: RefCounted = adapter.create_observe_owner(2, "enemy-1", enemy)
	owners.append(owner)
	owner.observe("phase", func() -> Dictionary: return {"type": "enum", "enumType": "game.Phase", "value": phase[0]}, true)
	owner.observe("empty", func() -> Dictionary: return {"type": "set", "elementType": "enum", "enumType": "game.Phase", "value": []}, true)
	adapter.update("observe")
	var port := int(OS.get_environment("GUA_BRIDGE_PORT"))
	if port <= 0:
		port = 8765
	if OS.has_feature("web"):
		print("Gua Observe Web fixture ready.")
		return
	if not adapter.start_inspector_bridge(port):
		get_tree().quit(1)
	print("Gua Observe external-client fixture ready.")

func _process(_delta: float) -> void:
	if adapter != null:
		adapter.update("observe")

func _exit_tree() -> void:
	if adapter != null:
		adapter.dispose()

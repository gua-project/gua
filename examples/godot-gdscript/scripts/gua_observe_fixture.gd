extends SceneTree

func _initialize() -> void:
	call_deferred("_start")

func _start() -> void:
	root.add_child(preload("res://scripts/gua_observe_fixture_host.gd").new())

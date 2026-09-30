extends RefCounted

# Retain this token. dispose() also handles same-ID object replacement without
# an absent published frame. Only additional state belongs here.
var context: RefCounted
var owner_id := 0
var source := 0
var target: WeakRef
var registrations: Dictionary = {}

func observe(name: String, getter: Callable, allow_player := false, sensitive := false) -> int:
	if owner_id == 0 or not getter.is_valid():
		return 0
	var id: int = context.register_observe(owner_id, name, allow_player, sensitive)
	if id != 0:
		registrations[id] = getter
	return id

func unregister(id: int) -> int:
	registrations.erase(id)
	if context == null or owner_id == 0:
		return 2
	return context.unregister_observe(id)

func notify(id: int) -> int:
	return _sample(id, false)

func _sample(id: int, stage: bool) -> int:
	if not registrations.has(id):
		return 2
	var alive: int = context.observe_registration_alive(id)
	if alive != 0:
		registrations.erase(id)
		return alive
	var getter: Callable = registrations[id]
	var value: Variant = getter.call() if getter.is_valid() else null
	# Recheck after the getter; it may unregister, dispose or reset.
	if context == null or owner_id == 0:
		return 2
	return context.publish_observe_json(id, JSON.stringify(value), stage)

func sample_frame(ui: bool) -> void:
	if target != null and target.get_ref() == null:
		dispose()
		return
	if (source == 1) == ui:
		for id: int in registrations.keys():
			_sample(id, true)

func dispose() -> void:
	registrations.clear()
	if owner_id != 0:
		context.destroy_observe_owner(owner_id)
		owner_id = 0
	context = null
	target = null

func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE:
		if context != null and owner_id != 0:
			context.destroy_observe_owner(owner_id)
		owner_id = 0
		registrations.clear()

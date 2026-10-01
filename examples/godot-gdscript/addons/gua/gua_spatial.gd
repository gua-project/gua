class_name GuaSpatialReader
extends RefCounted
## Trusted host-owned direct-space reader. Call pump only from the approved
## _physics_process boundary. No live node/collider settings are modified.
## Dispose before origin changes or World3D destruction; use a fresh spaceEpoch.

var _host: GuaSpatialHost
var _world: World3D
var _registration: Dictionary
var _policies: Dictionary
var provider: int
var _pumping := false
var _sample := 0
var _sphere := SphereShape3D.new()
var _capsule := CapsuleShape3D.new()
var _box := BoxShape3D.new()
var _shape_query := PhysicsShapeQueryParameters3D.new()
var _ray_query := PhysicsRayQueryParameters3D.new()

func configure(host: GuaSpatialHost, world: World3D, registration: Dictionary, policies: Dictionary) -> int:
	if provider != 0 or world == null or OS.get_thread_caller_id() != OS.get_main_thread_id():
		return 1
	# Freeze host dictionaries; masks, trigger flags and exclusions never come
	# from a query. RID exclusions are applied by physics before enumeration.
	_registration = registration.duplicate(true)
	_policies = policies.duplicate(true)
	if registration.provider.engine.name != "Godot" or registration.provider.engine.version != Engine.get_version_info().string or registration.provider.engine.backend != "GodotPhysics3D" or ProjectSettings.get_setting("physics/3d/physics_engine") != "GodotPhysics3D":
		return 5
	for policy in registration.policies:
		if not _policies.has(policy.id):
			return 1
		var filter: Dictionary = _policies[policy.id]
		if not filter.has_all(["mask","bodies","areas","backfaces","margin","exclude"]):
			return 1
		if typeof(filter.mask) != TYPE_INT or filter.mask < 0 or filter.mask > 4294967295 or typeof(filter.bodies) != TYPE_BOOL or typeof(filter.areas) != TYPE_BOOL or typeof(filter.backfaces) != TYPE_BOOL:
			return 1
		if not filter.exclude is Array or filter.exclude.size() > 1024:
			return 1
		for excluded in filter.exclude:
			if typeof(excluded) != TYPE_RID:
				return 1
		if filter.margin != 0.0:
			return 5 # Nonzero margin could expand the authorized query volume.
	_host = host
	_world = world
	var result := host.register_provider(JSON.stringify(_registration))
	if result.status == 0:
		provider = result.handle
	return result.status

func dispose() -> int:
	if _pumping:
		return 11
	if provider == 0:
		return 0
	var result := _host.unregister_provider(provider)
	provider = 0
	_world = null
	return result.status

func pump() -> int:
	if not Engine.is_in_physics_frame() or OS.get_thread_caller_id() != OS.get_main_thread_id() or _pumping or provider == 0:
		return 11
	_sample += 1
	# Real physics callback counter; no World snapshot inferred from it.
	var boundary := {"schemaVersion":"spatial-host-r1", "documentType":"boundary",
		"physicsSampleId":"godot:%s:%s" % [provider, _sample], "tick":Engine.get_physics_frames()}
	var begin := _host.begin(provider, JSON.stringify(boundary))
	if begin.status != 0:
		return begin.status
	_pumping = true
	var status := 0
	var space := _world.direct_space_state
	while true:
		var taken := _host.take(begin.handle)
		if taken.status == 10:
			break
		if taken.status != 0:
			status = taken.status
			break
		var query: Dictionary = JSON.parse_string(taken.json)
		var execution := _execute(space, query)
		var completed := _host.complete(begin.handle, JSON.stringify(execution))
		if completed.status != 0:
			status = completed.status
			break
	# Mandatory cleanup even when native validation/deadlines reject a result.
	var ended := _host.end(begin.handle)
	_pumping = false
	return status if status != 0 else ended.status

static func _v(value: Dictionary) -> Vector3:
	return Vector3(value.x, value.y, value.z)

static func _exact(value: Dictionary) -> bool:
	var v := _v(value)
	return v.x == value.x and v.y == value.y and v.z == value.z and v.is_finite() and maxf(absf(v.x), maxf(absf(v.y), absf(v.z))) <= 8192.0

static func _cardinal(v: Vector3) -> bool:
	return int(v.x != 0) + int(v.y != 0) + int(v.z != 0) <= 1

static func _supported(q: Dictionary) -> bool:
	if q.kind == "raycast":
		return _exact(q.segment.from) and _exact(q.segment.to) and _cardinal(_v(q.segment.to) - _v(q.segment.from))
	var shape: Dictionary = q.shape
	if q.kind == "sweep" and (not _exact(q.delta) or not _cardinal(_v(q.delta))):
		return false
	if shape.type == "box":
		if not _exact(shape.center) or not _exact(shape.halfExtents):
			return false
		for axis in [shape.basis.x, shape.basis.y, shape.basis.z]:
			if not _exact(axis) or not _cardinal(_v(axis)) or _v(axis).length_squared() != 1.0:
				return false
		return true
	if Vector3(shape.radius, 0, 0).x != shape.radius or shape.radius > 8192.0:
		return false
	if shape.type == "sphere":
		return _exact(shape.center)
	if not _exact(shape.pointA) or not _exact(shape.pointB):
		return false
	var a := _v(shape.pointA)
	var b := _v(shape.pointB)
	if not _cardinal(b-a):
		return false
	if a == b:
		return true
	# Input representability does not imply representability of the derived
	# engine center/height. Reject any expansion before touching physics.
	var desired_center := {"x":(shape.pointA.x+shape.pointB.x)*0.5,
		"y":(shape.pointA.y+shape.pointB.y)*0.5,"z":(shape.pointA.z+shape.pointB.z)*0.5}
	if not _exact(desired_center) or (a+b)*0.5 != _v(desired_center):
		return false
	var length: float = absf(shape.pointB.x-shape.pointA.x) + absf(shape.pointB.y-shape.pointA.y) + absf(shape.pointB.z-shape.pointA.z)
	var height: float = length + 2.0*shape.radius
	return a.distance_to(b) == length and length > 0.0 and Vector3(height,0,0).x == height

func _coverage(q: Dictionary) -> Dictionary:
	if not _registration.has("loadedRegion"):
		return {"state":"unknown", "reason":"loaded_region_unknown"}
	var low: Vector3
	var high: Vector3
	if q.kind == "raycast":
		low = _v(q.segment.from).min(_v(q.segment.to))
		high = _v(q.segment.from).max(_v(q.segment.to))
	else:
		var s: Dictionary = q.shape
		low = _v(s.pointA).min(_v(s.pointB)) if s.type == "capsule" else _v(s.center)
		high = _v(s.pointA).max(_v(s.pointB)) if s.type == "capsule" else low
		var extent: Vector3 = Vector3.ONE * float(s.radius) if s.type != "box" else _v(s.basis.x).abs() * s.halfExtents.x + _v(s.basis.y).abs() * s.halfExtents.y + _v(s.basis.z).abs() * s.halfExtents.z
		low -= extent
		high += extent
		if q.kind == "sweep":
			low = low.min(low + _v(q.delta))
			high = high.max(high + _v(q.delta))
	var region: Dictionary = _registration.loadedRegion
	var min_bound := _v(region.min) + Vector3.ONE * 0.001
	var max_bound := _v(region.max) - Vector3.ONE * 0.001
	if low.x > min_bound.x and low.y > min_bound.y and low.z > min_bound.z and high.x < max_bound.x and high.y < max_bound.y and high.z < max_bound.z:
		return {"state":"complete", "loadedRegion":region}
	return {"state":"unknown", "reason":"outside_loaded_region"}

func _prepare_shape(shape: Dictionary, policy: Dictionary) -> void:
	var transform := Transform3D.IDENTITY
	match shape.type:
		"sphere":
			_sphere.radius = shape.radius
			_shape_query.shape = _sphere
			transform.origin = _v(shape.center)
		"capsule":
			var a := _v(shape.pointA)
			var b := _v(shape.pointB)
			if a == b:
				_sphere.radius = shape.radius
				_shape_query.shape = _sphere
				transform.origin = a
			else:
				_capsule.radius = shape.radius
				_capsule.height = a.distance_to(b) + 2.0 * shape.radius
				_shape_query.shape = _capsule
				transform.origin = (a + b) * 0.5
				var y := (b - a).normalized()
				var helper := Vector3.RIGHT if abs(y.dot(Vector3.RIGHT)) < 0.9 else Vector3.FORWARD
				var x := helper.cross(y).normalized()
				transform.basis = Basis(x, y, x.cross(y))
		"box":
			_box.size = 2.0 * _v(shape.halfExtents)
			_shape_query.shape = _box
			transform.origin = _v(shape.center)
			transform.basis = Basis(_v(shape.basis.x), _v(shape.basis.y), _v(shape.basis.z))
	_shape_query.transform = transform
	_shape_query.motion = Vector3.ZERO
	_shape_query.margin = policy.margin
	_shape_query.collision_mask = policy.mask
	_shape_query.collide_with_bodies = policy.bodies
	_shape_query.collide_with_areas = policy.areas
	_shape_query.exclude = policy.exclude

func _execute(space: PhysicsDirectSpaceState3D, q: Dictionary) -> Dictionary:
	var r := {"schemaVersion":"spatial-host-r1", "documentType":"execution",
		"requestId":q.requestId, "sessionEpoch":q.sessionEpoch, "queryId":q.queryId,
		"spaceId":q.spaceId, "spaceEpoch":q.spaceEpoch, "kind":q.kind,
		"status":"completed", "coverage":{"state":"unknown", "reason":"engine_coverage_unverified"},
		"truncated":false, "hits":[]}
	var policy: Dictionary = _policies[q.queryPolicyId]
	if not _supported(q):
		for key in ["coverage", "truncated", "hits"]:
			r.erase(key)
		r.status = "unsupported"
		r.error = {"code":"unsupported_operation" if q.kind == "raycast" else "unsupported_shape"}
		return r
	r.coverage = _coverage(q)
	if q.kind == "raycast":
		_ray_query.from = _v(q.segment.from)
		_ray_query.to = _v(q.segment.to)
		_ray_query.collision_mask = policy.mask
		_ray_query.collide_with_bodies = policy.bodies
		_ray_query.collide_with_areas = policy.areas
		_ray_query.hit_back_faces = policy.backfaces
		_ray_query.hit_from_inside = true
		_ray_query.exclude = policy.exclude
		var hit := space.intersect_ray(_ray_query)
		r.outcome = "noHit" if hit.is_empty() else "hit"
		r.nearest = "none" if hit.is_empty() else "returnedHits"
		r.originInside = "unknown"
		if not hit.is_empty():
			r.hits.append(_anonymous())
		return r
	_prepare_shape(q.shape, policy)
	# One extra result allows truthful enumeration truncation within <=33 slots.
	var hits := space.intersect_shape(_shape_query, int(q.maxHits) + 1)
	if not hits.is_empty():
		r.outcome = "initialOverlap" if q.kind == "sweep" else "detected"
		r.truncated = hits.size() > int(q.maxHits)
		for _i in range(mini(hits.size(), int(q.maxHits))):
			r.hits.append(_anonymous())
	else:
		r.outcome = ("clear" if r.coverage.state == "complete" else "indeterminate") if q.kind == "sweep" else "notDetected"
	if q.kind == "sweep":
		r.initialOverlap = "detected" if not hits.is_empty() else "notDetected"
		var delta := _v(q.delta)
		if delta == Vector3.ZERO:
			r.motion = {"type":"zeroLength", "distance":0}
		elif hits.is_empty():
			_shape_query.motion = delta
			var fractions := space.cast_motion(_shape_query)
			if fractions.size() != 2:
				r.initialOverlap = "indeterminate"
			else:
				r.outcome = "blocked" if fractions[1] < 1.0 else ("clear" if r.coverage.state == "complete" else "indeterminate")
				r.motion = {"type":"nativeBracket", "source":"Godot.cast_motion",
					"safeFraction":fractions[0], "unsafeFraction":fractions[1],
					"error":{"state":"unknown", "reason":"backend_error_unmeasured"}}
	return r

static func _anonymous() -> Dictionary:
	return {"relation":"unknown", "missing":{"position":"not_observed", "distance":"not_observed",
		"normal":"not_observed", "collisionRef":"not_published", "worldObjectId":"not_published"}}

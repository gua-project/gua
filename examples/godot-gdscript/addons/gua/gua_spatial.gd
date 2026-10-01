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
	if registration.provider.engine.name != "Godot" or registration.provider.engine.version != Engine.get_version_info().string or registration.provider.engine.backend != ProjectSettings.get_setting("physics/3d/physics_engine") or registration.provider.engine.backend not in ["GodotPhysics3D","Jolt Physics"]:
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
	var result := host.register_provider(JSON.stringify(_registration, "", true, true))
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
	var begin := _host.begin(provider, JSON.stringify(boundary, "", true, true))
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
		var execution := _execute(space, query, begin.handle)
		var completed := _host.complete(begin.handle, JSON.stringify(execution, "", true, true))
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
	return v.is_finite() and maxf(absf(v.x),maxf(absf(v.y),absf(v.z))) <= 8192.0

static func _supported(q: Dictionary) -> bool:
	if q.kind == "raycast":
		return _exact(q.segment.from) and _exact(q.segment.to) and _v(q.segment.from) != _v(q.segment.to)
	var s: Dictionary = q.shape
	if q.kind == "sweep" and (not _exact(q.delta) or (_v(q.delta) == Vector3.ZERO and (q.delta.x != 0 or q.delta.y != 0 or q.delta.z != 0))):
		return false
	if s.type == "box":
		return _exact(s.center) and _exact(s.halfExtents) and _v(s.halfExtents).x > 0 and _v(s.halfExtents).y > 0 and _v(s.halfExtents).z > 0
	if not is_finite(s.radius) or s.radius > 8192 or Vector3(s.radius,0,0).x <= 0:
		return false
	if s.type == "sphere":
		return _exact(s.center)
	if not _exact(s.pointA) or not _exact(s.pointB):
		return false
	var a := _v(s.pointA)
	var b := _v(s.pointB)
	if a == b:
		return true
	var delta := b-a
	# Squared lengths in the subnormal range corrupt float normalization even
	# when nonzero. Refuse before constructing a scaled capsule transform.
	if delta.length_squared() < 1.1754943508222875e-38:
		return false
	var axis := delta.normalized()
	return axis.is_finite() and absf(axis.length_squared()-1.0) <= 0.000001

func _prepared_bounds(q: Dictionary) -> Array:
	var low := [INF,INF,INF]
	var high := [-INF,-INF,-INF]
	for encoded in range(2):
		var a: Array
		var b: Array
		if q.kind == "raycast":
			var from_v := _v(q.segment.from)
			var to_v := _v(q.segment.to)
			a = [from_v.x,from_v.y,from_v.z] if encoded else [q.segment.from.x,q.segment.from.y,q.segment.from.z]
			b = [to_v.x,to_v.y,to_v.z] if encoded else [q.segment.to.x,q.segment.to.y,q.segment.to.z]
		else:
			var s: Dictionary = q.shape
			var extent := [0.0,0.0,0.0]
			if encoded:
				var transform := _shape_query.transform
				var center := transform.origin
				var ends := Vector3.ZERO
				if s.type == "capsule" and _shape_query.shape == _capsule:
					ends = transform.basis.y * (_capsule.height * 0.5 - _capsule.radius)
				var av := center - ends
				var bv := center + ends
				a = [av.x,av.y,av.z]
				b = [bv.x,bv.y,bv.z]
				if s.type == "box":
					var h := _box.size * 0.5
					for i in range(3):
						extent[i] = absf(transform.basis.x[i])*h.x + absf(transform.basis.y[i])*h.y + absf(transform.basis.z[i])*h.z
				else:
					var radius: float = _sphere.radius if _shape_query.shape == _sphere else _capsule.radius
					extent = [radius,radius,radius]
			else:
				var av: Dictionary = s.pointA if s.type == "capsule" else s.center
				var bv: Dictionary = s.pointB if s.type == "capsule" else s.center
				a = [av.x,av.y,av.z]
				b = [bv.x,bv.y,bv.z]
				if s.type == "box":
					for i in range(3):
						var axis: String = ["x","y","z"][i]
						extent[i] = absf(s.basis.x[axis])*s.halfExtents.x + absf(s.basis.y[axis])*s.halfExtents.y + absf(s.basis.z[axis])*s.halfExtents.z
				else:
					extent = [s.radius,s.radius,s.radius]
			var delta := [0.0,0.0,0.0]
			if q.kind == "sweep":
				var dv := _v(q.delta)
				delta = [dv.x,dv.y,dv.z] if encoded else [q.delta.x,q.delta.y,q.delta.z]
			for i in range(3):
				var min_v: float = minf(a[i],b[i])-extent[i]+minf(0,delta[i])
				var max_v: float = maxf(a[i],b[i])+extent[i]+maxf(0,delta[i])
				a[i] = min_v
				b[i] = max_v
		for i in range(3):
			low[i] = minf(low[i],minf(a[i],b[i]))
			high[i] = maxf(high[i],maxf(a[i],b[i]))
	var scale := 1.0
	for i in range(3):
		scale = maxf(scale,maxf(absf(low[i]),absf(high[i])))
	# Guard parameter encoding, not collision-kernel numerical error.
	var guard := scale * 64.0 / 8388608.0
	return [low[0]-guard,low[1]-guard,low[2]-guard,high[0]+guard,high[1]+guard,high[2]+guard]

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

func _execute(space: PhysicsDirectSpaceState3D, q: Dictionary, lease: int) -> Dictionary:
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
	if q.kind != "raycast":
		_prepare_shape(q.shape, policy)
	var b := _prepared_bounds(q)
	var checked := _host.check_engine_bounds(lease,b[0],b[1],b[2],b[3],b[4],b[5])
	if checked.status != 0:
		for key in ["coverage","truncated","hits"]:
			r.erase(key)
		r.status = "unsupported"
		r.error = {"code":"unsupported_operation" if q.kind == "raycast" else "unsupported_shape"}
		return r
	r.coverage = {"state":"complete","loadedRegion":_registration.loadedRegion} if checked.loaded_complete else {"state":"unknown","reason":"outside_loaded_region"}
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

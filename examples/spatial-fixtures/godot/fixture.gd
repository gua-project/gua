extends Node3D

const Reader = preload("res://addons/gua/gua_spatial.gd")
var fixture: Dictionary
var host := GuaSpatialHost.new()
var reader: GuaSpatialReader
var viewport: SubViewport
var index := 0
var phase := 0
var spatial_owner: int
var queued_at: int
var evidence: Array = []
var lease_races: Array = []
var profile_records: Array = []
var profile_frame := 0
var profile_last := 0
var profile_query: Dictionary
var profile_sink := 0.0
var profile_phase_cpu := 0.0
var profile_phase_wall := 0
var door_sample: Dictionary
var region := {"min":{"x":-20,"y":-20,"z":-20},"max":{"x":20,"y":20,"z":20}}

func _ready() -> void:
	fixture = JSON.parse_string(FileAccess.get_file_as_string("res://spatial-engine-r1.json"))
	var version := Engine.get_version_info()
	if not _check(version.major == 4 and version.minor == 7 and version.patch == 0 and version.status == "stable" and version.hash.begins_with("5b4e0cb0f"), "pinned Godot patch"):
		set_physics_process(false)
		return
	_check(host.configure("fixture-godot", 2, 2, 4, 64, 2, 1000, 1000).status == 0, "host configure")
	# A permanently populated separate World3D must not contaminate empty cases.
	var other := SubViewport.new()
	other.own_world_3d = true
	add_child(other)
	_obstacle(other, {"center":{"x":0,"y":0,"z":0},"size":{"x":10,"y":10,"z":10},"category":"solid"})

func _check(condition: bool, label: String) -> bool:
	if not condition:
		push_error("SPATIAL FAIL: " + label)
		get_tree().quit(1)
	return condition

func _obstacle(parent: Node, obstacle: Dictionary) -> void:
	var body: CollisionObject3D = Area3D.new() if obstacle.category == "trigger" else StaticBody3D.new()
	body.collision_layer = 2 if obstacle.category == "self" else 1
	body.position = Vector3(obstacle.center.x, obstacle.center.y, obstacle.center.z)
	var collision := CollisionShape3D.new()
	if obstacle.get("mesh",false):
		var mesh := ConcavePolygonShape3D.new()
		mesh.set_faces(PackedVector3Array([Vector3(-2,-2,0),Vector3(2,-2,0),Vector3(0,2,0)]))
		mesh.backface_collision = false
		collision.shape = mesh
	else:
		var box := BoxShape3D.new()
		box.size = Vector3(obstacle.size.x, obstacle.size.y, obstacle.size.z)
		collision.shape = box
	body.add_child(collision)
	parent.add_child(body)

func _registration(loaded: bool) -> Dictionary:
	var basis := {"x":{"x":1,"y":0,"z":0},"y":{"x":0,"y":1,"z":0},"z":{"x":0,"y":0,"z":1}}
	var registration := {"schemaVersion":"spatial-host-r1", "documentType":"registration",
		"provider":{"schemaVersion":"spatial-r1", "documentType":"provider", "providerId":"godot-fixture",
		"spaceId":"fixture", "spaceEpoch":index+1, "worldSpace":"world3d", "basis":basis, "up":basis.y,
		"unit":{"label":"fixture-unit"}, "precision":{"representation":"binary32", "reason":"backend_error_unmeasured"},
		"operations":["raycast","overlap","sweep"], "shapes":["sphere","capsule","box"],
		"policies":["solid","triggers"], "consistencies":["bestEffort","samePhysicsSample"],
		"engine":{"name":"Godot", "version":Engine.get_version_info().string, "backend":ProjectSettings.get_setting("physics/3d/physics_engine"), "backendVersion":"unknown"},
		"limits":{"maxQueriesPerBatch":64,"maxHitsPerQuery":2,"maxDeadlineMs":1000}},
		"policies":[{"id":"solid","revision":1,"region":region},{"id":"triggers","revision":1,"region":region}]}
	if loaded:
		registration.loadedRegion = region
	return registration

func _physics_process(_delta: float) -> void:
	if index >= fixture.cases.size():
		if not _profile_frame():
			return
		var output := {"configuration":fixture.configuration,"backend":ProjectSettings.get_setting("physics/3d/physics_engine"),"results":evidence,"profile":profile_records,"leaseRaces":lease_races,"physicsTick":Engine.get_physics_frames()}
		var file := FileAccess.open("res://evidence.json", FileAccess.WRITE)
		file.store_string(JSON.stringify(output, "\t", true, true))
		print("SPATIAL PASS: ", evidence.size(), " real Godot cases")
		get_tree().quit(0)
		set_physics_process(false)
		return
	var case: Dictionary = fixture.cases[index]
	if phase == 0:
		case.query.spaceEpoch = index+1
		viewport = SubViewport.new()
		viewport.own_world_3d = true
		add_child(viewport)
		for obstacle in case.obstacles:
			_obstacle(viewport, obstacle)
		var misstated := _registration(case.loaded)
		misstated.provider.precision.representation = "binary64"
		var rejected_reader := Reader.new()
		_check(rejected_reader.configure(host,viewport.find_world_3d(),misstated,{})==5,"misstated Godot precision refused")
		reader = Reader.new()
		var policies := {"solid":{"mask":1,"bodies":true,"areas":false,"backfaces":false,"margin":0.0,"exclude":[]},
			"triggers":{"mask":1,"bodies":true,"areas":true,"backfaces":false,"margin":0.0,"exclude":[]}}
		if not _check(reader.configure(host, viewport.find_world_3d(), _registration(case.loaded), policies) == 0, case.id + " registration"):
			return
		var grants := {"schemaVersion":"spatial-host-r1","documentType":"owner","sessionEpoch":1,
			"profile":"Testing","enabled":true,"policies":["solid","triggers"],"region":region}
		var opened := host.open_owner(JSON.stringify(grants, "", true, true))
		if not _check(opened.status == 0, "spatial_owner"):
			return
		spatial_owner = opened.handle
		var denied_query: Dictionary = case.query.duplicate(true)
		denied_query.kind = "overlap"
		denied_query.erase("segment")
		denied_query.erase("delta")
		denied_query.shape = {"type":"sphere","center":{"x":19.75,"y":0,"z":0},"radius":0.5}
		var denied_batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":9999,
			"consistency":"samePhysicsSample","queries":[denied_query]}
		_check(host.enqueue(spatial_owner, JSON.stringify(denied_batch, "", true, true)).status == 8, "whole-shape region denied")
		grants.profile = "Player"
		_check(host.set_owner(spatial_owner, JSON.stringify(grants, "", true, true)).status == 0, "player grants")
		denied_batch.queries = [case.query]
		_check(host.enqueue(spatial_owner, JSON.stringify(denied_batch, "", true, true)).status == 8, "Player denied before physics")
		grants.profile = "Testing"
		_check(host.set_owner(spatial_owner, JSON.stringify(grants, "", true, true)).status == 0, "testing restore")
		var narrow_grants: Dictionary = grants.duplicate(true)
		narrow_grants.region.min.y = 0.74999998
		_check(host.set_owner(spatial_owner, JSON.stringify(narrow_grants, "", true, true)).status == 0, "derived geometry narrow grant")
		var rounding_query: Dictionary = case.query.duplicate(true)
		rounding_query.requestId = 10001
		rounding_query.queryId = "capsule-derived-rounding"
		rounding_query.kind = "overlap"
		rounding_query.erase("segment")
		rounding_query.erase("delta")
		rounding_query.shape = {"type":"capsule","pointA":{"x":0,"y":1,"z":0},
			"pointB":{"x":0,"y":1.0000001192092896,"z":0},"radius":0.25}
		var rounding_batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":10001,
			"consistency":"samePhysicsSample","queries":[rounding_query]}
		_check(host.enqueue(spatial_owner, JSON.stringify(rounding_batch, "", true, true)).status == 0, "original volume authorized")
		_check(reader.pump() == 0, "derived geometry refusal pump")
		var rounding_result := host.poll(spatial_owner, 10001)
		var rounding_doc: Dictionary = JSON.parse_string(rounding_result.json)
		_check(rounding_doc.items[0].state == "failed" and rounding_doc.items[0].reason == "unsupported_shape" and not rounding_doc.items[0].has("result"), "no expanded physics geometry")
		_check(host.set_owner(spatial_owner, JSON.stringify(grants, "", true, true)).status == 0, "normal grants restored")
		rounding_query.requestId = 10002
		rounding_query.queryId = "capsule-axis-underflow"
		rounding_query.shape.pointA = {"x":0,"y":0,"z":0}
		rounding_query.shape.pointB = {"x":1e-25,"y":0,"z":0}
		rounding_batch.batchId = 10002
		_check(host.enqueue(spatial_owner,JSON.stringify(rounding_batch, "", true, true)).status==0,"underflow input valid")
		_check(reader.pump()==0,"underflow refusal pump")
		var underflow_result := host.poll(spatial_owner,10002)
		var underflow_doc: Dictionary = JSON.parse_string(underflow_result.json)
		_check(underflow_doc.items[0].state=="failed" and underflow_doc.items[0].reason=="unsupported_shape" and not underflow_doc.items[0].has("result"),"no singular capsule transform")
		rounding_query.requestId = 10003
		rounding_query.queryId = "capsule-axis-subnormal"
		rounding_query.shape.pointB.x = 1e-22
		rounding_batch.batchId = 10003
		_check(host.enqueue(spatial_owner,JSON.stringify(rounding_batch,"",true,true)).status==0,"subnormal input valid")
		_check(reader.pump()==0,"subnormal refusal pump")
		var subnormal_result := host.poll(spatial_owner,10003)
		var subnormal_doc: Dictionary = JSON.parse_string(subnormal_result.json)
		_check(subnormal_doc.items[0].state=="failed" and subnormal_doc.items[0].reason=="unsupported_shape" and not subnormal_doc.items[0].has("result"),"no scaled capsule axis")
		var batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":index+1,
			"consistency":"samePhysicsSample","queries":[case.query]}
		queued_at = Time.get_ticks_usec()
		_check(host.enqueue(spatial_owner, JSON.stringify(batch, "", true, true)).status == 0, case.id + " enqueue")
		phase = 1
		return
	# Engine has advanced a normal physics boundary after host-created colliders.
	var start := Time.get_ticks_usec()
	if not _check(reader.pump() == 0, case.id + " pump"):
		set_physics_process(false)
		return
	var elapsed := Time.get_ticks_usec() - start
	var polled := host.poll(spatial_owner, index+1)
	if not _check(polled.status == 0, case.id + " poll"):
		return
	var result: Dictionary = JSON.parse_string(polled.json)
	var item: Dictionary = result.items[0]
	if not _check(item.state == "completed", case.id + " completion " + JSON.stringify(item, "", true, true)):
		return
	var expected: String = case.doorOpenOutcome if phase == 2 else case.outcome
	if expected != "engine-specific":
		if not _check(item.result.outcome == expected, case.id + " expected " + expected + " got " + JSON.stringify(item, "", true, true)):
			return
	_check(item.result.truncated == case.truncated, case.id + " truncation")
	if case.id == "inside-ray":
		_check(item.result.originInside == "unknown", "inside ray uncertainty")
	for hit in item.result.hits:
		_check(hit.missing.has("normal") and hit.relation == "unknown", "no fabricated normal/penetration")
	evidence.append({"case":case.id,"result":item.result,"pumpWallUs":elapsed,"queueWallUs":start-queued_at})
	if case.id == "door-transition" and phase == 1:
		door_sample = item.result.sample
		viewport.get_child(0).position = Vector3(0, 0, 10)
		case.query.requestId = 5000
		case.query.queryId = "door-transition-open"
		var batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":index+1,
			"consistency":"samePhysicsSample","queries":[case.query]}
		queued_at = Time.get_ticks_usec()
		_check(host.enqueue(spatial_owner, JSON.stringify(batch, "", true, true)).status == 0, "door open enqueue")
		phase = 2
		return
	if phase == 2:
		_check(item.result.sample.physicsSampleId != door_sample.physicsSampleId and item.result.sample.tick > door_sample.tick, "door state matches new sample/tick")
	var queries: Array = []
	for n in range(16):
		var query: Dictionary = case.query.duplicate(true)
		query.requestId = 6000+n
		query.queryId = "batch:%s" % n
		queries.append(query)
	var batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":6000,
		"consistency":"samePhysicsSample","queries":queries}
	var queue_start := Time.get_ticks_usec()
	_check(host.enqueue(spatial_owner, JSON.stringify(batch, "", true, true)).status == 0, "batch enqueue")
	var batch_start := Time.get_ticks_usec()
	_check(reader.pump() == 0, "batch pump")
	var batch_wall := Time.get_ticks_usec()-batch_start
	var batch_result := host.poll(spatial_owner, 6000)
	_check(batch_result.status == 0, "batch poll")
	var batch_doc: Dictionary = JSON.parse_string(batch_result.json)
	var sample_id: String = batch_doc.items[0].result.sample.physicsSampleId
	for batch_item in batch_doc.items:
		_check(batch_item.state == "completed" and batch_item.result.sample.physicsSampleId == sample_id, "one held physics sample")
		_check(expected == "engine-specific" or batch_item.result.outcome == expected, "batch fixed expectation")
		_check(batch_item.result.truncated == case.truncated, "batch truncation")
	evidence.append({"case":case.id,"batchSize":16,"pumpWallUs":batch_wall,"queueWallUs":batch_start-queue_start,"result":batch_doc})
	_lease_race(case,expected)
	if index == 0:
		_lease_race(case,expected,true)
	_check(reader.dispose() == 0, "dispose")
	var stale_batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":9999,
		"consistency":"samePhysicsSample","queries":[case.query]}
	_check(host.enqueue(spatial_owner, JSON.stringify(stale_batch, "", true, true)).status != 0, "unregistered world rejected")
	# Explicit host lifecycle: unregister before changing origin, then new epoch.
	for obstacle in viewport.get_children():
		obstacle.position.x += 100
	var new_registration := _registration(false)
	new_registration.provider.spaceEpoch = index+1001
	var replacement := Reader.new()
	_check(replacement.configure(host, viewport.find_world_3d(), new_registration, {"solid":{"mask":1,"bodies":true,"areas":false,"backfaces":false,"margin":0.0,"exclude":[]},"triggers":{"mask":1,"bodies":true,"areas":true,"backfaces":false,"margin":0.0,"exclude":[]}}) == 0, "new origin epoch registration")
	_check(host.enqueue(spatial_owner, JSON.stringify(stale_batch, "", true, true)).status != 0, "old origin epoch rejected")
	_check(replacement.dispose() == 0, "new epoch dispose")
	_check(host.close_owner(spatial_owner).status == 0, "close")
	viewport.queue_free()
	phase = 0
	index += 1

# Actual normal physics callbacks, with identical deterministic game work in
# baseline/single/batch phases. No simulation calls or inferred tick cadence.
func _profile_frame() -> bool:
	if profile_frame == 0:
		viewport = SubViewport.new()
		viewport.own_world_3d = true
		add_child(viewport)
		reader = Reader.new()
		var policy := {"solid":{"mask":1,"bodies":true,"areas":false,"backfaces":false,"margin":0.0,"exclude":[]},"triggers":{"mask":1,"bodies":true,"areas":true,"backfaces":false,"margin":0.0,"exclude":[]}}
		_check(reader.configure(host,viewport.find_world_3d(),_registration(true),policy)==0,"profile registration")
		var opened := host.open_owner(JSON.stringify({"schemaVersion":"spatial-host-r1","documentType":"owner","sessionEpoch":1,"profile":"Testing","enabled":true,"policies":["solid","triggers"],"region":region}, "", true, true))
		_check(opened.status==0,"profile owner")
		spatial_owner = opened.handle
		profile_query = fixture.cases[0].query.duplicate(true)
		profile_query.spaceEpoch = index+1
	var start := Time.get_ticks_usec()
	var cpu: Dictionary = host.thread_cpu_time()
	_check(cpu.status==0 and cpu.has("cycles"),"Windows thread CPU evidence")
	if profile_frame%150 == 30:
		profile_phase_cpu = cpu.microseconds
		profile_phase_wall = start
	var count: int = [0,1,16][mini(profile_frame/150,2)]
	for n in range(2000):
		profile_sink += sin(float(n)*0.01)*0.000001
	var pump_wall := 0
	var queue_wall := 0
	if count > 0:
		var queries: Array = []
		for n in range(count):
			var q := profile_query.duplicate(true)
			q.requestId = 100000+profile_frame*16+n
			q.queryId = "profile:%s:%s" % [profile_frame,n]
			queries.append(q)
		var batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":100000+profile_frame,"consistency":"samePhysicsSample","queries":queries}
		var queued := Time.get_ticks_usec()
		_check(host.enqueue(spatial_owner,JSON.stringify(batch, "", true, true)).status==0,"profile enqueue")
		var pump_start := Time.get_ticks_usec()
		_check(reader.pump()==0,"profile pump")
		pump_wall = Time.get_ticks_usec()-pump_start
		queue_wall = pump_start-queued
		var polled := host.poll(spatial_owner,100000+profile_frame)
		_check(polled.status==0,"profile poll")
		var result: Dictionary = JSON.parse_string(polled.json)
		for item in result.items:
			_check(item.state=="completed" and item.result.outcome=="clear","profile fixed geometry")
	var end := Time.get_ticks_usec()
	var cpu_end: Dictionary = host.thread_cpu_time()
	if profile_frame%150 >= 30:
		profile_records.append({"batchSize":count,"callbackWallUs":end-start,"threadCpuUs":cpu_end.microseconds-cpu.microseconds,"threadCpuCycles":cpu_end.cycles-cpu.cycles,"pumpWallUs":pump_wall,"queueWallUs":queue_wall,"callbackIntervalUs":start-profile_last,"tick":Engine.get_physics_frames(),"phaseMainThreadCpuUs":cpu_end.microseconds-profile_phase_cpu if profile_frame%150 == 149 else null,"phaseElapsedUs":end-profile_phase_wall if profile_frame%150 == 149 else null})
	profile_last = start
	profile_frame += 1
	if profile_frame == 450:
		_check(reader.dispose()==0,"profile dispose")
		_check(host.close_owner(spatial_owner).status==0,"profile close")
		viewport.queue_free()
		return true
	return false

# Deterministic owner revocation after Take; invokes the same completion helper
# as pump, then executes the next query against the actual held World3D state.
func _lease_race(case: Dictionary, expected: String, deadline: bool = false) -> void:
	var first: Dictionary = case.query.duplicate(true)
	first.requestId = 11000
	first.queryId = "race-revoked"
	if deadline:
		first.deadlineMs = 100
	first.kind = "overlap"
	first.erase("segment")
	first.erase("delta")
	first.shape = {"type":"sphere","center":{"x":9,"y":0,"z":0},"radius":0.25}
	var second: Dictionary = case.query.duplicate(true)
	second.requestId = 11001
	second.queryId = "race-eligible"
	var batch := {"schemaVersion":"spatial-host-r1","documentType":"batch","batchId":11000,"consistency":"samePhysicsSample","queries":[first,second]}
	_check(host.enqueue(spatial_owner,JSON.stringify(batch,"",true,true)).status==0,"race enqueue")
	var sample_id := "race:%s:%s" % [index,deadline]
	var boundary := {"schemaVersion":"spatial-host-r1","documentType":"boundary","physicsSampleId":sample_id,"tick":Engine.get_physics_frames()}
	var begun := host.begin(reader.provider,JSON.stringify(boundary,"",true,true))
	_check(begun.status==0,"race begin")
	var consumed := host.take(begun.handle)
	_check(consumed.status==0 and JSON.parse_string(consumed.json).requestId==11000,"race first consumed before terminal change")
	var grants := {"schemaVersion":"spatial-host-r1","documentType":"owner","sessionEpoch":1,"profile":"Testing","enabled":true,"policies":["solid","triggers"],"region":region.duplicate(true)}
	grants.region.max.x = 8
	if deadline:
		OS.delay_msec(120) # Deterministic host stall after Take; no physics simulation.
	else:
		_check(host.set_owner(spatial_owner,JSON.stringify(grants,"",true,true)).status==0,"race selective revoke")
	var execution := reader._execute(viewport.find_world_3d().direct_space_state,JSON.parse_string(consumed.json),begun.handle)
	_check(reader._complete_item(begun.handle,execution)==0,"race correlation released")
	var eligible := host.take(begun.handle)
	_check(eligible.status==0,"race next eligible taken")
	var actual := reader._execute(viewport.find_world_3d().direct_space_state,JSON.parse_string(eligible.json),begun.handle)
	_check(reader._complete_item(begun.handle,actual)==0,"race actual physics completed")
	var polled := host.poll(spatial_owner,11000)
	_check(polled.status==0,"race poll")
	var result: Dictionary = JSON.parse_string(polled.json)
	_check(result.items[0].state=="failed" and result.items[0].reason==("deadline_exceeded" if deadline else "not_authorized") and not result.items[0].has("result"),"race revoked geometry withheld")
	_check(result.items[1].state=="completed" and result.items[1].result.sample.physicsSampleId==sample_id,"race same held sample")
	_check(expected=="engine-specific" or result.items[1].result.outcome==expected,"race independent geometry")
	_check(host.end(begun.handle).status==0,"race mandatory end")
	grants.region = region
	_check(host.set_owner(spatial_owner,JSON.stringify(grants,"",true,true)).status==0,"race grants restored")
	lease_races.append({"case":case.id,"deadline":deadline,"result":result})

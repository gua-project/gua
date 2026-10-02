extends "res://fixture.gd"
var context := GuaContext.new()
var pumped := 0
var started := Time.get_ticks_msec()
func _ready() -> void:
	super._ready()
	viewport = SubViewport.new()
	viewport.own_world_3d = true
	add_child(viewport)
	_obstacle(viewport, {"center":{"x":0,"y":0,"z":2},"size":{"x":2,"y":2,"z":1},"category":"solid"})
	reader = Reader.new()
	var registration := _registration(true)
	var policy := {"mask":1,"bodies":true,"areas":false,"backfaces":false,"margin":0.0,"exclude":[]}
	var policies := {"solid":policy,"triggers":policy}
	var regression := OS.get_environment("GUA_SPATIAL_REGRESSION")
	if regression == "buffer" or regression == "both":
		for n in range(60):
			var id := "policy_%02d_" % n + "x".repeat(48)
			registration.provider.policies.append(id)
			registration.policies.append({"id":id,"revision":1,"region":region})
			policies[id] = policy
	_check(reader.configure(host, viewport.find_world_3d(), registration, policies) == 0,"transport reader")
	var grants := {"schemaVersion":"spatial-host-r1","documentType":"owner","sessionEpoch":1,
		"profile":"Testing","enabled":true,"policies":["solid"],"region":region}
	if regression == "buffer" or regression == "both":
		grants.policies = registration.provider.policies.duplicate()
	_check(context.bind_spatial(host,reader.provider,JSON.stringify(grants,"",true,true)) == 0,"explicit transport authorization")
	if regression == "unconfigured" or regression == "both":
		if not _check(context.bind_spatial(GuaSpatialHost.new(),reader.provider,JSON.stringify(grants,"",true,true)) != 0,"unconfigured replacement rejected"):
			return
	var version_json := context.get_version_json()
	var version = JSON.parse_string(version_json)
	if not _check(version is Dictionary and version.get("capabilities",[]).has("spatial_read_r1"),"valid version and preserved binding"):
		return
	if regression == "buffer" or regression == "both":
		if not _check(version_json.length() > 2048,"expanded version metadata exercised"):
			return
	context.begin_frame("spatial-route")
	context.end_frame()
	var port := int(OS.get_environment("GUA_BRIDGE_PORT"))
	_check(context.start_inspector_bridge(port),"transport bridge")
	var file := FileAccess.open("res://transport-ready.json",FileAccess.WRITE)
	file.store_string(version_json)
func _physics_process(_delta: float) -> void:
	if reader != null and reader.pump() == 0:
		pumped += 1
	if FileAccess.file_exists("res://transport-done"):
		context.disable_spatial()
		context.stop_inspector_bridge()
		reader.dispose()
		var file := FileAccess.open("res://transport-host-evidence.json",FileAccess.WRITE)
		file.store_string(JSON.stringify({"physicsBatches":pumped,"backend":ProjectSettings.get_setting("physics/3d/physics_engine"),"version":Engine.get_version_info().string},"\t",true,true))
		get_tree().quit(0)
	if Time.get_ticks_msec() - started > 180000:
		get_tree().quit(1)

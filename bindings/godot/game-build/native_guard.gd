extends RefCounted

const SLOT_SIZE := 65536
const MARKER := "GUAGDPOLICYSLOT1"

# Provider code + game-owned expected data. This never runs the SDK to decide
# whether it should be trusted, and requires no external compiler/build runtime.
func generate(native_hash: String, mode: String, source: String, build: String, target: String = "windows") -> String:
	var root := "res://addons/gua/editor/"
	var linux := target == "linux"
	var metadata: Variant = JSON.parse_string(FileAccess.get_file_as_string(root + ("linux-guard-template.json" if linux else "native-guard-template.json")))
	var template := root + ("GodotPolicyTemplate.so" if linux else "GodotPolicyTemplate.dll")
	if not metadata is Dictionary or metadata.get("schemaVersion") != 1 or FileAccess.get_sha256(template) != metadata.get("sha256"):
		push_error("The provider SDK is missing or has modified its game verification template.")
		return ""
	var image := FileAccess.get_file_as_bytes(template)
	if image.size() < 256:
		return ""
	if linux:
		if image.decode_u32(0) != 0x464c457f or image[4] != 2 or image[5] != 1 or image.decode_u16(16) != 3 or image.decode_u16(18) != 62:
			return ""
	else:
		if image.decode_u16(0) != 0x5a4d:
			return ""
		var pe := image.decode_u32(0x3c)
		if pe > image.size() - 24 or image.decode_u32(pe) != 0x4550 or image.decode_u16(pe + 4) != 0x8664:
			return ""
	var slot := -1
	var search := 0
	var marker := MARKER.to_ascii_buffer()
	while search < image.size():
		var found := image.find(marker[0], search)
		if found == -1:
			break
		if image.slice(found, found + marker.size()) == marker:
			if slot != -1:
				return ""
			slot = found
		search = found + 1
	if slot == -1 or slot > image.size() - SLOT_SIZE or native_hash.length() != 64 or native_hash.hex_decode().size() != 32:
		return ""
	var payload := PackedByteArray()
	_u32(payload, 0x50415547)
	_u32(payload, 1)
	_u32(payload, 4) # GDScript native host; root is the game verifier's directory.
	_u32(payload, ["off", "testing", "player"].find(mode))
	_text(payload, "")
	_text(payload, source)
	_text(payload, build)
	_u32(payload, 1)
	_u32(payload, 0) # Native member, also rejected if already loaded.
	_text(payload, "gua.so" if linux else "gua.dll")
	payload.append_array(native_hash.hex_decode())
	if payload.size() > SLOT_SIZE - 20:
		return ""
	image.encode_u32(slot + 16, payload.size())
	for index in payload.size():
		image[slot + 20 + index] = payload[index]
	var image_hash := _sha256(image)
	if image_hash.length() != 64:
		return ""
	var path := "res://gua-native/GameSecurity_Godot_" + image_hash.left(24) + (".so" if linux else ".dll")
	if FileAccess.file_exists(path) and FileAccess.get_sha256(path) == image_hash:
		return path
	var output := FileAccess.open(path, FileAccess.WRITE)
	if output == null:
		return ""
	output.store_buffer(image)
	if output.get_error() != OK:
		return ""
	return path

func _sha256(data: PackedByteArray) -> String:
	var context := HashingContext.new()
	if context.start(HashingContext.HASH_SHA256) != OK or context.update(data) != OK:
		return ""
	return context.finish().hex_encode()

func _u32(data: PackedByteArray, value: int) -> void:
	var offset := data.size()
	data.resize(offset + 4)
	data.encode_u32(offset, value)

func _text(data: PackedByteArray, value: String) -> void:
	var bytes := value.to_utf8_buffer()
	_u32(data, bytes.size())
	data.append_array(bytes)

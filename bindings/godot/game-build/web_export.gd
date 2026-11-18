extends RefCounted

# Only this reviewed custom shell is initially supported. Other custom pages
# must be reviewed and exercised before their digest can be added here.
const CUSTOM_SHA256 := "9179a46086f34a5150425f0bd5ea5d423c84610a6be15317185bf2e808e7eb9a"
const ROOT := "res://addons/gua/"

func validate_options(exporter: EditorExportPlugin) -> bool:
	if bool(exporter.get_option("variant/thread_support")) or not bool(exporter.get_option("variant/extensions_support")) or bool(exporter.get_option("progressive_web_app/enabled")):
		push_error("Protected Gua Web requires the single-threaded Godot 4.7 extension template without PWA.")
		return false
	var custom := str(exporter.get_option("html/custom_html_shell"))
	if not custom.is_empty():
		var source := FileAccess.get_file_as_string(custom).replace("\r\n", "\n")
		if source.sha256_text() != CUSTOM_SHA256:
			push_error("Unverified custom HTML shell. Use the supplied web-custom-shell.html; other shells need a focused startup review and browser acceptance before protection is claimed.")
			return false
	return true

func finish(path: String, mode: String, manifest: Dictionary) -> bool:
	var expected := ""
	for entry: Dictionary in manifest.get("files", []):
		if entry.get("path") == "gua.wasm":
			expected = str(entry.get("sha256", ""))
	if expected.length() != 64 or FileAccess.get_sha256(path.get_base_dir().path_join("gua.wasm")) != expected:
		push_error("The exported Gua Web module differs from the selected provider input.")
		return false
	var html := FileAccess.get_file_as_string(path).replace("\r\n", "\n")
	var prefix := RegEx.new()
	prefix.compile('(?is)\\A<!doctype html>\\s*<html(?: lang="[a-zA-Z-]+")?>\\s*<head>')
	var first := prefix.search(html)
	# This rules out executable content before the inserted verifier. Custom
	# shells are additionally byte-bound above; arbitrary HTML is not inferred safe.
	if first == null or html.count("new Engine(") != 1:
		push_error("Unsupported Godot Web startup page; expected the accepted 4.7 bootstrap.")
		return false
	var guard := FileAccess.get_file_as_string(ROOT + "editor/web_guard.js.txt")
	guard = guard.replace("/*EXPECTED*/", _json({"gua.wasm":expected})).replace("/*MODE*/", _json(mode))
	guard = guard.replace("/*SOURCE*/", _json(manifest.build.sourceCommit)).replace("/*BUILD*/", _json(manifest.build.buildId))
	# A CSP blocking the verifier must also prevent the engine constructor. The
	# game bootstrap references this game-owned object rather than starting Engine
	# independently. No SDK module is loaded merely by defining Engine.js.
	html = html.replace("new Engine(", "new globalThis.__guaGameIntegrity.Engine(")
	var end := first.get_end()
	html = html.substr(0, end) + "\n<script>\n" + guard + "\n</script>\n" + html.substr(end)
	var output := FileAccess.open(path, FileAccess.WRITE)
	if output == null:
		return false
	output.store_string(html)
	return output.get_error() == OK

func _json(value: Variant) -> String:
	# JSON strings are embedded inside an HTML script element, not a JS file.
	return JSON.stringify(value).replace("<", "\\u003c")

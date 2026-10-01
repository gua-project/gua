#include "gua/gua.hpp"
#include "../../gua-godot/include/gua/godot/copy_json.hpp"
#include <algorithm>
#include <cassert>
#include <cstring>
#include <string>
#ifdef _MSC_VER
#include <crtdbg.h>
#endif

static const char* vector_schema = R"({"type":"object","properties":{"x":{"type":"number","minimum":-1,"maximum":1,"description":"Horizontal movement; positive moves right"},"y":{"type":"number","minimum":-1,"maximum":1,"description":"Forward movement; positive moves forward"}},"required":["x","y"],"additionalProperties":false})";
static gua_game_input_action_descriptor_v3_t descriptor(int type = GUA_GAME_INPUT_VECTOR2) {
    gua_game_input_action_descriptor_v1_t base { sizeof(base), "move", "Move the player", type,
        -1, 1, type == GUA_GAME_INPUT_VECTOR2 || type == GUA_GAME_INPUT_AXIS1D, 1, 1, "[]", "safe", 0 };
    gua_game_input_action_descriptor_v2_t v2 { sizeof(v2), base, nullptr, nullptr, 0, nullptr, 0, GUA_AGENT_EXPOSURE_AUTO };
    return { sizeof(gua_game_input_action_descriptor_v3_t), v2, vector_schema, R"([{"x":0.5,"y":-1}])" };
}
static std::string copy(gua_context_t* context, bool metadata, int profile = GUA_OBSERVATION_PROFILE_DEBUG) {
    auto read = [&](char* out, int size) { return metadata ? gua_copy_game_input_actions_json_v2(context, profile, out, size)
        : gua_copy_game_input_actions_json_for_profile(context, profile, out, size); };
    auto size = read(nullptr, 0); assert(size > 0);
    std::string result(static_cast<size_t>(size), '\0'); assert(read(result.data(), size) == size); result.pop_back(); return result;
}
static void publish(gua_context_t* context, const gua_game_input_action_descriptor_v3_t& action) {
    assert(gua_begin_game_input_frame(context, "play"));
    assert(gua_register_game_input_action_v3(context, &action));
    assert(gua_end_game_input_frame(context));
}
static void invalid(gua_context_t* context, const gua_game_input_action_descriptor_v3_t& action) {
    const auto previous = copy(context, true);
    assert(gua_begin_game_input_frame(context, "invalid"));
    assert(!gua_register_game_input_action_v3(context, &action));
    assert(!gua_end_game_input_frame(context));
    assert(copy(context, true) == previous);
}
int main() {
    // Deterministically change the map between the size probe and each copy.
    // Never accept the truncated prefix, including when it grows twice.
    int reads = 0;
    const std::string grown = "{\"actions\":[{\"id\":\"larger_map\"}]}";
    const auto copied = gua::godot_detail::copy_json_with_retry([&](char* output, int size) {
        ++reads;
        if (!output) return 3;
        const std::string current = reads == 2 ? "{\"actions\":[]}" : grown;
        const int required = static_cast<int>(current.size() + 1);
        const int written = std::min(size - 1, static_cast<int>(current.size()));
        std::memcpy(output, current.data(), static_cast<std::size_t>(written));
        output[written] = '\0';
        return required;
    });
    assert(reads == 4 && copied == grown);
    assert(gua::godot_detail::copy_json_with_retry([](char*, int) { return 0; }).empty());
    int revoked_reads = 0;
    assert(gua::godot_detail::copy_json_with_retry([&](char*, int) { return ++revoked_reads == 1 ? 3 : 0; }).empty());
#ifdef _MSC_VER
    _set_error_mode(_OUT_TO_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    auto* context = gua_create_context(); assert(context);
    auto action = descriptor();
    // Existing ABI v1/v2 and wire v1 remain byte-identical after metadata registration.
    assert(gua_begin_game_input_frame(context, "play"));
    assert(gua_register_game_input_action_v1(context, &action.base.base)); assert(gua_end_game_input_frame(context));
    const auto legacy = copy(context, false);
    assert(gua_begin_game_input_frame(context, "play"));
    assert(gua_register_game_input_action_v2(context, &action.base)); assert(gua_end_game_input_frame(context));
    assert(copy(context, false) == legacy);
    publish(context, action);
    auto old = copy(context, false);
    assert(old.find("valueSchema") == old.npos && old.find("examples") == old.npos);
    assert(old.substr(old.find("\"context\"")) == legacy.substr(legacy.find("\"context\"")));
    const auto extended = copy(context, true);
    assert(extended.find("\"schemaVersion\":2") != extended.npos && extended.find(vector_schema) != extended.npos);
    gua_game_input_action_selector_v1_t selector { sizeof(selector), "move", nullptr, 0, 0, nullptr, nullptr, nullptr, 0, 20 };
    int size = gua_query_game_input_actions_json_v2(context, &selector, 0, nullptr, 0);
    std::string search(static_cast<size_t>(size), '\0'); assert(gua_query_game_input_actions_json_v2(context, &selector, 0, search.data(), size) == size);
    assert(search.find(vector_schema) != search.npos && search.find("\"count\":1") != search.npos);
    auto owner = gua_create_game_input_owner(context); uint64_t request_id = 0;
    gua_game_input_request_descriptor_v2_t escaped { sizeof(escaped), owner, GUA_GAME_INPUT_SEMANTIC, GUA_GAME_INPUT_SET,
        "move", R"({"\u0078":0,"y":0})", 0, 0, 5000, 0, 0, 0 };
    assert(gua_enqueue_game_input_v2(context, &escaped, &request_id) == GUA_GAME_INPUT_OK);
    gua_game_input_request_v1_t escaped_consumed {}; escaped_consumed.struct_size = sizeof(escaped_consumed);
    assert(gua_consume_game_input_request(context, &escaped_consumed));
    assert(gua_complete_game_input_request(context, escaped_consumed.request_id, 1, 0));
    size = gua_query_game_input_actions_json(context, &selector, 0, nullptr, 0);
    std::string old_search(static_cast<size_t>(size), '\0'); gua_query_game_input_actions_json(context, &selector, 0, old_search.data(), size);
    assert(old_search.find("valueSchema") == old_search.npos && old_search.find("\"schemaVersion\":1") != old_search.npos);
    for (const auto schema : { R"({"type":"number"})", R"({"type":"object","$ref":"https://example.com"})",
        R"({"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}},"required":["x","x"],"additionalProperties":false})",
        R"({"type":"object","properties":{"x":{"type":"number","minimum":2,"maximum":1},"y":{"type":"number"}},"required":["x","y"],"additionalProperties":false})",
        R"({"type":"object","type":"object"})" }) { auto bad = action; bad.value_schema_json = schema; invalid(context, bad); }
    for (const auto examples : { "null", "[null]", "[true]", R"([{"x":2,"y":0}])", R"([{"x":0,"y":0,"z":1}])", R"([{"nested":{"x":0},"y":0}])", R"([{"x":1e999,"y":0}])" }) {
        auto bad = action; bad.examples_json = examples; invalid(context, bad);
    }
    gua_game_input_request_descriptor_v2_t request { sizeof(request), owner, GUA_GAME_INPUT_SEMANTIC, GUA_GAME_INPUT_SET,
        "move", R"({"x":0.5,"y":0})", 0, 0, 5000, 0, 0, 0 };
    assert(gua_enqueue_game_input_v2(context, &request, &request_id) == GUA_GAME_INPUT_OK);
    action.value_schema_json = R"({"type":"object","properties":{"x":{"type":"number","maximum":0.25},"y":{"type":"number"}},"required":["x","y"],"additionalProperties":false})";
    action.examples_json = "[]"; publish(context, action);
    gua_game_input_request_v1_t consumed {}; consumed.struct_size = sizeof(consumed);
    assert(!gua_consume_game_input_request(context, &consumed));
    assert(gua_enqueue_game_input_v2(context, &request, &request_id) == GUA_GAME_INPUT_ERROR_INVALID_VALUE);
    // Current confirmation requirement applies even with valid examples/schema.
    action.base.base.requires_confirmation = 1; publish(context, action); request.value_json = R"({"x":0,"y":0})";
    assert(gua_enqueue_game_input_v2(context, &request, &request_id) == GUA_GAME_INPUT_ERROR_CONFIRMATION_REQUIRED);
    request.confirmed = 1; assert(gua_enqueue_game_input_v2(context, &request, &request_id) == GUA_GAME_INPUT_OK);
    action.base.agent_exposure = GUA_AGENT_EXPOSURE_PRIVATE; publish(context, action);
    assert(copy(context, true, GUA_OBSERVATION_PROFILE_PLAYER).find("Horizontal") == std::string::npos);
    assert(gua_enqueue_game_input_for_profile_v2(context, &request, GUA_OBSERVATION_PROFILE_PLAYER, &request_id) == GUA_GAME_INPUT_ERROR_ACTION_NOT_FOUND);
    // Other existing value types and examples, including non-BMP code-point length.
    for (int type : { GUA_GAME_INPUT_BUTTON, GUA_GAME_INPUT_AXIS1D, GUA_GAME_INPUT_TEXT }) {
        auto valid = descriptor(type);
        valid.value_schema_json = type == GUA_GAME_INPUT_BUTTON ? R"({"type":"boolean"})" : type == GUA_GAME_INPUT_AXIS1D ? R"({"type":"number","minimum":-0.5,"maximum":0.5})" : R"({"type":"string","minLength":1,"maxLength":2})";
        valid.examples_json = type == GUA_GAME_INPUT_BUTTON ? "[true,false]" : type == GUA_GAME_INPUT_AXIS1D ? "[0.25]" : R"(["\ud83d\ude00"])";
        publish(context, valid);
        auto bad = valid; bad.examples_json = type == GUA_GAME_INPUT_BUTTON ? "[1]" : type == GUA_GAME_INPUT_AXIS1D ? "[1]" : "[\"abc\"]"; invalid(context, bad);
        if (type == GUA_GAME_INPUT_BUTTON) { bad = valid; bad.base.base.holdable = 0; invalid(context, bad); bad.examples_json = "[]"; publish(context, bad); }
    }
    // Every original example literal must fit the existing 512-byte Set buffer.
    auto axis = descriptor(GUA_GAME_INPUT_AXIS1D);
    axis.value_schema_json = R"({"type":"number"})";
    const auto oversized = std::string("[0.1") + std::string(600, '0') + "]";
    axis.examples_json = oversized.c_str();
    invalid(context, axis);
    const auto oversized_vector = std::string("[{\"x\":0,\"y\":") + std::string(600, ' ') + "0}]";
    auto padded = descriptor(GUA_GAME_INPUT_VECTOR2);
    padded.examples_json = oversized_vector.c_str();
    invalid(context, padded);
    const auto exact_limit = std::string("[0.1") + std::string(509, '0') + "]";
    axis.examples_json = exact_limit.c_str();
    invalid(context, axis);
    const auto under_limit = std::string("[0.1") + std::string(508, '0') + "]";
    axis.examples_json = under_limit.c_str();
    publish(context, axis);
    const auto set_literal = under_limit.substr(1, under_limit.size() - 2);
    request.value_json = set_literal.c_str();
    assert(gua_enqueue_game_input_v2(context, &request, &request_id) == GUA_GAME_INPUT_OK);
    gua_destroy_context(context);
    // Public C++ binding round-trip uses the same ABI contract.
    gua::Context cpp; gua::GameInputAction cpp_action;
    cpp_action.id = "move"; cpp_action.description = "Move"; cpp_action.value_type = gua::GameInputValueType::vector2;
    cpp_action.value_schema_json = vector_schema; cpp_action.examples_json = R"([{"x":0,"y":1}])";
    cpp.publish_game_input_actions("play", { cpp_action });
    assert(cpp.game_input_actions_json().find("valueSchema") == std::string::npos);
    assert(cpp.game_input_actions_json_v2().find(vector_schema) != std::string::npos);
    assert(cpp.find_game_input_actions_json_v2({ .id = "move" }).find(vector_schema) != std::string::npos);
}

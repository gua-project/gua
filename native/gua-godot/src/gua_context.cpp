#include "gua/godot/gua_context.hpp"
#include "gua/godot/copy_json.hpp"

#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <cstdint>
#include <cmath>
#include <limits>
#include <string>
#include <vector>

namespace {

bool exactly_representable_as_double(std::int64_t value)
{
    const std::uint64_t magnitude = value < 0
        ? static_cast<std::uint64_t>(-(value + 1)) + 1U
        : static_cast<std::uint64_t>(value);
    constexpr std::uint64_t max_consecutive_integer = 1ULL << 53U;
    if (magnitude <= max_consecutive_integer) return true;

    std::uint64_t reduced = magnitude;
    unsigned discarded_bits = 0;
    while (reduced > max_consecutive_integer) {
        reduced >>= 1U;
        ++discarded_bits;
    }
    return (magnitude & ((1ULL << discarded_bits) - 1U)) == 0;
}

const char* event_type_name(int type)
{
    switch (type) {
    case GUA_EVENT_CLICK:
        return "click";
    case GUA_EVENT_FOCUS:
        return "focus";
    case GUA_EVENT_NONE:
    default:
        return "none";
    }
}

int action_type(const godot::String& name)
{
    if (name == "click") return GUA_ACTION_CLICK;
    if (name == "focus") return GUA_ACTION_FOCUS;
    if (name == "set_value") return GUA_ACTION_SET_VALUE;
    if (name == "set_checked") return GUA_ACTION_SET_CHECKED;
    if (name == "select") return GUA_ACTION_SELECT;
    if (name == "scroll") return GUA_ACTION_SCROLL;
    if (name == "press_key") return GUA_ACTION_PRESS_KEY;
    return 0;
}

int field_mode(const godot::String& name)
{
    if (name == "keep") return GUA_AGENT_FIELD_KEEP;
    if (name == "omit") return GUA_AGENT_FIELD_OMIT;
    if (name == "redact") return GUA_AGENT_FIELD_REDACT;
    if (name == "replace") return GUA_AGENT_FIELD_REPLACE;
    if (name == "quantize") return GUA_AGENT_FIELD_QUANTIZE;
    return -1;
}

struct AgentPolicyStorage {
    std::vector<godot::CharString> paths, strings;
    std::vector<gua_agent_field_rule_v1_t> rules;
    gua_agent_policy_v1_t policy { sizeof(gua_agent_policy_v1_t) };
};

bool agent_policy(const godot::Dictionary& source, AgentPolicyStorage& storage)
{
    const godot::String exposure = source.get("agent_exposure", godot::String("auto"));
    if (exposure != "auto" && exposure != "private") return false;
    storage.policy.exposure = exposure == "private" ? GUA_AGENT_EXPOSURE_PRIVATE : GUA_AGENT_EXPOSURE_AUTO;
    const godot::Array rules = source.get("agent_field_rules", godot::Array());
    storage.paths.reserve(rules.size()); storage.strings.reserve(rules.size()); storage.rules.reserve(rules.size());
    for (int index = 0; index < rules.size(); ++index) {
        if (static_cast<godot::Variant>(rules[index]).get_type() != godot::Variant::DICTIONARY) return false;
        const godot::Dictionary rule = rules[index];
        const godot::String path = rule.get("path", godot::String()), mode_name = rule.get("mode", godot::String("keep"));
        const int mode = field_mode(mode_name);
        if (path.is_empty() || mode < 0) return false;
        const godot::Variant replacement = rule.get("replacement", godot::Variant());
        int replacement_type = GUA_WORLD_VALUE_NULL;
        if (replacement.get_type() == godot::Variant::STRING) replacement_type = GUA_WORLD_VALUE_STRING;
        else if (replacement.get_type() == godot::Variant::INT || replacement.get_type() == godot::Variant::FLOAT) replacement_type = GUA_WORLD_VALUE_NUMBER;
        else if (replacement.get_type() == godot::Variant::BOOL) replacement_type = GUA_WORLD_VALUE_BOOLEAN;
        else if (replacement.get_type() != godot::Variant::NIL) return false;
        storage.paths.push_back(path.utf8());
        storage.strings.push_back(replacement_type == GUA_WORLD_VALUE_STRING ? godot::String(replacement).utf8() : godot::CharString());
        storage.rules.push_back(gua_agent_field_rule_v1_t { sizeof(gua_agent_field_rule_v1_t), nullptr, mode, replacement_type, nullptr,
            replacement_type == GUA_WORLD_VALUE_NUMBER ? static_cast<double>(replacement) : 0.0,
            replacement_type == GUA_WORLD_VALUE_BOOLEAN && static_cast<bool>(replacement) ? 1 : 0,
            static_cast<double>(rule.get("quantum", 0.0)) });
    }
    for (std::size_t index = 0; index < storage.rules.size(); ++index) {
        storage.rules[index].path = storage.paths[index].get_data();
        if (storage.rules[index].mode == GUA_AGENT_FIELD_REPLACE && storage.rules[index].replacement_type == GUA_WORLD_VALUE_STRING)
            storage.rules[index].string_value = storage.strings[index].get_data();
    }
    const godot::Array actions = source.get("agent_allowed_actions", godot::Array());
    storage.policy.has_allowed_actions = source.get("agent_allowed_actions_set", false) ? 1 : 0;
    for (int index = 0; index < actions.size(); ++index) {
        const int action = action_type(godot::String(actions[index]));
        if (action == 0) return false;
        storage.policy.allowed_actions |= 1ULL << static_cast<unsigned int>(action);
    }
    storage.policy.field_rules = storage.rules.data();
    storage.policy.field_rule_count = static_cast<uint32_t>(storage.rules.size());
    return true;
}

const char* action_name(int action)
{
    switch (action) {
    case GUA_ACTION_CLICK: return "click";
    case GUA_ACTION_FOCUS: return "focus";
    case GUA_ACTION_SET_VALUE: return "set_value";
    case GUA_ACTION_SET_CHECKED: return "set_checked";
    case GUA_ACTION_SELECT: return "select";
    case GUA_ACTION_SCROLL: return "scroll";
    case GUA_ACTION_PRESS_KEY: return "press_key";
    default: return "none";
    }
}

const char* clock_error_name(int result)
{
    switch (result) {
    case GUA_CLOCK_ERROR_NOT_INSTALLED: return "not_installed";
    case GUA_CLOCK_ERROR_INVALID_STATE: return "invalid_state";
    case GUA_CLOCK_ERROR_EXECUTION_LIMIT: return "execution_limit";
    case GUA_CLOCK_ERROR_INVALID_ARGUMENT: return "invalid_duration";
    default: return "";
    }
}

godot::Dictionary clock_result(gua_runtime_t* runtime, int result)
{
    gua_clock_status_t status { sizeof(gua_clock_status_t) };
    godot::Dictionary value;
    if (gua_runtime_clock_get_status(runtime, &status) != 0) {
        value["schema_version"] = 1;
        value["installed"] = status.installed != 0;
        value["state"] = status.paused != 0 ? "paused" : "running";
        value["now_ms"] = status.now_ms;
        value["default_step_ms"] = status.default_step_ms;
        value["pending_ms"] = status.pending_ms;
        value["generation"] = status.generation;
    }
    value["result"] = result;
    value["error"] = clock_error_name(result);
    return value;
}

template<typename CopyJson>
godot::String copy_json_with_retry(CopyJson copy_json)
{
    const auto json = gua::godot_detail::copy_json_with_retry(copy_json);
    return godot::String::utf8(json.c_str());
}

godot::String copy_runtime_json(gua_runtime_t* runtime, int (*copy_json)(gua_runtime_t*, char*, int))
{
    return copy_json_with_retry([&](char* output, int size) { return copy_json(runtime, output, size); });
}

} // namespace

namespace godot {

GuaContext::GuaContext()
    : runtime_(gua_runtime_create())
{
    gua_runtime_set_godot_plugin_version(runtime_, GUA_GODOT_PLUGIN_VERSION);
    if (runtime_ == nullptr) {
        UtilityFunctions::push_error(
            "GuaContext failed to create a Gua runtime. Check that the Gua native library and dependent DLLs are available.");
    }
}

GuaContext::~GuaContext()
{
    gua_enum_catalog_destroy(observe_catalog_);
    gua_runtime_destroy(runtime_);
    runtime_ = nullptr;
}

void GuaContext::begin_frame(const String& screen)
{
    const CharString screen_utf8 = screen.utf8();
    gua_runtime_begin_frame(runtime_, screen_utf8.get_data());
}

void GuaContext::end_frame()
{
    gua_runtime_end_frame(runtime_);
}

void GuaContext::set_screenshot(const String& data_uri, int width, int height)
{
    const CharString data_uri_utf8 = data_uri.utf8();
    gua_runtime_set_screenshot(runtime_, data_uri_utf8.get_data(), width, height);
}

String GuaContext::get_screenshot_json() const
{
    return copy_runtime_json(runtime_, gua_runtime_copy_screenshot_json);
}

Dictionary GuaContext::consume_screenshot_request()
{
    gua_screenshot_request_t request { sizeof(gua_screenshot_request_t) };
    if (gua_runtime_consume_screenshot_request(runtime_, &request) == 0) return Dictionary();
    Dictionary result;
    result["request_id"] = request.request_id;
    result["session_epoch"] = request.session_epoch;
    result["after_frame_sequence"] = request.after_frame_sequence;
    return result;
}

bool GuaContext::complete_screenshot_request(const Dictionary& source)
{
    const String data_uri = source.get("data_uri", String());
    const CharString utf8 = data_uri.utf8();
    int result = GUA_SCREENSHOT_AVAILABLE;
    const String unavailable = source.get("unavailable", String());
    if (unavailable == "headless") result = GUA_SCREENSHOT_UNAVAILABLE_HEADLESS;
    else if (unavailable == "rendering_disabled") result = GUA_SCREENSHOT_UNAVAILABLE_RENDERING_DISABLED;
    else if (unavailable == "unsupported") result = GUA_SCREENSHOT_UNAVAILABLE_UNSUPPORTED;
    return gua_runtime_complete_screenshot_request(
        runtime_, source.get("request_id", 0), result, utf8.get_data(), source.get("width", 0), source.get("height", 0)) != 0;
}

void GuaContext::register_node(
    const String& id,
    const String& role,
    const String& label,
    const Rect2& bounds,
    bool visible,
    bool enabled)
{
    const CharString id_utf8 = id.utf8();
    const CharString role_utf8 = role.utf8();
    const CharString label_utf8 = label.utf8();

    gua_runtime_register_node(
        runtime_,
        id_utf8.get_data(),
        role_utf8.get_data(),
        label_utf8.get_data(),
        gua_bounds_t {
            static_cast<float>(bounds.position.x),
            static_cast<float>(bounds.position.y),
            static_cast<float>(bounds.size.x),
            static_cast<float>(bounds.size.y),
        },
        visible ? 1 : 0,
        enabled ? 1 : 0);
}

bool GuaContext::register_node_v2(const Dictionary& source)
{
    if (!source.has("id") || !source.has("role") || !source.has("bounds")) {
        UtilityFunctions::push_error("GuaContext.register_node_v2 requires id, role, and bounds.");
        return false;
    }

    const String id = source["id"];
    const String role = source["role"];
    const String label = source.get("label", String());
    const String parent_id = source.get("parent_id", String());
    const String text = source.get("text", String());
    const String value = source.get("value", String());
    const Rect2 bounds = source["bounds"];
    const CharString id_utf8 = id.utf8();
    const CharString parent_id_utf8 = parent_id.utf8();
    const CharString role_utf8 = role.utf8();
    const CharString label_utf8 = label.utf8();
    const CharString text_utf8 = text.utf8();
    const CharString value_utf8 = value.utf8();

    unsigned long long known_mask = 0;
    if (source.has("parent_id")) known_mask |= GUA_NODE_KNOWN_PARENT_ID;
    if (source.has("text")) known_mask |= GUA_NODE_KNOWN_TEXT;
    if (source.has("value")) known_mask |= GUA_NODE_KNOWN_VALUE;
    if (source.has("focused")) known_mask |= GUA_NODE_KNOWN_FOCUSED;
    if (source.has("hovered")) known_mask |= GUA_NODE_KNOWN_HOVERED;
    if (source.has("pressed")) known_mask |= GUA_NODE_KNOWN_PRESSED;
    if (source.has("checked")) known_mask |= GUA_NODE_KNOWN_CHECKED;
    if (source.has("selected")) known_mask |= GUA_NODE_KNOWN_SELECTED;
    if (source.has("caret_position")) known_mask |= GUA_NODE_KNOWN_CARET_POSITION;
    if (source.has("selection_start") && source.has("selection_end")) known_mask |= GUA_NODE_KNOWN_SELECTION;
    if (source.has("scroll_x") && source.has("scroll_y")) known_mask |= GUA_NODE_KNOWN_SCROLL;
    if (source.has("scroll_max_x") && source.has("scroll_max_y")) known_mask |= GUA_NODE_KNOWN_SCROLL_MAX;
    if (source.has("range_value")) known_mask |= GUA_NODE_KNOWN_RANGE_VALUE;
    if (source.has("range_min")) known_mask |= GUA_NODE_KNOWN_RANGE_MIN;
    if (source.has("range_max")) known_mask |= GUA_NODE_KNOWN_RANGE_MAX;
    if (source.has("selected_index")) known_mask |= GUA_NODE_KNOWN_SELECTED_INDEX;

    const gua_node_descriptor_v2_t descriptor {
        sizeof(gua_node_descriptor_v2_t),
        known_mask,
        id_utf8.get_data(),
        source.has("parent_id") ? parent_id_utf8.get_data() : nullptr,
        role_utf8.get_data(),
        label_utf8.get_data(),
        source.has("text") ? text_utf8.get_data() : nullptr,
        source.has("value") ? value_utf8.get_data() : nullptr,
        gua_bounds_t {
            static_cast<float>(bounds.position.x), static_cast<float>(bounds.position.y),
            static_cast<float>(bounds.size.x), static_cast<float>(bounds.size.y),
        },
        source.get("visible", true) ? 1 : 0,
        source.get("enabled", true) ? 1 : 0,
        source.get("focused", false) ? 1 : 0,
        source.get("hovered", false) ? 1 : 0,
        source.get("pressed", false) ? 1 : 0,
        source.get("checked", false) ? 1 : 0,
        source.get("selected", false) ? 1 : 0,
    };
    const gua_node_descriptor_v3_t detailed {
        sizeof(gua_node_descriptor_v3_t), descriptor,
        source.get("caret_position", 0), source.get("selection_start", 0), source.get("selection_end", 0),
        source.get("scroll_x", 0.0), source.get("scroll_y", 0.0), source.get("scroll_max_x", 0.0), source.get("scroll_max_y", 0.0),
        source.get("range_value", 0.0), source.get("range_min", 0.0), source.get("range_max", 0.0), source.get("selected_index", -1)
    };
    AgentPolicyStorage policy;
    if (!agent_policy(source, policy)) return false;
    const gua_node_descriptor_v4_t secured { sizeof(gua_node_descriptor_v4_t), detailed, policy.policy };
    return gua_runtime_register_node_v4(runtime_, &secured) != 0;
}

bool GuaContext::begin_world_frame(const String& scene)
{
    const CharString value = scene.utf8();
    return gua_runtime_begin_world_frame(runtime_, value.get_data()) != 0;
}

bool GuaContext::register_world_object(const Dictionary& source)
{
    const auto reject = [this]() { gua_runtime_abort_world_frame(runtime_); return false; };
    if (!source.has("id") || !source.has("kind") || !source.has("label") || !source.has("position") || !source.has("space")) return reject();
    if ((source.has("visible_to_player") && static_cast<Variant>(source["visible_to_player"]).get_type() != Variant::BOOL) ||
        (source.has("active") && static_cast<Variant>(source["active"]).get_type() != Variant::BOOL)) return reject();
    const String id = source["id"], parent = source.get("parent_id", String()), kind = source["kind"], label = source["label"];
    const String description = source.get("description", String()), domain = source.get("domain_id", String()), related = source.get("related_ui_node_id", String());
    const CharString id8 = id.utf8(), parent8 = parent.utf8(), kind8 = kind.utf8(), label8 = label.utf8(), description8 = description.utf8(), domain8 = domain.utf8(), related8 = related.utf8();
    const Vector3 position = source["position"];
    const Array tags = source.get("tags", Array());
    std::vector<CharString> tag_strings; std::vector<const char*> tag_pointers;
    tag_strings.reserve(tags.size()); tag_pointers.reserve(tags.size());
    for (int i = 0; i < tags.size(); ++i) tag_strings.push_back(String(tags[i]).utf8());
    for (const auto& value : tag_strings) tag_pointers.push_back(value.get_data());
    const Dictionary state = source.get("state", Dictionary());
    const Array keys = state.keys();
    std::vector<CharString> state_keys, state_strings; std::vector<gua_world_state_value_v1_t> state_values;
    state_keys.reserve(keys.size()); state_strings.reserve(keys.size()); state_values.reserve(keys.size());
    for (int i = 0; i < keys.size(); ++i) { state_keys.push_back(String(keys[i]).utf8()); state_strings.emplace_back(); }
    for (int i = 0; i < keys.size(); ++i) {
        const Variant value = state[keys[i]]; gua_world_state_value_v1_t item { sizeof(gua_world_state_value_v1_t), state_keys[i].get_data() };
        if (value.get_type() == Variant::NIL) item.type = GUA_WORLD_VALUE_NULL;
        else if (value.get_type() == Variant::BOOL) { item.type = GUA_WORLD_VALUE_BOOLEAN; item.bool_value = static_cast<bool>(value) ? 1 : 0; }
        else if (value.get_type() == Variant::INT) {
            const auto integer = static_cast<std::int64_t>(value);
            if (!exactly_representable_as_double(integer)) return reject();
            item.type = GUA_WORLD_VALUE_NUMBER; item.number_value = static_cast<double>(integer);
        }
        else if (value.get_type() == Variant::FLOAT) { item.type = GUA_WORLD_VALUE_NUMBER; item.number_value = static_cast<double>(value); }
        else if (value.get_type() == Variant::STRING || value.get_type() == Variant::STRING_NAME) { state_strings[i] = String(value).utf8(); item.type = GUA_WORLD_VALUE_STRING; item.string_value = state_strings[i].get_data(); }
        else return reject();
        state_values.push_back(item);
    }
    const String space = source["space"], exposure = source.get("agent_exposure", String("auto"));
    if ((space != "world2d" && space != "world3d") || (exposure != "auto" && exposure != "private")) return reject();
    const gua_world_object_descriptor_v1_t descriptor { sizeof(gua_world_object_descriptor_v1_t), id8.get_data(), parent.is_empty() ? nullptr : parent8.get_data(), kind8.get_data(), label8.get_data(),
        description.is_empty() ? nullptr : description8.get_data(), space == "world3d" ? GUA_WORLD_SPACE_3D : GUA_WORLD_SPACE_2D,
        position.x, position.y, position.z, static_cast<bool>(source.get("visible_to_player", false)) ? 1 : 0,
        static_cast<bool>(source.get("active", true)) ? 1 : 0,
        exposure == "private" ? GUA_AGENT_EXPOSURE_PRIVATE : GUA_AGENT_EXPOSURE_AUTO,
        domain.is_empty() ? nullptr : domain8.get_data(), related.is_empty() ? nullptr : related8.get_data(),
        tag_pointers.data(), static_cast<uint32_t>(tag_pointers.size()), state_values.data(), static_cast<uint32_t>(state_values.size()) };
    AgentPolicyStorage policy;
    Dictionary policy_source = source;
    policy_source["agent_allowed_actions_set"] = source.has("agent_allowed_actions");
    if (!agent_policy(policy_source, policy)) return reject();
    const gua_world_object_descriptor_v2_t secured { sizeof(gua_world_object_descriptor_v2_t), descriptor, policy.policy };
    return gua_runtime_register_world_object_v2(runtime_, &secured) != 0;
}

bool GuaContext::end_world_frame() { return gua_runtime_end_world_frame(runtime_) != 0; }
bool GuaContext::abort_world_frame() { return gua_runtime_abort_world_frame(runtime_) != 0; }
String GuaContext::get_world_object_tree_json() const { return copy_runtime_json(runtime_, gua_runtime_copy_world_object_tree_json); }
String GuaContext::get_player_world_object_tree_json() const { return copy_runtime_json(runtime_, gua_runtime_copy_player_world_object_tree_json); }
String GuaContext::query_world_objects_json(const Dictionary& source) const { return query_world_objects_json_with_projection(source, false); }
String GuaContext::query_player_world_objects_json(const Dictionary& source) const { return query_world_objects_json_with_projection(source, true); }
String GuaContext::query_world_objects_json_with_projection(const Dictionary& source, bool player_projection) const
{
    const String id = source.get("id", String()), kind = source.get("kind", String()), label = source.get("label", String());
    const String tag = source.get("tag", String()), parent = source.get("parent_id", String()), state_key = source.get("state_key", String());
    const Variant relative_value = source.get("relative_to_object_id", String());
    const String relative = relative_value;
    const CharString id8 = id.utf8(), kind8 = kind.utf8(), label8 = label.utf8(), tag8 = tag.utf8(), parent8 = parent.utf8(), state_key8 = state_key.utf8(), relative8 = relative.utf8();
    const int direct_child = source.get("direct_child", 0), visible = source.get("visible_to_player", 0), active = source.get("active", 0);
    const bool has_relative = source.has("relative_to_object_id"), has_max_distance = source.has("max_distance"), has_limit = source.has("limit");
    const Variant max_distance_value = source.get("max_distance", 0.0), limit_value = source.get("limit", 0);
    const bool max_distance_is_number = max_distance_value.get_type() == Variant::INT || max_distance_value.get_type() == Variant::FLOAT;
    const bool limit_is_integer = limit_value.get_type() == Variant::INT;
    const double max_distance = max_distance_value;
    const std::int64_t limit = limit_value;
    if (direct_child < 0 || direct_child > 1 || (direct_child != 0 && parent.is_empty()) || visible < 0 || visible > 2 || active < 0 || active > 2 ||
        has_relative != has_max_distance || (has_relative && (relative_value.get_type() != Variant::STRING || !max_distance_is_number || relative.is_empty() || !std::isfinite(max_distance) || max_distance < 0)) ||
        (has_limit && (!limit_is_integer || !has_relative || limit < 1 || static_cast<std::uint64_t>(limit) > std::numeric_limits<std::uint32_t>::max())))
        return "{\"valid\":false,\"error\":\"invalid_selector\",\"matches\":[]}";

    gua_world_state_value_v1_t state { sizeof(gua_world_state_value_v1_t) };
    CharString state_string8;
    const gua_world_state_value_v1_t* state_pointer = nullptr;
    if (!state_key.is_empty()) {
        state.key = state_key8.get_data();
        state.type = source.get("state_type", GUA_WORLD_VALUE_NULL);
        if (state.type == GUA_WORLD_VALUE_STRING) {
            state_string8 = String(source.get("state_string", String())).utf8();
            state.string_value = state_string8.get_data();
        } else if (state.type == GUA_WORLD_VALUE_NUMBER) {
            state.number_value = source.get("state_number", 0.0);
        } else if (state.type == GUA_WORLD_VALUE_BOOLEAN) {
            state.bool_value = static_cast<bool>(source.get("state_bool", false)) ? 1 : 0;
        } else if (state.type != GUA_WORLD_VALUE_NULL) {
            return "{\"valid\":false,\"error\":\"invalid_selector\",\"matches\":[]}";
        }
        state_pointer = &state;
    }
    const gua_world_selector_v1_t selector {
        sizeof(gua_world_selector_v1_t), id.is_empty() ? nullptr : id8.get_data(), GUA_MATCH_EXACT,
        kind.is_empty() ? nullptr : kind8.get_data(), GUA_MATCH_EXACT,
        label.is_empty() ? nullptr : label8.get_data(), GUA_MATCH_EXACT,
        tag.is_empty() ? nullptr : tag8.get_data(), GUA_MATCH_EXACT,
        parent.is_empty() ? nullptr : parent8.get_data(), direct_child != 0 ? 1 : 0, visible, active, state_pointer,
    };
    const gua_world_near_v1_t near { sizeof(gua_world_near_v1_t), has_relative ? relative8.get_data() : nullptr, max_distance };
    const gua_world_selector_v2_t selector_v2 { sizeof(gua_world_selector_v2_t), selector, has_relative ? &near : nullptr,
        has_limit ? static_cast<std::uint32_t>(limit) : 0U };
    const auto query = player_projection ? gua_runtime_query_player_world_objects_v2_json : gua_runtime_query_world_objects_v2_json;
    int required = query(runtime_, &selector_v2, nullptr, 0);
    if (required <= 0) return "{\"valid\":false,\"error\":\"unsupported\",\"matches\":[]}";
    std::vector<char> json(static_cast<std::size_t>(required));
    required = query(runtime_, &selector_v2, json.data(), static_cast<int>(json.size()));
    return required > 0 ? String::utf8(json.data()) : String("{\"valid\":false,\"error\":\"unsupported\",\"matches\":[]}");
}
void GuaContext::enable_world_object_tree_adapter() { gua_runtime_set_world_object_tree_enabled(runtime_, 1); }

String GuaContext::get_ui_tree_json() const
{
    return copy_runtime_json(runtime_, gua_runtime_copy_ui_tree_json);
}

String GuaContext::get_player_ui_tree_json() const
{
    return copy_runtime_json(runtime_, gua_runtime_copy_player_ui_tree_json);
}

String GuaContext::get_version_json() const
{
    char json[2048] {};
    gua_runtime_copy_version_json(runtime_, json, static_cast<int>(sizeof(json)));
    return String::utf8(json);
}

int GuaContext::get_observation_profile() const
{
    return gua_runtime_get_observation_profile(runtime_);
}

bool GuaContext::enqueue_click(const String& node_id)
{
    const CharString node_id_utf8 = node_id.utf8();
    return gua_runtime_enqueue_click(runtime_, node_id_utf8.get_data()) != 0;
}

bool GuaContext::consume_click_request(const String& node_id)
{
    const CharString node_id_utf8 = node_id.utf8();
    return gua_runtime_consume_click_request(runtime_, node_id_utf8.get_data()) != 0;
}

bool GuaContext::emit_click(const String& node_id)
{
    const CharString node_id_utf8 = node_id.utf8();
    return gua_runtime_emit_click(runtime_, node_id_utf8.get_data()) != 0;
}

Dictionary GuaContext::poll_event()
{
    gua_event_t event {};
    if (gua_runtime_poll_event(runtime_, &event) == 0) {
        return Dictionary();
    }

    Dictionary result;
    result["type"] = event_type_name(event.type);
    result["node_id"] = String::utf8(event.node_id);
    return result;
}

Dictionary GuaContext::enqueue_action(const Dictionary& source)
{
    const String action = source.get("action", String());
    const String node_id = source.get("node_id", String());
    const String value = source.get("value", String());
    const String key = source.get("key", String());
    const CharString node_utf8 = node_id.utf8();
    const CharString value_utf8 = value.utf8();
    const CharString key_utf8 = key.utf8();
    const gua_action_request_descriptor_t request {
        sizeof(gua_action_request_descriptor_t), action_type(action), node_id.is_empty() ? nullptr : node_utf8.get_data(),
        value.is_empty() ? nullptr : value_utf8.get_data(), source.get("delta_x", 0.0), source.get("delta_y", 0.0),
        source.get("bool_value", false) ? 1 : 0, key.is_empty() ? nullptr : key_utf8.get_data(),
        static_cast<uint32_t>(static_cast<int64_t>(source.get("modifiers", 0))), source.get("sensitive", false) ? 1 : 0,
        source.get("scroll_unit", 0)
    };
    uint64_t request_id = 0;
    const int code = gua_runtime_enqueue_action(runtime_, &request, &request_id);
    Dictionary result;
    result["error_code"] = code == GUA_ACTION_ACCEPTED ? 0 : code;
    result["request_id"] = request_id;
    return result;
}

Dictionary GuaContext::enqueue_player_action(const Dictionary& source)
{
    const String action = source.get("action", String());
    const String node_id = source.get("node_id", String());
    const String value = source.get("value", String());
    const String key = source.get("key", String());
    const CharString node_utf8 = node_id.utf8();
    const CharString value_utf8 = value.utf8();
    const CharString key_utf8 = key.utf8();
    const gua_action_request_descriptor_t request {
        sizeof(gua_action_request_descriptor_t), action_type(action), node_id.is_empty() ? nullptr : node_utf8.get_data(),
        value.is_empty() ? nullptr : value_utf8.get_data(), source.get("delta_x", 0.0), source.get("delta_y", 0.0),
        source.get("bool_value", false) ? 1 : 0, key.is_empty() ? nullptr : key_utf8.get_data(),
        static_cast<uint32_t>(static_cast<int64_t>(source.get("modifiers", 0))), source.get("sensitive", false) ? 1 : 0,
        source.get("scroll_unit", 0)
    };
    uint64_t request_id = 0;
    const int code = gua_runtime_enqueue_player_action(runtime_, &request, &request_id);
    Dictionary result;
    result["error_code"] = code == GUA_ACTION_ACCEPTED ? 0 : code;
    result["request_id"] = request_id;
    return result;
}

int GuaContext::cancel_action_request(uint64_t request_id)
{
    if (request_id == 0) return GUA_ACTION_CANCEL_NOT_FOUND;
    return gua_runtime_cancel_action_request(runtime_, request_id);
}

Dictionary GuaContext::consume_action_request(const String& action, const String& node_id)
{
    const CharString node_utf8 = node_id.utf8();
    gua_action_request_t request { sizeof(gua_action_request_t) };
    if (gua_runtime_consume_action_request(runtime_, action_type(action), node_utf8.get_data(), &request) == 0) return Dictionary();
    Dictionary result;
    result["request_id"] = request.request_id;
    result["observation_profile"] = gua_runtime_get_action_request_observation_profile(runtime_, request.request_id);
    result["action"] = action_name(request.action);
    result["node_id"] = String::utf8(request.node_id);
    result["value"] = String::utf8(request.value);
    result["delta_x"] = request.delta_x;
    result["delta_y"] = request.delta_y;
    result["bool_value"] = request.bool_value != 0;
    result["key"] = String::utf8(request.key);
    result["modifiers"] = request.modifiers;
    result["sensitive"] = request.sensitive != 0;
    result["scroll_unit"] = request.scroll_unit;
    return result;
}

bool GuaContext::emit_action_result(const Dictionary& source)
{
    const String node_id = source.get("node_id", String());
    const String value = source.get("value", String());
    const CharString node_utf8 = node_id.utf8();
    const CharString value_utf8 = value.utf8();
    const gua_action_result_t result {
        sizeof(gua_action_result_t), source.get("request_id", 0), action_type(source.get("action", String())),
        source.get("succeeded", true) ? GUA_ACTION_STATUS_SUCCEEDED : GUA_ACTION_STATUS_FAILED,
        source.get("error_code", 0), node_utf8.get_data(), value.is_empty() ? nullptr : value_utf8.get_data(),
        source.get("sensitive", false) ? 1 : 0
    };
    return gua_runtime_emit_action_result(runtime_, &result) != 0;
}

Dictionary GuaContext::poll_event_v2()
{
    gua_event_v2_t event { sizeof(gua_event_v2_t) };
    if (gua_runtime_poll_event_v2(runtime_, &event) == 0) return Dictionary();
    Dictionary result;
    result["request_id"] = event.request_id;
    result["action"] = action_name(event.action);
    result["succeeded"] = event.status == GUA_ACTION_STATUS_SUCCEEDED;
    result["error_code"] = event.error_code;
    result["node_id"] = String::utf8(event.node_id);
    result["value"] = String::utf8(event.value);
    result["sensitive"] = event.sensitive != 0;
    return result;
}

Dictionary GuaContext::poll_action_result(uint64_t request_id)
{
    if (request_id == 0) return Dictionary();
    gua_event_v3_t event { sizeof(gua_event_v3_t), { sizeof(gua_event_v2_t) } };
    if (gua_runtime_poll_event_v3_for_request(runtime_, request_id, &event) == 0) return Dictionary();
    Dictionary result;
    result["requestId"] = event.base.request_id;
    result["action"] = action_name(event.base.action);
    result["succeeded"] = event.base.status == GUA_ACTION_STATUS_SUCCEEDED;
    result["error"] = event.base.error_code;
    result["nodeId"] = String::utf8(event.base.node_id);
    result["value"] = event.base.sensitive != 0 ? String() : String::utf8(event.base.value);
    result["sensitive"] = event.base.sensitive != 0;
    result["sessionEpoch"] = event.session_epoch;
    result["frameSequence"] = event.frame_sequence;
    result["revision"] = event.revision;
    return result;
}

Dictionary GuaContext::get_clock() const
{
    gua_clock_status_t status { sizeof(gua_clock_status_t) }; Dictionary result;
    if (gua_runtime_clock_get_status(runtime_, &status) == 0) return result;
    result["schema_version"] = 1; result["installed"] = status.installed != 0;
    result["state"] = status.paused != 0 ? "paused" : "running"; result["now_ms"] = status.now_ms;
    result["default_step_ms"] = status.default_step_ms; result["pending_ms"] = status.pending_ms; result["generation"] = status.generation;
    return result;
}
Dictionary GuaContext::clock_install(double initial_time_ms, double step_ms) { return clock_result(runtime_, gua_runtime_clock_install(runtime_, initial_time_ms, step_ms)); }
Dictionary GuaContext::clock_pause() { return clock_result(runtime_, gua_runtime_clock_pause(runtime_)); }
Dictionary GuaContext::clock_run_for(double duration_ms, const Variant& step_ms)
{
    const Dictionary status = get_clock();
    const double actual = step_ms.get_type() == Variant::NIL
        ? static_cast<double>(status["default_step_ms"])
        : static_cast<double>(step_ms);
    return clock_result(runtime_, gua_runtime_clock_run_for(runtime_, duration_ms, actual));
}
Dictionary GuaContext::clock_resume() { return clock_result(runtime_, gua_runtime_clock_resume(runtime_)); }
Dictionary GuaContext::clock_advance(double duration_ms) { return clock_result(runtime_, gua_runtime_clock_advance(runtime_, duration_ms)); }
Dictionary GuaContext::consume_clock_step()
{
    Dictionary result; gua_clock_step_t step { sizeof(gua_clock_step_t) };
    if (gua_runtime_clock_consume_step(runtime_, &step) == 0) return result;
    result["delta_ms"] = step.delta_ms; result["final"] = step.final_step != 0; result["generation"] = step.generation;
    return result;
}
Array GuaContext::consume_clock_steps()
{
    Array result; gua_clock_step_t step { sizeof(gua_clock_step_t) };
    while (gua_runtime_clock_consume_step(runtime_, &step) != 0) { Dictionary item; item["delta_ms"] = step.delta_ms; item["final"] = step.final_step != 0; item["generation"] = step.generation; result.push_back(item); step = gua_clock_step_t { sizeof(gua_clock_step_t) }; }
    return result;
}

void GuaContext::enable_virtual_clock_adapter()
{
    gua_runtime_set_virtual_clock_enabled(runtime_, 1);
}

bool GuaContext::publish_game_input_actions(const String& input_context, const Array& actions)
{
    const CharString context_utf8 = input_context.utf8();
    if (gua_runtime_begin_game_input_frame(runtime_, context_utf8.get_data()) == 0) return false;
    for (int index = 0; index < actions.size(); ++index) {
        if (actions[index].get_type() != Variant::DICTIONARY) {
            gua_runtime_abort_game_input_frame(runtime_);
            return false;
        }
        const Dictionary source = actions[index];
        for (const char* field : { "id", "description", "value_type", "risk", "category", "agent_exposure" }) {
            if (source.has(field) && source[field].get_type() != Variant::STRING) {
                gua_runtime_abort_game_input_frame(runtime_);
                return false;
            }
        }
        if ((source.has("aliases") && source["aliases"].get_type() != Variant::ARRAY) ||
            (source.has("tags") && source["tags"].get_type() != Variant::ARRAY)) {
            gua_runtime_abort_game_input_frame(runtime_);
            return false;
        }
        const String id = source.get("id", String());
        const String description = source.get("description", String());
        const String type = source.get("value_type", String("button"));
        const String risk = source.get("risk", String("safe"));
        const String category = source.get("category", String());
        const String exposure = source.get("agent_exposure", String("auto"));
        const String bindings_json = JSON::stringify(source.get("bindings", Array()));
        const CharString id_utf8 = id.utf8(), description_utf8 = description.utf8();
        const CharString bindings_utf8 = bindings_json.utf8(), risk_utf8 = risk.utf8(), category_utf8 = category.utf8();
        if (type != "button" && type != "axis1d" && type != "vector2" && type != "text") {
            gua_runtime_abort_game_input_frame(runtime_);
            return false;
        }
        const int value_type = type == "axis1d" ? GUA_GAME_INPUT_AXIS1D :
            type == "vector2" ? GUA_GAME_INPUT_VECTOR2 : type == "text" ? GUA_GAME_INPUT_TEXT : GUA_GAME_INPUT_BUTTON;
        const bool has_range = source.has("minimum") || source.has("maximum");
        if (has_range && (!source.has("minimum") || !source.has("maximum"))) {
            gua_runtime_abort_game_input_frame(runtime_);
            return false;
        }
        std::vector<CharString> alias_strings, tag_strings;
        std::vector<const char*> alias_pointers, tag_pointers;
        const Array aliases = source.get("aliases", Array()), tags = source.get("tags", Array());
        const auto contains_nul = [](const String& value) {
            for (int64_t position = 0; position < value.length(); ++position) if (value.unicode_at(position) == 0) return true;
            return false;
        };
        if (contains_nul(category)) { gua_runtime_abort_game_input_frame(runtime_); return false; }
        alias_strings.reserve(aliases.size()); tag_strings.reserve(tags.size());
        for (int i = 0; i < aliases.size(); ++i) {
            if (aliases[i].get_type() != Variant::STRING || contains_nul(String(aliases[i]))) { gua_runtime_abort_game_input_frame(runtime_); return false; }
            alias_strings.push_back(String(aliases[i]).utf8());
        }
        for (int i = 0; i < tags.size(); ++i) {
            if (tags[i].get_type() != Variant::STRING || contains_nul(String(tags[i]))) { gua_runtime_abort_game_input_frame(runtime_); return false; }
            tag_strings.push_back(String(tags[i]).utf8());
        }
        for (const auto& value : alias_strings) alias_pointers.push_back(value.get_data());
        for (const auto& value : tag_strings) tag_pointers.push_back(value.get_data());
        const gua_game_input_action_descriptor_v1_t base {
            sizeof(gua_game_input_action_descriptor_v1_t), id_utf8.get_data(), description_utf8.get_data(), value_type,
            source.get("minimum", 0.0), source.get("maximum", 0.0), has_range ? 1 : 0,
            static_cast<bool>(source.get("holdable", false)) ? 1 : 0,
            static_cast<bool>(source.get("active", true)) ? 1 : 0,
            bindings_utf8.get_data(), risk_utf8.get_data(),
            static_cast<bool>(source.get("requires_confirmation", false)) ? 1 : 0
        };
        const gua_game_input_action_descriptor_v2_t descriptor { sizeof(descriptor), base,
            category.is_empty() ? nullptr : category_utf8.get_data(), alias_pointers.empty() ? nullptr : alias_pointers.data(),
            static_cast<uint32_t>(alias_pointers.size()), tag_pointers.empty() ? nullptr : tag_pointers.data(),
            static_cast<uint32_t>(tag_pointers.size()), exposure == "private" ? GUA_AGENT_EXPOSURE_PRIVATE : GUA_AGENT_EXPOSURE_AUTO };
        const String schema = source.has("value_schema") ? JSON::stringify(source["value_schema"]) : String();
        const String examples = source.has("examples") ? JSON::stringify(source["examples"]) : String();
        const CharString schema_utf8 = schema.utf8(), examples_utf8 = examples.utf8();
        const gua_game_input_action_descriptor_v3_t extended { sizeof(extended), descriptor,
            source.has("value_schema") ? schema_utf8.get_data() : nullptr, source.has("examples") ? examples_utf8.get_data() : nullptr };
        const int registered = source.has("value_schema") || source.has("examples")
            ? gua_runtime_register_game_input_action_v3(runtime_, &extended) : gua_runtime_register_game_input_action_v2(runtime_, &descriptor);
        if ((exposure != "auto" && exposure != "private") || registered == 0) {
            gua_runtime_abort_game_input_frame(runtime_);
            return false;
        }
    }
    return gua_runtime_end_game_input_frame(runtime_) != 0;
}

String GuaContext::get_game_input_actions_json_v2(int observation_profile) const
{
    const auto copy = [&](char* output, int size) { return gua_runtime_copy_game_input_actions_json_v2(runtime_, observation_profile, output, size); };
    return copy_json_with_retry(copy);
}

String GuaContext::get_game_input_actions_json() const
{
    return copy_runtime_json(runtime_, gua_runtime_copy_game_input_actions_json);
}

String GuaContext::get_player_game_input_actions_json() const
{
    return copy_runtime_json(runtime_, gua_runtime_copy_player_game_input_actions_json);
}

String GuaContext::find_game_input_actions_json(const Dictionary& selector, int observation_profile) const
{ return find_game_input_actions_json_impl(selector, observation_profile, false); }
String GuaContext::find_game_input_actions_json_v2(const Dictionary& selector, int observation_profile) const
{ return find_game_input_actions_json_impl(selector, observation_profile, true); }
String GuaContext::find_game_input_actions_json_impl(const Dictionary& selector, int observation_profile, bool metadata) const
{
    const auto invalid_selector = []() {
        return String("{\"code\":\"invalid_request\",\"message\":\"Invalid game input selector.\"}");
    };
    for (const char* field : { "id", "query", "context", "category", "value_type" })
        if (selector.has(field) && selector[field].get_type() != Variant::STRING) return invalid_selector();
    if (selector.has("active") && selector["active"].get_type() != Variant::BOOL) return invalid_selector();
    // Godot's JSON parser represents numbers as FLOAT, including integer limits.
    // Check the range before narrowing so large integers cannot wrap into range.
    const Variant limit_value = selector.get("limit", 20);
    if (limit_value.get_type() != Variant::INT && limit_value.get_type() != Variant::FLOAT) return invalid_selector();
    const double numeric_limit = limit_value;
    if (!std::isfinite(numeric_limit) || numeric_limit < 1 || numeric_limit > 100 ||
        std::floor(numeric_limit) != numeric_limit) return invalid_selector();
    const int limit = static_cast<int>(numeric_limit);
    const String id = selector.get("id", String()), query = selector.get("query", String()), context = selector.get("context", String());
    const String category = selector.get("category", String()), value_type = selector.get("value_type", String());
    const auto contains_nul = [](const String& value) {
        for (int64_t index = 0; index < value.length(); ++index) if (value.unicode_at(index) == 0) return true;
        return false;
    };
    if (contains_nul(id) || contains_nul(query) || contains_nul(context) || contains_nul(category)) return invalid_selector();
    const CharString id_utf8 = id.utf8(), query_utf8 = query.utf8(), context_utf8 = context.utf8(), category_utf8 = category.utf8();
    if (selector.has("tags") && selector["tags"].get_type() != Variant::ARRAY) return invalid_selector();
    const Array tags = selector.get("tags", Array());
    if (tags.size() > 16) return invalid_selector();
    std::vector<CharString> tag_strings; std::vector<const char*> tag_pointers;
    for (int i = 0; i < tags.size(); ++i) {
        if (tags[i].get_type() != Variant::STRING || String(tags[i]).is_empty() || contains_nul(String(tags[i]))) return invalid_selector();
        tag_strings.push_back(String(tags[i]).utf8());
    }
    for (const auto& tag : tag_strings) tag_pointers.push_back(tag.get_data());
    const int native_type = value_type == "button" ? GUA_GAME_INPUT_BUTTON : value_type == "axis1d" ? GUA_GAME_INPUT_AXIS1D :
        value_type == "vector2" ? GUA_GAME_INPUT_VECTOR2 : value_type == "text" ? GUA_GAME_INPUT_TEXT : 0;
    if (!value_type.is_empty() && native_type == 0) return invalid_selector();
    int active = GUA_FILTER_ANY;
    if (selector.has("active")) active = static_cast<bool>(selector["active"]) ? GUA_FILTER_TRUE : GUA_FILTER_FALSE;
    gua_game_input_action_selector_v1_t native { sizeof(native), id.is_empty() ? nullptr : id_utf8.get_data(),
        query.is_empty() ? nullptr : query_utf8.get_data(), native_type, active, context.is_empty() ? nullptr : context_utf8.get_data(),
        category.is_empty() ? nullptr : category_utf8.get_data(), tag_pointers.empty() ? nullptr : tag_pointers.data(),
        static_cast<uint32_t>(tag_pointers.size()), static_cast<uint32_t>(limit) };
    const auto copy = [&](char* output, int size) { return metadata ? gua_runtime_query_game_input_actions_json_v2(runtime_, &native, observation_profile, output, size)
        : gua_runtime_query_game_input_actions_json(runtime_, &native, observation_profile, output, size); };
    return copy_json_with_retry(copy);
}

void GuaContext::enable_game_input_adapter(int capabilities, int player_capabilities)
{
    gua_runtime_set_game_input_capabilities(runtime_, static_cast<uint32_t>(capabilities));
    gua_runtime_set_player_game_input_capabilities(runtime_, static_cast<uint32_t>(player_capabilities));
}

int GuaContext::get_game_input_capabilities(int observation_profile) const
{
    return static_cast<int>(gua_runtime_get_game_input_capabilities(runtime_, observation_profile));
}

uint64_t GuaContext::create_game_input_owner()
{
    return gua_runtime_create_game_input_owner(runtime_);
}

bool GuaContext::release_game_input_owner(uint64_t owner_id)
{
    return gua_runtime_release_game_input_owner(runtime_, owner_id) != 0;
}

Dictionary GuaContext::enqueue_game_input(const Dictionary& source)
{
    const String target = source.get("target", String());
    const String value_json = JSON::stringify(source.get("value", Variant()));
    const CharString target_utf8 = target.utf8(), value_utf8 = value_json.utf8();
    const gua_game_input_request_descriptor_v2_t descriptor {
        sizeof(gua_game_input_request_descriptor_v2_t),
        static_cast<uint64_t>(static_cast<int64_t>(source.get("owner_id", 0))),
        source.get("kind", 0), source.get("operation", 0), target_utf8.get_data(), value_utf8.get_data(),
        source.get("x", 0.0), source.get("y", 0.0),
        static_cast<uint32_t>(static_cast<int64_t>(source.get("lease_ms", 5000))),
        source.get("device_index", 0), static_cast<bool>(source.get("sensitive", false)) ? 1 : 0,
        static_cast<bool>(source.get("confirmed", false)) ? 1 : 0
    };
    uint64_t request_id = 0;
    const int observation_profile = source.get("observation_profile", gua_runtime_get_observation_profile(runtime_));
    const int code = gua_runtime_enqueue_game_input_for_profile_v2(runtime_, &descriptor, observation_profile, &request_id);
    Dictionary result;
    result["error_code"] = code == GUA_GAME_INPUT_OK ? 0 : code;
    result["request_id"] = request_id;
    return result;
}

String GuaContext::get_game_input_state_json(uint64_t owner_id) const
{
    char probe = '\0';
    const int required = gua_runtime_copy_game_input_state_json(runtime_, owner_id, &probe, 0);
    if (required <= 0) return String("{}");
    std::vector<char> buffer(static_cast<size_t>(required));
    gua_runtime_copy_game_input_state_json(runtime_, owner_id, buffer.data(), required);
    return String::utf8(buffer.data());
}

String GuaContext::get_game_input_result_json(uint64_t owner_id, uint64_t request_id) const
{
    char probe = '\0';
    const int required = gua_runtime_copy_game_input_result_json(runtime_, owner_id, request_id, &probe, 0);
    if (required <= 0) return String("{}");
    std::vector<char> buffer(static_cast<size_t>(required));
    gua_runtime_copy_game_input_result_json(runtime_, owner_id, request_id, buffer.data(), required);
    return String::utf8(buffer.data());
}

Dictionary GuaContext::consume_game_input_request()
{
    gua_game_input_request_v1_t request { sizeof(gua_game_input_request_v1_t) };
    if (gua_runtime_consume_game_input_request(runtime_, &request) == 0) return Dictionary();
    Dictionary result;
    result["request_id"] = request.request_id;
    result["owner_id"] = request.owner_id;
    result["kind"] = request.kind;
    result["operation"] = request.operation;
    result["target"] = String::utf8(request.target);
    result["value_json"] = String::utf8(request.value_json);
    result["x"] = request.x; result["y"] = request.y;
    result["lease_ms"] = request.lease_ms;
    result["device_index"] = request.device_index;
    result["sensitive"] = request.sensitive != 0;
    return result;
}

bool GuaContext::complete_game_input_request(const Dictionary& result)
{
    return gua_runtime_complete_game_input_request(runtime_, result.get("request_id", 0),
        static_cast<bool>(result.get("succeeded", false)) ? 1 : 0, result.get("error_code", 0)) != 0;
}

int GuaContext::tick_game_input_leases(double elapsed_ms)
{
    return gua_runtime_tick_game_input_leases(runtime_, elapsed_ms);
}

Dictionary GuaContext::get_context_status() const
{
    gua_context_status_t status { sizeof(gua_context_status_t) };
    if (gua_runtime_get_context_status(runtime_, &status) == 0) return Dictionary();
    Dictionary result;
    result["session_epoch"] = status.session_epoch;
    result["frame_sequence"] = status.frame_sequence;
    result["revision"] = status.revision;
    result["node_count"] = status.node_count;
    result["pending_request_count"] = status.pending_request_count;
    result["in_flight_request_count"] = status.in_flight_request_count;
    result["unconsumed_event_count"] = status.unconsumed_event_count;
    result["log_count"] = status.log_count;
    result["has_screenshot"] = status.has_screenshot != 0;
    result["first_pending_action"] = action_name(status.first_pending_action);
    result["first_pending_node_id"] = String::utf8(status.first_pending_node_id);
    result["first_event_action"] = action_name(status.first_event_action);
    result["first_event_node_id"] = String::utf8(status.first_event_node_id);
    result["world_frame_sequence"] = status.world_frame_sequence;
    result["world_revision"] = status.world_revision;
    result["world_object_count"] = status.world_object_count;
    return result;
}

Dictionary GuaContext::reset_context(const Dictionary& source)
{
    const gua_reset_options_t options {
        sizeof(gua_reset_options_t),
        static_cast<uint32_t>(static_cast<int64_t>(source.get("flags", GUA_RESET_DEFAULT_V3))),
        source.get("strict", false) ? 1 : 0,
        static_cast<uint64_t>(static_cast<int64_t>(source.get("expected_session_epoch", 0))),
        GUA_RESET_FLAGS_VERSION_CURRENT,
    };
    gua_reset_report_t report { sizeof(gua_reset_report_t) };
    const int code = gua_runtime_reset_context(runtime_, &options, &report);
    Dictionary result;
    result["result"] = code;
    result["previous_session_epoch"] = report.previous_session_epoch;
    result["session_epoch"] = report.session_epoch;
    result["pending_request_count"] = report.pending_request_count;
    result["in_flight_request_count"] = report.in_flight_request_count;
    result["unconsumed_event_count"] = report.unconsumed_event_count;
    result["discarded_node_count"] = report.discarded_node_count;
    result["discarded_pending_request_count"] = report.discarded_pending_request_count;
    result["discarded_in_flight_request_count"] = report.discarded_in_flight_request_count;
    result["discarded_event_count"] = report.discarded_event_count;
    result["discarded_log_count"] = report.discarded_log_count;
    result["discarded_screenshot"] = report.discarded_screenshot != 0;
    result["first_pending_action"] = action_name(report.first_pending_action);
    result["first_pending_node_id"] = String::utf8(report.first_pending_node_id);
    result["first_event_action"] = action_name(report.first_event_action);
    result["first_event_node_id"] = String::utf8(report.first_event_node_id);
    result["discarded_world_object_count"] = report.discarded_world_object_count;
    return result;
}

bool GuaContext::start_inspector_bridge(int port)
{
    return gua_runtime_start_inspector_bridge(runtime_, port) != 0;
}

void GuaContext::stop_inspector_bridge()
{
    gua_runtime_stop_inspector_bridge(runtime_);
}

bool GuaContext::inspector_bridge_running() const
{
    return gua_runtime_inspector_bridge_running(runtime_) != 0;
}

String GuaContext::inspector_bridge_url() const
{
    return String::utf8(gua_runtime_inspector_bridge_url(runtime_));
}

void GuaContext::publish_inspector_snapshot()
{
    gua_runtime_publish_inspector_snapshot(runtime_);
}

uint64_t GuaContext::create_observe_owner(int source,const String& runtime_id) {
    auto text=runtime_id.utf8(); uint64_t id=0;
    gua_observe_create_owner(gua_runtime_borrow_context(runtime_),source,{text.get_data(),static_cast<uint32_t>(text.length())},&id); return id;
}
int GuaContext::destroy_observe_owner(uint64_t id) { return gua_observe_destroy_owner(gua_runtime_borrow_context(runtime_),id); }
uint64_t GuaContext::register_observe(uint64_t owner,const String& name,bool player,bool sensitive) {
    auto text=name.utf8(); uint64_t id=0;
    gua_observe_registration_v1_t d{sizeof(d),owner,{text.get_data(),static_cast<uint32_t>(text.length())},player ? 1 : 0,sensitive ? 1 : 0};
    gua_observe_register_v1(gua_runtime_borrow_context(runtime_),&d,&id); return id;
}
int GuaContext::unregister_observe(uint64_t id) { return gua_observe_unregister(gua_runtime_borrow_context(runtime_),id); }
int GuaContext::observe_registration_alive(uint64_t id) const { return gua_observe_registration_alive(gua_runtime_borrow_context(runtime_),id); }
int GuaContext::register_value_enum(const String& type,const Array& members) {
    if(!observe_catalog_) observe_catalog_=gua_enum_catalog_create();
    std::vector<CharString> strings; std::vector<gua_value_text_t> texts; auto id=type.utf8();
    for(int i=0;i<members.size();++i) { if(members[i].get_type()!=Variant::STRING) return GUA_VALUE_STRUCTURE; strings.push_back(String(members[i]).utf8()); }
    for(const auto& s:strings) texts.push_back({s.get_data(),static_cast<uint32_t>(s.length())});
    gua_value_error_t error{};
    return gua_enum_catalog_register(observe_catalog_,{id.get_data(),static_cast<uint32_t>(id.length())},texts.data(),static_cast<uint32_t>(texts.size()),&error);
}
int GuaContext::publish_observe_json(uint64_t registration,const String& json,bool stage) {
    auto* context=gua_runtime_borrow_context(runtime_);
    int alive=gua_observe_registration_alive(context,registration); if(alive) return alive;
    auto text=json.utf8(); gua_value_t* value=nullptr; gua_value_error_t error{};
    int code=gua_value_from_json({text.get_data(),static_cast<uint32_t>(text.length())},observe_catalog_,&value,&error);
    int result=gua_observe_publish(context,registration,value,code,stage ? 1 : 0); gua_value_destroy(value); return result;
}
uint64_t GuaContext::create_observe_client(int profile) { return gua_runtime_create_observe_client(runtime_,profile); }
void GuaContext::release_observe_client(uint64_t client) { gua_runtime_release_observe_client(runtime_,client); }
Dictionary GuaContext::observe_command(uint64_t client,int command,uint64_t subscription) {
    Dictionary response; gua_observe_result_t* result=nullptr; uint64_t id=0;
    int code=gua_runtime_observe_command(runtime_,client,command,subscription,&id,&result);
    response["status"]=code; response["subscriptionId"]=id;
    if(!code && result) {
        int n=gua_observe_result_copy_transport_json(result,nullptr,0); std::vector<char> json(static_cast<size_t>(n));
        if(n>0 && gua_observe_result_copy_transport_json(result,json.data(),n)==n) response["json"]=String::utf8(json.data());
        else response["status"]=GUA_OBSERVE_INTERNAL;
    }
    gua_observe_result_destroy(result); return response;
}

void GuaContext::_bind_methods()
{
    ClassDB::bind_method(D_METHOD("create_observe_owner", "source", "runtime_id"), &GuaContext::create_observe_owner);
    ClassDB::bind_method(D_METHOD("destroy_observe_owner", "owner"), &GuaContext::destroy_observe_owner);
    ClassDB::bind_method(D_METHOD("register_observe", "owner", "name", "allow_player", "sensitive"), &GuaContext::register_observe);
    ClassDB::bind_method(D_METHOD("unregister_observe", "registration"), &GuaContext::unregister_observe);
    ClassDB::bind_method(D_METHOD("observe_registration_alive", "registration"), &GuaContext::observe_registration_alive);
    ClassDB::bind_method(D_METHOD("register_value_enum", "enum_type", "members"), &GuaContext::register_value_enum);
    ClassDB::bind_method(D_METHOD("publish_observe_json", "registration", "json", "stage"), &GuaContext::publish_observe_json);
    ClassDB::bind_method(D_METHOD("create_observe_client", "profile"), &GuaContext::create_observe_client);
    ClassDB::bind_method(D_METHOD("release_observe_client", "client"), &GuaContext::release_observe_client);
    ClassDB::bind_method(D_METHOD("observe_command", "client", "command", "subscription"), &GuaContext::observe_command);
    ClassDB::bind_method(D_METHOD("begin_frame", "screen"), &GuaContext::begin_frame);
    ClassDB::bind_method(D_METHOD("end_frame"), &GuaContext::end_frame);
    ClassDB::bind_method(
        D_METHOD("register_node", "id", "role", "label", "bounds", "visible", "enabled"),
        &GuaContext::register_node,
        DEFVAL(true),
        DEFVAL(true));
    ClassDB::bind_method(D_METHOD("register_node_v2", "descriptor"), &GuaContext::register_node_v2);
    ClassDB::bind_method(D_METHOD("begin_world_frame", "scene"), &GuaContext::begin_world_frame);
    ClassDB::bind_method(D_METHOD("register_world_object", "descriptor"), &GuaContext::register_world_object);
    ClassDB::bind_method(D_METHOD("end_world_frame"), &GuaContext::end_world_frame);
    ClassDB::bind_method(D_METHOD("abort_world_frame"), &GuaContext::abort_world_frame);
    ClassDB::bind_method(D_METHOD("get_world_object_tree_json"), &GuaContext::get_world_object_tree_json);
    ClassDB::bind_method(D_METHOD("query_world_objects_json", "selector"), &GuaContext::query_world_objects_json);
    ClassDB::bind_method(D_METHOD("get_player_world_object_tree_json"), &GuaContext::get_player_world_object_tree_json);
    ClassDB::bind_method(D_METHOD("query_player_world_objects_json", "selector"), &GuaContext::query_player_world_objects_json);
    ClassDB::bind_method(D_METHOD("enable_world_object_tree_adapter"), &GuaContext::enable_world_object_tree_adapter);
    ClassDB::bind_method(D_METHOD("get_ui_tree_json"), &GuaContext::get_ui_tree_json);
    ClassDB::bind_method(D_METHOD("get_player_ui_tree_json"), &GuaContext::get_player_ui_tree_json);
    ClassDB::bind_method(D_METHOD("get_version_json"), &GuaContext::get_version_json);
    ClassDB::bind_method(D_METHOD("get_observation_profile"), &GuaContext::get_observation_profile);
    ClassDB::bind_method(D_METHOD("set_screenshot", "data_uri", "width", "height"), &GuaContext::set_screenshot);
    ClassDB::bind_method(D_METHOD("get_screenshot_json"), &GuaContext::get_screenshot_json);
    ClassDB::bind_method(D_METHOD("consume_screenshot_request"), &GuaContext::consume_screenshot_request);
    ClassDB::bind_method(D_METHOD("complete_screenshot_request", "result"), &GuaContext::complete_screenshot_request);
    ClassDB::bind_method(D_METHOD("enqueue_click", "node_id"), &GuaContext::enqueue_click);
    ClassDB::bind_method(D_METHOD("consume_click_request", "node_id"), &GuaContext::consume_click_request);
    ClassDB::bind_method(D_METHOD("emit_click", "node_id"), &GuaContext::emit_click);
    ClassDB::bind_method(D_METHOD("poll_event"), &GuaContext::poll_event);
    ClassDB::bind_method(D_METHOD("enqueue_action", "request"), &GuaContext::enqueue_action);
    ClassDB::bind_method(D_METHOD("enqueue_player_action", "request"), &GuaContext::enqueue_player_action);
    ClassDB::bind_method(D_METHOD("cancel_action_request", "request_id"), &GuaContext::cancel_action_request);
    ClassDB::bind_method(D_METHOD("consume_action_request", "action", "node_id"), &GuaContext::consume_action_request);
    ClassDB::bind_method(D_METHOD("emit_action_result", "result"), &GuaContext::emit_action_result);
    ClassDB::bind_method(D_METHOD("poll_event_v2"), &GuaContext::poll_event_v2);
    ClassDB::bind_method(D_METHOD("poll_action_result", "request_id"), &GuaContext::poll_action_result);
    ClassDB::bind_method(D_METHOD("get_context_status"), &GuaContext::get_context_status);
    ClassDB::bind_method(D_METHOD("reset_context", "options"), &GuaContext::reset_context, DEFVAL(Dictionary()));
    ClassDB::bind_method(D_METHOD("clock_install", "initial_time_ms", "step_ms"), &GuaContext::clock_install, DEFVAL(0.0), DEFVAL(1000.0 / 60.0));
    ClassDB::bind_method(D_METHOD("clock_pause"), &GuaContext::clock_pause);
    ClassDB::bind_method(D_METHOD("clock_run_for", "duration_ms", "step_ms"), &GuaContext::clock_run_for, DEFVAL(Variant()));
    ClassDB::bind_method(D_METHOD("clock_resume"), &GuaContext::clock_resume);
    ClassDB::bind_method(D_METHOD("clock_advance", "duration_ms"), &GuaContext::clock_advance);
    ClassDB::bind_method(D_METHOD("get_clock"), &GuaContext::get_clock);
    ClassDB::bind_method(D_METHOD("consume_clock_step"), &GuaContext::consume_clock_step);
    ClassDB::bind_method(D_METHOD("consume_clock_steps"), &GuaContext::consume_clock_steps);
    ClassDB::bind_method(D_METHOD("enable_virtual_clock_adapter"), &GuaContext::enable_virtual_clock_adapter);
    ClassDB::bind_method(D_METHOD("publish_game_input_actions", "input_context", "actions"), &GuaContext::publish_game_input_actions);
    ClassDB::bind_method(D_METHOD("get_game_input_actions_json_v2", "observation_profile"), &GuaContext::get_game_input_actions_json_v2, DEFVAL(0));
    ClassDB::bind_method(D_METHOD("find_game_input_actions_json_v2", "selector", "observation_profile"), &GuaContext::find_game_input_actions_json_v2, DEFVAL(0));
    ClassDB::bind_method(D_METHOD("get_game_input_actions_json"), &GuaContext::get_game_input_actions_json);
    ClassDB::bind_method(D_METHOD("get_player_game_input_actions_json"), &GuaContext::get_player_game_input_actions_json);
    ClassDB::bind_method(D_METHOD("find_game_input_actions_json", "selector", "observation_profile"), &GuaContext::find_game_input_actions_json, DEFVAL(0));
    ClassDB::bind_method(D_METHOD("enable_game_input_adapter", "capabilities", "player_capabilities"),
        &GuaContext::enable_game_input_adapter, DEFVAL(0));
    ClassDB::bind_method(D_METHOD("get_game_input_capabilities", "observation_profile"),
        &GuaContext::get_game_input_capabilities);
    ClassDB::bind_method(D_METHOD("create_game_input_owner"), &GuaContext::create_game_input_owner);
    ClassDB::bind_method(D_METHOD("release_game_input_owner", "owner_id"), &GuaContext::release_game_input_owner);
    ClassDB::bind_method(D_METHOD("enqueue_game_input", "request"), &GuaContext::enqueue_game_input);
    ClassDB::bind_method(D_METHOD("get_game_input_state_json", "owner_id"), &GuaContext::get_game_input_state_json);
    ClassDB::bind_method(D_METHOD("get_game_input_result_json", "owner_id", "request_id"), &GuaContext::get_game_input_result_json);
    ClassDB::bind_method(D_METHOD("consume_game_input_request"), &GuaContext::consume_game_input_request);
    ClassDB::bind_method(D_METHOD("complete_game_input_request", "result"), &GuaContext::complete_game_input_request);
    ClassDB::bind_method(D_METHOD("tick_game_input_leases", "elapsed_ms"), &GuaContext::tick_game_input_leases);
    ClassDB::bind_method(D_METHOD("start_inspector_bridge", "port"), &GuaContext::start_inspector_bridge, DEFVAL(8765));
    ClassDB::bind_method(D_METHOD("stop_inspector_bridge"), &GuaContext::stop_inspector_bridge);
    ClassDB::bind_method(D_METHOD("inspector_bridge_running"), &GuaContext::inspector_bridge_running);
    ClassDB::bind_method(D_METHOD("inspector_bridge_url"), &GuaContext::inspector_bridge_url);
    ClassDB::bind_method(D_METHOD("publish_inspector_snapshot"), &GuaContext::publish_inspector_snapshot);
}

} // namespace godot

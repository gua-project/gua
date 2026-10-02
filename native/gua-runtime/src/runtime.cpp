#include "gua/runtime.h"
#include "gua/semantic_lint.h"
#include "../../gua-core/src/value_json.hpp"
#include <optional>

#if GUA_RUNTIME_WITH_WS
#include "gua/ws_bridge.hpp"
#endif

#include <cstdio>
#include <cstdlib>
#include <atomic>
#include <chrono>
#include <algorithm>
#include <deque>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include <memory>
#include <map>
#include <mutex>
#include <string>
#include <string_view>
#include <utility>

struct gua_runtime_t {
    gua_spatial_host_t* spatial_host = nullptr;
    gua_spatial_document_t* spatial_grants = nullptr;
    uint64_t spatial_provider = 0, spatial_epoch = 0, next_spatial_client = 1;
    std::unordered_map<uint64_t,uint64_t> spatial_clients;
    struct ObserveClient { int profile; std::unordered_set<uint64_t> subscriptions; };
    uint64_t next_observe_client = 1;
    std::unordered_map<uint64_t, ObserveClient> observe_clients;
    struct ScreenshotBatch {
        uint64_t session_epoch = 0;
        std::vector<uint64_t> request_ids;
    };
    struct GameInputAuthorization {
        int observation_profile = GUA_OBSERVATION_PROFILE_DEBUG;
        uint64_t owner_id = 0;
        bool consumed = false;
    };

    gua_context_t* context = nullptr;
    mutable std::mutex context_mutex;
    mutable std::mutex bridge_mutex;
#if GUA_RUNTIME_WITH_WS
    std::unique_ptr<gua::ws::BridgeServer> bridge;
#endif
    int bridge_port = 0;
    std::string bridge_url;
    std::string ui_tree_json;
    std::string logs_json;
    std::string screenshot_json;
    std::string diagnostics_json;
    std::string world_object_tree_json;
    std::string godot_plugin_version;
    std::map<std::string, std::string> adapter_versions;
    bool virtual_clock_enabled = false;
    uint32_t game_input_capabilities = 0;
    uint32_t player_game_input_capabilities = 0;
    std::unordered_map<uint64_t, GameInputAuthorization> game_input_request_profiles;
    bool world_object_tree_enabled = false;
    bool player_screenshot_enabled = false;
    int observation_profile = GUA_OBSERVATION_PROFILE_DEBUG;
    std::atomic_bool bridge_stopping = false;
    uint64_t next_screenshot_request_id = 1;
    std::deque<gua_screenshot_request_t> screenshot_requests;
    std::unordered_map<uint64_t, ScreenshotBatch> screenshot_batches;
    std::unordered_map<uint64_t, std::string> screenshot_results;
};

std::string escape_json(std::string_view value);
#include "runtime_spatial.inc"

namespace {

bool valid_runtime(gua_runtime_t* runtime)
{
    return runtime != nullptr && runtime->context != nullptr;
}

bool inspector_bridge_running_unlocked(gua_runtime_t* runtime)
{
#if GUA_RUNTIME_WITH_WS
    return runtime->bridge != nullptr && runtime->bridge->running();
#else
    (void)runtime;
    return false;
#endif
}

constexpr uint32_t all_game_input_capabilities =
    GUA_RUNTIME_GAME_INPUT_SEMANTIC | GUA_RUNTIME_GAME_INPUT_KEYBOARD |
    GUA_RUNTIME_GAME_INPUT_POINTER | GUA_RUNTIME_GAME_INPUT_GAMEPAD | GUA_RUNTIME_GAME_INPUT_TEXT;

uint32_t effective_game_input_capabilities(const gua_runtime_t* runtime, int observation_profile)
{
    if (observation_profile == GUA_OBSERVATION_PROFILE_DEBUG) return runtime->game_input_capabilities;
    if (observation_profile == GUA_OBSERVATION_PROFILE_PLAYER)
        return runtime->game_input_capabilities & runtime->player_game_input_capabilities;
    return 0;
}

uint32_t required_game_input_capability(int kind)
{
    switch (kind) {
    case GUA_GAME_INPUT_SEMANTIC: return GUA_RUNTIME_GAME_INPUT_SEMANTIC;
    case GUA_GAME_INPUT_KEYBOARD: return GUA_RUNTIME_GAME_INPUT_KEYBOARD;
    case GUA_GAME_INPUT_POINTER: return GUA_RUNTIME_GAME_INPUT_POINTER;
    case GUA_GAME_INPUT_GAMEPAD: return GUA_RUNTIME_GAME_INPUT_GAMEPAD;
    case GUA_GAME_INPUT_TEXT_INPUT: return GUA_RUNTIME_GAME_INPUT_TEXT;
    case GUA_GAME_INPUT_CLEANUP: return 0;
    default: return UINT32_MAX;
    }
}

int release_game_input_owner_unlocked(gua_runtime_t* runtime, uint64_t owner_id)
{
    const int result = gua_release_game_input_owner(runtime->context, owner_id);
    if (result != 0) {
        std::erase_if(runtime->game_input_request_profiles, [&](const auto& entry) {
            return entry.second.owner_id == owner_id && !entry.second.consumed;
        });
    }
    return result;
}

int copy_json_string(const std::string& json, char* out_json, int out_json_size)
{
    const int required_size = static_cast<int>(json.size() + 1U);
    if (out_json != nullptr && out_json_size > 0) {
        std::snprintf(out_json, static_cast<std::size_t>(out_json_size), "%s", json.c_str());
    }
    return required_size;
}

void remove_capability(std::string& json, std::string_view capability_name)
{
    constexpr std::string_view key = "\"capabilities\":[";
    const std::string capability = "\"" + std::string(capability_name) + "\"";
    const auto key_position = json.find(key);
    if (key_position == std::string::npos) return;
    const auto array_begin = key_position + key.size();
    const auto array_end = json.find(']', array_begin);
    auto position = json.find(capability, array_begin);
    if (array_end == std::string::npos || position == std::string::npos || position >= array_end) return;
    auto size = capability.size();
    if (position > array_begin && json[position - 1] == ',') {
        --position;
        ++size;
    } else if (position + size < array_end && json[position + size] == ',') {
        ++size;
    }
    json.erase(position, size);
}

void filter_runtime_capabilities(std::string& json, bool virtual_clock_enabled, uint32_t game_input_capabilities)
{
    if (!virtual_clock_enabled) remove_capability(json, "virtual_clock_v1");
    if ((game_input_capabilities & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) remove_capability(json, "semantic_game_input_v1");
    if ((game_input_capabilities & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) remove_capability(json, "semantic_game_input_search_v1");
    if ((game_input_capabilities & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) remove_capability(json, "semantic_game_input_metadata_v1");
    if ((game_input_capabilities & GUA_RUNTIME_GAME_INPUT_KEYBOARD) == 0) remove_capability(json, "raw_keyboard_input_v1");
    if ((game_input_capabilities & GUA_RUNTIME_GAME_INPUT_POINTER) == 0) remove_capability(json, "raw_pointer_input_v1");
    if ((game_input_capabilities & GUA_RUNTIME_GAME_INPUT_GAMEPAD) == 0) remove_capability(json, "raw_gamepad_input_v1");
    if ((game_input_capabilities & GUA_RUNTIME_GAME_INPUT_TEXT) == 0) remove_capability(json, "text_input_v1");
    if (game_input_capabilities == 0) remove_capability(json, "game_input_lease_v1");
}

std::string core_version_json()
{
    const int size = gua_copy_version_json(nullptr, 0);
    std::string json(static_cast<std::size_t>(size), '\0');
    gua_copy_version_json(json.data(), size);
    json.resize(static_cast<std::size_t>(size - 1));
    return json;
}

std::string decorate_version_json(gua_runtime_t* runtime, std::string json,std::optional<uint64_t> spatial_client=std::nullopt)
{
    const auto spatial=spatial_advertisement_unlocked(runtime,spatial_client);
    if (!spatial.empty()) {
        const auto at=json.find("\"capabilities\":[");
        if(at!=std::string::npos) json.insert(at+16,"\"spatial_read_r1\",");
        json.insert(json.size()-1,",\"spatial\":"+spatial);
    }
    filter_runtime_capabilities(json, runtime->virtual_clock_enabled,
        effective_game_input_capabilities(runtime, runtime->observation_profile));
    if (!runtime->world_object_tree_enabled) remove_capability(json, "world_object_tree_v1");
    if (!runtime->godot_plugin_version.empty()) {
        const std::string marker = "\"godotPluginVersion\":null";
        const auto position = json.find(marker);
        if (position != std::string::npos)
            json.replace(position, marker.size(), "\"godotPluginVersion\":\"" + escape_json(runtime->godot_plugin_version) + "\"");
    }
    const std::string adapter_marker = "\"adapterVersions\":{}";
    const auto adapter_position = json.find(adapter_marker);
    if (adapter_position != std::string::npos && !runtime->adapter_versions.empty()) {
        std::string adapters = "\"adapterVersions\":{";
        bool first = true;
        for (const auto& [name, version] : runtime->adapter_versions) {
            if (!first) adapters += ',';
            first = false;
            adapters += "\"" + escape_json(name) + "\":\"" + escape_json(version) + "\"";
        }
        adapters += '}';
        json.replace(adapter_position, adapter_marker.size(), adapters);
    }
    return json;
}

std::string copy_ui_tree_json_for_profile(gua_runtime_t* runtime, int profile)
{
    const std::lock_guard lock(runtime->context_mutex);
    const int size = gua_copy_ui_tree_json_for_profile(runtime->context, profile, nullptr, 0);
    std::string json(static_cast<std::size_t>(size), '\0');
    gua_copy_ui_tree_json_for_profile(runtime->context, profile, json.data(), size);
    json.resize(static_cast<std::size_t>(size - 1));
    return json;
}

std::string copy_ui_tree_json(gua_runtime_t* runtime)
{
    return copy_ui_tree_json_for_profile(runtime, runtime->observation_profile);
}

std::string observation_profile_from_environment()
{
#ifdef _MSC_VER
    char* value = nullptr;
    std::size_t size = 0;
    if (_dupenv_s(&value, &size, "GUA_OBSERVATION_PROFILE") != 0 || value == nullptr) return {};
    std::string result(value);
    std::free(value);
    return result;
#else
    const char* value = std::getenv("GUA_OBSERVATION_PROFILE");
    return value == nullptr ? std::string() : std::string(value);
#endif
}

std::string copy_world_object_tree_json_unlocked(gua_runtime_t* runtime, int observation_profile)
{
    const int size = gua_copy_world_object_tree_json(runtime->context, observation_profile, nullptr, 0);
    if (size <= 0) return "{}";
    std::string json(static_cast<std::size_t>(size), '\0');
    gua_copy_world_object_tree_json(runtime->context, observation_profile, json.data(), size);
    json.resize(static_cast<std::size_t>(size - 1));
    return json;
}

std::string copy_world_object_tree_json_unlocked(gua_runtime_t* runtime)
{
    return copy_world_object_tree_json_unlocked(runtime, runtime->observation_profile);
}

std::string copy_world_object_tree_json(gua_runtime_t* runtime)
{
    const std::lock_guard lock(runtime->context_mutex);
    return copy_world_object_tree_json_unlocked(runtime);
}

std::string unsupported_world_object_tree_json(gua_runtime_t* runtime)
{
    gua_context_status_t status { sizeof(gua_context_status_t) };
    const auto session_epoch = gua_get_context_status(runtime->context, &status) != 0 ? status.session_epoch : 1ULL;
    return "{\"schemaVersion\":1,\"sessionEpoch\":" + std::to_string(session_epoch) +
        ",\"frameSequence\":0,\"revision\":0,\"scene\":\"unsupported\",\"objects\":[]}";
}

uint32_t count_json_object_array(std::string_view json, std::string_view key)
{
    const auto key_position = json.find(key);
    if (key_position == std::string_view::npos) return 0;
    const auto array_position = json.find('[', key_position + key.size());
    if (array_position == std::string_view::npos) return 0;
    uint32_t count = 0;
    std::size_t depth = 0;
    bool in_string = false;
    bool escaped = false;
    for (std::size_t i = array_position; i < json.size(); ++i) {
        const char ch = json[i];
        if (in_string) {
            if (escaped) escaped = false;
            else if (ch == '\\') escaped = true;
            else if (ch == '"') in_string = false;
            continue;
        }
        if (ch == '"') { in_string = true; continue; }
        if (ch == '[' || ch == '{') {
            if (ch == '{' && depth == 1) ++count;
            ++depth;
        } else if (ch == ']' || ch == '}') {
            if (depth == 0) return 0;
            --depth;
            if (depth == 0) return count;
        }
    }
    return 0;
}

uint64_t json_unsigned(std::string_view json, std::string_view key)
{
    const auto position = json.find(key);
    if (position == std::string_view::npos) return 0;
    return std::strtoull(json.data() + position + key.size(), nullptr, 10);
}

struct PlayerSummary {
    uint64_t ui_revision = 0;
    uint32_t ui_node_count = 0, pending_count = 0, in_flight_count = 0, event_count = 0;
};

PlayerSummary player_summary_unlocked(gua_runtime_t* runtime)
{
    PlayerSummary result;
    int size = gua_copy_ui_tree_json_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, nullptr, 0);
    std::string ui_json(static_cast<std::size_t>(size), '\0');
    gua_copy_ui_tree_json_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, ui_json.data(), size);
    result.ui_revision = json_unsigned(ui_json, "\"revision\":");
    result.ui_node_count = count_json_object_array(ui_json, "\"nodes\":");
    size = gua_copy_diagnostics_json_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, nullptr, 0);
    std::string diagnostics(static_cast<std::size_t>(size), '\0');
    gua_copy_diagnostics_json_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, diagnostics.data(), size);
    result.pending_count = static_cast<uint32_t>(json_unsigned(diagnostics, "\"pendingRequestCount\":"));
    result.in_flight_count = static_cast<uint32_t>(json_unsigned(diagnostics, "\"inFlightRequestCount\":"));
    result.event_count = static_cast<uint32_t>(json_unsigned(diagnostics, "\"unconsumedEventCount\":"));
    return result;
}

std::string copy_logs_json(gua_runtime_t* runtime)
{
    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER) return "[]";
    return gua_get_logs_json(runtime->context);
}

std::string copy_screenshot_json(gua_runtime_t* runtime)
{
    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && !runtime->player_screenshot_enabled)
        return "{\"dataUri\":\"\",\"width\":0,\"height\":0}";
    return gua_get_screenshot_json(runtime->context);
}

const char* screenshot_unavailable_name(int result)
{
    if (result == GUA_SCREENSHOT_UNAVAILABLE_HEADLESS) return "headless";
    if (result == GUA_SCREENSHOT_UNAVAILABLE_RENDERING_DISABLED) return "rendering_disabled";
    if (result == GUA_SCREENSHOT_UNAVAILABLE_STALE_SESSION) return "stale_session";
    return "unsupported";
}

std::string copy_diagnostics_json(gua_runtime_t* runtime)
{
    const std::lock_guard lock(runtime->context_mutex);
    const int size = gua_copy_diagnostics_json_for_profile(runtime->context, runtime->observation_profile, nullptr, 0);
    std::string json(static_cast<std::size_t>(size), '\0');
    gua_copy_diagnostics_json_for_profile(runtime->context, runtime->observation_profile, json.data(), size);
    json.resize(static_cast<std::size_t>(size - 1));
    const std::string unfiltered_version = core_version_json();
    const std::string decorated_version = decorate_version_json(runtime, unfiltered_version);
    const std::string marker = ",\"version\":" + unfiltered_version + ",\"uiTree\":";
    const auto marker_position = json.rfind(marker);
    if (marker_position != std::string::npos) {
        const auto version_position = marker_position + std::string_view(",\"version\":").size();
        json.replace(version_position, unfiltered_version.size(), decorated_version);
    }
    return json;
}

bool valid_adapter_name(std::string_view adapter)
{
    if (adapter.empty()) return false;
    for (const unsigned char ch : adapter) {
        if ((ch < 'a' || ch > 'z') && (ch < '0' || ch > '9') && ch != '_') return false;
    }
    return true;
}

std::string copy_version_json(gua_runtime_t* runtime)
{
    const std::lock_guard lock(runtime->context_mutex);
    return decorate_version_json(runtime, core_version_json());
}

std::string status_json(gua_runtime_t* runtime)
{
    gua_context_status_t status { sizeof(gua_context_status_t) };
    const std::lock_guard lock(runtime->context_mutex);
    if (gua_get_context_status(runtime->context, &status) == 0) return "null";
    auto ui_revision = status.revision;
    auto ui_node_count = status.node_count;
    auto world_revision = status.world_revision;
    auto world_object_count = status.world_object_count;
    auto pending_request_count = status.pending_request_count;
    auto in_flight_request_count = status.in_flight_request_count;
    auto event_count = status.unconsumed_event_count;
    auto log_count = status.log_count;
    auto has_screenshot = status.has_screenshot != 0;
    auto first_pending_action = status.first_pending_action;
    auto first_event_action = status.first_event_action;
    std::string first_pending_node_id = status.first_pending_node_id;
    std::string first_event_node_id = status.first_event_node_id;
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER) {
        const auto player = player_summary_unlocked(runtime);
        ui_revision = player.ui_revision; ui_node_count = player.ui_node_count;
        pending_request_count = player.pending_count; in_flight_request_count = player.in_flight_count; event_count = player.event_count;
        log_count = 0; has_screenshot = runtime->player_screenshot_enabled && status.has_screenshot != 0;
        first_pending_action = 0; first_event_action = 0; first_pending_node_id.clear(); first_event_node_id.clear();
    }
    if (!runtime->world_object_tree_enabled) {
        world_revision = 0;
        world_object_count = 0;
    } else if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER) {
        const int size = gua_copy_world_object_tree_json(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, nullptr, 0);
        std::string world_json(static_cast<std::size_t>(size), '\0');
        gua_copy_world_object_tree_json(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, world_json.data(), size);
        constexpr std::string_view revision_key = "\"revision\":";
        const auto revision_position = world_json.find(revision_key);
        world_revision = revision_position == std::string::npos ? 0 : std::strtoull(
            world_json.c_str() + revision_position + revision_key.size(), nullptr, 10);
        world_object_count = count_json_object_array(world_json, "\"objects\":");
    }
    return "{\"sessionEpoch\":" + std::to_string(status.session_epoch) +
        ",\"frameSequence\":" + std::to_string(status.frame_sequence) +
        ",\"revision\":" + std::to_string(ui_revision) +
        ",\"nodeCount\":" + std::to_string(ui_node_count) +
        ",\"pendingRequestCount\":" + std::to_string(pending_request_count) +
        ",\"inFlightRequestCount\":" + std::to_string(in_flight_request_count) +
        ",\"unconsumedEventCount\":" + std::to_string(event_count) +
        ",\"logCount\":" + std::to_string(log_count) +
        ",\"hasScreenshot\":" + (has_screenshot ? "true" : "false") +
        ",\"firstPendingAction\":" + std::to_string(first_pending_action) +
        ",\"firstPendingNodeId\":\"" + escape_json(first_pending_node_id) + "\"" +
        ",\"firstEventAction\":" + std::to_string(first_event_action) +
        ",\"firstEventNodeId\":\"" + escape_json(first_event_node_id) + "\"" +
        ",\"worldFrameSequence\":" + std::to_string(status.world_frame_sequence) +
        ",\"worldRevision\":" + std::to_string(world_revision) +
        ",\"worldObjectCount\":" + std::to_string(world_object_count) + "}";
}

std::string stale_screenshot_json(uint64_t request_id, const gua_context_status_t& status)
{
    return "{\"requestId\":" + std::to_string(request_id) +
        ",\"sessionEpoch\":" + std::to_string(status.session_epoch) +
        ",\"frameSequence\":" + std::to_string(status.frame_sequence) +
        ",\"unavailable\":\"stale_session\"}";
}

void invalidate_screenshot_requests(gua_runtime_t* runtime)
{
    gua_context_status_t status { sizeof(gua_context_status_t) };
    if (gua_get_context_status(runtime->context, &status) == 0) return;
    for (const auto& request : runtime->screenshot_requests)
        runtime->screenshot_results[request.request_id] = stale_screenshot_json(request.request_id, status);
    runtime->screenshot_requests.clear();
    for (const auto& [leader, batch] : runtime->screenshot_batches) {
        (void)leader;
        for (const auto request_id : batch.request_ids)
            runtime->screenshot_results[request_id] = stale_screenshot_json(request_id, status);
    }
    runtime->screenshot_batches.clear();
    for (auto& [request_id, result] : runtime->screenshot_results)
        result = stale_screenshot_json(request_id, status);
}

std::string reset_report_json(gua_runtime_t* runtime, unsigned long long expected_epoch, unsigned int flags,
    unsigned int flags_version, bool strict)
{
    gua_reset_options_t options { sizeof(gua_reset_options_t), flags, strict ? 1 : 0, expected_epoch, flags_version };
    gua_reset_report_t report { sizeof(gua_reset_report_t) };
    const std::lock_guard lock(runtime->context_mutex);
    uint32_t projected_world_count = 0;
    PlayerSummary projected_ui;
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER) projected_ui = player_summary_unlocked(runtime);
    if (runtime->world_object_tree_enabled && runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER) {
        const int size = gua_copy_world_object_tree_json(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, nullptr, 0);
        std::string world_json(static_cast<std::size_t>(size), '\0');
        gua_copy_world_object_tree_json(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, world_json.data(), size);
        projected_world_count = count_json_object_array(world_json, "\"objects\":");
    }
    const int result = gua_reset_context(runtime->context, &options, &report);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER) {
        report.pending_request_count = projected_ui.pending_count;
        report.in_flight_request_count = projected_ui.in_flight_count;
        report.unconsumed_event_count = projected_ui.event_count;
        if (report.discarded_node_count != 0) report.discarded_node_count = projected_ui.ui_node_count;
        if (report.discarded_pending_request_count != 0) report.discarded_pending_request_count = projected_ui.pending_count;
        if (report.discarded_in_flight_request_count != 0) report.discarded_in_flight_request_count = projected_ui.in_flight_count;
        if (report.discarded_event_count != 0) report.discarded_event_count = projected_ui.event_count;
        report.discarded_log_count = 0;
        report.discarded_screenshot = runtime->player_screenshot_enabled ? report.discarded_screenshot : 0;
        if (report.discarded_world_object_count != 0) report.discarded_world_object_count = projected_world_count;
        report.first_pending_action = 0; report.first_event_action = 0;
        report.first_pending_node_id[0] = '\0'; report.first_event_node_id[0] = '\0';
    }
    if (result == GUA_RESET_SUCCEEDED) {
        spatial_close_clients_unlocked(runtime);
        invalidate_screenshot_requests(runtime);
        if ((options.flags & GUA_RESET_REQUESTS) != 0) {
            std::erase_if(runtime->game_input_request_profiles,
                [](const auto& entry) { return !entry.second.consumed; });
        }
    }
    return "{\"result\":" + std::to_string(result) +
        ",\"previousSessionEpoch\":" + std::to_string(report.previous_session_epoch) +
        ",\"sessionEpoch\":" + std::to_string(report.session_epoch) +
        ",\"pendingRequestCount\":" + std::to_string(report.pending_request_count) +
        ",\"inFlightRequestCount\":" + std::to_string(report.in_flight_request_count) +
        ",\"unconsumedEventCount\":" + std::to_string(report.unconsumed_event_count) +
        ",\"discardedNodeCount\":" + std::to_string(report.discarded_node_count) +
        ",\"discardedPendingRequestCount\":" + std::to_string(report.discarded_pending_request_count) +
        ",\"discardedInFlightRequestCount\":" + std::to_string(report.discarded_in_flight_request_count) +
        ",\"discardedEventCount\":" + std::to_string(report.discarded_event_count) +
        ",\"discardedLogCount\":" + std::to_string(report.discarded_log_count) +
        ",\"discardedScreenshot\":" + (report.discarded_screenshot != 0 ? "true" : "false") +
        ",\"firstPendingAction\":" + std::to_string(report.first_pending_action) +
        ",\"firstPendingNodeId\":\"" + escape_json(report.first_pending_node_id) + "\"" +
        ",\"firstEventAction\":" + std::to_string(report.first_event_action) +
        ",\"firstEventNodeId\":\"" + escape_json(report.first_event_node_id) + "\"" +
        ",\"discardedWorldObjectCount\":" + std::to_string(report.discarded_world_object_count) + "}";
}

} // namespace

extern "C" gua_runtime_t* gua_runtime_create(void)
{
    auto runtime = std::make_unique<gua_runtime_t>();
    runtime->context = gua_create_context();
    if (runtime->context == nullptr) {
        return nullptr;
    }
    if (observation_profile_from_environment() == "player")
        runtime->observation_profile = GUA_OBSERVATION_PROFILE_PLAYER;

    return runtime.release();
}

extern "C" void gua_runtime_destroy(gua_runtime_t* runtime)
{
    if (runtime == nullptr) {
        return;
    }

    gua_runtime_stop_inspector_bridge(runtime);
    {
        const std::lock_guard lock(runtime->context_mutex);
        spatial_clear_unlocked(runtime);
        gua_destroy_context(runtime->context);
        runtime->context = nullptr;
    }

    delete runtime;
}

extern "C" gua_context_t* gua_runtime_borrow_context(gua_runtime_t* runtime) {
    return valid_runtime(runtime) ? runtime->context : nullptr;
}
extern "C" uint64_t gua_runtime_create_observe_client(gua_runtime_t* r,int profile) {
    if(!valid_runtime(r) || profile<0 || profile>1) return 0;
    try { std::lock_guard lock(r->context_mutex);
        if(r->observation_profile==GUA_OBSERVATION_PROFILE_PLAYER && profile==GUA_OBSERVATION_PROFILE_DEBUG) return 0;
        auto id=r->next_observe_client++;
        r->observe_clients.emplace(id,gua_runtime_t::ObserveClient{profile,{}}); return id;
    } catch(...) { return 0; }
}
extern "C" void gua_runtime_release_observe_client(gua_runtime_t* r,uint64_t client) {
    if(!valid_runtime(r)) return;
    try { std::lock_guard lock(r->context_mutex); auto i=r->observe_clients.find(client);
        if(i==r->observe_clients.end()) return;
        for(auto id:i->second.subscriptions) gua_observe_unsubscribe(r->context,id);
        r->observe_clients.erase(i);
    } catch(...) {}
}
extern "C" int gua_runtime_observe_command(gua_runtime_t* r,uint64_t client,int command,uint64_t subscription,
    uint64_t* out_subscription,gua_observe_result_t** result) {
    if(result) *result=nullptr; if(out_subscription) *out_subscription=0;
    if(!valid_runtime(r) || !result || !out_subscription || command<1 || command>4 ||
        ((command>=3)!=(subscription!=0))) return GUA_OBSERVE_ARGUMENT;
    try { std::lock_guard lock(r->context_mutex); auto i=r->observe_clients.find(client);
        if(i==r->observe_clients.end()) return GUA_OBSERVE_STALE;
        auto& c=i->second;
        if(r->observation_profile==GUA_OBSERVATION_PROFILE_PLAYER && c.profile==GUA_OBSERVATION_PROFILE_DEBUG) {
            for(auto id:c.subscriptions) gua_observe_unsubscribe(r->context,id);
            c.subscriptions.clear(); return GUA_OBSERVE_STALE;
        }
        if(command>=3 && !c.subscriptions.count(subscription)) return GUA_OBSERVE_STALE;
        if(command==GUA_OBSERVE_SNAPSHOT) return gua_observe_snapshot(r->context,c.profile,result);
        if(command==GUA_OBSERVE_SUBSCRIBE) {
            if(c.subscriptions.size()>=64) return GUA_OBSERVE_ARGUMENT;
            uint64_t id=0; int status=gua_observe_subscribe(r->context,c.profile,&id,result);
            if(status) return status;
            try { c.subscriptions.insert(id); }
            catch(...) { gua_observe_unsubscribe(r->context,id); gua_observe_result_destroy(*result); *result=nullptr; throw; }
            *out_subscription=id; return 0;
        }
        if(command==GUA_OBSERVE_POLL) return gua_observe_poll(r->context,subscription,result);
        int status=gua_observe_unsubscribe(r->context,subscription); c.subscriptions.erase(subscription); return status;
    } catch(...) { return GUA_OBSERVE_INTERNAL; }
}

extern "C" void gua_runtime_begin_frame(gua_runtime_t* runtime, const char* screen)
{
    if (!valid_runtime(runtime)) {
        return;
    }

    const std::lock_guard lock(runtime->context_mutex);
    gua_begin_frame(runtime->context, screen);
}

extern "C" void gua_runtime_end_frame(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) {
        return;
    }

    {
        const std::lock_guard lock(runtime->context_mutex);
        gua_end_frame(runtime->context);
    }

    gua_runtime_publish_inspector_snapshot(runtime);
}

extern "C" void gua_runtime_register_node(
    gua_runtime_t* runtime,
    const char* id,
    const char* role,
    const char* label,
    gua_bounds_t bounds,
    int visible,
    int enabled)
{
    if (!valid_runtime(runtime)) {
        return;
    }

    const std::lock_guard lock(runtime->context_mutex);
    gua_register_node(runtime->context, id, role, label, bounds, visible, enabled);
}

extern "C" int gua_runtime_register_node_v2(gua_runtime_t* runtime, const gua_node_descriptor_v2_t* descriptor)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_node_v2(runtime->context, descriptor);
}

extern "C" int gua_runtime_register_node_v3(gua_runtime_t* runtime, const gua_node_descriptor_v3_t* descriptor)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_node_v3(runtime->context, descriptor);
}

extern "C" int gua_runtime_register_node_v4(gua_runtime_t* runtime, const gua_node_descriptor_v4_t* descriptor)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_node_v4(runtime->context, descriptor);
}

extern "C" int gua_runtime_begin_world_frame(gua_runtime_t* runtime, const char* scene)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_begin_world_frame(runtime->context, scene);
}

extern "C" int gua_runtime_register_world_object_v1(gua_runtime_t* runtime, const gua_world_object_descriptor_v1_t* descriptor)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_world_object_v1(runtime->context, descriptor);
}

extern "C" int gua_runtime_register_world_object_v2(gua_runtime_t* runtime, const gua_world_object_descriptor_v2_t* descriptor)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_world_object_v2(runtime->context, descriptor);
}

extern "C" int gua_runtime_end_world_frame(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return 0;
    int result;
    { const std::lock_guard lock(runtime->context_mutex); result = gua_end_world_frame(runtime->context); }
    if (result != 0) gua_runtime_publish_inspector_snapshot(runtime);
    return result;
}

extern "C" int gua_runtime_abort_world_frame(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_abort_world_frame(runtime->context);
}

extern "C" int gua_runtime_copy_world_object_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (!runtime->world_object_tree_enabled) {
        return copy_json_string(unsupported_world_object_tree_json(runtime), out_json, out_json_size);
    }
    return copy_json_string(copy_world_object_tree_json_unlocked(runtime), out_json, out_json_size);
}

extern "C" int gua_runtime_query_world_objects_json(gua_runtime_t* runtime, const gua_world_selector_v1_t* selector, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (!runtime->world_object_tree_enabled) return copy_json_string("{\"valid\":false,\"error\":\"unsupported\",\"matches\":[]}", out_json, out_json_size);
    return gua_query_world_objects_json(runtime->context, selector, runtime->observation_profile, out_json, out_json_size);
}

extern "C" int gua_runtime_query_world_objects_v2_json(gua_runtime_t* runtime, const gua_world_selector_v2_t* selector, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (!runtime->world_object_tree_enabled) return copy_json_string("{\"valid\":false,\"error\":\"unsupported\",\"matches\":[]}", out_json, out_json_size);
    return gua_query_world_objects_v2_json(runtime->context, selector, runtime->observation_profile, out_json, out_json_size);
}

extern "C" int gua_runtime_copy_player_world_object_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (!runtime->world_object_tree_enabled) {
        return copy_json_string(unsupported_world_object_tree_json(runtime), out_json, out_json_size);
    }
    return copy_json_string(copy_world_object_tree_json_unlocked(runtime, GUA_OBSERVATION_PROFILE_PLAYER), out_json, out_json_size);
}

extern "C" int gua_runtime_query_player_world_objects_json(gua_runtime_t* runtime, const gua_world_selector_v1_t* selector, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (!runtime->world_object_tree_enabled) return copy_json_string("{\"valid\":false,\"error\":\"unsupported\",\"matches\":[]}", out_json, out_json_size);
    return gua_query_world_objects_json(runtime->context, selector, GUA_OBSERVATION_PROFILE_PLAYER, out_json, out_json_size);
}

extern "C" int gua_runtime_query_player_world_objects_v2_json(gua_runtime_t* runtime, const gua_world_selector_v2_t* selector, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (!runtime->world_object_tree_enabled) return copy_json_string("{\"valid\":false,\"error\":\"unsupported\",\"matches\":[]}", out_json, out_json_size);
    return gua_query_world_objects_v2_json(runtime->context, selector, GUA_OBSERVATION_PROFILE_PLAYER, out_json, out_json_size);
}

extern "C" const char* gua_runtime_get_ui_tree_json(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) {
        return "{}";
    }

    runtime->ui_tree_json = copy_ui_tree_json(runtime);
    return runtime->ui_tree_json.c_str();
}

extern "C" int gua_runtime_copy_ui_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) {
        return copy_json_string("{}", out_json, out_json_size);
    }

    return copy_json_string(copy_ui_tree_json(runtime), out_json, out_json_size);
}

extern "C" int gua_runtime_copy_player_ui_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (runtime == nullptr) return 0;
    return copy_json_string(
        copy_ui_tree_json_for_profile(runtime, GUA_OBSERVATION_PROFILE_PLAYER), out_json, out_json_size);
}

extern "C" void gua_runtime_add_log(gua_runtime_t* runtime, int level, const char* message)
{
    if (!valid_runtime(runtime)) {
        return;
    }

    const std::lock_guard lock(runtime->context_mutex);
    gua_add_log(runtime->context, level, message);
}

extern "C" const char* gua_runtime_get_logs_json(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) {
        return "[]";
    }

    runtime->logs_json = copy_logs_json(runtime);
    return runtime->logs_json.c_str();
}

extern "C" int gua_runtime_copy_logs_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) {
        return copy_json_string("[]", out_json, out_json_size);
    }

    return copy_json_string(copy_logs_json(runtime), out_json, out_json_size);
}

extern "C" void gua_runtime_set_screenshot(gua_runtime_t* runtime, const char* data_uri, int width, int height)
{
    if (!valid_runtime(runtime)) {
        return;
    }

    const std::lock_guard lock(runtime->context_mutex);
    gua_set_screenshot(runtime->context, data_uri, width, height);
}

extern "C" const char* gua_runtime_get_screenshot_json(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) {
        return "{\"dataUri\":\"\",\"width\":0,\"height\":0}";
    }

    runtime->screenshot_json = copy_screenshot_json(runtime);
    return runtime->screenshot_json.c_str();
}

extern "C" int gua_runtime_copy_screenshot_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) {
        return copy_json_string("{\"dataUri\":\"\",\"width\":0,\"height\":0}", out_json, out_json_size);
    }

    return copy_json_string(copy_screenshot_json(runtime), out_json, out_json_size);
}

extern "C" int gua_runtime_enqueue_screenshot_request(gua_runtime_t* runtime, uint64_t after_frame_sequence, uint64_t* out_request_id)
{
    if (!valid_runtime(runtime) || out_request_id == nullptr) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && !runtime->player_screenshot_enabled) return 0;
    gua_context_status_t status { sizeof(gua_context_status_t) };
    if (gua_get_context_status(runtime->context, &status) == 0) return 0;
    const uint64_t id = runtime->next_screenshot_request_id++;
    runtime->screenshot_requests.push_back({ sizeof(gua_screenshot_request_t), id, status.session_epoch, after_frame_sequence });
    *out_request_id = id;
    return 1;
}

extern "C" int gua_runtime_consume_screenshot_request(gua_runtime_t* runtime, gua_screenshot_request_t* out_request)
{
    if (!valid_runtime(runtime) || out_request == nullptr || out_request->struct_size < sizeof(gua_screenshot_request_t)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    gua_context_status_t status { sizeof(gua_context_status_t) };
    gua_get_context_status(runtime->context, &status);
    while (!runtime->screenshot_requests.empty() && runtime->screenshot_requests.front().session_epoch != status.session_epoch) {
        const auto stale = runtime->screenshot_requests.front();
        runtime->screenshot_requests.pop_front();
        runtime->screenshot_results[stale.request_id] = "{\"requestId\":" + std::to_string(stale.request_id) +
            ",\"sessionEpoch\":" + std::to_string(status.session_epoch) +
            ",\"frameSequence\":" + std::to_string(status.frame_sequence) + ",\"unavailable\":\"stale_session\"}";
    }
    const auto first_ready = std::find_if(runtime->screenshot_requests.begin(), runtime->screenshot_requests.end(),
        [&status](const auto& pending) {
            return pending.session_epoch == status.session_epoch && status.frame_sequence > pending.after_frame_sequence;
        });
    if (first_ready == runtime->screenshot_requests.end()) return 0;
    auto first = *first_ready;
    uint64_t after = first.after_frame_sequence;
    std::vector<uint64_t> batch;
    for (auto pending = runtime->screenshot_requests.begin(); pending != runtime->screenshot_requests.end();) {
        if (pending->session_epoch == first.session_epoch && status.frame_sequence > pending->after_frame_sequence) {
            batch.push_back(pending->request_id);
            after = std::max(after, pending->after_frame_sequence);
            pending = runtime->screenshot_requests.erase(pending);
        } else {
            ++pending;
        }
    }
    first.after_frame_sequence = after;
    runtime->screenshot_batches[first.request_id] = { first.session_epoch, std::move(batch) };
    *out_request = first;
    return 1;
}

extern "C" int gua_runtime_complete_screenshot_request(gua_runtime_t* runtime, uint64_t request_id, int result, const char* data_uri, int width, int height)
{
    if (!valid_runtime(runtime) || request_id == 0) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    const auto batch = runtime->screenshot_batches.find(request_id);
    if (batch == runtime->screenshot_batches.end()) return 0;
    gua_context_status_t status { sizeof(gua_context_status_t) };
    gua_get_context_status(runtime->context, &status);
    const bool stale_session = batch->second.session_epoch != status.session_epoch;
    if (!stale_session && result == GUA_SCREENSHOT_AVAILABLE) gua_set_screenshot(runtime->context, data_uri, width, height);
    for (const auto id : batch->second.request_ids) {
        if (stale_session) {
            runtime->screenshot_results[id] = "{\"requestId\":" + std::to_string(id) +
                ",\"sessionEpoch\":" + std::to_string(status.session_epoch) +
                ",\"frameSequence\":" + std::to_string(status.frame_sequence) + ",\"unavailable\":\"stale_session\"}";
        } else if (result == GUA_SCREENSHOT_AVAILABLE) {
            runtime->screenshot_results[id] = "{\"requestId\":" + std::to_string(id) +
                ",\"sessionEpoch\":" + std::to_string(status.session_epoch) +
                ",\"frameSequence\":" + std::to_string(status.frame_sequence) +
                ",\"width\":" + std::to_string(std::max(0, width)) + ",\"height\":" + std::to_string(std::max(0, height)) +
                ",\"dataUri\":\"" + escape_json(data_uri == nullptr ? "" : data_uri) + "\"}";
        } else {
            runtime->screenshot_results[id] = "{\"requestId\":" + std::to_string(id) +
                ",\"sessionEpoch\":" + std::to_string(status.session_epoch) +
                ",\"frameSequence\":" + std::to_string(status.frame_sequence) +
                ",\"unavailable\":\"" + screenshot_unavailable_name(result) + "\"}";
        }
    }
    runtime->screenshot_batches.erase(batch);
    return 1;
}

extern "C" int gua_runtime_poll_screenshot_result_json(gua_runtime_t* runtime, uint64_t request_id, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime) || request_id == 0) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    const auto found = runtime->screenshot_results.find(request_id);
    if (found == runtime->screenshot_results.end()) return 0;
    const int size = copy_json_string(found->second, out_json, out_json_size);
    if (out_json != nullptr && out_json_size >= size) runtime->screenshot_results.erase(found);
    return size;
}

extern "C" int gua_runtime_cancel_screenshot_request(gua_runtime_t* runtime, uint64_t request_id)
{
    if (!valid_runtime(runtime) || request_id == 0) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    bool removed = runtime->screenshot_results.erase(request_id) != 0;
    const auto before = runtime->screenshot_requests.size();
    runtime->screenshot_requests.erase(
        std::remove_if(runtime->screenshot_requests.begin(), runtime->screenshot_requests.end(),
            [request_id](const auto& request) { return request.request_id == request_id; }),
        runtime->screenshot_requests.end());
    removed = removed || before != runtime->screenshot_requests.size();
    for (auto batch_entry = runtime->screenshot_batches.begin(); batch_entry != runtime->screenshot_batches.end();) {
        auto& batch = batch_entry->second;
        const auto batch_before = batch.request_ids.size();
        batch.request_ids.erase(std::remove(batch.request_ids.begin(), batch.request_ids.end(), request_id), batch.request_ids.end());
        removed = removed || batch_before != batch.request_ids.size();
        if (batch.request_ids.empty()) batch_entry = runtime->screenshot_batches.erase(batch_entry);
        else ++batch_entry;
    }
    return removed ? 1 : 0;
}

extern "C" int gua_runtime_set_diagnostics_history_limit(gua_runtime_t* runtime, uint32_t history_limit)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_set_diagnostics_history_limit(runtime->context, history_limit);
}

extern "C" int gua_runtime_set_diagnostics_environment_json(gua_runtime_t* runtime, const char* environment_json)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_set_diagnostics_environment_json(runtime->context, environment_json);
}

extern "C" const char* gua_runtime_get_diagnostics_json(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return "{}";
    runtime->diagnostics_json = copy_diagnostics_json(runtime);
    return runtime->diagnostics_json.c_str();
}

extern "C" int gua_runtime_copy_diagnostics_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return copy_json_string("{}", out_json, out_json_size);
    return copy_json_string(copy_diagnostics_json(runtime), out_json, out_json_size);
}

extern "C" int gua_runtime_copy_version_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return copy_json_string("{}", out_json, out_json_size);
    return copy_json_string(copy_version_json(runtime), out_json, out_json_size);
}

extern "C" int gua_runtime_clock_install(gua_runtime_t* runtime, double initial_time_ms, double step_ms)
{
    if (!valid_runtime(runtime)) return GUA_CLOCK_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_install(runtime->context, initial_time_ms, step_ms);
}

extern "C" int gua_runtime_clock_pause(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return GUA_CLOCK_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_pause(runtime->context);
}

extern "C" int gua_runtime_clock_run_for(gua_runtime_t* runtime, double duration_ms, double step_ms)
{
    if (!valid_runtime(runtime)) return GUA_CLOCK_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_run_for(runtime->context, duration_ms, step_ms);
}

extern "C" int gua_runtime_clock_resume(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return GUA_CLOCK_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_resume(runtime->context);
}

extern "C" int gua_runtime_clock_advance(gua_runtime_t* runtime, double duration_ms)
{
    if (!valid_runtime(runtime)) return GUA_CLOCK_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_advance(runtime->context, duration_ms);
}

extern "C" int gua_runtime_clock_get_status(gua_runtime_t* runtime, gua_clock_status_t* out_status)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_get_status(runtime->context, out_status);
}

extern "C" int gua_runtime_clock_copy_status_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return copy_json_string("{}", out_json, out_json_size);
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_copy_status_json(runtime->context, out_json, out_json_size);
}

extern "C" int gua_runtime_clock_consume_step(gua_runtime_t* runtime, gua_clock_step_t* out_step)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_clock_consume_step(runtime->context, out_step);
}

extern "C" void gua_runtime_set_godot_plugin_version(gua_runtime_t* runtime, const char* version)
{
    if (!valid_runtime(runtime)) return;
    const std::lock_guard lock(runtime->context_mutex);
    runtime->godot_plugin_version = version == nullptr ? "" : version;
    if (runtime->godot_plugin_version.empty()) runtime->adapter_versions.erase("godot");
    else runtime->adapter_versions["godot"] = runtime->godot_plugin_version;
}

extern "C" void gua_runtime_set_adapter_version(gua_runtime_t* runtime, const char* adapter, const char* version)
{
    if (!valid_runtime(runtime) || adapter == nullptr || !valid_adapter_name(adapter)) return;
    const std::lock_guard lock(runtime->context_mutex);
    if (version == nullptr || version[0] == '\0') runtime->adapter_versions.erase(adapter);
    else runtime->adapter_versions[adapter] = version;
}

extern "C" void gua_runtime_set_virtual_clock_enabled(gua_runtime_t* runtime, int enabled)
{
    if (!valid_runtime(runtime)) return;
    const std::lock_guard lock(runtime->context_mutex);
    runtime->virtual_clock_enabled = enabled != 0;
}

extern "C" void gua_runtime_set_game_input_capabilities(gua_runtime_t* runtime, uint32_t capabilities)
{
    if (!valid_runtime(runtime)) return;
    const std::lock_guard lock(runtime->context_mutex);
    runtime->game_input_capabilities = capabilities & all_game_input_capabilities;
    runtime->player_game_input_capabilities &= runtime->game_input_capabilities;
}

extern "C" void gua_runtime_set_player_game_input_capabilities(gua_runtime_t* runtime, uint32_t capabilities)
{
    if (!valid_runtime(runtime)) return;
    const std::lock_guard lock(runtime->context_mutex);
    runtime->player_game_input_capabilities = capabilities & runtime->game_input_capabilities & all_game_input_capabilities;
}

extern "C" uint32_t gua_runtime_get_game_input_capabilities(gua_runtime_t* runtime, int observation_profile)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return effective_game_input_capabilities(runtime, observation_profile);
}

extern "C" int gua_runtime_begin_game_input_frame(gua_runtime_t* runtime, const char* input_context)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_begin_game_input_frame(runtime->context, input_context);
}

extern "C" int gua_runtime_register_game_input_action_v1(gua_runtime_t* runtime, const gua_game_input_action_descriptor_v1_t* descriptor)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_game_input_action_v1(runtime->context, descriptor);
}

int copy_game_input_result_unlocked(gua_runtime_t* runtime, uint64_t owner_id, uint64_t request_id,
    char* out_json, int out_json_size)
{
    const int required_size = gua_copy_game_input_result_json(runtime->context, owner_id, request_id, out_json, out_json_size);
    if (required_size > 0 && out_json != nullptr && out_json_size >= required_size &&
        std::string_view(out_json, static_cast<std::size_t>(required_size - 1)).starts_with("{\"completed\":true"))
        runtime->game_input_request_profiles.erase(request_id);
    return required_size;
}

extern "C" int gua_runtime_register_game_input_action_v3(gua_runtime_t* runtime, const gua_game_input_action_descriptor_v3_t* descriptor)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_game_input_action_v3(runtime->context, descriptor);
}

extern "C" int gua_runtime_register_game_input_action_v2(gua_runtime_t* runtime, const gua_game_input_action_descriptor_v2_t* descriptor)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_register_game_input_action_v2(runtime->context, descriptor);
}

extern "C" int gua_runtime_end_game_input_frame(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_end_game_input_frame(runtime->context);
}

extern "C" int gua_runtime_abort_game_input_frame(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_abort_game_input_frame(runtime->context);
}

extern "C" uint64_t gua_runtime_create_game_input_owner(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_create_game_input_owner(runtime->context);
}

extern "C" int gua_runtime_release_game_input_owner(gua_runtime_t* runtime, uint64_t owner_id)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return release_game_input_owner_unlocked(runtime, owner_id);
}

extern "C" int gua_runtime_enqueue_game_input(gua_runtime_t* runtime,
    const gua_game_input_request_descriptor_v1_t* descriptor, uint64_t* out_request_id)
{
    if (!valid_runtime(runtime)) return GUA_GAME_INPUT_ERROR_INVALID_ARGUMENT;
    if (descriptor == nullptr) return GUA_GAME_INPUT_ERROR_INVALID_ARGUMENT;
    gua_game_input_request_descriptor_v2_t upgraded { sizeof(upgraded), descriptor->owner_id, descriptor->kind,
        descriptor->operation, descriptor->target, descriptor->value_json, descriptor->x, descriptor->y,
        descriptor->lease_ms, descriptor->device_index, descriptor->sensitive, 0 };
    return gua_runtime_enqueue_game_input_for_profile_v2(runtime, &upgraded, runtime->observation_profile, out_request_id);
}

extern "C" int gua_runtime_enqueue_game_input_v2(gua_runtime_t* runtime,
    const gua_game_input_request_descriptor_v2_t* descriptor, uint64_t* out_request_id)
{
    return gua_runtime_enqueue_game_input_for_profile_v2(runtime, descriptor,
        valid_runtime(runtime) ? runtime->observation_profile : GUA_OBSERVATION_PROFILE_DEBUG, out_request_id);
}

extern "C" int gua_runtime_enqueue_game_input_for_profile_v2(gua_runtime_t* runtime,
    const gua_game_input_request_descriptor_v2_t* descriptor, int observation_profile, uint64_t* out_request_id)
{
    if (!valid_runtime(runtime) || descriptor == nullptr ||
        (observation_profile != GUA_OBSERVATION_PROFILE_DEBUG && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER))
        return GUA_GAME_INPUT_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER)
        return GUA_GAME_INPUT_ERROR_UNSUPPORTED;
    const uint32_t required = required_game_input_capability(descriptor->kind);
    const uint32_t available = effective_game_input_capabilities(runtime, observation_profile);
    if (required == UINT32_MAX || (required != 0 && (available & required) == 0))
        return GUA_GAME_INPUT_ERROR_UNSUPPORTED;
    uint64_t request_id = 0;
    const int result = gua_enqueue_game_input_for_profile_v2(runtime->context, descriptor, observation_profile, &request_id);
    if (result == GUA_GAME_INPUT_OK) {
        runtime->game_input_request_profiles[request_id] = { observation_profile, descriptor->owner_id, false };
        if (out_request_id != nullptr) *out_request_id = request_id;
    }
    return result;
}

extern "C" int gua_runtime_consume_game_input_request(gua_runtime_t* runtime, gua_game_input_request_v1_t* out_request)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    while (gua_consume_game_input_request(runtime->context, out_request) != 0) {
        const auto profile = runtime->game_input_request_profiles.find(out_request->request_id);
        const bool internal_cleanup = profile == runtime->game_input_request_profiles.end();
        const int observation_profile = profile == runtime->game_input_request_profiles.end()
            ? runtime->observation_profile : profile->second.observation_profile;
        if (profile != runtime->game_input_request_profiles.end()) profile->second.consumed = true;
        const uint32_t required = required_game_input_capability(out_request->kind);
        const uint32_t available = effective_game_input_capabilities(runtime, observation_profile);
        if (internal_cleanup || required == 0 || (required != UINT32_MAX && (available & required) != 0)) return 1;
        (void)gua_complete_game_input_request(runtime->context, out_request->request_id, 0, GUA_GAME_INPUT_ERROR_UNSUPPORTED);
        runtime->game_input_request_profiles.erase(out_request->request_id);
    }
    return 0;
}

extern "C" int gua_runtime_complete_game_input_request(gua_runtime_t* runtime, uint64_t request_id, int succeeded, int error_code)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    const int result = gua_complete_game_input_request(runtime->context, request_id, succeeded, error_code);
    if (result != 0) runtime->game_input_request_profiles.erase(request_id);
    return result;
}

extern "C" int gua_runtime_tick_game_input_leases(gua_runtime_t* runtime, double elapsed_ms)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_tick_game_input_leases(runtime->context, elapsed_ms);
}

extern "C" int gua_runtime_copy_game_input_actions_json_v2(gua_runtime_t* runtime, int observation_profile, char* output, int size)
{
    if (!valid_runtime(runtime) || (observation_profile != GUA_OBSERVATION_PROFILE_DEBUG && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER) return 0;
    if ((effective_game_input_capabilities(runtime, observation_profile) & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) return 0;
    return gua_copy_game_input_actions_json_v2(runtime->context, observation_profile, output, size);
}

extern "C" int gua_runtime_query_game_input_actions_json_v2(gua_runtime_t* runtime,
    const gua_game_input_action_selector_v1_t* selector, int observation_profile, char* output, int size)
{
    if (!valid_runtime(runtime) || !selector || (observation_profile != GUA_OBSERVATION_PROFILE_DEBUG && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER) return 0;
    if ((effective_game_input_capabilities(runtime, observation_profile) & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) return 0;
    return gua_query_game_input_actions_json_v2(runtime->context, selector, observation_profile, output, size);
}

extern "C" int gua_runtime_copy_game_input_actions_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return copy_json_string("{}", out_json, out_json_size);
    const std::lock_guard lock(runtime->context_mutex);
    return gua_copy_game_input_actions_json_for_profile(runtime->context, runtime->observation_profile, out_json, out_json_size);
}

extern "C" int gua_runtime_copy_player_game_input_actions_json(gua_runtime_t* runtime, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return copy_json_string("{}", out_json, out_json_size);
    const std::lock_guard lock(runtime->context_mutex);
    if ((effective_game_input_capabilities(runtime, GUA_OBSERVATION_PROFILE_PLAYER) & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0)
        return copy_json_string("{}", out_json, out_json_size);
    return gua_copy_game_input_actions_json_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, out_json, out_json_size);
}

extern "C" int gua_runtime_query_game_input_actions_json(gua_runtime_t* runtime,
    const gua_game_input_action_selector_v1_t* selector, int observation_profile, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime) || selector == nullptr ||
        (observation_profile != GUA_OBSERVATION_PROFILE_DEBUG && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && observation_profile != GUA_OBSERVATION_PROFILE_PLAYER) return 0;
    if ((effective_game_input_capabilities(runtime, observation_profile) & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) return 0;
    return gua_query_game_input_actions_json(runtime->context, selector, observation_profile, out_json, out_json_size);
}

extern "C" int gua_runtime_copy_game_input_state_json(gua_runtime_t* runtime, uint64_t owner_id, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return copy_json_string("{}", out_json, out_json_size);
    const std::lock_guard lock(runtime->context_mutex);
    return gua_copy_game_input_state_json(runtime->context, owner_id, out_json, out_json_size);
}

extern "C" int gua_runtime_copy_game_input_result_json(gua_runtime_t* runtime, uint64_t owner_id, uint64_t request_id,
    char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return copy_json_string("{}", out_json, out_json_size);
    const std::lock_guard lock(runtime->context_mutex);
    return copy_game_input_result_unlocked(runtime, owner_id, request_id, out_json, out_json_size);
}

extern "C" void gua_runtime_set_world_object_tree_enabled(gua_runtime_t* runtime, int enabled)
{
    if (!valid_runtime(runtime)) return;
    const std::lock_guard lock(runtime->context_mutex);
    runtime->world_object_tree_enabled = enabled != 0;
}

extern "C" int gua_runtime_set_observation_profile(gua_runtime_t* runtime, int profile)
{
    if (!valid_runtime(runtime) || (profile != GUA_OBSERVATION_PROFILE_DEBUG && profile != GUA_OBSERVATION_PROFILE_PLAYER)) return 0;
    const std::lock_guard bridge_lock(runtime->bridge_mutex);
    const std::lock_guard context_lock(runtime->context_mutex);
    if ((inspector_bridge_running_unlocked(runtime) && runtime->observation_profile != profile) ||
        (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && profile == GUA_OBSERVATION_PROFILE_DEBUG)) return 0;
    runtime->observation_profile = profile;
    if(profile==GUA_OBSERVATION_PROFILE_PLAYER) spatial_close_clients_unlocked(runtime);
    return 1;
}

extern "C" int gua_runtime_get_observation_profile(gua_runtime_t* runtime)
{
    if (!valid_runtime(runtime)) return -1;
    const std::lock_guard lock(runtime->context_mutex);
    return runtime->observation_profile;
}

extern "C" int gua_runtime_set_player_screenshot_enabled(gua_runtime_t* runtime, int enabled)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard bridge_lock(runtime->bridge_mutex);
    const std::lock_guard context_lock(runtime->context_mutex);
    if (inspector_bridge_running_unlocked(runtime)) return 0;
    runtime->player_screenshot_enabled = enabled != 0;
    return 1;
}

extern "C" int gua_runtime_get_node_state(gua_runtime_t* runtime, const char* node_id, gua_node_state_t* out_state)
{
    if (!valid_runtime(runtime) || out_state == nullptr) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    gua_node_state_v2_t detailed { sizeof(gua_node_state_v2_t) };
    if (gua_get_node_state_v2_for_profile(runtime->context, node_id, runtime->observation_profile, &detailed) == 0) return 0;
    out_state->visible = detailed.visible; out_state->enabled = detailed.enabled;
    return 1;
}

extern "C" int gua_runtime_get_node_state_v2(gua_runtime_t* runtime, const char* node_id, gua_node_state_v2_t* out_state)
{
    if (!valid_runtime(runtime) || out_state == nullptr) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_get_node_state_v2_for_profile(runtime->context, node_id, runtime->observation_profile, out_state);
}

extern "C" int gua_runtime_find_node_by_id(gua_runtime_t* runtime, const char* node_id, char* out_node_id, int out_node_id_size)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_find_node_by_id_for_profile(runtime->context, node_id, runtime->observation_profile, out_node_id, out_node_id_size);
}

extern "C" int gua_runtime_find_node_by_role(
    gua_runtime_t* runtime,
    const char* role,
    const char* name,
    char* out_node_id,
    int out_node_id_size)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_find_node_by_role_for_profile(runtime->context, role, name, runtime->observation_profile, out_node_id, out_node_id_size);
}

extern "C" int gua_runtime_find_node_by_text(gua_runtime_t* runtime, const char* text, char* out_node_id, int out_node_id_size)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_find_node_by_text_for_profile(runtime->context, text, runtime->observation_profile, out_node_id, out_node_id_size);
}

extern "C" int gua_runtime_query_nodes_json(gua_runtime_t* runtime, const gua_selector_v1_t* selector, char* out_json, int out_json_size)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_query_nodes_json_for_profile(runtime->context, selector, runtime->observation_profile, out_json, out_json_size);
}

extern "C" int gua_runtime_enqueue_click(gua_runtime_t* runtime, const char* node_id)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    const gua_action_request_descriptor_t descriptor { sizeof(gua_action_request_descriptor_t), GUA_ACTION_CLICK, node_id };
    uint64_t request_id = 0;
    return gua_enqueue_action_for_profile(runtime->context, &descriptor, runtime->observation_profile, &request_id) == GUA_ACTION_ACCEPTED;
}

extern "C" int gua_runtime_consume_click_request(gua_runtime_t* runtime, const char* node_id)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_consume_click_request(runtime->context, node_id);
}

extern "C" int gua_runtime_emit_click(gua_runtime_t* runtime, const char* node_id)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    return gua_emit_click(runtime->context, node_id);
}

extern "C" int gua_runtime_poll_event(gua_runtime_t* runtime, gua_event_t* out_event)
{
    if (!valid_runtime(runtime)) {
        return 0;
    }

    const std::lock_guard lock(runtime->context_mutex);
    if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_DEBUG) return gua_poll_event(runtime->context, out_event);
    gua_event_v2_t detailed { sizeof(gua_event_v2_t) };
    if (gua_poll_event_v2_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, &detailed) == 0) return 0;
    out_event->type = detailed.action; std::snprintf(out_event->node_id, sizeof(out_event->node_id), "%s", detailed.node_id); return 1;
}

std::string escape_json(std::string_view value)
{
    std::string escaped;
    for (char ch : value) {
        switch (ch) {
        case '\\': escaped += "\\\\"; break;
        case '"': escaped += "\\\""; break;
        case '\n': escaped += "\\n"; break;
        case '\r': escaped += "\\r"; break;
        case '\t': escaped += "\\t"; break;
        default:
            if (static_cast<unsigned char>(ch) < 0x20U) {
                constexpr char hex[] = "0123456789abcdef";
                const unsigned char byte_value = static_cast<unsigned char>(ch);
                escaped += "\\u00";
                escaped += hex[byte_value >> 4U];
                escaped += hex[byte_value & 0x0fU];
            } else {
                escaped.push_back(ch);
            }
            break;
        }
    }
    return escaped;
}

extern "C" int gua_runtime_enqueue_action(gua_runtime_t* runtime, const gua_action_request_descriptor_t* descriptor, uint64_t* out_request_id)
{
    if (!valid_runtime(runtime)) return GUA_ACTION_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_enqueue_action_for_profile(runtime->context, descriptor, runtime->observation_profile, out_request_id);
}

extern "C" int gua_runtime_enqueue_player_action(
    gua_runtime_t* runtime, const gua_action_request_descriptor_t* descriptor, uint64_t* out_request_id)
{
    if (runtime == nullptr) return GUA_ACTION_ERROR_UNSUPPORTED;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_enqueue_action_for_profile(
        runtime->context, descriptor, GUA_OBSERVATION_PROFILE_PLAYER, out_request_id);
}

extern "C" int gua_runtime_cancel_action_request(gua_runtime_t* runtime, uint64_t request_id)
{
    if (!valid_runtime(runtime)) return GUA_ACTION_CANCEL_NOT_FOUND;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_cancel_action_request(runtime->context, request_id);
}

extern "C" int gua_runtime_get_action_request_observation_profile(gua_runtime_t* runtime, uint64_t request_id)
{
    if (!valid_runtime(runtime)) return -1;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_get_action_request_observation_profile(runtime->context, request_id);
}

extern "C" int gua_runtime_consume_action_request(gua_runtime_t* runtime, int action, const char* node_id, gua_action_request_t* out_request)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_consume_action_request(runtime->context, action, node_id, out_request);
}

extern "C" int gua_runtime_emit_action_result(gua_runtime_t* runtime, const gua_action_result_t* result)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return gua_emit_action_result(runtime->context, result);
}

extern "C" int gua_runtime_poll_event_v2(gua_runtime_t* runtime, gua_event_v2_t* out_event)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return runtime->observation_profile == GUA_OBSERVATION_PROFILE_DEBUG
        ? gua_poll_event_v2(runtime->context, out_event)
        : gua_poll_event_v2_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, out_event);
}

extern "C" int gua_runtime_poll_event_v2_for_request(gua_runtime_t* runtime, uint64_t request_id, gua_event_v2_t* out_event)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return runtime->observation_profile == GUA_OBSERVATION_PROFILE_DEBUG
        ? gua_poll_event_v2_for_request(runtime->context, request_id, out_event)
        : gua_poll_event_v2_for_request_and_profile(runtime->context, request_id, GUA_OBSERVATION_PROFILE_PLAYER, out_event);
}

extern "C" int gua_runtime_poll_event_v3(gua_runtime_t* runtime, gua_event_v3_t* out_event)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return runtime->observation_profile == GUA_OBSERVATION_PROFILE_DEBUG
        ? gua_poll_event_v3(runtime->context, out_event)
        : gua_poll_event_v3_for_profile(runtime->context, GUA_OBSERVATION_PROFILE_PLAYER, out_event);
}

extern "C" int gua_runtime_poll_event_v3_for_request(gua_runtime_t* runtime, uint64_t request_id, gua_event_v3_t* out_event)
{
    if (!valid_runtime(runtime)) return 0;
    const std::lock_guard lock(runtime->context_mutex);
    return runtime->observation_profile == GUA_OBSERVATION_PROFILE_DEBUG
        ? gua_poll_event_v3_for_request(runtime->context, request_id, out_event)
        : gua_poll_event_v3_for_request_and_profile(runtime->context, request_id, GUA_OBSERVATION_PROFILE_PLAYER, out_event);
}

extern "C" int gua_runtime_get_context_status(gua_runtime_t* runtime, gua_context_status_t* out_status)
{
    if (!valid_runtime(runtime) || out_status == nullptr) return 0;
    const uint32_t output_size = out_status->struct_size;
    const std::lock_guard lock(runtime->context_mutex);
    if (gua_get_context_status(runtime->context, out_status) == 0) return 0;
    if (runtime->observation_profile != GUA_OBSERVATION_PROFILE_PLAYER) return 1;
    const auto summary = player_summary_unlocked(runtime);
    out_status->revision = summary.ui_revision; out_status->node_count = summary.ui_node_count;
    out_status->pending_request_count = summary.pending_count; out_status->in_flight_request_count = summary.in_flight_count;
    out_status->unconsumed_event_count = summary.event_count; out_status->log_count = 0;
    out_status->has_screenshot = runtime->player_screenshot_enabled ? out_status->has_screenshot : 0;
    out_status->first_pending_action = 0; out_status->first_event_action = 0;
    out_status->first_pending_node_id[0] = '\0'; out_status->first_event_node_id[0] = '\0';
    if (output_size >= sizeof(gua_context_status_t) && !runtime->world_object_tree_enabled) { out_status->world_revision = 0; out_status->world_object_count = 0; }
    else if (output_size >= sizeof(gua_context_status_t)) {
        const auto world = copy_world_object_tree_json_unlocked(runtime);
        out_status->world_revision = json_unsigned(world, "\"revision\":");
        out_status->world_object_count = count_json_object_array(world, "\"objects\":");
    }
    return 1;
}

extern "C" int gua_runtime_reset_context(gua_runtime_t* runtime, const gua_reset_options_t* options, gua_reset_report_t* out_report)
{
    if (!valid_runtime(runtime)) return GUA_RESET_ERROR_INVALID_ARGUMENT;
    const std::lock_guard lock(runtime->context_mutex);
    const bool player = runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER;
    const auto summary = player ? player_summary_unlocked(runtime) : PlayerSummary {};
    uint32_t world_count = 0;
    if (player && runtime->world_object_tree_enabled) {
        const auto world = copy_world_object_tree_json_unlocked(runtime);
        world_count = count_json_object_array(world, "\"objects\":");
    }
    const uint32_t output_size = out_report == nullptr ? 0 : out_report->struct_size;
    const int result = gua_reset_context(runtime->context, options, out_report);
    if (result == GUA_RESET_SUCCEEDED) spatial_close_clients_unlocked(runtime);
    if (player && out_report != nullptr && result != GUA_RESET_ERROR_INVALID_ARGUMENT) {
        out_report->pending_request_count = summary.pending_count; out_report->in_flight_request_count = summary.in_flight_count;
        out_report->unconsumed_event_count = summary.event_count;
        if (out_report->discarded_node_count != 0) out_report->discarded_node_count = summary.ui_node_count;
        if (out_report->discarded_pending_request_count != 0) out_report->discarded_pending_request_count = summary.pending_count;
        if (out_report->discarded_in_flight_request_count != 0) out_report->discarded_in_flight_request_count = summary.in_flight_count;
        if (out_report->discarded_event_count != 0) out_report->discarded_event_count = summary.event_count;
        out_report->discarded_log_count = 0; if (!runtime->player_screenshot_enabled) out_report->discarded_screenshot = 0;
        if (output_size >= sizeof(gua_reset_report_t) && out_report->discarded_world_object_count != 0) out_report->discarded_world_object_count = world_count;
        out_report->first_pending_action = 0; out_report->first_event_action = 0;
        out_report->first_pending_node_id[0] = '\0'; out_report->first_event_node_id[0] = '\0';
    }
    if (result == GUA_RESET_SUCCEEDED) {
        invalidate_screenshot_requests(runtime);
        if (options != nullptr && (options->flags & GUA_RESET_REQUESTS) != 0) {
            std::erase_if(runtime->game_input_request_profiles,
                [](const auto& entry) { return !entry.second.consumed; });
        }
    }
    return result;
}

extern "C" int gua_runtime_start_inspector_bridge(gua_runtime_t* runtime, int port)
{
#if !GUA_RUNTIME_WITH_WS
    (void)runtime;
    (void)port;
    return 0;
#else
    if (!valid_runtime(runtime) || port <= 0 || port > 65535) {
        return 0;
    }

    const std::lock_guard bridge_lock(runtime->bridge_mutex);
    if (runtime->bridge != nullptr && runtime->bridge->running()) {
        return runtime->bridge->port() == static_cast<unsigned short>(port) ? 1 : 0;
    }
    runtime->bridge_stopping.store(false);

    gua::ws::BridgeHandlers handlers {
        .create_spatial_client = [runtime] { return gua_runtime_create_spatial_client(runtime); },
        .release_spatial_client = [runtime](unsigned long long client) { gua_runtime_release_spatial_client(runtime,client); },
        .spatial_command = [runtime](unsigned long long client,int operation,std::string_view wire,unsigned long long batch_id) -> gua::ws::CommandResult {
            gua_spatial_error_t error{}; gua_spatial_document_t* batch=nullptr; gua_spatial_document_t* raw=nullptr;
            if(operation==GUA_SPATIAL_ENQUEUE) {
                gua_spatial_parse_options_v1_t options{sizeof(options),GUA_SPATIAL_BATCH};
                if(wire.size()>1048576 || gua_spatial_from_json(&options,{wire.data(),static_cast<uint32_t>(wire.size())},&batch,&error))
                    return {false,"","invalid_request"};
            }
            std::unique_ptr<gua_spatial_document_t,decltype(&gua_spatial_destroy)> input(batch,gua_spatial_destroy);
            int status=gua_runtime_spatial_command(runtime,client,operation,batch,batch_id,&raw);
            std::unique_ptr<gua_spatial_document_t,decltype(&gua_spatial_destroy)> result(raw,gua_spatial_destroy);
            if(status==GUA_SPATIAL_NOT_READY) return {true,"null",""};
            if(status) return {false,"",spatial_safe_error(status)};
            return {true,raw ? spatial_document_json(raw) : "null",""};
        },
        .create_observe_client = [runtime] { return gua_runtime_create_observe_client(runtime,runtime->observation_profile); },
        .release_observe_client = [runtime](unsigned long long client) { gua_runtime_release_observe_client(runtime,client); },
        .observe_command = [runtime](unsigned long long client,int operation,unsigned long long subscription) -> gua::ws::CommandResult {
            gua_observe_result_t* raw=nullptr; uint64_t id=0;
            int status=gua_runtime_observe_command(runtime,client,operation,subscription,&id,&raw);
            std::unique_ptr<gua_observe_result_t,decltype(&gua_observe_result_destroy)> result(raw,gua_observe_result_destroy);
            if(status) return {false,"","observe_rejected"};
            if(operation==4) return {true,"null",""};
            int n=gua_observe_result_copy_transport_json(raw,nullptr,0); if(n<=0) return {false,"","observe_failed"};
            std::string json(static_cast<size_t>(n),'\0'); gua_observe_result_copy_transport_json(raw,json.data(),n); json.pop_back();
            if(operation==2) json="{\"subscriptionId\":"+std::to_string(id)+",\"snapshot\":"+json+"}";
            return {true,std::move(json),""};
        },
        .get_ui_tree_json = [runtime] {
            return copy_ui_tree_json(runtime);
        },
        .get_world_object_tree_json = [runtime] {
            const std::lock_guard lock(runtime->context_mutex);
            if (!runtime->world_object_tree_enabled)
                return unsupported_world_object_tree_json(runtime);
            const int size = gua_copy_world_object_tree_json(runtime->context, runtime->observation_profile, nullptr, 0);
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_copy_world_object_tree_json(runtime->context, runtime->observation_profile, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .get_logs_json = [runtime] {
            return copy_logs_json(runtime);
        },
        .get_screenshot_json = [runtime] {
            return copy_screenshot_json(runtime);
        },
        .get_snapshot_json = [runtime] {
            const std::lock_guard lock(runtime->context_mutex);
            const int ui_size = gua_copy_ui_tree_json_for_profile(runtime->context, runtime->observation_profile, nullptr, 0);
            std::string ui_tree(static_cast<std::size_t>(ui_size), '\0');
            gua_copy_ui_tree_json_for_profile(runtime->context, runtime->observation_profile, ui_tree.data(), ui_size);
            ui_tree.resize(static_cast<std::size_t>(ui_size - 1));
            std::string world_tree;
            if (!runtime->world_object_tree_enabled) {
                world_tree = unsupported_world_object_tree_json(runtime);
            } else {
                const int size = gua_copy_world_object_tree_json(runtime->context, runtime->observation_profile, nullptr, 0);
                world_tree.resize(static_cast<std::size_t>(size));
                gua_copy_world_object_tree_json(runtime->context, runtime->observation_profile, world_tree.data(), size);
                world_tree.resize(static_cast<std::size_t>(size - 1));
            }
            return "{\"uiTree\":" + ui_tree + ",\"worldObjectTree\":" + world_tree +
                ",\"logs\":" + (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER ? "[]" : gua_get_logs_json(runtime->context)) +
                ",\"screenshot\":" + (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && !runtime->player_screenshot_enabled
                    ? "{\"dataUri\":\"\",\"width\":0,\"height\":0}" : gua_get_screenshot_json(runtime->context)) + '}';
        },
        .capture_screenshot = [runtime](unsigned long long after_frame_sequence, unsigned int timeout_ms) {
            if (runtime->observation_profile == GUA_OBSERVATION_PROFILE_PLAYER && !runtime->player_screenshot_enabled)
                return gua::ws::CommandResult { false, {}, "unsupported" };
            uint64_t request_id = 0;
            if (gua_runtime_enqueue_screenshot_request(runtime, after_frame_sequence, &request_id) == 0)
                return gua::ws::CommandResult { false, {}, "capture_screenshot request could not be queued" };
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms == 0 ? 1 : timeout_ms);
            while (std::chrono::steady_clock::now() < deadline) {
                const int size = gua_runtime_poll_screenshot_result_json(runtime, request_id, nullptr, 0);
                if (size > 0) {
                    std::string json(static_cast<std::size_t>(size), '\0');
                    gua_runtime_poll_screenshot_result_json(runtime, request_id, json.data(), size);
                    json.resize(static_cast<std::size_t>(size - 1));
                    if (json.find("\"unavailable\"") != std::string::npos)
                        return gua::ws::CommandResult { false, {}, json };
                    return gua::ws::CommandResult { true, std::move(json), {} };
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
            }
            gua_runtime_cancel_screenshot_request(runtime, request_id);
            return gua::ws::CommandResult { false, {}, "capture_screenshot timed out" };
        },
        .get_diagnostics_json = [runtime] {
            return copy_diagnostics_json(runtime);
        },
        .semantic_lint = [runtime](bool include_world) {
            const std::lock_guard lock(runtime->context_mutex);
            const gua_semantic_lint_options_v1_t options {sizeof(options), runtime->observation_profile, include_world ? 1 : 0};
            gua_semantic_lint_report_t* raw = nullptr;
            if (gua_semantic_lint_analyze(runtime->context, &options, &raw)) return gua::ws::CommandResult {false, {}, "semantic_lint_failed"};
            const std::unique_ptr<gua_semantic_lint_report_t, decltype(&gua_semantic_lint_report_destroy)> report(raw, gua_semantic_lint_report_destroy);
            const int size = gua_semantic_lint_report_copy_json(raw, nullptr, 0);
            if (size <= 0) return gua::ws::CommandResult {false, {}, "semantic_lint_failed"};
            std::string json(static_cast<size_t>(size), '\0');
            gua_semantic_lint_report_copy_json(raw, json.data(), size); json.pop_back();
            return gua::ws::CommandResult {true, std::move(json), {}};
        },
        .get_version_json = [runtime] {
            return copy_version_json(runtime);
        },
        .get_connection_version_json = [runtime](unsigned long long client) {
            const std::lock_guard lock(runtime->context_mutex);
            return decorate_version_json(runtime,core_version_json(),client);
        },
        .clock_supported = [runtime] {
            const std::lock_guard lock(runtime->context_mutex);
            return runtime->virtual_clock_enabled;
        },
        .get_clock_json = [runtime] {
            const std::lock_guard lock(runtime->context_mutex);
            const int size = gua_clock_copy_status_json(runtime->context, nullptr, 0);
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_clock_copy_status_json(runtime->context, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .control_clock = [runtime](std::string_view command, double value_ms, double step_ms, bool step_ms_present) {
            const auto clock_error = [](int result) {
                return result == GUA_CLOCK_ERROR_NOT_INSTALLED ? "not_installed" :
                    result == GUA_CLOCK_ERROR_INVALID_STATE ? "invalid_state" :
                    result == GUA_CLOCK_ERROR_EXECUTION_LIMIT ? "execution_limit" : "invalid_duration";
            };
            const auto copy_clock = [runtime] {
                const int size = gua_clock_copy_status_json(runtime->context, nullptr, 0);
                std::string json(static_cast<std::size_t>(size), '\0');
                gua_clock_copy_status_json(runtime->context, json.data(), size);
                json.resize(static_cast<std::size_t>(size - 1));
                return json;
            };

            if (command == "clock_pause") {
                const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
                bool waiting_for_frame = false;
                unsigned long long completion_after_frame = 0;
                unsigned long long session_epoch = 0;
                while (std::chrono::steady_clock::now() < deadline) {
                    if (runtime->bridge_stopping.load())
                        return gua::ws::CommandResult { false, {}, "stale_session" };
                    {
                        const std::lock_guard lock(runtime->context_mutex);
                        gua_clock_status_t clock { sizeof(gua_clock_status_t) };
                        gua_clock_operation_status_t operation { sizeof(gua_clock_operation_status_t) };
                        gua_context_status_t context { sizeof(gua_context_status_t) };
                        if (gua_clock_get_status(runtime->context, &clock) == 0 ||
                            gua_clock_get_operation_status(runtime->context, &operation) == 0 ||
                            gua_get_context_status(runtime->context, &context) == 0)
                            return gua::ws::CommandResult { false, {}, "invalid_state" };
                        if (waiting_for_frame && context.session_epoch != session_epoch)
                            return gua::ws::CommandResult { false, {}, "stale_session" };
                        if (clock.pending_ms > 0.0 ||
                            operation.latest_operation_sequence > operation.completed_operation_sequence) {
                            waiting_for_frame = true;
                            completion_after_frame = context.frame_sequence;
                            session_epoch = context.session_epoch;
                        } else if (!waiting_for_frame || context.frame_sequence > completion_after_frame) {
                            const int result = gua_clock_pause(runtime->context);
                            if (result == GUA_CLOCK_OK)
                                return gua::ws::CommandResult { true, copy_clock(), {} };
                            if (result != GUA_CLOCK_ERROR_INVALID_STATE)
                                return gua::ws::CommandResult { false, {}, clock_error(result) };
                        }
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(1));
                }
                return gua::ws::CommandResult { false, {}, "invalid_state" };
            }

            const std::lock_guard lock(runtime->context_mutex);
            int result = GUA_CLOCK_ERROR_INVALID_ARGUMENT;
            gua_context_status_t completion_context { sizeof(gua_context_status_t) };
            gua_clock_operation_status_t operation_status { sizeof(gua_clock_operation_status_t) };
            if (command == "clock_install")
                result = gua_clock_install(runtime->context, value_ms, step_ms_present ? step_ms : 1000.0 / 60.0);
            else if (command == "clock_run_for") {
                gua_clock_status_t clock { sizeof(gua_clock_status_t) };
                if (gua_clock_get_status(runtime->context, &clock) != 0 &&
                    gua_get_context_status(runtime->context, &completion_context) != 0)
                    result = gua_clock_run_for(runtime->context, value_ms, step_ms_present ? step_ms : clock.default_step_ms);
            } else if (command == "clock_resume") result = gua_clock_resume(runtime->context);
            if (result != GUA_CLOCK_OK) {
                return gua::ws::CommandResult { false, {}, clock_error(result) };
            }
            std::string json = copy_clock();
            if (command == "clock_run_for") {
                if (gua_clock_get_operation_status(runtime->context, &operation_status) == 0)
                    return gua::ws::CommandResult { false, {}, "invalid_state" };
                json.pop_back();
                json += ",\"completionSessionEpoch\":" + std::to_string(completion_context.session_epoch) +
                    ",\"completionAfterFrameSequence\":" + std::to_string(completion_context.frame_sequence) +
                    ",\"operationSequence\":" + std::to_string(operation_status.latest_operation_sequence) + '}';
            }
            return gua::ws::CommandResult { true, std::move(json), {} };
        },
        .query_nodes_json = [runtime](const gua::ws::QuerySelector& selector) {
            gua_selector_v1_t native {
                sizeof(gua_selector_v1_t),
                selector.id.empty() ? nullptr : selector.id.c_str(), selector.id_match,
                selector.role.empty() ? nullptr : selector.role.c_str(), selector.role_match,
                selector.name.empty() ? nullptr : selector.name.c_str(), selector.name_match,
                selector.text.empty() ? nullptr : selector.text.c_str(), selector.text_match,
                selector.parent_id.empty() ? nullptr : selector.parent_id.c_str(),
                selector.direct_child ? 1 : 0, selector.visible, selector.enabled,
            };
            const std::lock_guard lock(runtime->context_mutex);
            const int size = gua_query_nodes_json_for_profile(runtime->context, &native, runtime->observation_profile, nullptr, 0);
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_query_nodes_json_for_profile(runtime->context, &native, runtime->observation_profile, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .query_world_objects_json = [runtime](const gua::ws::WorldQuerySelector& selector) {
            gua_world_state_value_v1_t state { sizeof(gua_world_state_value_v1_t),
                selector.state_key.empty() ? nullptr : selector.state_key.c_str(), selector.state_type,
                selector.state_type == GUA_WORLD_VALUE_STRING ? selector.state_string.c_str() : nullptr, selector.state_number,
                selector.state_bool ? 1 : 0 };
            gua_world_selector_v1_t native { sizeof(gua_world_selector_v1_t),
                selector.id.empty() ? nullptr : selector.id.c_str(), selector.id_match,
                selector.kind.empty() ? nullptr : selector.kind.c_str(), selector.kind_match,
                selector.label.empty() ? nullptr : selector.label.c_str(), selector.label_match,
                selector.tag.empty() ? nullptr : selector.tag.c_str(), selector.tag_match,
                selector.parent_id.empty() ? nullptr : selector.parent_id.c_str(), selector.direct_child ? 1 : 0,
                selector.visible_to_player, selector.active, selector.state_type < 0 ? nullptr : &state };
            gua_world_near_v1_t near { sizeof(gua_world_near_v1_t),
                selector.relative_to_object_id.empty() ? nullptr : selector.relative_to_object_id.c_str(), selector.max_distance };
            gua_world_selector_v2_t native_v2 { sizeof(gua_world_selector_v2_t), native,
                selector.spatial ? &near : nullptr, selector.limit };
            const std::lock_guard lock(runtime->context_mutex);
            if (!runtime->world_object_tree_enabled) return std::string("{\"valid\":false,\"error\":\"unsupported\",\"matches\":[]}");
            const int size = gua_query_world_objects_v2_json(runtime->context, &native_v2, runtime->observation_profile, nullptr, 0);
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_query_world_objects_v2_json(runtime->context, &native_v2, runtime->observation_profile, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .get_context_status_json = [runtime] { return status_json(runtime); },
        .reset_context_json = [runtime](unsigned long long expected_epoch, unsigned int flags, unsigned int flags_version, bool strict) {
            return reset_report_json(runtime, expected_epoch, flags, flags_version, strict);
        },
        .click_node = [runtime](std::string_view node_id) {
            const std::string id(node_id);
            const std::lock_guard lock(runtime->context_mutex);
            return gua_enqueue_click(runtime->context, id.c_str()) != 0;
        },
        .focus_node = [runtime](std::string_view node_id) {
            const std::string id(node_id);
            char found[128] {};
            const std::lock_guard lock(runtime->context_mutex);
            if (gua_find_node_by_id(runtime->context, id.c_str(), found, static_cast<int>(sizeof(found))) == 0) {
                return false;
            }
            const std::string message = "focus_node(" + id + ")";
            gua_add_log(runtime->context, GUA_LOG_DEBUG, message.c_str());
            return true;
        },
        .press_key = [runtime](std::string_view key) {
            const std::string key_string(key);
            const std::lock_guard lock(runtime->context_mutex);
            const std::string message = "press_key(" + key_string + ")";
            gua_add_log(runtime->context, GUA_LOG_INFO, message.c_str());
            return !key.empty();
        },
        .enqueue_action = [runtime](const gua::ws::ActionCommand& command) -> long long {
            int action = 0;
            if (command.type == "click_node") action = GUA_ACTION_CLICK;
            else if (command.type == "focus_node") action = GUA_ACTION_FOCUS;
            else if (command.type == "set_value") action = GUA_ACTION_SET_VALUE;
            else if (command.type == "set_checked") action = GUA_ACTION_SET_CHECKED;
            else if (command.type == "select") action = GUA_ACTION_SELECT;
            else if (command.type == "scroll") action = GUA_ACTION_SCROLL;
            else if (command.type == "press_key") action = GUA_ACTION_PRESS_KEY;
            const gua_action_request_descriptor_t descriptor {
                sizeof(gua_action_request_descriptor_t), action,
                command.node_id.empty() ? nullptr : command.node_id.c_str(),
                command.value.empty() ? nullptr : command.value.c_str(),
                command.delta_x, command.delta_y, command.bool_value ? 1 : 0,
                command.key.empty() ? nullptr : command.key.c_str(), command.modifiers,
                command.sensitive ? 1 : 0, command.scroll_unit
            };
            std::uint64_t request_id = 0;
            const std::lock_guard lock(runtime->context_mutex);
            const int result = gua_enqueue_action_for_profile(runtime->context, &descriptor, runtime->observation_profile, &request_id);
            return result == GUA_ACTION_ACCEPTED ? static_cast<long long>(request_id) : static_cast<long long>(result);
        },
        .poll_action_event_json = [runtime](unsigned long long request_id) {
            gua_event_v3_t event { sizeof(gua_event_v3_t), { sizeof(gua_event_v2_t) } };
            const std::lock_guard lock(runtime->context_mutex);
            const int found = request_id == 0
                ? gua_poll_event_v3_for_profile(runtime->context, runtime->observation_profile, &event)
                : gua_poll_event_v3_for_request_and_profile(runtime->context, request_id, runtime->observation_profile, &event);
            if (found == 0) return std::string("null");
            return std::string("{\"requestId\":") + std::to_string(event.base.request_id) +
                ",\"action\":" + std::to_string(event.base.action) +
                ",\"succeeded\":" + (event.base.status == GUA_ACTION_STATUS_SUCCEEDED ? "true" : "false") +
                ",\"error\":" + std::to_string(event.base.error_code) +
                ",\"nodeId\":\"" + escape_json(event.base.node_id) + "\"" +
                ",\"value\":\"" + escape_json(event.base.value) + "\"" +
                ",\"sensitive\":" + (event.base.sensitive != 0 ? "true" : "false") +
                ",\"sessionEpoch\":" + std::to_string(event.session_epoch) +
                ",\"frameSequence\":" + std::to_string(event.frame_sequence) +
                ",\"revision\":" + std::to_string(event.revision) + "}";
        },
        .create_game_input_owner = [runtime] {
            const std::lock_guard lock(runtime->context_mutex);
            return gua_create_game_input_owner(runtime->context);
        },
        .release_game_input_owner = [runtime](unsigned long long owner_id) {
            const std::lock_guard lock(runtime->context_mutex);
            (void)release_game_input_owner_unlocked(runtime, owner_id);
        },
        .game_input_supported = [runtime](unsigned int capability) {
            const std::lock_guard lock(runtime->context_mutex);
            return (effective_game_input_capabilities(runtime, runtime->observation_profile) & capability) != 0;
        },
        .get_game_input_actions_json = [runtime] {
            const std::lock_guard lock(runtime->context_mutex);
            const int size = gua_copy_game_input_actions_json_for_profile(runtime->context, runtime->observation_profile, nullptr, 0);
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_copy_game_input_actions_json_for_profile(runtime->context, runtime->observation_profile, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .query_game_input_actions_json = [runtime](const gua::ws::GameInputQuerySelector& selector) {
            const auto contains_nul = [](const std::string& value) { return value.find('\0') != std::string::npos; };
            if (contains_nul(selector.id) || contains_nul(selector.query) || contains_nul(selector.context) ||
                contains_nul(selector.category) || std::any_of(selector.tags.begin(), selector.tags.end(), contains_nul)) return std::string();
            std::vector<const char*> tag_pointers;
            tag_pointers.reserve(selector.tags.size());
            for (const auto& tag : selector.tags) tag_pointers.push_back(tag.c_str());
            gua_game_input_action_selector_v1_t native { sizeof(gua_game_input_action_selector_v1_t),
                selector.id.empty() ? nullptr : selector.id.c_str(), selector.query.empty() ? nullptr : selector.query.c_str(),
                selector.value_type, selector.active, selector.context.empty() ? nullptr : selector.context.c_str(),
                selector.category.empty() ? nullptr : selector.category.c_str(),
                tag_pointers.empty() ? nullptr : tag_pointers.data(), static_cast<std::uint32_t>(tag_pointers.size()), selector.limit };
            const std::lock_guard lock(runtime->context_mutex);
            const int size = gua_query_game_input_actions_json(runtime->context, &native, runtime->observation_profile, nullptr, 0);
            if (size <= 0) return std::string();
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_query_game_input_actions_json(runtime->context, &native, runtime->observation_profile, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .get_game_input_actions_json_v2 = [runtime] {
            const std::lock_guard lock(runtime->context_mutex);
            if ((effective_game_input_capabilities(runtime, runtime->observation_profile) & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) return std::string();
            const int size = gua_copy_game_input_actions_json_v2(runtime->context, runtime->observation_profile, nullptr, 0);
            if (size <= 0) return std::string();
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_copy_game_input_actions_json_v2(runtime->context, runtime->observation_profile, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .query_game_input_actions_json_v2 = [runtime](const gua::ws::GameInputQuerySelector& selector) {
            const auto contains_nul = [](const std::string& value) { return value.find('\0') != std::string::npos; };
            if (contains_nul(selector.id) || contains_nul(selector.query) || contains_nul(selector.context) ||
                contains_nul(selector.category) || std::any_of(selector.tags.begin(), selector.tags.end(), contains_nul)) return std::string();
            std::vector<const char*> tag_pointers;
            tag_pointers.reserve(selector.tags.size());
            for (const auto& tag : selector.tags) tag_pointers.push_back(tag.c_str());
            gua_game_input_action_selector_v1_t native { sizeof(gua_game_input_action_selector_v1_t),
                selector.id.empty() ? nullptr : selector.id.c_str(), selector.query.empty() ? nullptr : selector.query.c_str(),
                selector.value_type, selector.active, selector.context.empty() ? nullptr : selector.context.c_str(),
                selector.category.empty() ? nullptr : selector.category.c_str(),
                tag_pointers.empty() ? nullptr : tag_pointers.data(), static_cast<std::uint32_t>(tag_pointers.size()), selector.limit };
            const std::lock_guard lock(runtime->context_mutex);
            if ((effective_game_input_capabilities(runtime, runtime->observation_profile) & GUA_RUNTIME_GAME_INPUT_SEMANTIC) == 0) return std::string();
            const int size = gua_query_game_input_actions_json_v2(runtime->context, &native, runtime->observation_profile, nullptr, 0);
            if (size <= 0) return std::string();
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_query_game_input_actions_json_v2(runtime->context, &native, runtime->observation_profile, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .get_game_input_state_json = [runtime](unsigned long long owner_id) {
            const std::lock_guard lock(runtime->context_mutex);
            const int size = gua_copy_game_input_state_json(runtime->context, owner_id, nullptr, 0);
            if (size <= 0) return std::string("{}");
            std::string json(static_cast<std::size_t>(size), '\0');
            gua_copy_game_input_state_json(runtime->context, owner_id, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
        .enqueue_game_input = [runtime](unsigned long long owner_id, const gua::ws::GameInputCommand& command) -> long long {
            int kind = 0, operation = 0;
            unsigned int required = 0;
            if (command.type == "press_game_input_action" || command.type == "set_game_input_action" ||
                command.type == "release_game_input_action") {
                kind = GUA_GAME_INPUT_SEMANTIC; required = GUA_RUNTIME_GAME_INPUT_SEMANTIC;
                operation = command.type == "press_game_input_action" ? GUA_GAME_INPUT_PRESS :
                    command.type == "set_game_input_action" ? GUA_GAME_INPUT_SET : GUA_GAME_INPUT_RELEASE;
            } else if (command.type == "key_down" || command.type == "key_up" || command.type == "press_physical_key") {
                kind = GUA_GAME_INPUT_KEYBOARD; required = GUA_RUNTIME_GAME_INPUT_KEYBOARD;
                operation = command.type == "key_down" ? GUA_GAME_INPUT_DOWN :
                    command.type == "key_up" ? GUA_GAME_INPUT_UP : GUA_GAME_INPUT_PRESS;
            } else if (command.type.rfind("pointer_", 0) == 0) {
                kind = GUA_GAME_INPUT_POINTER; required = GUA_RUNTIME_GAME_INPUT_POINTER;
                operation = command.type == "pointer_move" ?
                    (command.target.rfind("delta:", 0) == 0 ? GUA_GAME_INPUT_MOVE_DELTA : GUA_GAME_INPUT_MOVE_ABSOLUTE) :
                    command.type == "pointer_button_down" ? GUA_GAME_INPUT_DOWN :
                    command.type == "pointer_button_up" ? GUA_GAME_INPUT_UP : GUA_GAME_INPUT_WHEEL;
            } else if (command.type == "gamepad_button_down" || command.type == "gamepad_button_up" ||
                command.type == "set_gamepad_axis" || command.type == "reset_gamepad") {
                kind = GUA_GAME_INPUT_GAMEPAD; required = GUA_RUNTIME_GAME_INPUT_GAMEPAD;
                operation = command.type == "gamepad_button_down" ? GUA_GAME_INPUT_DOWN :
                    command.type == "gamepad_button_up" ? GUA_GAME_INPUT_UP :
                    command.type == "set_gamepad_axis" ? GUA_GAME_INPUT_SET : GUA_GAME_INPUT_RESET;
            } else if (command.type == "text_input") {
                kind = GUA_GAME_INPUT_TEXT_INPUT; operation = GUA_GAME_INPUT_SET; required = GUA_RUNTIME_GAME_INPUT_TEXT;
            } else if (command.type == "release_all_game_inputs") {
                kind = GUA_GAME_INPUT_CLEANUP; operation = GUA_GAME_INPUT_RELEASE_ALL;
                required = GUA_RUNTIME_GAME_INPUT_SEMANTIC | GUA_RUNTIME_GAME_INPUT_KEYBOARD |
                    GUA_RUNTIME_GAME_INPUT_POINTER | GUA_RUNTIME_GAME_INPUT_GAMEPAD | GUA_RUNTIME_GAME_INPUT_TEXT;
            }
            const std::lock_guard lock(runtime->context_mutex);
            const uint32_t available = effective_game_input_capabilities(runtime, runtime->observation_profile);
            if (kind == 0 || required == 0 || (kind != GUA_GAME_INPUT_CLEANUP && (available & required) == 0))
                return static_cast<long long>(GUA_GAME_INPUT_ERROR_UNSUPPORTED);
            gua_game_input_request_descriptor_v2_t descriptor { sizeof(descriptor), owner_id, kind, operation,
                command.target.c_str(), command.value_json.c_str(), command.x, command.y, command.lease_ms,
                command.device_index, command.sensitive ? 1 : 0, command.confirmed ? 1 : 0 };
            std::uint64_t request_id = 0;
            const int result = gua_enqueue_game_input_for_profile_v2(runtime->context, &descriptor,
                runtime->observation_profile, &request_id);
            if (result == GUA_GAME_INPUT_OK)
                runtime->game_input_request_profiles[request_id] = { runtime->observation_profile, owner_id, false };
            return result == GUA_GAME_INPUT_OK ? static_cast<long long>(request_id) : static_cast<long long>(result);
        },
        .poll_game_input_result_json = [runtime](unsigned long long owner_id, unsigned long long request_id) {
            const std::lock_guard lock(runtime->context_mutex);
            const int size = copy_game_input_result_unlocked(runtime, owner_id, request_id, nullptr, 0);
            if (size <= 0) return std::string("null");
            std::string json(static_cast<std::size_t>(size), '\0');
            copy_game_input_result_unlocked(runtime, owner_id, request_id, json.data(), size);
            json.resize(static_cast<std::size_t>(size - 1));
            return json;
        },
    };

    auto bridge = std::make_unique<gua::ws::BridgeServer>(
        std::move(handlers),
        gua::ws::BridgeOptions { .port = static_cast<unsigned short>(port) });

    bridge->start();
    if (!bridge->running()) {
        const std::lock_guard lock(runtime->context_mutex);
        gua_add_log(runtime->context, GUA_LOG_ERROR, "Inspector bridge failed to listen.");
        return 0;
    }

    runtime->bridge_port = port;
    runtime->bridge_url = "ws://127.0.0.1:" + std::to_string(port);
    runtime->bridge = std::move(bridge);

    {
        const std::lock_guard lock(runtime->context_mutex);
        const std::string message = "Inspector bridge listening on " + runtime->bridge_url;
        gua_add_log(runtime->context, GUA_LOG_INFO, message.c_str());
    }

    return 1;
#endif
}

extern "C" void gua_runtime_stop_inspector_bridge(gua_runtime_t* runtime)
{
#if !GUA_RUNTIME_WITH_WS
    (void)runtime;
#else
    if (runtime == nullptr) {
        return;
    }

    runtime->bridge_stopping.store(true);
    std::unique_ptr<gua::ws::BridgeServer> bridge;
    {
        const std::lock_guard bridge_lock(runtime->bridge_mutex);
        bridge = std::move(runtime->bridge);
        runtime->bridge_port = 0;
        runtime->bridge_url.clear();
    }

    if (bridge != nullptr) {
        bridge->stop();
    }
#endif
}

extern "C" int gua_runtime_inspector_bridge_running(gua_runtime_t* runtime)
{
#if !GUA_RUNTIME_WITH_WS
    (void)runtime;
    return 0;
#else
    if (runtime == nullptr) {
        return 0;
    }

    const std::lock_guard bridge_lock(runtime->bridge_mutex);
    return inspector_bridge_running_unlocked(runtime) ? 1 : 0;
#endif
}

extern "C" const char* gua_runtime_inspector_bridge_url(gua_runtime_t* runtime)
{
    if (runtime == nullptr) {
        return "";
    }

    const std::lock_guard bridge_lock(runtime->bridge_mutex);
    return runtime->bridge_url.c_str();
}

extern "C" void gua_runtime_publish_inspector_snapshot(gua_runtime_t* runtime)
{
#if !GUA_RUNTIME_WITH_WS
    (void)runtime;
#else
    if (runtime == nullptr) {
        return;
    }

    const std::lock_guard bridge_lock(runtime->bridge_mutex);
    if (runtime->bridge != nullptr && runtime->bridge->running()) {
        runtime->bridge->publish_snapshot();
    }
#endif
}

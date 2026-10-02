#pragma once

#include "gua/gua.h"
#include "gua/observe.h"
#include "gua/spatial_host.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct gua_runtime_t gua_runtime_t;
/* Host-only borrowed context for existing Observe C/C++ bindings. Never destroy
 * it. Synchronize host calls/getters with runtime destruction, as for all APIs. */
gua_context_t* gua_runtime_borrow_context(gua_runtime_t* runtime);
/* Trusted host opt-in. Retains host, copies grants, and closes old connection
 * owners. NULL host disables. Only enabled Testing/Debug at the current runtime
 * epoch is accepted; a Player runtime cannot elevate. Configure before bridge
 * startup; reconnect after replacing grants. No client supplies these grants. */
int gua_runtime_bind_spatial(gua_runtime_t*, gua_spatial_host_t*, uint64_t provider,
    const gua_spatial_document_t* grants);
uint64_t gua_runtime_create_spatial_client(gua_runtime_t*);
void gua_runtime_release_spatial_client(gua_runtime_t*, uint64_t client);
enum { GUA_SPATIAL_INFO=1, GUA_SPATIAL_ENQUEUE=2, GUA_SPATIAL_POLL=3, GUA_SPATIAL_CANCEL=4 };
int gua_runtime_spatial_command(gua_runtime_t*, uint64_t client, int command,
    const gua_spatial_document_t* batch, uint64_t batch_id,
    gua_spatial_document_t** result);
enum { GUA_OBSERVE_SNAPSHOT = 1, GUA_OBSERVE_SUBSCRIBE = 2,
    GUA_OBSERVE_POLL = 3, GUA_OBSERVE_UNSUBSCRIBE = 4 };
/* A transport client owns its profile and subscriptions (limit 64). Requests
 * cannot change profile or consume another client's cursor. Disconnect closes it.
 * Creation returns 0 if the profile exceeds the host ceiling. A later host
 * downgrade invalidates Debug clients; commands release cursors and return STALE. */
uint64_t gua_runtime_create_observe_client(gua_runtime_t*, int profile);
void gua_runtime_release_observe_client(gua_runtime_t*, uint64_t client);
int gua_runtime_observe_command(gua_runtime_t*, uint64_t client, int command,
    uint64_t subscription, uint64_t* out_subscription, gua_observe_result_t** result);

enum {
    GUA_SCREENSHOT_AVAILABLE = 1,
    GUA_SCREENSHOT_UNAVAILABLE_HEADLESS = -1,
    GUA_SCREENSHOT_UNAVAILABLE_RENDERING_DISABLED = -2,
    GUA_SCREENSHOT_UNAVAILABLE_UNSUPPORTED = -3,
    GUA_SCREENSHOT_UNAVAILABLE_STALE_SESSION = -4
};

typedef struct gua_screenshot_request_t {
    uint32_t struct_size;
    uint64_t request_id;
    uint64_t session_epoch;
    uint64_t after_frame_sequence;
} gua_screenshot_request_t;

gua_runtime_t* gua_runtime_create(void);
void gua_runtime_destroy(gua_runtime_t* runtime);

void gua_runtime_begin_frame(gua_runtime_t* runtime, const char* screen);
void gua_runtime_end_frame(gua_runtime_t* runtime);
void gua_runtime_register_node(
    gua_runtime_t* runtime,
    const char* id,
    const char* role,
    const char* label,
    gua_bounds_t bounds,
    int visible,
    int enabled
);
int gua_runtime_register_node_v2(gua_runtime_t* runtime, const gua_node_descriptor_v2_t* descriptor);
int gua_runtime_register_node_v3(gua_runtime_t* runtime, const gua_node_descriptor_v3_t* descriptor);
int gua_runtime_register_node_v4(gua_runtime_t* runtime, const gua_node_descriptor_v4_t* descriptor);
int gua_runtime_begin_world_frame(gua_runtime_t* runtime, const char* scene);
int gua_runtime_register_world_object_v1(gua_runtime_t* runtime, const gua_world_object_descriptor_v1_t* descriptor);
int gua_runtime_register_world_object_v2(gua_runtime_t* runtime, const gua_world_object_descriptor_v2_t* descriptor);
int gua_runtime_end_world_frame(gua_runtime_t* runtime);
int gua_runtime_abort_world_frame(gua_runtime_t* runtime);
int gua_runtime_copy_world_object_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_query_world_objects_json(gua_runtime_t* runtime, const gua_world_selector_v1_t* selector, char* out_json, int out_json_size);
int gua_runtime_query_world_objects_v2_json(gua_runtime_t* runtime, const gua_world_selector_v2_t* selector, char* out_json, int out_json_size);
/* Browser/public-agent projection. These calls cannot elevate to the runtime's Debug profile. */
int gua_runtime_copy_player_world_object_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_query_player_world_objects_json(gua_runtime_t* runtime, const gua_world_selector_v1_t* selector, char* out_json, int out_json_size);
int gua_runtime_query_player_world_objects_v2_json(gua_runtime_t* runtime, const gua_world_selector_v2_t* selector, char* out_json, int out_json_size);

const char* gua_runtime_get_ui_tree_json(gua_runtime_t* runtime);
/* Returns the required byte size including the trailing NUL. Output is NUL-terminated when out_json_size > 0. */
int gua_runtime_copy_ui_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
/* Browser/public-agent projection. This call cannot elevate to the runtime's Debug profile. */
int gua_runtime_copy_player_ui_tree_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
void gua_runtime_add_log(gua_runtime_t* runtime, int level, const char* message);
const char* gua_runtime_get_logs_json(gua_runtime_t* runtime);
/* Returns the required byte size including the trailing NUL. Output is NUL-terminated when out_json_size > 0. */
int gua_runtime_copy_logs_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
void gua_runtime_set_screenshot(gua_runtime_t* runtime, const char* data_uri, int width, int height);
const char* gua_runtime_get_screenshot_json(gua_runtime_t* runtime);
/* Returns the required byte size including the trailing NUL. Output is NUL-terminated when out_json_size > 0. */
int gua_runtime_copy_screenshot_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_enqueue_screenshot_request(gua_runtime_t* runtime, uint64_t after_frame_sequence, uint64_t* out_request_id);
int gua_runtime_consume_screenshot_request(gua_runtime_t* runtime, gua_screenshot_request_t* out_request);
int gua_runtime_complete_screenshot_request(gua_runtime_t* runtime, uint64_t request_id, int result, const char* data_uri, int width, int height);
int gua_runtime_poll_screenshot_result_json(gua_runtime_t* runtime, uint64_t request_id, char* out_json, int out_json_size);
int gua_runtime_cancel_screenshot_request(gua_runtime_t* runtime, uint64_t request_id);
int gua_runtime_set_diagnostics_history_limit(gua_runtime_t* runtime, uint32_t history_limit);
int gua_runtime_set_diagnostics_environment_json(gua_runtime_t* runtime, const char* environment_json);
const char* gua_runtime_get_diagnostics_json(gua_runtime_t* runtime);
int gua_runtime_copy_diagnostics_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_copy_version_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_clock_install(gua_runtime_t* runtime, double initial_time_ms, double step_ms);
int gua_runtime_clock_pause(gua_runtime_t* runtime);
int gua_runtime_clock_run_for(gua_runtime_t* runtime, double duration_ms, double step_ms);
int gua_runtime_clock_advance(gua_runtime_t* runtime, double duration_ms);
int gua_runtime_clock_resume(gua_runtime_t* runtime);
int gua_runtime_clock_get_status(gua_runtime_t* runtime, gua_clock_status_t* out_status);
int gua_runtime_clock_copy_status_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_clock_consume_step(gua_runtime_t* runtime, gua_clock_step_t* out_step);
void gua_runtime_set_godot_plugin_version(gua_runtime_t* runtime, const char* version);
void gua_runtime_set_adapter_version(gua_runtime_t* runtime, const char* adapter, const char* version);
/* Adapter opt-in: enable only when the host pumps and consumes clock steps. */
void gua_runtime_set_virtual_clock_enabled(gua_runtime_t* runtime, int enabled);
enum {
    GUA_RUNTIME_GAME_INPUT_SEMANTIC = 1U << 0,
    GUA_RUNTIME_GAME_INPUT_KEYBOARD = 1U << 1,
    GUA_RUNTIME_GAME_INPUT_POINTER = 1U << 2,
    GUA_RUNTIME_GAME_INPUT_GAMEPAD = 1U << 3,
    GUA_RUNTIME_GAME_INPUT_TEXT = 1U << 4
};
/* Adapters opt in only after their host-frame input pump and cleanup route exist. */
void gua_runtime_set_game_input_capabilities(gua_runtime_t* runtime, uint32_t capabilities);
/* Player/Public Agent input is denied by default and may only narrow the adapter capabilities above. */
void gua_runtime_set_player_game_input_capabilities(gua_runtime_t* runtime, uint32_t capabilities);
uint32_t gua_runtime_get_game_input_capabilities(gua_runtime_t* runtime, int observation_profile);
int gua_runtime_begin_game_input_frame(gua_runtime_t* runtime, const char* input_context);
int gua_runtime_register_game_input_action_v1(gua_runtime_t* runtime, const gua_game_input_action_descriptor_v1_t* descriptor);
int gua_runtime_register_game_input_action_v3(gua_runtime_t* runtime, const gua_game_input_action_descriptor_v3_t* descriptor);
int gua_runtime_copy_game_input_actions_json_v2(gua_runtime_t* runtime, int observation_profile, char* output, int size);
int gua_runtime_query_game_input_actions_json_v2(gua_runtime_t* runtime, const gua_game_input_action_selector_v1_t* selector, int observation_profile, char* output, int size);
int gua_runtime_register_game_input_action_v2(gua_runtime_t* runtime, const gua_game_input_action_descriptor_v2_t* descriptor);
int gua_runtime_end_game_input_frame(gua_runtime_t* runtime);
int gua_runtime_abort_game_input_frame(gua_runtime_t* runtime);
uint64_t gua_runtime_create_game_input_owner(gua_runtime_t* runtime);
int gua_runtime_release_game_input_owner(gua_runtime_t* runtime, uint64_t owner_id);
int gua_runtime_enqueue_game_input(gua_runtime_t* runtime, const gua_game_input_request_descriptor_v1_t* descriptor, uint64_t* out_request_id);
int gua_runtime_enqueue_game_input_v2(gua_runtime_t* runtime, const gua_game_input_request_descriptor_v2_t* descriptor, uint64_t* out_request_id);
/* Trusted adapter entry point. A Player runtime may not elevate a request to Debug. */
int gua_runtime_enqueue_game_input_for_profile_v2(gua_runtime_t* runtime, const gua_game_input_request_descriptor_v2_t* descriptor,
    int observation_profile, uint64_t* out_request_id);
int gua_runtime_consume_game_input_request(gua_runtime_t* runtime, gua_game_input_request_v1_t* out_request);
int gua_runtime_enqueue_game_input_guarded_v2(gua_runtime_t* runtime, const gua_game_input_request_descriptor_v2_t* descriptor,
    int observation_profile, uint64_t expected_session_epoch, uint64_t expected_action_revision, uint64_t* out_request_id);
int gua_runtime_complete_game_input_request(gua_runtime_t* runtime, uint64_t request_id, int succeeded, int error_code);
int gua_runtime_tick_game_input_leases(gua_runtime_t* runtime, double elapsed_ms);
int gua_runtime_copy_game_input_actions_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_copy_player_game_input_actions_json(gua_runtime_t* runtime, char* out_json, int out_json_size);
int gua_runtime_query_game_input_actions_json(gua_runtime_t* runtime, const gua_game_input_action_selector_v1_t* selector,
    int observation_profile, char* out_json, int out_json_size);
int gua_runtime_copy_game_input_state_json(gua_runtime_t* runtime, uint64_t owner_id, char* out_json, int out_json_size);
int gua_runtime_copy_game_input_result_json(gua_runtime_t* runtime, uint64_t owner_id, uint64_t request_id, char* out_json, int out_json_size);
/* Adapter opt-in: enable only after the host publishes world frames. */
void gua_runtime_set_world_object_tree_enabled(gua_runtime_t* runtime, int enabled);
/* Host-owned ceiling; transports cannot override this profile per request. */
int gua_runtime_set_observation_profile(gua_runtime_t* runtime, int profile);
int gua_runtime_get_observation_profile(gua_runtime_t* runtime);
/* Player screenshots are denied unless the host opts in before the bridge starts. */
int gua_runtime_set_player_screenshot_enabled(gua_runtime_t* runtime, int enabled);
int gua_runtime_get_node_state(gua_runtime_t* runtime, const char* node_id, gua_node_state_t* out_state);
int gua_runtime_get_node_state_v2(gua_runtime_t* runtime, const char* node_id, gua_node_state_v2_t* out_state);
int gua_runtime_find_node_by_id(gua_runtime_t* runtime, const char* node_id, char* out_node_id, int out_node_id_size);
int gua_runtime_find_node_by_role(gua_runtime_t* runtime, const char* role, const char* name, char* out_node_id, int out_node_id_size);
int gua_runtime_find_node_by_text(gua_runtime_t* runtime, const char* text, char* out_node_id, int out_node_id_size);
int gua_runtime_query_nodes_json(gua_runtime_t* runtime, const gua_selector_v1_t* selector, char* out_json, int out_json_size);
int gua_runtime_enqueue_click(gua_runtime_t* runtime, const char* node_id);
int gua_runtime_consume_click_request(gua_runtime_t* runtime, const char* node_id);
int gua_runtime_emit_click(gua_runtime_t* runtime, const char* node_id);
int gua_runtime_poll_event(gua_runtime_t* runtime, gua_event_t* out_event);
int gua_runtime_enqueue_action(gua_runtime_t* runtime, const gua_action_request_descriptor_t* descriptor, uint64_t* out_request_id);
/* Browser/public-agent authorization. The captured request profile is always Player. */
int gua_runtime_enqueue_player_action(gua_runtime_t* runtime, const gua_action_request_descriptor_t* descriptor, uint64_t* out_request_id);
int gua_runtime_cancel_action_request(gua_runtime_t* runtime, uint64_t request_id);
int gua_runtime_get_action_request_observation_profile(gua_runtime_t* runtime, uint64_t request_id);
int gua_runtime_consume_action_request(gua_runtime_t* runtime, int action, const char* node_id, gua_action_request_t* out_request);
int gua_runtime_emit_action_result(gua_runtime_t* runtime, const gua_action_result_t* result);
int gua_runtime_poll_event_v2(gua_runtime_t* runtime, gua_event_v2_t* out_event);
int gua_runtime_poll_event_v2_for_request(gua_runtime_t* runtime, uint64_t request_id, gua_event_v2_t* out_event);
int gua_runtime_poll_event_v3(gua_runtime_t* runtime, gua_event_v3_t* out_event);
int gua_runtime_poll_event_v3_for_request(gua_runtime_t* runtime, uint64_t request_id, gua_event_v3_t* out_event);
int gua_runtime_get_context_status(gua_runtime_t* runtime, gua_context_status_t* out_status);
int gua_runtime_reset_context(gua_runtime_t* runtime, const gua_reset_options_t* options, gua_reset_report_t* out_report);

int gua_runtime_start_inspector_bridge(gua_runtime_t* runtime, int port);
void gua_runtime_stop_inspector_bridge(gua_runtime_t* runtime);
int gua_runtime_inspector_bridge_running(gua_runtime_t* runtime);
const char* gua_runtime_inspector_bridge_url(gua_runtime_t* runtime);
void gua_runtime_publish_inspector_snapshot(gua_runtime_t* runtime);

#ifdef __cplusplus
}
#endif

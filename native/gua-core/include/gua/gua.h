#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct gua_context_t gua_context_t;

typedef struct gua_bounds_t {
    float x;
    float y;
    float w;
    float h;
} gua_bounds_t;

typedef struct gua_event_t {
    int type;
    char node_id[128];
} gua_event_t;

enum {
    GUA_ACTION_CLICK = 1,
    GUA_ACTION_FOCUS = 2,
    GUA_ACTION_SET_VALUE = 3,
    GUA_ACTION_SET_CHECKED = 4,
    GUA_ACTION_SELECT = 5,
    GUA_ACTION_SCROLL = 6,
    GUA_ACTION_PRESS_KEY = 7
};

enum {
    GUA_GAME_INPUT_BUTTON = 1,
    GUA_GAME_INPUT_AXIS1D = 2,
    GUA_GAME_INPUT_VECTOR2 = 3,
    GUA_GAME_INPUT_TEXT = 4
};

enum {
    GUA_GAME_INPUT_SEMANTIC = 1,
    GUA_GAME_INPUT_KEYBOARD = 2,
    GUA_GAME_INPUT_POINTER = 3,
    GUA_GAME_INPUT_GAMEPAD = 4,
    GUA_GAME_INPUT_TEXT_INPUT = 5,
    GUA_GAME_INPUT_CLEANUP = 6
};

enum {
    GUA_GAME_INPUT_PRESS = 1,
    GUA_GAME_INPUT_SET = 2,
    GUA_GAME_INPUT_RELEASE = 3,
    GUA_GAME_INPUT_DOWN = 4,
    GUA_GAME_INPUT_UP = 5,
    GUA_GAME_INPUT_MOVE_ABSOLUTE = 6,
    GUA_GAME_INPUT_MOVE_DELTA = 7,
    GUA_GAME_INPUT_WHEEL = 8,
    GUA_GAME_INPUT_RESET = 9,
    GUA_GAME_INPUT_RELEASE_ALL = 10
};

enum {
    GUA_GAME_INPUT_OK = 1,
    GUA_GAME_INPUT_ERROR_INVALID_ARGUMENT = -1,
    GUA_GAME_INPUT_ERROR_ACTION_NOT_FOUND = -2,
    GUA_GAME_INPUT_ERROR_INACTIVE = -3,
    GUA_GAME_INPUT_ERROR_UNSUPPORTED = -4,
    GUA_GAME_INPUT_ERROR_INVALID_VALUE = -5,
    GUA_GAME_INPUT_ERROR_LEASE = -6,
    GUA_GAME_INPUT_ERROR_CONFIRMATION_REQUIRED = -7
};

typedef struct gua_game_input_action_descriptor_v1_t {
    uint32_t struct_size;
    const char* id;
    const char* description;
    int value_type;
    double minimum;
    double maximum;
    int has_range;
    int holdable;
    int active;
    const char* bindings_json;
    const char* risk;
    int requires_confirmation;
} gua_game_input_action_descriptor_v1_t;

typedef struct gua_game_input_action_descriptor_v2_t {
    uint32_t struct_size;
    gua_game_input_action_descriptor_v1_t base;
    const char* category;
    const char* const* aliases;
    uint32_t alias_count;
    const char* const* tags;
    uint32_t tag_count;
    int agent_exposure;
} gua_game_input_action_descriptor_v2_t;

// Optional bounded JSON metadata. Null pointers mean absent; v1/v2 stay unchanged.
typedef struct gua_game_input_action_descriptor_v3_t {
    uint32_t struct_size;
    gua_game_input_action_descriptor_v2_t base;
    const char* value_schema_json;
    const char* examples_json;
} gua_game_input_action_descriptor_v3_t;

typedef struct gua_game_input_action_selector_v1_t {
    uint32_t struct_size;
    const char* id;
    const char* query;
    int value_type;
    int active;
    const char* context;
    const char* category;
    const char* const* tags;
    uint32_t tag_count;
    uint32_t limit;
} gua_game_input_action_selector_v1_t;

typedef struct gua_game_input_request_descriptor_v1_t {
    uint32_t struct_size;
    uint64_t owner_id;
    int kind;
    int operation;
    const char* target;
    const char* value_json;
    double x;
    double y;
    uint32_t lease_ms;
    int device_index;
    int sensitive;
} gua_game_input_request_descriptor_v1_t;

typedef struct gua_game_input_request_descriptor_v2_t {
    uint32_t struct_size;
    uint64_t owner_id;
    int kind;
    int operation;
    const char* target;
    const char* value_json;
    double x;
    double y;
    uint32_t lease_ms;
    int device_index;
    int sensitive;
    int confirmed;
} gua_game_input_request_descriptor_v2_t;

typedef struct gua_game_input_request_v1_t {
    uint32_t struct_size;
    uint64_t request_id;
    uint64_t owner_id;
    int kind;
    int operation;
    char target[128];
    char value_json[512];
    double x;
    double y;
    uint32_t lease_ms;
    int device_index;
    int sensitive;
} gua_game_input_request_v1_t;

enum {
    GUA_CLOCK_OK = 1,
    GUA_CLOCK_ERROR_INVALID_ARGUMENT = -1,
    GUA_CLOCK_ERROR_NOT_INSTALLED = -2,
    GUA_CLOCK_ERROR_INVALID_STATE = -3,
    GUA_CLOCK_ERROR_EXECUTION_LIMIT = -4
};

typedef struct gua_clock_status_t {
    uint32_t struct_size;
    int installed;
    int paused;
    double now_ms;
    double default_step_ms;
    double pending_ms;
    uint64_t generation;
} gua_clock_status_t;

typedef struct gua_clock_step_t {
    uint32_t struct_size;
    double delta_ms;
    int final_step;
    uint64_t generation;
} gua_clock_step_t;

typedef struct gua_clock_operation_status_t {
    uint32_t struct_size;
    uint64_t latest_operation_sequence;
    uint64_t pending_operation_sequence;
    uint64_t completed_operation_sequence;
} gua_clock_operation_status_t;

enum {
    GUA_ACTION_ACCEPTED = 1,
    GUA_ACTION_ERROR_INVALID_ARGUMENT = -1,
    GUA_ACTION_ERROR_NODE_NOT_FOUND = -2,
    GUA_ACTION_ERROR_HIDDEN = -3,
    GUA_ACTION_ERROR_DISABLED = -4,
    GUA_ACTION_ERROR_UNSUPPORTED = -5,
    GUA_ACTION_ERROR_INVALID_VALUE = -6
};

enum {
    GUA_ACTION_CANCEL_IN_FLIGHT = -1,
    GUA_ACTION_CANCEL_NOT_FOUND = 0,
    GUA_ACTION_CANCELLED = 1
};

enum {
    GUA_ACTION_STATUS_SUCCEEDED = 1,
    GUA_ACTION_STATUS_FAILED = 2
};

typedef struct gua_action_request_descriptor_t {
    uint32_t struct_size;
    int action;
    const char* node_id;
    const char* value;
    float delta_x;
    float delta_y;
    int bool_value;
    const char* key;
    uint32_t modifiers;
    int sensitive;
    int scroll_unit;
} gua_action_request_descriptor_t;

typedef struct gua_action_request_t {
    uint32_t struct_size;
    uint64_t request_id;
    int action;
    char node_id[128];
    char value[256];
    float delta_x;
    float delta_y;
    int bool_value;
    char key[64];
    uint32_t modifiers;
    int sensitive;
    int scroll_unit;
} gua_action_request_t;

typedef struct gua_action_result_t {
    uint32_t struct_size;
    uint64_t request_id;
    int action;
    int status;
    int error_code;
    const char* node_id;
    const char* value;
    int sensitive;
} gua_action_result_t;

typedef struct gua_event_v2_t {
    uint32_t struct_size;
    uint64_t request_id;
    int action;
    int status;
    int error_code;
    char node_id[128];
    char value[256];
    int sensitive;
} gua_event_v2_t;

typedef struct gua_event_v3_t {
    uint32_t struct_size;
    gua_event_v2_t base;
    uint64_t session_epoch;
    uint64_t frame_sequence;
    uint64_t revision;
} gua_event_v3_t;

enum {
    GUA_RESET_NODES = 1U << 0,
    GUA_RESET_REQUESTS = 1U << 1,
    GUA_RESET_EVENTS = 1U << 2,
    GUA_RESET_HISTORY = 1U << 3,
    GUA_RESET_LOGS = 1U << 4,
    GUA_RESET_SCREENSHOT = 1U << 5,
    GUA_RESET_CLOCK = 1U << 6,
    GUA_RESET_WORLD_OBJECTS = 1U << 7,
    /* Published legacy value. Use GUA_RESET_DEFAULT_V3 for current default behavior. */
    GUA_RESET_DEFAULT = GUA_RESET_NODES | GUA_RESET_REQUESTS | GUA_RESET_EVENTS | GUA_RESET_HISTORY,
    GUA_RESET_DEFAULT_V2 = GUA_RESET_DEFAULT | GUA_RESET_CLOCK,
    GUA_RESET_DEFAULT_V3 = GUA_RESET_DEFAULT_V2 | GUA_RESET_WORLD_OBJECTS
};

enum {
    GUA_RESET_FLAGS_VERSION_LEGACY = 0,
    GUA_RESET_FLAGS_VERSION_V1 = 1,
    GUA_RESET_FLAGS_VERSION_CURRENT = 2
};

enum {
    GUA_RESET_SUCCEEDED = 1,
    GUA_RESET_ERROR_INVALID_ARGUMENT = -1,
    GUA_RESET_ERROR_DIRTY = -2,
    GUA_RESET_ERROR_STALE_EPOCH = -3
};

typedef struct gua_context_status_t {
    uint32_t struct_size;
    uint64_t session_epoch;
    uint64_t frame_sequence;
    uint64_t revision;
    uint32_t node_count;
    uint32_t pending_request_count;
    uint32_t in_flight_request_count;
    uint32_t unconsumed_event_count;
    uint32_t log_count;
    int has_screenshot;
    int first_pending_action;
    char first_pending_node_id[128];
    int first_event_action;
    char first_event_node_id[128];
    uint64_t world_frame_sequence;
    uint64_t world_revision;
    uint32_t world_object_count;
} gua_context_status_t;

typedef struct gua_reset_options_t {
    uint32_t struct_size;
    uint32_t flags;
    int strict;
    uint64_t expected_session_epoch;
    uint32_t flags_version;
} gua_reset_options_t;

typedef struct gua_reset_report_t {
    uint32_t struct_size;
    int result;
    uint64_t previous_session_epoch;
    uint64_t session_epoch;
    uint32_t pending_request_count;
    uint32_t in_flight_request_count;
    uint32_t unconsumed_event_count;
    uint32_t discarded_node_count;
    uint32_t discarded_pending_request_count;
    uint32_t discarded_in_flight_request_count;
    uint32_t discarded_event_count;
    uint32_t discarded_log_count;
    int discarded_screenshot;
    int first_pending_action;
    char first_pending_node_id[128];
    int first_event_action;
    char first_event_node_id[128];
    uint32_t discarded_world_object_count;
} gua_reset_report_t;

enum {
    GUA_WORLD_SPACE_2D = 1,
    GUA_WORLD_SPACE_3D = 2
};

enum {
    GUA_AGENT_EXPOSURE_AUTO = 0,
    GUA_AGENT_EXPOSURE_PRIVATE = 1
};

enum {
    GUA_AGENT_FIELD_KEEP = 0,
    GUA_AGENT_FIELD_OMIT = 1,
    GUA_AGENT_FIELD_REDACT = 2,
    GUA_AGENT_FIELD_REPLACE = 3,
    GUA_AGENT_FIELD_QUANTIZE = 4
};

typedef struct gua_agent_field_rule_v1_t {
    uint32_t struct_size;
    const char* path;
    int mode;
    /* Uses GUA_WORLD_VALUE_* primitive tags. */
    int replacement_type;
    const char* string_value;
    double number_value;
    int bool_value;
    double quantum;
} gua_agent_field_rule_v1_t;

typedef struct gua_agent_policy_v1_t {
    uint32_t struct_size;
    int exposure;
    int has_allowed_actions;
    uint64_t allowed_actions;
    const gua_agent_field_rule_v1_t* field_rules;
    uint32_t field_rule_count;
} gua_agent_policy_v1_t;

enum {
    GUA_OBSERVATION_PROFILE_DEBUG = 0,
    GUA_OBSERVATION_PROFILE_PLAYER = 1
};

enum {
    GUA_WORLD_VALUE_NULL = 0,
    GUA_WORLD_VALUE_STRING = 1,
    GUA_WORLD_VALUE_NUMBER = 2,
    GUA_WORLD_VALUE_BOOLEAN = 3
};

typedef struct gua_world_state_value_v1_t {
    uint32_t struct_size;
    const char* key;
    int type;
    const char* string_value;
    double number_value;
    int bool_value;
} gua_world_state_value_v1_t;

typedef struct gua_world_object_descriptor_v1_t {
    uint32_t struct_size;
    const char* id;
    const char* parent_id;
    const char* kind;
    const char* label;
    const char* description;
    int space;
    double position_x;
    double position_y;
    double position_z;
    int visible_to_player;
    int active;
    int agent_exposure;
    const char* domain_id;
    const char* related_ui_node_id;
    const char* const* tags;
    uint32_t tag_count;
    const gua_world_state_value_v1_t* state_values;
    uint32_t state_value_count;
} gua_world_object_descriptor_v1_t;

typedef struct gua_world_object_descriptor_v2_t {
    uint32_t struct_size;
    gua_world_object_descriptor_v1_t base;
    gua_agent_policy_v1_t agent_policy;
} gua_world_object_descriptor_v2_t;

typedef struct gua_world_selector_v1_t {
    uint32_t struct_size;
    const char* id;
    int id_match;
    const char* kind;
    int kind_match;
    const char* label;
    int label_match;
    const char* tag;
    int tag_match;
    const char* parent_id;
    int direct_child;
    int visible_to_player;
    int active;
    const gua_world_state_value_v1_t* state;
} gua_world_selector_v1_t;

typedef struct gua_world_near_v1_t {
    uint32_t struct_size;
    const char* relative_to_object_id;
    double max_distance;
} gua_world_near_v1_t;

typedef struct gua_world_selector_v2_t {
    uint32_t struct_size;
    gua_world_selector_v1_t base;
    const gua_world_near_v1_t* near;
    uint32_t limit;
} gua_world_selector_v2_t;

typedef struct gua_node_state_t {
    int visible;
    int enabled;
} gua_node_state_t;

enum {
    GUA_NODE_KNOWN_PARENT_ID = 1ULL << 0,
    GUA_NODE_KNOWN_TEXT = 1ULL << 1,
    GUA_NODE_KNOWN_VALUE = 1ULL << 2,
    GUA_NODE_KNOWN_FOCUSED = 1ULL << 3,
    GUA_NODE_KNOWN_HOVERED = 1ULL << 4,
    GUA_NODE_KNOWN_PRESSED = 1ULL << 5,
    GUA_NODE_KNOWN_CHECKED = 1ULL << 6,
    GUA_NODE_KNOWN_SELECTED = 1ULL << 7,
    GUA_NODE_KNOWN_CARET_POSITION = 1ULL << 8,
    GUA_NODE_KNOWN_SELECTION = 1ULL << 9,
    GUA_NODE_KNOWN_SCROLL = 1ULL << 10,
    GUA_NODE_KNOWN_SCROLL_MAX = 1ULL << 11,
    GUA_NODE_KNOWN_RANGE_VALUE = 1ULL << 12,
    GUA_NODE_KNOWN_RANGE_MIN = 1ULL << 13,
    GUA_NODE_KNOWN_RANGE_MAX = 1ULL << 14,
    GUA_NODE_KNOWN_SELECTED_INDEX = 1ULL << 15
};

typedef struct gua_node_descriptor_v2_t {
    uint32_t struct_size;
    uint64_t known_mask;
    const char* id;
    const char* parent_id;
    const char* role;
    const char* label;
    const char* text;
    const char* value;
    gua_bounds_t bounds;
    int visible;
    int enabled;
    int focused;
    int hovered;
    int pressed;
    int checked;
    int selected;
} gua_node_descriptor_v2_t;

typedef struct gua_node_state_v2_t {
    uint32_t struct_size;
    uint64_t known_mask;
    int visible;
    int enabled;
    int focused;
    int hovered;
    int pressed;
    int checked;
    int selected;
    char parent_id[128];
    char text[256];
    char value[256];
} gua_node_state_v2_t;

typedef struct gua_node_descriptor_v3_t {
    uint32_t struct_size;
    gua_node_descriptor_v2_t base;
    int64_t caret_position, selection_start, selection_end;
    double scroll_x, scroll_y, scroll_max_x, scroll_max_y;
    double range_value, range_min, range_max;
    int64_t selected_index;
} gua_node_descriptor_v3_t;

typedef struct gua_node_descriptor_v4_t {
    uint32_t struct_size;
    gua_node_descriptor_v3_t base;
    gua_agent_policy_v1_t agent_policy;
} gua_node_descriptor_v4_t;

typedef struct gua_node_state_v3_t {
    uint32_t struct_size;
    gua_node_state_v2_t base;
    int64_t caret_position, selection_start, selection_end;
    double scroll_x, scroll_y, scroll_max_x, scroll_max_y;
    double range_value, range_min, range_max;
    int64_t selected_index;
} gua_node_state_v3_t;

enum {
    GUA_MATCH_EXACT = 0,
    GUA_MATCH_CONTAINS = 1,
    GUA_MATCH_REGEX = 2
};

enum {
    GUA_FILTER_ANY = 0,
    GUA_FILTER_FALSE = 1,
    GUA_FILTER_TRUE = 2
};

typedef struct gua_selector_v1_t {
    uint32_t struct_size;
    const char* id;
    int id_match;
    const char* role;
    int role_match;
    const char* name;
    int name_match;
    const char* text;
    int text_match;
    const char* parent_id;
    int direct_child;
    int visible;
    int enabled;
} gua_selector_v1_t;

enum {
    GUA_LOG_TRACE = 0,
    GUA_LOG_DEBUG = 1,
    GUA_LOG_INFO = 2,
    GUA_LOG_WARN = 3,
    GUA_LOG_ERROR = 4
};

enum {
    GUA_EVENT_NONE = 0,
    GUA_EVENT_CLICK = 1,
    GUA_EVENT_FOCUS = 2
};

gua_context_t* gua_create_context(void);
void gua_destroy_context(gua_context_t* ctx);

void gua_begin_frame(gua_context_t* ctx, const char* screen);
void gua_end_frame(gua_context_t* ctx);

void gua_register_node(
    gua_context_t* ctx,
    const char* id,
    const char* role,
    const char* label,
    gua_bounds_t bounds,
    int visible,
    int enabled
);
int gua_register_node_v2(gua_context_t* ctx, const gua_node_descriptor_v2_t* descriptor);
int gua_register_node_v3(gua_context_t* ctx, const gua_node_descriptor_v3_t* descriptor);
int gua_register_node_v4(gua_context_t* ctx, const gua_node_descriptor_v4_t* descriptor);

const char* gua_get_ui_tree_json(gua_context_t* ctx);
/* Returns the required byte size including the trailing NUL. Output is NUL-terminated when out_json_size > 0. */
int gua_copy_ui_tree_json(gua_context_t* ctx, char* out_json, int out_json_size);
int gua_copy_ui_tree_json_for_profile(gua_context_t* ctx, int observation_profile, char* out_json, int out_json_size);
void gua_add_log(gua_context_t* ctx, int level, const char* message);
const char* gua_get_logs_json(gua_context_t* ctx);
/* Returns the required byte size including the trailing NUL. Output is NUL-terminated when out_json_size > 0. */
int gua_copy_logs_json(gua_context_t* ctx, char* out_json, int out_json_size);
void gua_set_screenshot(gua_context_t* ctx, const char* data_uri, int width, int height);
const char* gua_get_screenshot_json(gua_context_t* ctx);
/* Returns the required byte size including the trailing NUL. Output is NUL-terminated when out_json_size > 0. */
int gua_copy_screenshot_json(gua_context_t* ctx, char* out_json, int out_json_size);
/* Configures the bounded diagnostics history. A limit of 0 disables retained history. */
int gua_set_diagnostics_history_limit(gua_context_t* ctx, uint32_t history_limit);
/* Stores caller-provided environment metadata as a JSON object for later diagnostics capture. */
int gua_set_diagnostics_environment_json(gua_context_t* ctx, const char* environment_json);
const char* gua_get_diagnostics_json(gua_context_t* ctx);
/* Returns the required byte size including the trailing NUL. Output is NUL-terminated when out_json_size > 0. */
int gua_copy_diagnostics_json(gua_context_t* ctx, char* out_json, int out_json_size);
int gua_copy_diagnostics_json_for_profile(gua_context_t* ctx, int observation_profile, char* out_json, int out_json_size);
/* Returns version/capability JSON. The required byte size includes the trailing NUL. */
int gua_copy_version_json(char* out_json, int out_json_size);
int gua_clock_install(gua_context_t* ctx, double initial_time_ms, double step_ms);
int gua_clock_pause(gua_context_t* ctx);
int gua_clock_run_for(gua_context_t* ctx, double duration_ms, double step_ms);
int gua_clock_advance(gua_context_t* ctx, double duration_ms);
int gua_clock_resume(gua_context_t* ctx);
int gua_clock_get_status(gua_context_t* ctx, gua_clock_status_t* out_status);
int gua_clock_get_operation_status(gua_context_t* ctx, gua_clock_operation_status_t* out_status);
int gua_clock_copy_status_json(gua_context_t* ctx, char* out_json, int out_json_size);
int gua_clock_consume_step(gua_context_t* ctx, gua_clock_step_t* out_step);
int gua_get_node_state(gua_context_t* ctx, const char* node_id, gua_node_state_t* out_state);
/* Returns 0 rather than a partial state when a v2 string does not fit its fixed output buffer. */
int gua_get_node_state_v2(gua_context_t* ctx, const char* node_id, gua_node_state_v2_t* out_state);
int gua_get_node_state_v2_for_profile(gua_context_t* ctx, const char* node_id, int observation_profile, gua_node_state_v2_t* out_state);
int gua_get_node_state_v3(gua_context_t* ctx, const char* node_id, gua_node_state_v3_t* out_state);
int gua_find_node_by_id(gua_context_t* ctx, const char* node_id, char* out_node_id, int out_node_id_size);
int gua_find_node_by_role(gua_context_t* ctx, const char* role, const char* name, char* out_node_id, int out_node_id_size);
int gua_find_node_by_text(gua_context_t* ctx, const char* text, char* out_node_id, int out_node_id_size);
int gua_find_node_by_id_for_profile(gua_context_t* ctx, const char* node_id, int observation_profile, char* out_node_id, int out_node_id_size);
int gua_find_node_by_role_for_profile(gua_context_t* ctx, const char* role, const char* name, int observation_profile, char* out_node_id, int out_node_id_size);
int gua_find_node_by_text_for_profile(gua_context_t* ctx, const char* text, int observation_profile, char* out_node_id, int out_node_id_size);
/* Returns the required JSON byte size including the trailing NUL. The result contains valid, matches, and optional error fields. */
int gua_query_nodes_json(gua_context_t* ctx, const gua_selector_v1_t* selector, char* out_json, int out_json_size);
int gua_query_nodes_json_for_profile(gua_context_t* ctx, const gua_selector_v1_t* selector, int observation_profile, char* out_json, int out_json_size);
int gua_begin_world_frame(gua_context_t* ctx, const char* scene);
int gua_register_world_object_v1(gua_context_t* ctx, const gua_world_object_descriptor_v1_t* descriptor);
int gua_register_world_object_v2(gua_context_t* ctx, const gua_world_object_descriptor_v2_t* descriptor);
int gua_end_world_frame(gua_context_t* ctx);
int gua_abort_world_frame(gua_context_t* ctx);
int gua_copy_world_object_tree_json(gua_context_t* ctx, int observation_profile, char* out_json, int out_json_size);
int gua_query_world_objects_json(gua_context_t* ctx, const gua_world_selector_v1_t* selector, int observation_profile, char* out_json, int out_json_size);
/* v2 keeps matches as World Object values and adds snapshot metadata plus optional spatial metadata. limit 0 means unbounded. */
int gua_query_world_objects_v2_json(gua_context_t* ctx, const gua_world_selector_v2_t* selector, int observation_profile, char* out_json, int out_json_size);
int gua_enqueue_click(gua_context_t* ctx, const char* node_id);
int gua_consume_click_request(gua_context_t* ctx, const char* node_id);
int gua_emit_click(gua_context_t* ctx, const char* node_id);
int gua_poll_event(gua_context_t* ctx, gua_event_t* out_event);
int gua_enqueue_action(gua_context_t* ctx, const gua_action_request_descriptor_t* descriptor, uint64_t* out_request_id);
int gua_enqueue_action_for_profile(gua_context_t* ctx, const gua_action_request_descriptor_t* descriptor, int observation_profile, uint64_t* out_request_id);
/* Cancels only a queued, not-yet-consumed request. Returns GUA_ACTION_CANCEL_* above. */
int gua_cancel_action_request(gua_context_t* ctx, uint64_t request_id);
/* Returns the captured GUA_OBSERVATION_PROFILE_* value, or -1 when the request is unknown. */
int gua_get_action_request_observation_profile(gua_context_t* ctx, uint64_t request_id);
int gua_consume_action_request(gua_context_t* ctx, int action, const char* node_id, gua_action_request_t* out_request);
int gua_emit_action_result(gua_context_t* ctx, const gua_action_result_t* result);
int gua_poll_event_v2(gua_context_t* ctx, gua_event_v2_t* out_event);
int gua_poll_event_v2_for_request(gua_context_t* ctx, uint64_t request_id, gua_event_v2_t* out_event);
int gua_poll_event_v2_for_profile(gua_context_t* ctx, int observation_profile, gua_event_v2_t* out_event);
int gua_poll_event_v2_for_request_and_profile(gua_context_t* ctx, uint64_t request_id, int observation_profile, gua_event_v2_t* out_event);
int gua_poll_event_v3(gua_context_t* ctx, gua_event_v3_t* out_event);
int gua_poll_event_v3_for_request(gua_context_t* ctx, uint64_t request_id, gua_event_v3_t* out_event);
int gua_poll_event_v3_for_profile(gua_context_t* ctx, int observation_profile, gua_event_v3_t* out_event);
int gua_poll_event_v3_for_request_and_profile(gua_context_t* ctx, uint64_t request_id, int observation_profile, gua_event_v3_t* out_event);
int gua_get_context_status(gua_context_t* ctx, gua_context_status_t* out_status);
int gua_reset_context(gua_context_t* ctx, const gua_reset_options_t* options, gua_reset_report_t* out_report);

/* Game input action-map publication is atomic and independent from the UI tree. */
int gua_begin_game_input_frame(gua_context_t* ctx, const char* input_context);
int gua_register_game_input_action_v1(gua_context_t* ctx, const gua_game_input_action_descriptor_v1_t* descriptor);
int gua_register_game_input_action_v3(gua_context_t* ctx, const gua_game_input_action_descriptor_v3_t* descriptor);
int gua_register_game_input_action_v2(gua_context_t* ctx, const gua_game_input_action_descriptor_v2_t* descriptor);
int gua_end_game_input_frame(gua_context_t* ctx);
int gua_abort_game_input_frame(gua_context_t* ctx);
int gua_copy_game_input_actions_json_v2(gua_context_t* ctx, int observation_profile, char* out_json, int out_json_size);
int gua_query_game_input_actions_json_v2(gua_context_t* ctx, const gua_game_input_action_selector_v1_t* selector,
    int observation_profile, char* out_json, int out_json_size);
int gua_copy_game_input_actions_json(gua_context_t* ctx, char* out_json, int out_json_size);
int gua_copy_game_input_actions_json_for_profile(gua_context_t* ctx, int observation_profile, char* out_json, int out_json_size);
int gua_query_game_input_actions_json(gua_context_t* ctx, const gua_game_input_action_selector_v1_t* selector,
    int observation_profile, char* out_json, int out_json_size);
/* Owners isolate held inputs. A zero owner is invalid. */
uint64_t gua_create_game_input_owner(gua_context_t* ctx);
int gua_release_game_input_owner(gua_context_t* ctx, uint64_t owner_id);
int gua_enqueue_game_input(gua_context_t* ctx, const gua_game_input_request_descriptor_v1_t* descriptor, uint64_t* out_request_id);
int gua_enqueue_game_input_v2(gua_context_t* ctx, const gua_game_input_request_descriptor_v2_t* descriptor, uint64_t* out_request_id);
int gua_enqueue_game_input_for_profile_v2(gua_context_t* ctx, const gua_game_input_request_descriptor_v2_t* descriptor,
    int observation_profile, uint64_t* out_request_id);
int gua_consume_game_input_request(gua_context_t* ctx, gua_game_input_request_v1_t* out_request);
/* Explicit guarded path. Epoch and semantic Action Map revision must still match at enqueue
 * and consumption. Cleanup is sent through the existing owner-scoped path. */
int gua_enqueue_game_input_guarded_v2(gua_context_t* ctx, const gua_game_input_request_descriptor_v2_t* descriptor,
    int observation_profile, uint64_t expected_session_epoch, uint64_t expected_action_revision, uint64_t* out_request_id);
/* Pure preflight through the same validator; no owner, request ID, queue, result or Trace side effects.
 * Guarded payloads must fit the request's 512-byte value_json buffer including the terminator. */
int gua_validate_game_input_guarded_v2(gua_context_t* ctx, const gua_game_input_request_descriptor_v2_t* descriptor,
    int observation_profile, uint64_t expected_session_epoch, uint64_t expected_action_revision);
int gua_complete_game_input_request(gua_context_t* ctx, uint64_t request_id, int succeeded, int error_code);
/* Advance safety leases with unscaled host time, never GuaClock time. */
int gua_tick_game_input_leases(gua_context_t* ctx, double elapsed_ms);
int gua_copy_game_input_state_json(gua_context_t* ctx, uint64_t owner_id, char* out_json, int out_json_size);
/* Atomic, values-free active-owner lifetime safety evidence, independent of journal retention.
 * lease_expired remains set until this owner disconnects. Returns 0 for a missing owner. */
int gua_get_game_input_owner_health(gua_context_t* ctx, uint64_t owner_id, uint64_t* out_session_epoch, int* out_lease_expired);
int gua_copy_game_input_result_json(gua_context_t* ctx, uint64_t owner_id, uint64_t request_id, char* out_json, int out_json_size);

#ifdef __cplusplus
}
#endif

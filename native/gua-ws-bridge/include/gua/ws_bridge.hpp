#pragma once

#include <functional>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace gua::ws {

struct QuerySelector {
    std::string id;
    int id_match = 0;
    std::string role;
    int role_match = 0;
    std::string name;
    int name_match = 0;
    std::string text;
    int text_match = 0;
    std::string parent_id;
    bool direct_child = false;
    int visible = 0;
    int enabled = 0;
};

struct WorldQuerySelector {
    std::string id; int id_match = 0;
    std::string kind; int kind_match = 0;
    std::string label; int label_match = 0;
    std::string tag; int tag_match = 0;
    std::string parent_id; bool direct_child = false;
    int visible_to_player = 0; int active = 0;
    std::string state_key;
    int state_type = -1;
    std::string state_string;
    double state_number = 0;
    bool state_bool = false;
    std::string relative_to_object_id;
    double max_distance = 0;
    unsigned int limit = 0;
    bool spatial = false;
};

struct GameInputQuerySelector {
    std::string id;
    std::string query;
    int value_type = 0;
    int active = 0;
    std::string context;
    std::string category;
    std::vector<std::string> tags;
    unsigned int limit = 20;
};

namespace detail {
[[nodiscard]] bool valid_game_input_query_selector(const GameInputQuerySelector& selector);
[[nodiscard]] bool valid_game_input_query_request_json(std::string_view json);
}

struct ActionCommand {
    std::string type;
    std::string node_id;
    std::string value;
    float delta_x = 0;
    float delta_y = 0;
    bool bool_value = false;
    std::string key;
    unsigned int modifiers = 0;
    bool sensitive = false;
    int scroll_unit = 0;
    bool guarded = false;
    unsigned long long owner_id = 0, expected_epoch = 0, expected_revision = 0;
    int expected_profile = -1;
};

struct GameInputCommand {
    std::string type;
    std::string target;
    std::string value_json = "null";
    double x = 0;
    double y = 0;
    unsigned int lease_ms = 5000;
    int device_index = 0;
    bool sensitive = false;
    bool confirmed = false;
    bool guarded = false;
    unsigned long long expected_epoch = 0, expected_revision = 0;
    int expected_profile = -1;
};

struct CommandResult {
    bool ok = false;
    std::string json;
    std::string error;
};

struct BridgeHandlers {
    std::function<unsigned long long()> create_spatial_client;
    std::function<void(unsigned long long)> release_spatial_client;
    std::function<CommandResult(unsigned long long, int, std::string_view, unsigned long long)> spatial_command;
    std::function<unsigned long long()> create_observe_client;
    std::function<void(unsigned long long)> release_observe_client;
    std::function<CommandResult(unsigned long long, int, unsigned long long)> observe_command;
    std::function<std::string()> get_ui_tree_json;
    std::function<std::string()> get_world_object_tree_json;
    std::function<std::string()> get_logs_json;
    std::function<std::string()> get_screenshot_json;
    std::function<std::string()> get_snapshot_json;
    std::function<CommandResult(unsigned long long after_frame_sequence, unsigned int timeout_ms)> capture_screenshot;
    std::function<std::string()> get_diagnostics_json;
    std::function<std::string(unsigned long long)> get_connection_diagnostics_json;
    std::function<CommandResult(bool include_world)> semantic_lint;
    std::function<std::string()> get_version_json;
    std::function<std::string(unsigned long long)> get_connection_version_json;
    std::function<bool()> clock_supported;
    std::function<std::string()> get_clock_json;
    std::function<CommandResult(std::string_view command, double value_ms, double step_ms, bool step_ms_present)> control_clock;
    std::function<std::string(const QuerySelector& selector)> query_nodes_json;
    std::function<std::string(const WorldQuerySelector& selector)> query_world_objects_json;
    std::function<std::string()> get_context_status_json;
    std::function<std::string(unsigned long long expected_epoch, unsigned int flags, unsigned int flags_version, bool strict)> reset_context_json;
    std::function<bool(std::string_view node_id)> click_node;
    std::function<bool(std::string_view node_id)> focus_node;
    std::function<bool(std::string_view key)> press_key;
    std::function<long long(const ActionCommand& command)> enqueue_action;
    std::function<std::string(unsigned long long request_id)> poll_action_event_json;
    std::function<unsigned long long()> create_game_input_owner;
    std::function<void(unsigned long long owner_id)> release_game_input_owner;
    std::function<bool(unsigned int capability)> game_input_supported;
    std::function<std::string()> get_game_input_actions_json;
    std::function<std::string(const GameInputQuerySelector& selector)> query_game_input_actions_json;
    std::function<std::string()> get_game_input_actions_json_v2;
    std::function<std::string(const GameInputQuerySelector& selector)> query_game_input_actions_json_v2;
    std::function<std::string(unsigned long long owner_id)> get_game_input_state_json;
    std::function<long long(unsigned long long owner_id, const GameInputCommand& command)> enqueue_game_input;
    std::function<std::string(unsigned long long owner_id, unsigned long long request_id)> poll_game_input_result_json;
    std::function<bool()> guarded_dispatch_supported;
    std::function<CommandResult(bool, unsigned long long, unsigned long long, unsigned long long, int)> poll_guarded_result;
};

struct BridgeOptions {
    unsigned short port = 8765;
};

class BridgeServer {
public:
    BridgeServer(BridgeHandlers handlers, BridgeOptions options = {});
    BridgeServer(const BridgeServer&) = delete;
    BridgeServer& operator=(const BridgeServer&) = delete;
    BridgeServer(BridgeServer&&) noexcept;
    BridgeServer& operator=(BridgeServer&&) noexcept;
    ~BridgeServer();

    void start();
    void stop();
    void publish_snapshot();
    [[nodiscard]] bool running() const;
    [[nodiscard]] unsigned short port() const;

private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace gua::ws

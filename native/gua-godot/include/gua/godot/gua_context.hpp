#pragma once
#include "gua/godot/spatial_host.hpp"

#include "gua/runtime.h"

#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/rect2.hpp>
#include <godot_cpp/variant/string.hpp>
#include <godot_cpp/variant/variant.hpp>

namespace godot {

class GuaContext : public RefCounted {
    GDCLASS(GuaContext, RefCounted)

public:
    GuaContext();
    ~GuaContext();

    void begin_frame(const String& screen);
    void end_frame();
    void register_node(
        const String& id,
        const String& role,
        const String& label,
        const Rect2& bounds,
        bool visible = true,
        bool enabled = true);
    bool register_node_v2(const Dictionary& descriptor);
    bool begin_world_frame(const String& scene);
    bool register_world_object(const Dictionary& descriptor);
    bool end_world_frame();
    bool abort_world_frame();
    String get_world_object_tree_json() const;
    String query_world_objects_json(const Dictionary& selector) const;
    String get_player_world_object_tree_json() const;
    String query_player_world_objects_json(const Dictionary& selector) const;
    void enable_world_object_tree_adapter();
    int bind_spatial(const Ref<GuaSpatialHost>& host, uint64_t provider, const String& grants);
    int disable_spatial();
    uint64_t create_observe_owner(int source, const String& runtime_id);
    int destroy_observe_owner(uint64_t owner);
    uint64_t register_observe(uint64_t owner, const String& name, bool allow_player, bool sensitive);
    int unregister_observe(uint64_t registration);
    int observe_registration_alive(uint64_t registration) const;
    int register_value_enum(const String& enum_type, const Array& members);
    int publish_observe_json(uint64_t registration, const String& json, bool stage);
    uint64_t create_observe_client(int profile);
    void release_observe_client(uint64_t client);
    Dictionary observe_command(uint64_t client, int command, uint64_t subscription);

    String get_ui_tree_json() const;
    String get_player_ui_tree_json() const;
    String get_version_json() const;
    int get_observation_profile() const;
    void set_screenshot(const String& data_uri, int width, int height);
    String get_screenshot_json() const;
    Dictionary consume_screenshot_request();
    bool complete_screenshot_request(const Dictionary& result);
    bool enqueue_click(const String& node_id);
    bool consume_click_request(const String& node_id);
    bool emit_click(const String& node_id);
    Dictionary poll_event();
    Dictionary enqueue_action(const Dictionary& request);
    Dictionary enqueue_player_action(const Dictionary& request);
    int cancel_action_request(uint64_t request_id);
    Dictionary consume_action_request(const String& action, const String& node_id);
    bool emit_action_result(const Dictionary& result);
    Dictionary poll_event_v2();
    Dictionary poll_action_result(uint64_t request_id);
    Dictionary get_context_status() const;
    Dictionary reset_context(const Dictionary& options = Dictionary());
    Dictionary clock_install(double initial_time_ms = 0.0, double step_ms = 1000.0 / 60.0);
    Dictionary clock_pause();
    Dictionary clock_run_for(double duration_ms, const Variant& step_ms = Variant());
    Dictionary clock_resume();
    Dictionary clock_advance(double duration_ms);
    Dictionary get_clock() const;
    Dictionary consume_clock_step();
    Array consume_clock_steps();
    void enable_virtual_clock_adapter();
    bool publish_game_input_actions(const String& input_context, const Array& actions);
    String get_game_input_actions_json_v2(int observation_profile = 0) const;
    String find_game_input_actions_json_v2(const Dictionary& selector, int observation_profile = 0) const;
    String find_game_input_actions_json_impl(const Dictionary& selector, int observation_profile, bool metadata) const;
    String get_game_input_actions_json() const;
    String get_player_game_input_actions_json() const;
    String find_game_input_actions_json(const Dictionary& selector, int observation_profile = 0) const;
    void enable_game_input_adapter(int capabilities, int player_capabilities = 0);
    int get_game_input_capabilities(int observation_profile) const;
    uint64_t create_game_input_owner();
    bool release_game_input_owner(uint64_t owner_id);
    Dictionary enqueue_game_input(const Dictionary& request);
    String get_game_input_state_json(uint64_t owner_id) const;
    String get_game_input_result_json(uint64_t owner_id, uint64_t request_id) const;
    Dictionary consume_game_input_request();
    bool complete_game_input_request(const Dictionary& result);
    int tick_game_input_leases(double elapsed_ms);
    bool start_inspector_bridge(int port = 8765);
    void stop_inspector_bridge();
    bool inspector_bridge_running() const;
    String inspector_bridge_url() const;
    void publish_inspector_snapshot();

protected:
    static void _bind_methods();

private:
    String query_world_objects_json_with_projection(const Dictionary& selector, bool player_projection) const;
    gua_runtime_t* runtime_ = nullptr;
    gua_enum_catalog_t* observe_catalog_ = nullptr;
};

} // namespace godot

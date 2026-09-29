#pragma once

#include "gua/gua.h"

#include <algorithm>
#include <functional>
#include <list>
#include <stdexcept>
#include <cstdint>
#include <optional>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>
#include <unordered_set>

namespace gua {

class Context;

struct ContextLifetime {
    Context* context = nullptr;
    std::list<std::function<bool(bool)>> frame_observers;
};

struct Rect {
    float x;
    float y;
    float w;
    float h;
};

enum class EventType {
    none = GUA_EVENT_NONE,
    click = GUA_EVENT_CLICK,
    focus = GUA_EVENT_FOCUS,
};

enum class LogLevel {
    trace = GUA_LOG_TRACE,
    debug = GUA_LOG_DEBUG,
    info = GUA_LOG_INFO,
    warn = GUA_LOG_WARN,
    error = GUA_LOG_ERROR,
};

struct Event {
    EventType type = EventType::none;
    std::string node_id;
};

enum class ActionType {
    click = GUA_ACTION_CLICK, focus = GUA_ACTION_FOCUS, set_value = GUA_ACTION_SET_VALUE,
    set_checked = GUA_ACTION_SET_CHECKED, select = GUA_ACTION_SELECT, scroll = GUA_ACTION_SCROLL,
    press_key = GUA_ACTION_PRESS_KEY,
};

enum class ObservationProfile { debug = GUA_OBSERVATION_PROFILE_DEBUG, player = GUA_OBSERVATION_PROFILE_PLAYER };
enum class AgentExposure { automatic = GUA_AGENT_EXPOSURE_AUTO, private_ = GUA_AGENT_EXPOSURE_PRIVATE };
enum class AgentFieldMode {
    keep = GUA_AGENT_FIELD_KEEP, omit = GUA_AGENT_FIELD_OMIT, redact = GUA_AGENT_FIELD_REDACT,
    replace = GUA_AGENT_FIELD_REPLACE, quantize = GUA_AGENT_FIELD_QUANTIZE,
};

struct AgentFieldRule {
    std::string path;
    AgentFieldMode mode = AgentFieldMode::keep;
    int replacement_type = GUA_WORLD_VALUE_NULL;
    std::string string_value;
    double number_value = 0, quantum = 0;
    bool bool_value = false;
};

struct AgentPolicy {
    AgentExposure exposure = AgentExposure::automatic;
    std::optional<std::uint64_t> allowed_actions;
    std::vector<AgentFieldRule> field_rules;
};

enum class ActionCancelResult {
    in_flight = GUA_ACTION_CANCEL_IN_FLIGHT,
    not_found = GUA_ACTION_CANCEL_NOT_FOUND,
    cancelled = GUA_ACTION_CANCELLED,
};

struct ActionRequest {
    std::uint64_t request_id = 0;
    ActionType action = ActionType::click;
    std::string node_id;
    std::string value;
    float delta_x = 0;
    float delta_y = 0;
    bool bool_value = false;
    std::string key;
    std::uint32_t modifiers = 0;
    bool sensitive = false;
    int scroll_unit = 0;
};

struct ActionEvent {
    std::uint64_t request_id = 0;
    ActionType action = ActionType::click;
    int status = GUA_ACTION_STATUS_SUCCEEDED;
    int error_code = 0;
    std::string node_id;
    std::string value;
    bool sensitive = false;
};

enum class GameInputValueType { button = GUA_GAME_INPUT_BUTTON, axis1d = GUA_GAME_INPUT_AXIS1D, vector2 = GUA_GAME_INPUT_VECTOR2, text = GUA_GAME_INPUT_TEXT };
struct GameInputAction {
    std::string id, description;
    GameInputValueType value_type = GameInputValueType::button;
    std::optional<double> minimum, maximum;
    bool holdable = false, active = true;
    std::string bindings_json = "[]";
    std::string risk = "safe";
    bool requires_confirmation = false;
    std::string category;
    std::vector<std::string> aliases;
    std::vector<std::string> tags;
    int agent_exposure = GUA_AGENT_EXPOSURE_AUTO;
};
struct GameInputActionSelector {
    std::string id, query, context, category;
    std::optional<GameInputValueType> value_type;
    std::optional<bool> active;
    std::vector<std::string> tags;
    std::uint32_t limit = 20;
};
struct GameInputActionSearchResult {
    std::string json;
};
struct GameInputRequest {
    std::uint64_t request_id = 0, owner_id = 0;
    int kind = 0, operation = 0;
    std::string target, value_json;
    double x = 0, y = 0;
    std::uint32_t lease_ms = 0;
    int device_index = 0;
    bool sensitive = false;
};

struct NodeProperties {
    std::optional<std::string_view> parent_id;
    std::optional<std::string_view> text;
    std::optional<std::string_view> value;
    std::optional<bool> focused;
    std::optional<bool> hovered;
    std::optional<bool> pressed;
    std::optional<bool> checked;
    std::optional<bool> selected;
    std::optional<std::int64_t> caret_position, selection_start, selection_end;
    std::optional<double> scroll_x, scroll_y, scroll_max_x, scroll_max_y;
    std::optional<double> range_value, range_min, range_max;
    std::optional<std::int64_t> selected_index;
};

class Context {
public:
    Context()
        : context_(gua_create_context()), lifetime_(std::make_shared<ContextLifetime>())
    {
        if (context_ == nullptr) {
            throw std::runtime_error("Failed to create Gua context");
        }
        lifetime_->context = this;
    }

    Context(const Context&) = delete;
    Context& operator=(const Context&) = delete;

    Context(Context&& other) noexcept
        : context_(other.context_), sensitive_node_ids_(std::move(other.sensitive_node_ids_)),
          lifetime_(std::move(other.lifetime_))
    {
        if (lifetime_ != nullptr) lifetime_->context = this;
        other.context_ = nullptr;
    }

    Context& operator=(Context&& other) noexcept
    {
        if (this != &other) {
            reset();
            context_ = other.context_;
            sensitive_node_ids_ = std::move(other.sensitive_node_ids_);
            lifetime_ = std::move(other.lifetime_);
            if (lifetime_ != nullptr) lifetime_->context = this;
            other.context_ = nullptr;
        }
        return *this;
    }

    ~Context()
    {
        reset();
    }

    [[nodiscard]] gua_context_t* native_handle() const noexcept
    {
        return context_;
    }

    std::weak_ptr<ContextLifetime> lifetime() const { return lifetime_; }

    void sample_observations(bool ui) {
        auto lifetime = lifetime_;
        if (!lifetime) return;
        auto observers = std::move(lifetime->frame_observers);
        try {
            while (!observers.empty()) {
                auto current = observers.begin();
                if ((*current)(ui))
                    lifetime->frame_observers.splice(lifetime->frame_observers.end(), observers, current);
                else
                    observers.erase(current);
            }
        } catch (...) {
            // Preserve the throwing and unvisited callbacks without allocation.
            // Already processed callbacks and reentrant registrations stay intact.
            lifetime->frame_observers.splice(lifetime->frame_observers.end(), observers);
            throw;
        }
    }
    void end_world_frame() { auto lifetime = lifetime_; sample_observations(false); if (lifetime && lifetime->context == this && !gua_end_world_frame(context_)) throw std::runtime_error("World frame rejected"); }

    void mark_node_sensitive(std::string_view id)
    {
        sensitive_node_ids_.emplace(id);
    }

    [[nodiscard]] bool is_node_sensitive(std::string_view id) const
    {
        return sensitive_node_ids_.contains(std::string(id));
    }

    void install_clock(double initial_time_ms = 0.0, double step_ms = 1000.0 / 60.0)
    { check_clock(gua_clock_install(context_, initial_time_ms, step_ms)); }
    void pause_clock() { check_clock(gua_clock_pause(context_)); }
    void run_clock_for(double duration_ms)
    { run_clock_for(duration_ms, clock_status().default_step_ms); }
    void run_clock_for(double duration_ms, double step_ms)
    { check_clock(gua_clock_run_for(context_, duration_ms, step_ms)); }
    void resume_clock() { check_clock(gua_clock_resume(context_)); }
    [[nodiscard]] gua_clock_status_t clock_status() const
    { gua_clock_status_t value { sizeof(gua_clock_status_t) }; if (!gua_clock_get_status(context_, &value)) throw std::runtime_error("Failed to inspect Gua clock"); return value; }
    bool poll_clock_step(gua_clock_step_t& step)
    { step = gua_clock_step_t { sizeof(gua_clock_step_t) }; return gua_clock_consume_step(context_, &step) != 0; }

    void begin_frame(std::string_view screen)
    {
        screen_buffer_.assign(screen);
        gua_begin_frame(context_, screen_buffer_.c_str());
    }

    void end_frame()
    {
        auto lifetime = lifetime_;
        sample_observations(true);
        if (lifetime && lifetime->context == this) gua_end_frame(context_);
    }

    void node(
        std::string_view id,
        std::string_view role,
        std::string_view label,
        Rect bounds,
        bool visible = true,
        bool enabled = true)
    {
        id_buffer_.assign(id);
        role_buffer_.assign(role);
        label_buffer_.assign(label);
        gua_register_node(
            context_,
            id_buffer_.c_str(),
            role_buffer_.c_str(),
            label_buffer_.c_str(),
            gua_bounds_t { bounds.x, bounds.y, bounds.w, bounds.h },
            visible ? 1 : 0,
            enabled ? 1 : 0);
    }

    void node_v2(
        std::string_view id,
        std::string_view role,
        std::string_view label,
        Rect bounds,
        const NodeProperties& properties = {},
        bool visible = true,
        bool enabled = true,
        const AgentPolicy& policy = {})
    {
        id_buffer_.assign(id);
        role_buffer_.assign(role);
        label_buffer_.assign(label);
        parent_id_buffer_ = properties.parent_id ? std::string(*properties.parent_id) : std::string();
        text_buffer_ = properties.text ? std::string(*properties.text) : std::string();
        value_buffer_ = properties.value ? std::string(*properties.value) : std::string();

        unsigned long long known_mask = 0;
        if (properties.parent_id) known_mask |= GUA_NODE_KNOWN_PARENT_ID;
        if (properties.text) known_mask |= GUA_NODE_KNOWN_TEXT;
        if (properties.value) known_mask |= GUA_NODE_KNOWN_VALUE;
        if (properties.focused) known_mask |= GUA_NODE_KNOWN_FOCUSED;
        if (properties.hovered) known_mask |= GUA_NODE_KNOWN_HOVERED;
        if (properties.pressed) known_mask |= GUA_NODE_KNOWN_PRESSED;
        if (properties.checked) known_mask |= GUA_NODE_KNOWN_CHECKED;
        if (properties.selected) known_mask |= GUA_NODE_KNOWN_SELECTED;
        if (properties.caret_position) known_mask |= GUA_NODE_KNOWN_CARET_POSITION;
        if (properties.selection_start && properties.selection_end) known_mask |= GUA_NODE_KNOWN_SELECTION;
        if (properties.scroll_x && properties.scroll_y) known_mask |= GUA_NODE_KNOWN_SCROLL;
        if (properties.scroll_max_x && properties.scroll_max_y) known_mask |= GUA_NODE_KNOWN_SCROLL_MAX;
        if (properties.range_value) known_mask |= GUA_NODE_KNOWN_RANGE_VALUE;
        if (properties.range_min) known_mask |= GUA_NODE_KNOWN_RANGE_MIN;
        if (properties.range_max) known_mask |= GUA_NODE_KNOWN_RANGE_MAX;
        if (properties.selected_index) known_mask |= GUA_NODE_KNOWN_SELECTED_INDEX;

        const gua_node_descriptor_v2_t descriptor {
            sizeof(gua_node_descriptor_v2_t),
            known_mask,
            id_buffer_.c_str(),
            properties.parent_id ? parent_id_buffer_.c_str() : nullptr,
            role_buffer_.c_str(),
            label_buffer_.c_str(),
            properties.text ? text_buffer_.c_str() : nullptr,
            properties.value ? value_buffer_.c_str() : nullptr,
            gua_bounds_t { bounds.x, bounds.y, bounds.w, bounds.h },
            visible ? 1 : 0,
            enabled ? 1 : 0,
            properties.focused.value_or(false) ? 1 : 0,
            properties.hovered.value_or(false) ? 1 : 0,
            properties.pressed.value_or(false) ? 1 : 0,
            properties.checked.value_or(false) ? 1 : 0,
            properties.selected.value_or(false) ? 1 : 0,
        };
        const gua_node_descriptor_v3_t detailed {
            sizeof(gua_node_descriptor_v3_t), descriptor,
            properties.caret_position.value_or(0), properties.selection_start.value_or(0), properties.selection_end.value_or(0),
            properties.scroll_x.value_or(0), properties.scroll_y.value_or(0), properties.scroll_max_x.value_or(0), properties.scroll_max_y.value_or(0),
            properties.range_value.value_or(0), properties.range_min.value_or(0), properties.range_max.value_or(0), properties.selected_index.value_or(-1)
        };
        std::vector<gua_agent_field_rule_v1_t> native_rules;
        native_rules.reserve(policy.field_rules.size());
        for (const auto& rule : policy.field_rules) native_rules.push_back({ sizeof(gua_agent_field_rule_v1_t), rule.path.c_str(),
            static_cast<int>(rule.mode), rule.replacement_type,
            rule.mode == AgentFieldMode::replace && rule.replacement_type == GUA_WORLD_VALUE_STRING
                ? rule.string_value.c_str() : (rule.string_value.empty() ? nullptr : rule.string_value.c_str()), rule.number_value,
            rule.bool_value ? 1 : 0, rule.quantum });
        const gua_agent_policy_v1_t native_policy { sizeof(gua_agent_policy_v1_t), static_cast<int>(policy.exposure),
            policy.allowed_actions.has_value() ? 1 : 0, policy.allowed_actions.value_or(0),
            native_rules.empty() ? nullptr : native_rules.data(), static_cast<std::uint32_t>(native_rules.size()) };
        const gua_node_descriptor_v4_t secured { sizeof(gua_node_descriptor_v4_t), detailed, native_policy };
        if (gua_register_node_v4(context_, &secured) == 0) {
            throw std::runtime_error("Failed to register Gua v2 node");
        }
    }

    void button(std::string_view id, std::string_view label, Rect bounds, bool visible = true, bool enabled = true)
    {
        node(id, "button", label, bounds, visible, enabled);
    }

    void text(std::string_view id, std::string_view label, Rect bounds, bool visible = true)
    {
        node(id, "text", label, bounds, visible, false);
    }

    void panel(std::string_view id, std::string_view label, Rect bounds, bool visible = true)
    {
        node(id, "panel", label, bounds, visible, false);
    }

    [[nodiscard]] std::string ui_tree_json() const
    {
        return copy_json(gua_copy_ui_tree_json);
    }

    [[nodiscard]] std::string ui_tree_json(ObservationProfile profile) const
    {
        return copy_json([profile](gua_context_t* context, char* output, int size) {
            return gua_copy_ui_tree_json_for_profile(context, static_cast<int>(profile), output, size);
        });
    }

    [[nodiscard]] std::string world_object_tree_json(ObservationProfile profile = ObservationProfile::debug) const
    {
        return copy_json([profile](gua_context_t* context, char* output, int size) {
            return gua_copy_world_object_tree_json(context, static_cast<int>(profile), output, size);
        });
    }

    [[nodiscard]] std::string query_world_objects_json(
        const gua_world_selector_v2_t& selector,
        ObservationProfile profile = ObservationProfile::debug) const
    {
        return copy_json([&selector, profile](gua_context_t* context, char* output, int size) {
            return gua_query_world_objects_v2_json(context, &selector, static_cast<int>(profile), output, size);
        });
    }

    void log(LogLevel level, std::string_view message)
    {
        message_buffer_.assign(message);
        gua_add_log(context_, static_cast<int>(level), message_buffer_.c_str());
    }

    [[nodiscard]] std::string logs_json() const
    {
        return copy_json(gua_copy_logs_json);
    }

    void set_screenshot(std::string_view data_uri, int width, int height)
    {
        screenshot_buffer_.assign(data_uri);
        gua_set_screenshot(context_, screenshot_buffer_.c_str(), width, height);
    }

    [[nodiscard]] std::string screenshot_json() const
    {
        return copy_json(gua_copy_screenshot_json);
    }

    void configure_diagnostics(std::uint32_t history_limit, std::string_view environment_json = "{}")
    {
        environment_buffer_.assign(environment_json);
        if (gua_set_diagnostics_history_limit(context_, history_limit) == 0 ||
            gua_set_diagnostics_environment_json(context_, environment_buffer_.c_str()) == 0) {
            throw std::invalid_argument("Invalid Gua diagnostics configuration");
        }
    }

    [[nodiscard]] std::string diagnostics_json() const
    {
        return copy_json(gua_copy_diagnostics_json);
    }

    [[nodiscard]] std::string diagnostics_json(ObservationProfile profile) const
    {
        return copy_json([profile](gua_context_t* context, char* output, int size) {
            return gua_copy_diagnostics_json_for_profile(context, static_cast<int>(profile), output, size);
        });
    }

    [[nodiscard]] bool enqueue_click(std::string_view node_id)
    {
        id_buffer_.assign(node_id);
        return gua_enqueue_click(context_, id_buffer_.c_str()) != 0;
    }

    [[nodiscard]] bool consume_click_request(std::string_view node_id)
    {
        id_buffer_.assign(node_id);
        return gua_consume_click_request(context_, id_buffer_.c_str()) != 0;
    }

    [[nodiscard]] bool emit_click(std::string_view node_id)
    {
        id_buffer_.assign(node_id);
        return gua_emit_click(context_, id_buffer_.c_str()) != 0;
    }

    [[nodiscard]] bool poll_event(Event& out_event)
    {
        gua_event_t native_event {};
        if (gua_poll_event(context_, &native_event) == 0) {
            return false;
        }

        out_event.type = static_cast<EventType>(native_event.type);
        out_event.node_id = native_event.node_id;
        return true;
    }

    [[nodiscard]] int enqueue_action(const ActionRequest& request, std::uint64_t& request_id)
    {
        id_buffer_.assign(request.node_id);
        value_buffer_.assign(request.value);
        key_buffer_.assign(request.key);
        const gua_action_request_descriptor_t descriptor {
            sizeof(gua_action_request_descriptor_t), static_cast<int>(request.action),
            id_buffer_.empty() ? nullptr : id_buffer_.c_str(), value_buffer_.empty() ? nullptr : value_buffer_.c_str(),
            request.delta_x, request.delta_y, request.bool_value ? 1 : 0,
            key_buffer_.empty() ? nullptr : key_buffer_.c_str(), request.modifiers, request.sensitive ? 1 : 0, request.scroll_unit
        };
        return gua_enqueue_action(context_, &descriptor, &request_id);
    }

    [[nodiscard]] int enqueue_action(const ActionRequest& request, ObservationProfile profile, std::uint64_t& request_id)
    {
        id_buffer_.assign(request.node_id); value_buffer_.assign(request.value); key_buffer_.assign(request.key);
        const gua_action_request_descriptor_t descriptor { sizeof(gua_action_request_descriptor_t), static_cast<int>(request.action),
            id_buffer_.empty() ? nullptr : id_buffer_.c_str(), value_buffer_.empty() ? nullptr : value_buffer_.c_str(),
            request.delta_x, request.delta_y, request.bool_value ? 1 : 0, key_buffer_.empty() ? nullptr : key_buffer_.c_str(),
            request.modifiers, request.sensitive ? 1 : 0, request.scroll_unit };
        return gua_enqueue_action_for_profile(context_, &descriptor, static_cast<int>(profile), &request_id);
    }

    [[nodiscard]] ActionCancelResult cancel_action(std::uint64_t request_id)
    {
        return static_cast<ActionCancelResult>(gua_cancel_action_request(context_, request_id));
    }

    [[nodiscard]] bool consume_action(ActionType action, std::string_view node_id, ActionRequest& out)
    {
        id_buffer_.assign(node_id);
        gua_action_request_t request { sizeof(gua_action_request_t) };
        if (gua_consume_action_request(context_, static_cast<int>(action), id_buffer_.c_str(), &request) == 0) return false;
        out = ActionRequest { request.request_id, static_cast<ActionType>(request.action), request.node_id, request.value,
            request.delta_x, request.delta_y, request.bool_value != 0, request.key, request.modifiers, request.sensitive != 0, request.scroll_unit };
        return true;
    }

    [[nodiscard]] bool emit_action_result(const ActionEvent& event)
    {
        id_buffer_.assign(event.node_id);
        value_buffer_.assign(event.value);
        const gua_action_result_t result { sizeof(gua_action_result_t), event.request_id, static_cast<int>(event.action),
            event.status, event.error_code, id_buffer_.c_str(), value_buffer_.empty() ? nullptr : value_buffer_.c_str(), event.sensitive ? 1 : 0 };
        return gua_emit_action_result(context_, &result) != 0;
    }

    [[nodiscard]] bool poll_action_event(ActionEvent& out)
    {
        gua_event_v2_t event { sizeof(gua_event_v2_t) };
        if (gua_poll_event_v2(context_, &event) == 0) return false;
        out = ActionEvent { event.request_id, static_cast<ActionType>(event.action), event.status, event.error_code,
            event.node_id, event.value, event.sensitive != 0 };
        return true;
    }

    [[nodiscard]] bool poll_action_event(std::uint64_t request_id, ActionEvent& out)
    {
        gua_event_v2_t event { sizeof(gua_event_v2_t) };
        if (gua_poll_event_v2_for_request(context_, request_id, &event) == 0) return false;
        out = ActionEvent { event.request_id, static_cast<ActionType>(event.action), event.status, event.error_code,
            event.node_id, event.value, event.sensitive != 0 };
        return true;
    }

    void publish_game_input_actions(std::string_view input_context, const std::vector<GameInputAction>& actions)
    {
        std::string context(input_context);
        if (!gua_begin_game_input_frame(context_, context.c_str())) throw std::runtime_error("Failed to begin game input frame");
        for (const auto& action : actions) {
            const auto contains_nul = [](const std::string& value) { return value.find('\0') != std::string::npos; };
            if (contains_nul(action.category) ||
                std::any_of(action.aliases.begin(), action.aliases.end(), contains_nul) ||
                std::any_of(action.tags.begin(), action.tags.end(), contains_nul)) {
                gua_abort_game_input_frame(context_);
                throw std::invalid_argument("Game input category, aliases, and tags must not contain embedded NUL characters");
            }
            const bool has_range = action.minimum.has_value() || action.maximum.has_value();
            if (has_range && (!action.minimum.has_value() || !action.maximum.has_value())) {
                gua_abort_game_input_frame(context_);
                throw std::invalid_argument("Game input range requires minimum and maximum");
            }
            gua_game_input_action_descriptor_v1_t base { sizeof(base), action.id.c_str(), action.description.c_str(),
                static_cast<int>(action.value_type), action.minimum.value_or(0), action.maximum.value_or(0), has_range ? 1 : 0,
                action.holdable ? 1 : 0, action.active ? 1 : 0, action.bindings_json.c_str(), action.risk.c_str(),
                action.requires_confirmation ? 1 : 0 };
            std::vector<const char*> aliases, tags;
            for (const auto& alias : action.aliases) aliases.push_back(alias.c_str());
            for (const auto& tag : action.tags) tags.push_back(tag.c_str());
            gua_game_input_action_descriptor_v2_t descriptor { sizeof(descriptor), base,
                action.category.empty() ? nullptr : action.category.c_str(), aliases.empty() ? nullptr : aliases.data(),
                static_cast<std::uint32_t>(aliases.size()), tags.empty() ? nullptr : tags.data(),
                static_cast<std::uint32_t>(tags.size()), action.agent_exposure };
            if (!gua_register_game_input_action_v2(context_, &descriptor)) {
                gua_abort_game_input_frame(context_);
                throw std::invalid_argument("Invalid game input action: " + action.id);
            }
        }
        if (!gua_end_game_input_frame(context_)) throw std::runtime_error("Failed to commit game input frame");
    }
    [[nodiscard]] std::string game_input_actions_json() const
    { return copy_json([](auto* context, char* output, int size) { return gua_copy_game_input_actions_json(context, output, size); }); }
    [[nodiscard]] std::string game_input_actions_json(int observation_profile) const
    { return copy_json([observation_profile](auto* context, char* output, int size) { return gua_copy_game_input_actions_json_for_profile(context, observation_profile, output, size); }); }
    [[nodiscard]] std::string find_game_input_actions_json(const GameInputActionSelector& selector,
        int observation_profile = GUA_OBSERVATION_PROFILE_DEBUG) const
    {
        const auto contains_nul = [](const std::string& value) { return value.find('\0') != std::string::npos; };
        if (contains_nul(selector.id) || contains_nul(selector.query) || contains_nul(selector.context) ||
            contains_nul(selector.category) || std::any_of(selector.tags.begin(), selector.tags.end(), contains_nul))
            throw std::invalid_argument("Game input selectors must not contain embedded NUL characters");
        std::vector<const char*> tags;
        for (const auto& tag : selector.tags) tags.push_back(tag.c_str());
        gua_game_input_action_selector_v1_t native { sizeof(native), selector.id.empty() ? nullptr : selector.id.c_str(),
            selector.query.empty() ? nullptr : selector.query.c_str(),
            selector.value_type.has_value() ? static_cast<int>(*selector.value_type) : 0,
            selector.active.has_value() ? (*selector.active ? GUA_FILTER_TRUE : GUA_FILTER_FALSE) : GUA_FILTER_ANY,
            selector.context.empty() ? nullptr : selector.context.c_str(), selector.category.empty() ? nullptr : selector.category.c_str(),
            tags.empty() ? nullptr : tags.data(), static_cast<std::uint32_t>(tags.size()), selector.limit };
        return copy_json([&](auto* context, char* output, int size) {
            return gua_query_game_input_actions_json(context, &native, observation_profile, output, size);
        });
    }
    [[nodiscard]] GameInputActionSearchResult find_game_input_actions(const GameInputActionSelector& selector,
        int observation_profile = GUA_OBSERVATION_PROFILE_DEBUG) const
    { return { find_game_input_actions_json(selector, observation_profile) }; }
    [[nodiscard]] std::uint64_t create_game_input_owner() { return gua_create_game_input_owner(context_); }
    bool release_game_input_owner(std::uint64_t owner_id) { return gua_release_game_input_owner(context_, owner_id) != 0; }
    [[nodiscard]] std::string game_input_state_json(std::uint64_t owner_id) const
    { return copy_json([owner_id](auto* context, char* output, int size) { return gua_copy_game_input_state_json(context, owner_id, output, size); }); }
    [[nodiscard]] std::string game_input_result_json(std::uint64_t owner_id, std::uint64_t request_id) const
    { return copy_json([owner_id, request_id](auto* context, char* output, int size) { return gua_copy_game_input_result_json(context, owner_id, request_id, output, size); }); }
    [[nodiscard]] std::uint64_t enqueue_game_input(std::uint64_t owner_id, int kind, int operation,
        std::string_view target, std::string_view value_json = "null", std::uint32_t lease_ms = 5000,
        double x = 0, double y = 0, int device_index = 0, bool sensitive = false, bool confirmed = false)
    {
        std::string target_value(target), json(value_json);
        gua_game_input_request_descriptor_v2_t descriptor { sizeof(descriptor), owner_id, kind, operation,
            target_value.c_str(), json.c_str(), x, y, lease_ms, device_index, sensitive ? 1 : 0, confirmed ? 1 : 0 };
        std::uint64_t request_id = 0;
        const int result = gua_enqueue_game_input_v2(context_, &descriptor, &request_id);
        if (result != GUA_GAME_INPUT_OK) throw std::runtime_error("Game input request rejected: " + std::to_string(result));
        return request_id;
    }
    [[nodiscard]] bool consume_game_input(GameInputRequest& out)
    {
        gua_game_input_request_v1_t request { sizeof(request) };
        if (!gua_consume_game_input_request(context_, &request)) return false;
        out = { request.request_id, request.owner_id, request.kind, request.operation, request.target, request.value_json,
            request.x, request.y, request.lease_ms, request.device_index, request.sensitive != 0 };
        return true;
    }
    bool complete_game_input(std::uint64_t request_id, bool succeeded, int error_code = 0)
    { return gua_complete_game_input_request(context_, request_id, succeeded ? 1 : 0, error_code) != 0; }
    int tick_game_input_leases(double elapsed_ms) { return gua_tick_game_input_leases(context_, elapsed_ms); }

private:
    friend class GameInputSession;
    static void check_clock(int result) { if (result != GUA_CLOCK_OK) throw std::runtime_error("Gua clock operation failed: " + std::to_string(result)); }
    template <typename CopyJson>
    [[nodiscard]] std::string copy_json(CopyJson copy) const
    {
        int required_size = copy(context_, nullptr, 0);
        if (required_size <= 0) {
            throw std::runtime_error("Failed to copy Gua JSON");
        }

        for (;;) {
            std::string json(static_cast<std::size_t>(required_size), '\0');
            const int actual_size = copy(context_, json.data(), static_cast<int>(json.size()));
            if (actual_size <= 0) {
                throw std::runtime_error("Failed to copy Gua JSON");
            }
            if (actual_size <= static_cast<int>(json.size())) {
                json.resize(static_cast<std::size_t>(actual_size - 1));
                return json;
            }
            required_size = actual_size;
        }
    }

    void reset() noexcept
    {
        if (lifetime_ != nullptr) lifetime_->context = nullptr;
        gua_destroy_context(context_);
        context_ = nullptr;
        sensitive_node_ids_.clear();
    }

    gua_context_t* context_;
    std::shared_ptr<ContextLifetime> lifetime_;
    mutable std::string id_buffer_;
    mutable std::string role_buffer_;
    mutable std::string label_buffer_;
    mutable std::string parent_id_buffer_;
    mutable std::string text_buffer_;
    mutable std::string value_buffer_;
    mutable std::string key_buffer_;
    mutable std::string message_buffer_;
    std::string screenshot_buffer_;
    std::string environment_buffer_;
    std::string screen_buffer_;
    std::unordered_set<std::string> sensitive_node_ids_;
};

class GameInputSession {
public:
    explicit GameInputSession(Context& context) : context_(context.lifetime_), owner_id_(context.create_game_input_owner())
    { if (owner_id_ == 0) throw std::runtime_error("Failed to create game input session"); }
    ~GameInputSession() { reset(); }
    GameInputSession(const GameInputSession&) = delete;
    GameInputSession& operator=(const GameInputSession&) = delete;
    GameInputSession(GameInputSession&& other) noexcept
        : context_(std::move(other.context_)), owner_id_(std::exchange(other.owner_id_, 0)) {}
    GameInputSession& operator=(GameInputSession&& other) noexcept
    { if (this != &other) { reset(); context_ = std::move(other.context_); owner_id_ = std::exchange(other.owner_id_, 0); } return *this; }
    [[nodiscard]] std::uint64_t owner_id() const noexcept { return owner_id_; }
    [[nodiscard]] std::uint64_t send(int kind, int operation, std::string_view target,
        std::string_view value_json = "null", std::uint32_t lease_ms = 5000,
        double x = 0, double y = 0, int device_index = 0, bool sensitive = false, bool confirmed = false)
    { return context().enqueue_game_input(owner_id_, kind, operation, target, value_json, lease_ms, x, y, device_index, sensitive, confirmed); }
    [[nodiscard]] std::string state_json() const
    { return context().game_input_state_json(owner_id_); }
    [[nodiscard]] std::string result_json(std::uint64_t request_id) const
    { return context().game_input_result_json(owner_id_, request_id); }
    void reset() noexcept { if (const auto lifetime = context_.lock(); lifetime != nullptr && lifetime->context != nullptr) lifetime->context->release_game_input_owner(owner_id_); context_.reset(); owner_id_ = 0; }
private:
    [[nodiscard]] Context& context() const
    {
        const auto lifetime = context_.lock();
        if (owner_id_ == 0 || lifetime == nullptr || lifetime->context == nullptr)
            throw std::runtime_error("Game input session is disposed");
        return *lifetime->context;
    }
    std::weak_ptr<ContextLifetime> context_;
    std::uint64_t owner_id_ = 0;
};

inline void begin_frame(Context& context, std::string_view screen)
{
    context.begin_frame(screen);
}

inline void end_frame(Context& context)
{
    context.end_frame();
}

inline void button(Context& context, std::string_view id, std::string_view label, Rect bounds, bool visible = true, bool enabled = true)
{
    context.button(id, label, bounds, visible, enabled);
}

inline void text(Context& context, std::string_view id, std::string_view label, Rect bounds, bool visible = true)
{
    context.text(id, label, bounds, visible);
}

inline void panel(Context& context, std::string_view id, std::string_view label, Rect bounds, bool visible = true)
{
    context.panel(id, label, bounds, visible);
}

inline void log(Context& context, LogLevel level, std::string_view message)
{
    context.log(level, message);
}

inline void set_screenshot(Context& context, std::string_view data_uri, int width, int height)
{
    context.set_screenshot(data_uri, width, height);
}

} // namespace gua

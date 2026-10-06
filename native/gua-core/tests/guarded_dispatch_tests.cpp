#include "gua/gua.h"
#include <cassert>

static void frame(gua_context_t* ctx, const char* label) {
    gua_begin_frame(ctx, "guard");
    gua_register_node(ctx, "buy", "button", label, {0, 0, 20, 20}, 1, 1);
    gua_end_frame(ctx);
}
int main() {
    auto* ctx = gua_create_context();
    frame(ctx, "Buy");
    gua_context_status_t status {sizeof(status)};
    assert(gua_get_context_status(ctx, &status));
    const auto owner = gua_create_game_input_owner(ctx);
    const auto other = gua_create_game_input_owner(ctx);
    gua_action_request_descriptor_t action {sizeof(action), GUA_ACTION_CLICK, "buy"};
    uint64_t id = 0;
    assert(gua_enqueue_action_guarded_v1(ctx, &action, owner, 0, 0, status.revision, &id) == GUA_ACTION_ERROR_STALE_GUARD);
    assert(id == 0);
    assert(gua_enqueue_action_guarded_v1(ctx, &action, owner, 0, status.session_epoch, status.revision + 1, &id) == GUA_ACTION_ERROR_STALE_GUARD);
    assert(id == 0);
    assert(gua_enqueue_action_guarded_v1(ctx, &action, owner, 0, status.session_epoch, status.revision, &id) == 1);
    gua_action_request_t request {sizeof(request)};
    // Authoritative profile changes between enqueue and host consumption.
    assert(!gua_consume_action_request_for_profile(ctx, GUA_ACTION_CLICK, "buy", 1, &request));
    gua_event_v3_t event {sizeof(event), {sizeof(gua_event_v2_t)}};
    assert(!gua_poll_event_v3_for_request(ctx, id, &event));
    assert(!gua_poll_owned_action_event_v1(ctx, other, id, &event));
    assert(gua_poll_owned_action_event_v1(ctx, owner, id, &event));
    assert(event.base.request_id == id && event.base.error_code == GUA_ACTION_ERROR_STALE_GUARD);
    assert(!gua_poll_owned_action_event_v1(ctx, owner, id, &event));
    assert(gua_enqueue_action_guarded_v1(ctx, &action, owner, 0, status.session_epoch, status.revision, &id) == 1);
    frame(ctx, "Changed");
    assert(!gua_consume_action_request(ctx, GUA_ACTION_CLICK, "buy", &request));
    assert(gua_poll_owned_action_event_v1(ctx, owner, id, &event));
    assert(event.base.error_code == GUA_ACTION_ERROR_STALE_GUARD);
    assert(gua_get_context_status(ctx, &status));
    assert(gua_enqueue_action_guarded_v1(ctx, &action, owner, 0, status.session_epoch, status.revision, &id) == 1);
    assert(gua_consume_action_request(ctx, GUA_ACTION_CLICK, "buy", &request));
    assert(gua_release_game_input_owner(ctx, owner));
    // Disconnect cannot destroy the consumed request's host completion path.
    gua_action_result_t result {sizeof(result), id, GUA_ACTION_CLICK, GUA_ACTION_STATUS_SUCCEEDED, 0, "buy"};
    assert(gua_emit_action_result(ctx, &result));
    assert(!gua_poll_event_v3(ctx, &event));
    assert(!gua_poll_owned_action_event_v1(ctx, other, id, &event));
    assert(gua_enqueue_action_for_profile(ctx, &action, 0, &id) == 1);
    assert(gua_consume_action_request(ctx, GUA_ACTION_CLICK, "buy", &request));
    result.request_id = id;
    assert(gua_emit_action_result(ctx, &result));
    assert(gua_poll_event_v3(ctx, &event)); // Legacy path is unchanged.
    for (int i = 0; i < 256; ++i)
        assert(gua_enqueue_action_guarded_v1(ctx, &action, other, 0, status.session_epoch, status.revision, &id) == 1);
    uint64_t overflow = 0;
    assert(gua_enqueue_action_guarded_v1(ctx, &action, other, 0, status.session_epoch, status.revision, &overflow) == GUA_ACTION_ERROR_INVALID_ARGUMENT);
    assert(overflow == 0);
    assert(gua_release_game_input_owner(ctx, other));
    assert(gua_get_context_status(ctx, &status));
    assert(status.pending_request_count == 0);
    gua_destroy_context(ctx);
}

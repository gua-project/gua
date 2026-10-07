#include "gua/runtime.h"
#include <cassert>
#include <string>
#include <vector>

int main() {
    auto* runtime = gua_runtime_create();
    assert(runtime);
    gua_runtime_set_game_input_capabilities(runtime, GUA_RUNTIME_GAME_INPUT_KEYBOARD);
    gua_runtime_set_player_game_input_capabilities(runtime, GUA_RUNTIME_GAME_INPUT_KEYBOARD);
    const auto owner = gua_runtime_create_game_input_owner(runtime);
    gua_context_status_t status {sizeof(status)};
    assert(gua_runtime_get_context_status(runtime, &status));
    gua_game_input_request_descriptor_v2_t input {sizeof(input), owner,
        GUA_GAME_INPUT_KEYBOARD, GUA_GAME_INPUT_DOWN, "KeyA", "null", 0, 0, 5000};
    uint64_t id = 0;
    assert(gua_runtime_enqueue_game_input_guarded_v2(runtime, &input, GUA_OBSERVATION_PROFILE_DEBUG,
        status.session_epoch, 0, &id) == GUA_GAME_INPUT_OK);
    // Profile is immutable with an active bridge; this covers the shared runtime
    // consume boundary directly, including a permitted Player keyboard capability.
    assert(gua_runtime_set_observation_profile(runtime, GUA_OBSERVATION_PROFILE_PLAYER));
    gua_game_input_request_v1_t consumed {sizeof(consumed)};
    assert(!gua_runtime_consume_game_input_request(runtime, &consumed));
    const int size = gua_runtime_copy_game_input_result_json(runtime, owner, id, nullptr, 0);
    assert(size > 0);
    std::vector<char> result(size);
    assert(gua_runtime_copy_game_input_result_json(runtime, owner, id, result.data(), size) == size);
    const std::string json(result.data());
    assert(json.find("\"completed\":true") != std::string::npos);
    assert(json.find("\"succeeded\":false") != std::string::npos);
    assert(json.find("\"errorCode\":-4") != std::string::npos);
    gua_runtime_destroy(runtime);
}

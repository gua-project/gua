#include <gua/ws_bridge.hpp>

#include <cassert>
#include <chrono>
#include <iostream>
#include <string>
#include <vector>

int main(int argc, char** argv)
{
    if (argc == 2 && std::string_view(argv[1]) == "--metadata-revocation-fixture") {
        // Preflight succeeds; authoritative callbacks simulate later revocation.
        gua::ws::BridgeServer fixture({
            .game_input_supported = [](unsigned int) { return true; },
            .get_game_input_actions_json_v2 = [] { return std::string(); },
            .query_game_input_actions_json_v2 = [](const auto&) { return std::string(); },
        }, { .port = 0 });
        fixture.start();
        std::cerr << "GUA_REVOCATION_PORT=" << fixture.port() << std::endl;
        std::string stop;
        std::getline(std::cin, stop);
        return 0;
    }
    const gua::ws::GameInputQuerySelector valid { .id = "jump", .query = "Jump", .value_type = 1,
        .active = 2, .context = "gameplay", .category = "movement", .tags = { "core", "player" }, .limit = 20 };
    assert(gua::ws::detail::valid_game_input_query_selector(valid));
    auto invalid = valid;
    invalid.id = "Invalid";
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    invalid = valid; invalid.category = std::string(128, 'a');
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    invalid = valid; invalid.query = std::string(129, 'q');
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    invalid = valid; invalid.tags = { std::string(65, 't') };
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    invalid = valid; invalid.tags = { "same", "same" };
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    invalid = valid; invalid.tags = std::vector<std::string>(17, "tag");
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    invalid = valid; invalid.limit = 101;
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    invalid = valid; invalid.query = std::string("before\0after", 12);
    assert(!gua::ws::detail::valid_game_input_query_selector(invalid));
    assert(gua::ws::detail::valid_game_input_query_request_json(
        R"({"id":1,"type":"find_game_input_actions","tags":["x"]})"));
    assert(!gua::ws::detail::valid_game_input_query_request_json(
        R"({"id":1,"type":"find_game_input_actions","tags":["x",]})"));

    gua::ws::BridgeServer bridge({}, { .port = 0 });
    bridge.start();
    assert(bridge.running());
    assert(bridge.port() != 0);

    const auto started = std::chrono::steady_clock::now();
    bridge.stop();
    assert(!bridge.running());
    assert(std::chrono::steady_clock::now() - started < std::chrono::seconds(3));
}

#include "gua/semantic_lint.hpp"
#include "../src/value_json.hpp"
#include <cassert>
#include <fstream>
#include <iterator>
#include <set>
using namespace gua_value_detail;
std::string dump(const json& value) {
    if (value.type == json::string) return quote(value.text);
    if (value.type == json::number || value.type == json::boolean) return value.text;
    if (value.type == json::null) return "null";
    std::string result = value.type == json::array ? "[" : "{";
    if (value.type == json::array) for (const auto& item : value.items) { if (result.size() > 1) result += ','; result += dump(item); }
    else for (const auto& [key, field] : value.fields) { if (result.size() > 1) result += ','; result += quote(key) + ':' + dump(field); }
    return result + (value.type == json::array ? ']' : '}');
}
std::string analyze(gua_context_t* context, int profile) {
    const gua_semantic_lint_options_v1_t options {sizeof(options), profile, 1};
    gua_semantic_lint_report_t* raw = nullptr;
    assert(gua_semantic_lint_analyze(context, &options, &raw) == 0);
    gua::SemanticLintReport report(raw); return report.json();
}
int main() {
    std::ifstream input(GUA_LINT_FIXTURE);
    const auto fixture = parser(std::string(std::istreambuf_iterator<char>(input), {})).parse();
    for (const auto& test : fixture.at("cases").items) {
        const auto ui = dump(test.at("uiTree")), world = dump(test.at("worldObjectTree"));
        std::string debug;
        for (const int profile : {GUA_OBSERVATION_PROFILE_DEBUG, GUA_OBSERVATION_PROFILE_PLAYER}) {
            auto report = gua::SemanticLinter::analyze_snapshots(ui, world.c_str(), profile);
            auto parsed = parser(report.json()).parse(); std::set<std::string> actual, expected;
            for (const auto& finding : parsed.at("findings").items) {
                actual.insert(finding.at("ruleId").text);
                assert(!finding.at("message").text.empty());
                assert(finding.at("path").text.starts_with("$."));
                if (finding.at("ruleId").text == "parent-cycle") assert(finding.at("targetId").text != "desc");
            }
            for (const auto& rule : test.at("expected").items) expected.insert(rule.text);
            assert(actual == expected);
            if (profile == 0) debug = dump(parsed.at("findings")); else assert(debug == dump(parsed.at("findings")));
            assert(integer(parsed.at("summary").at("total").text, "$") == static_cast<int64_t>(parsed.at("findings").items.size()));
        }
    }
    gua::Context context; auto* ctx = context.native_handle();
    assert(parser(analyze(ctx, 0)).parse().at("findings").items.empty());
    gua_begin_frame(ctx, "published");
    gua_register_node(ctx, "safe", "button", "Safe", {0,0,1,1}, 1, 1);
    gua_register_node(ctx, "secret", "button", "", {0,0,1,1}, 0, 1);
    gua_end_frame(ctx);
    const auto debug = analyze(ctx, 0), player = analyze(ctx, 1);
    assert(debug.find("secret") != std::string::npos);
    assert(player.find("secret") == std::string::npos);
    gua_begin_frame(ctx, "staging-secret");
    gua_register_node(ctx, "staging", "button", "", {0,0,1,1}, 1, 1);
    assert(analyze(ctx, 0) == debug); assert(analyze(ctx, 1) == player);
    gua_end_frame(ctx);
    assert(analyze(ctx, 0).find("staging") != std::string::npos);
    gua_semantic_lint_report_t* raw = nullptr;
    gua_semantic_lint_options_v1_t invalid {0, 0, 1};
    assert(gua_semantic_lint_analyze(ctx, &invalid, &raw) == 1 && !raw);
    assert(gua_semantic_lint_analyze_snapshots("{}", nullptr, 0, &raw) == 1 && !raw);
    assert(gua_semantic_lint_analyze_snapshots("{}", nullptr, 2, &raw) == 1 && !raw);
}

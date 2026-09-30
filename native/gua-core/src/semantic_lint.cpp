#include "gua/semantic_lint.h"
#include "value_json.hpp"
#include <cstring>
#include <limits>
#include <memory>
#include <locale>
#include <sstream>
#include <unordered_map>
#include <unordered_set>

struct gua_semantic_lint_report { std::string json; };
namespace {
using namespace gua_value_detail;
const json* member(const json& value, const std::string& name) {
    const auto it = value.fields.find(name);
    return it == value.fields.end() ? nullptr : &it->second;
}
std::string string(const json& value, const std::string& name) {
    const auto* field = member(value, name);
    if (!field) return {};
    if (field->type != json::string) fail(GUA_VALUE_STRUCTURE);
    return field->text;
}
double numeric(const json& value) {
    if (value.type != json::number) fail(GUA_VALUE_STRUCTURE);
    double n = 0;
    std::istringstream stream(value.text);
    stream.imbue(std::locale::classic());
    if (!(stream >> n) || !stream.eof() || !std::isfinite(n)) fail(GUA_VALUE_STRUCTURE);
    return n;
}
// JSON Schema integer includes integral decimal/exponent representations. Keep
// their exact decimal value: int64 indices must not round through double.
std::string integral(const json& value) {
    if (value.type != json::number) fail(GUA_VALUE_STRUCTURE);
    std::string_view source = value.text;
    const bool negative = source.starts_with('-'); if (negative) source.remove_prefix(1);
    const auto e = source.find_first_of("eE"); const auto mantissa = source.substr(0, e);
    long long exponent = 0;
    if (e != source.npos) {
        auto digits = source.substr(e + 1); const bool minus = digits.starts_with('-');
        if (digits.starts_with('-') || digits.starts_with('+')) digits.remove_prefix(1);
        const auto bound = static_cast<long long>(source.size()) + 1024;
        for (const char c : digits) exponent = std::min(bound, exponent * 10 + c - '0');
        if (minus) exponent = -exponent;
    }
    std::string digits; bool after_dot = false;
    for (const char c : mantissa) { if (c == '.') after_dot = true; else { digits += c; if (after_dot) --exponent; } }
    const auto first = digits.find_first_not_of('0'); if (first == digits.npos) return "0";
    digits.erase(0, first);
    while (digits.back() == '0') { digits.pop_back(); ++exponent; }
    if (exponent < 0 || exponent > 1024) fail(GUA_VALUE_RANGE);
    digits.append(static_cast<size_t>(exponent), '0');
    return negative ? '-' + digits : digits;
}
bool greater_integer(const std::string& left, const std::string& right) {
    const bool ln = left.starts_with('-'), rn = right.starts_with('-');
    if (ln != rn) return !ln;
    const bool greater = left.size() == right.size() ? left > right : left.size() > right.size();
    return ln ? left != right && !greater : greater;
}
std::string metadata_integer(const json& value) {
    const auto result = integral(value);
    if (result.starts_with('-') || result.size() > 20 || (result.size() == 20 && result > "18446744073709551615")) fail(GUA_VALUE_RANGE);
    return result;
}
std::string metadata(const json& tree, bool world) {
    std::string result = "{";
    for (const auto* name : {"schemaVersion", "sessionEpoch", "frameSequence", "revision"}) {
        const auto& value = tree.at(name);
        const auto number = metadata_integer(value);
        if (std::string_view(name) == "schemaVersion" && number != (world ? "1" : "2")) fail(GUA_VALUE_STRUCTURE);
        if (std::string_view(name) == "sessionEpoch" && number == "0") fail(GUA_VALUE_STRUCTURE);
        if (result.size() > 1) result += ',';
        result += quote(name) + ':' + number;
    }
    const auto name = world ? "scene" : "screen";
    const auto label = string(tree, name);
    if (label.empty()) fail(GUA_VALUE_STRUCTURE);
    return result + ',' + quote(name) + ':' + quote(label) + '}';
}
const std::vector<json>& entities(const json& tree, bool world) {
    if (tree.type != json::object) fail(GUA_VALUE_STRUCTURE);
    const auto& values = tree.at(world ? "objects" : "nodes");
    if (values.type != json::array) fail(GUA_VALUE_STRUCTURE);
    for (const auto& value : values.items) {
        if (value.type != json::object || string(value, "id").empty()) fail(GUA_VALUE_STRUCTURE);
        string(value, "parentId");
        if (!world) {
            if (string(value, "role").empty()) fail(GUA_VALUE_STRUCTURE);
            const auto& actions = value.at("actions");
            if (actions.type != json::array) fail(GUA_VALUE_STRUCTURE);
            for (const auto& action : actions.items) if (action.type != json::string) fail(GUA_VALUE_STRUCTURE);
            if (const auto* state = member(value, "state")) {
                if (state->type != json::object) fail(GUA_VALUE_STRUCTURE);
                for (const auto& [key, field] : state->fields) {
                    if (key == "value") continue;
                    if (key == "focused" || key == "hovered" || key == "pressed" || key == "checked" || key == "selected") {
                        if (field.type != json::boolean) fail(GUA_VALUE_STRUCTURE);
                    } else numeric(field);
                }
            }
        } else string(value, "relatedUiNodeId");
    }
    return values.items;
}
struct Findings {
    std::vector<std::string> values;
    size_t errors = 0, warnings = 0;
    void add(const char* rule, const char* severity, bool world, size_t index, const json& entity,
        const std::string& field, const char* message) {
        if (std::string_view(severity) == "error") ++errors; else ++warnings;
        values.push_back("{\"ruleId\":" + quote(rule) + ",\"severity\":" + quote(severity) +
            ",\"message\":" + quote(message) + ",\"targetKind\":" + quote(world ? "world" : "ui") +
            ",\"targetId\":" + quote(string(entity, "id")) + ",\"path\":" +
            quote(std::string(world ? "$.worldObjectTree.objects[" : "$.uiTree.nodes[") + std::to_string(index) + "]." + field) + '}');
    }
};
void structure(const std::vector<json>& values, bool world, Findings& findings) {
    std::unordered_map<std::string, size_t> counts;
    std::unordered_map<std::string, std::string> parents;
    for (const auto& value : values) ++counts[string(value, "id")];
    for (const auto& value : values) if (counts[string(value, "id")] == 1)
        parents.emplace(string(value, "id"), string(value, "parentId"));
    // Resolve each functional parent graph once. Only cycle members are marked;
    // descendants of cycles have an existing parent and are not themselves cycles.
    std::unordered_set<std::string> done, cycles;
    for (const auto& [id, parent] : parents) {
        (void)parent;
        std::vector<std::string> chain;
        std::unordered_map<std::string, size_t> positions;
        auto current = id;
        while (parents.contains(current) && !done.contains(current)) {
            if (positions.contains(current)) {
                for (size_t i = positions.at(current); i < chain.size(); ++i) cycles.insert(chain[i]);
                break;
            }
            positions.emplace(current, chain.size()); chain.push_back(current); current = parents.at(current);
        }
        done.insert(chain.begin(), chain.end());
    }
    for (size_t i = 0; i < values.size(); ++i) {
        const auto& value = values[i];
        const auto id = string(value, "id"), parent = string(value, "parentId");
        if (counts[id] > 1) findings.add("duplicate-id", "error", world, i, value, "id", "Assign a unique ID within this tree.");
        if (!parent.empty() && !counts.contains(parent)) findings.add("missing-parent", "error", world, i, value, "parentId", "Reference an existing parent in this published tree.");
        if (cycles.contains(id)) findings.add("parent-cycle", "error", world, i, value, "parentId", "Remove the cycle from this parent chain.");
    }
}
bool one_of(const std::string& value, std::initializer_list<const char*> values) {
    for (const auto* candidate : values) if (value == candidate) return true;
    return false;
}
bool action_supported(const std::string& role, const std::string& action) {
    if (action == "focus") return one_of(role, {"button", "checkbox", "radio", "tab", "textbox", "slider", "combobox", "list"});
    if (action == "click") return one_of(role, {"button", "checkbox", "radio", "tab"});
    if (action == "set_value") return one_of(role, {"textbox", "slider"});
    if (action == "set_checked") return one_of(role, {"checkbox", "radio"});
    if (action == "select") return one_of(role, {"combobox", "list", "listitem", "tablist", "tab"});
    if (action == "scroll") return one_of(role, {"list", "scrollarea"});
    return action == "press_key" && role == "textbox";
}
void semantics(const std::vector<json>& nodes, Findings& findings) {
    for (size_t i = 0; i < nodes.size(); ++i) {
        const auto& node = nodes[i]; const auto role = string(node, "role");
        const auto& actions = node.at("actions").items;
        const auto label = string(node, "label");
        const bool interactive = !actions.empty() || one_of(role, {"button", "checkbox", "radio", "slider", "textbox", "list", "listitem", "menuitem", "combobox", "tablist", "tab", "scrollarea"});
        if (interactive && label.find_first_not_of(" \t\r\n") == std::string::npos)
            findings.add("missing-accessible-name", "warning", false, i, node, "label", "Provide a nonblank accessible label for this interactive node.");
        for (size_t a = 0; a < actions.size(); ++a) if (!action_supported(role, actions[a].text))
            findings.add("role-action-contradiction", "error", false, i, node, "actions[" + std::to_string(a) + "]", "Advertise only actions supported by this role.");
        if (const auto* enabled = member(node, "enabled"); enabled && enabled->type == json::boolean && enabled->text == "false" && !actions.empty())
            findings.add("disabled-actions", "error", false, i, node, "actions", "A disabled node must not advertise executable actions.");
        const auto* state = member(node, "state"); if (!state) continue;
        for (const auto& [key, field] : state->fields) {
            (void)field;
            bool compatible = true;
            if (key == "checked") compatible = one_of(role, {"checkbox", "radio"});
            else if (key == "selected") compatible = one_of(role, {"listitem", "tab"});
            else if (key == "caretPosition" || key == "selectionStart" || key == "selectionEnd") compatible = role == "textbox";
            else if (key.starts_with("range")) compatible = role == "slider";
            else if (key.starts_with("scroll")) compatible = one_of(role, {"list", "scrollarea"});
            else if (key == "selectedIndex") compatible = one_of(role, {"list", "combobox", "tablist"});
            if (!compatible) findings.add("role-state-contradiction", "error", false, i, node, "state." + key, "Publish this state only on a role that defines it.");
        }
        const auto compare = [&](const char* left, const char* right, const char* rule, const char* message) {
            const auto* l = member(*state, left); const auto* r = member(*state, right);
            if (l && r && numeric(*l) > numeric(*r)) findings.add(rule, "error", false, i, node, "state." + std::string(left), message);
        };
        compare("rangeMin", "rangeMax", "range-bounds", "Set rangeMin no greater than rangeMax.");
        compare("rangeMin", "rangeValue", "range-value", "Keep rangeValue at or above rangeMin.");
        compare("rangeValue", "rangeMax", "range-value", "Keep rangeValue at or below rangeMax.");
        const auto* start = member(*state, "selectionStart"); const auto* end = member(*state, "selectionEnd");
        if (start && end) {
            try {
                if (greater_integer(integral(*start), integral(*end))) findings.add("selection-order", "error", false, i, node,
                    "state.selectionStart", "Set selectionStart no greater than selectionEnd.");
            } catch (const failure&) { /* Fractional indices are diagnosed below. */ }
        }
        for (const auto* key : {"caretPosition", "selectionStart", "selectionEnd", "selectedIndex"}) {
            const auto* field = member(*state, key);
            if (field) {
                bool invalid = false;
                try { invalid = greater_integer(std::string_view(key) == "selectedIndex" ? "-1" : "0", integral(*field)); }
                catch (const failure&) { invalid = true; }
                if (invalid) findings.add("selection-index", "error", false, i, node, "state." + std::string(key), "Use an integral index; text indices must be nonnegative and selectedIndex must be at least -1.");
            }
        }
        for (const auto* axis : {"X", "Y"}) {
            const auto offset = std::string("scroll") + axis, maximum = std::string("scrollMax") + axis;
            for (const auto& key : {offset, maximum}) if (const auto* f = member(*state, key); f && numeric(*f) < 0)
                findings.add("scroll-bounds", "error", false, i, node, "state." + key, "Use a nonnegative scroll offset and maximum.");
            compare(offset.c_str(), maximum.c_str(), "scroll-bounds", "Keep the scroll offset at or below its maximum.");
        }
    }
}
}
extern "C" int gua_semantic_lint_analyze_snapshots(const char* ui, const char* world, int profile, gua_semantic_lint_report_t** out) {
    if (!out) return 1;
    *out = nullptr;
    if (!ui || (profile != GUA_OBSERVATION_PROFILE_DEBUG && profile != GUA_OBSERVATION_PROFILE_PLAYER)) return 1;
    try {
        const auto ui_tree = parser(ui).parse(); const auto& nodes = entities(ui_tree, false);
        const auto ui_metadata = metadata(ui_tree, false);
        Findings findings; structure(nodes, false, findings); semantics(nodes, findings);
        std::string world_metadata = "null";
        if (world) {
            const auto world_tree = parser(world).parse(); const auto& objects = entities(world_tree, true);
            world_metadata = metadata(world_tree, true);
            if (metadata_integer(world_tree.at("sessionEpoch")) != metadata_integer(ui_tree.at("sessionEpoch"))) return 1;
            structure(objects, true, findings);
            std::unordered_set<std::string> ids;
            for (const auto& node : nodes) ids.insert(string(node, "id"));
            for (size_t i = 0; i < objects.size(); ++i) {
                const auto related = string(objects[i], "relatedUiNodeId");
                if (!related.empty() && !ids.contains(related)) findings.add("broken-related-ui", "error", true, i, objects[i], "relatedUiNodeId", "Reference a UI node present in the same projected snapshot pair.");
            }
        }
        auto report = std::make_unique<gua_semantic_lint_report>();
        report->json = "{\"schemaVersion\":1,\"profile\":" + quote(profile == GUA_OBSERVATION_PROFILE_PLAYER ? "player" : "debug") +
            ",\"uiTree\":" + ui_metadata + ",\"worldObjectTree\":" + world_metadata + ",\"summary\":{\"error\":" +
            std::to_string(findings.errors) + ",\"warning\":" + std::to_string(findings.warnings) + ",\"info\":0,\"total\":" +
            std::to_string(findings.values.size()) + "},\"findings\":[";
        for (size_t i = 0; i < findings.values.size(); ++i) { if (i) report->json += ','; report->json += findings.values[i]; }
        report->json += "]}"; *out = report.release(); return 0;
    } catch (const failure&) { return 1; } catch (...) { return 2; }
}
extern "C" int gua_semantic_lint_report_copy_json(const gua_semantic_lint_report_t* report, char* buffer, int size) {
    if (!report || report->json.size() >= static_cast<size_t>(std::numeric_limits<int>::max())) return 0;
    const int required = static_cast<int>(report->json.size()) + 1;
    if (buffer && size >= required) std::memcpy(buffer, report->json.c_str(), static_cast<size_t>(required));
    return required;
}
extern "C" void gua_semantic_lint_report_destroy(gua_semantic_lint_report_t* report) { delete report; }

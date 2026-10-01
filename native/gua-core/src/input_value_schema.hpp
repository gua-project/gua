#pragma once
#include "value_json.hpp"
#include <optional>
#include <limits>
#include <sstream>
#include <locale>

// Deliberately bounded JSON Schema 2020-12 subset, not a general evaluator.
namespace gua_input_detail {
using gua_value_detail::json;
inline double numeric(const json& value) {
    if (value.type != json::number) gua_value_detail::fail(GUA_VALUE_STRUCTURE);
    double result = 0;
    // Apple's older libc++ lacks floating-point from_chars. The JSON parser
    // already checks number grammar; use the classic locale on every platform.
    std::istringstream stream(value.text);
    stream.imbue(std::locale::classic());
    stream >> result;
    if (stream.fail() || !stream.eof() || !std::isfinite(result))
        gua_value_detail::fail(GUA_VALUE_RANGE);
    return result;
}
inline std::size_t length(const std::string& text) {
    return static_cast<std::size_t>(std::count_if(text.begin(), text.end(), [](unsigned char c) { return (c & 0xc0) != 0x80; }));
}
inline double bound(const json& schema, const char* key, double fallback) {
    auto found = schema.fields.find(key);
    return found == schema.fields.end() ? fallback : numeric(found->second);
}
inline bool schema_valid(const json& schema, int type, bool has_range, double minimum, double maximum, bool root = true) {
    if (schema.type != json::object) return false;
    const auto expected = type == 1 ? "boolean" : type == 2 ? "number" : type == 3 ? "object" : "string";
    if (schema.at("type").type != json::string || schema.at("type").text != expected) return false;
    for (const auto& [key, value] : schema.fields) {
        if (key == "type") continue;
        if (key == "description") { if (value.type != json::string || length(value.text) > 1024) return false; continue; }
        if (root && key == "$schema") {
            if (value.type != json::string || value.text != "https://json-schema.org/draft/2020-12/schema") return false;
            continue;
        }
        if (type == 2 && (key == "minimum" || key == "maximum")) { (void)numeric(value); continue; }
        if (type == 4 && (key == "minLength" || key == "maxLength")) {
            const auto n = numeric(value); if (n < 0 || n > 40 || std::floor(n) != n) return false; continue;
        }
        if (type == 3 && (key == "properties" || key == "required" || key == "additionalProperties")) continue;
        return false;
    }
    if (type == 2) {
        const auto low = bound(schema, "minimum", has_range ? minimum : -std::numeric_limits<double>::max());
        const auto high = bound(schema, "maximum", has_range ? maximum : std::numeric_limits<double>::max());
        if (low > high || (has_range && (low < minimum || high > maximum))) return false;
    }
    if (type == 4 && bound(schema, "minLength", 0) > bound(schema, "maxLength", 40)) return false;
    if (type == 3) {
        const auto& properties = schema.at("properties");
        const auto& required = schema.at("required");
        const auto& additional = schema.at("additionalProperties");
        if (properties.type != json::object || properties.fields.size() != 2 ||
            required.type != json::array || required.items.size() != 2 ||
            additional.type != json::boolean || additional.text != "false") return false;
        const auto& a = required.items[0]; const auto& b = required.items[1];
        if (a.type != json::string || b.type != json::string ||
            !((a.text == "x" && b.text == "y") || (a.text == "y" && b.text == "x"))) return false;
        if (!schema_valid(properties.at("x"), 2, has_range, minimum, maximum, false) ||
            !schema_valid(properties.at("y"), 2, has_range, minimum, maximum, false)) return false;
    }
    return true;
}
inline bool value_valid(const json& value, int type, bool has_range, double minimum, double maximum, const json* schema) {
    if (type == 1) return value.type == json::boolean;
    if (type == 2) {
        const auto n = numeric(value);
        return (!has_range || (n >= minimum && n <= maximum)) &&
            (!schema || (n >= bound(*schema, "minimum", -std::numeric_limits<double>::max()) &&
                         n <= bound(*schema, "maximum", std::numeric_limits<double>::max())));
    }
    if (type == 3) {
        if (value.type != json::object || value.fields.size() != 2) return false;
        const auto* properties = schema ? &schema->at("properties") : nullptr;
        return value_valid(value.at("x"), 2, has_range, minimum, maximum, properties ? &properties->at("x") : nullptr) &&
               value_valid(value.at("y"), 2, has_range, minimum, maximum, properties ? &properties->at("y") : nullptr);
    }
    if (value.type != json::string) return false;
    const auto n = length(value.text);
    return n <= 40 && (!schema || (n >= bound(*schema, "minLength", 0) && n <= bound(*schema, "maxLength", 40)));
}
inline bool validate_value(std::string_view value, int type, bool has_range, double minimum, double maximum, const std::string& schema) {
    try {
        auto parsed = gua_value_detail::parser(value).parse();
        std::optional<json> s;
        if (!schema.empty()) s = gua_value_detail::parser(schema).parse();
        return value_valid(parsed, type, has_range, minimum, maximum, s ? &*s : nullptr);
    } catch (const gua_value_detail::failure&) { return false; }
}
inline bool validate_metadata(const std::string& schema, const std::string& examples, int type,
    bool has_range, double minimum, double maximum, bool holdable) {
    if (schema.size() > 16384 || examples.size() > 16384 ||
        ((!schema.empty() || !examples.empty()) && has_range && type != 2 && type != 3)) return false;
    try {
        std::optional<json> s;
        if (!schema.empty()) {
            s = gua_value_detail::parser(schema).parse();
            if (!schema_valid(*s, type, has_range, minimum, maximum)) return false;
        }
        if (!examples.empty()) {
            auto e = gua_value_detail::parser(examples).parse();
            if (e.type != json::array || e.items.size() > 16 || (type == 1 && !holdable && !e.items.empty())) return false;
            // The parsed document is valid JSON. Measure each original literal,
            // including whitespace inside an object, rather than its decoded value.
            size_t start = examples.find('[') + 1;
            int depth = 1;
            bool quoted = false, escaped = false;
            for (size_t i = start; i < examples.size(); ++i) {
                const char c = examples[i];
                if (quoted) {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '[' || c == '{') ++depth;
                else if (c == ']' || c == '}') --depth;
                if ((c == ',' && depth == 1) || depth == 0) {
                    auto end = i;
                    while (start < end && std::string_view(" \t\r\n").find(examples[start]) != std::string_view::npos) ++start;
                    while (end > start && std::string_view(" \t\r\n").find(examples[end - 1]) != std::string_view::npos) --end;
                    if (end - start >= 512) return false;
                    start = i + 1;
                }
            }
            for (const auto& value : e.items)
                if (!value_valid(value, type, has_range, minimum, maximum, s ? &*s : nullptr)) return false;
        }
        return true;
    } catch (const gua_value_detail::failure&) { return false; }
}
}

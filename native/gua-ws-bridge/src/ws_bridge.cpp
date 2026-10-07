#include "gua/ws_bridge.hpp"
#include "socket_platform.hpp"
#include "../../gua-core/src/value_json.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cctype>
#include <cmath>
#include <cstring>
#include <future>
#include <iostream>
#include <locale>
#include <limits>
#include <memory>
#include <map>
#include <mutex>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace {

using gua::ws::platform::NetworkSession;
using gua::ws::platform::Socket;
using gua::ws::platform::SocketHandle;
using gua::ws::platform::invalid_socket;

constexpr std::string_view websocket_guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

struct Command {
    int id = 0;
    std::string type;
    std::string node_id;
    std::string key;
    gua::ws::QuerySelector selector;
    gua::ws::WorldQuerySelector world_selector;
    gua::ws::GameInputQuerySelector game_input_selector;
    std::string value;
    float delta_x = 0;
    float delta_y = 0;
    bool bool_value = false;
    unsigned int modifiers = 0;
    bool sensitive = false;
    bool confirmed = false;
    int scroll_unit = 0;
    unsigned long long request_id = 0;
    unsigned long long expected_session_epoch = 0;
    unsigned long long after_frame_sequence = 0;
    unsigned int timeout_ms = 10000;
    unsigned int reset_flags = 207;
    unsigned int reset_flags_version = 2;
    bool reset_flags_version_valid = true;
    bool strict = false;
    double initial_time_ms = 0;
    bool initial_time_ms_valid = true;
    double duration_ms = 0;
    bool duration_ms_valid = false;
    double step_ms = 0;
    bool step_ms_present = false;
    std::string action_id;
    std::string code;
    std::string button;
    std::string axis;
    std::string mode;
    std::string coordinate_space;
    std::string wheel_unit;
    std::string raw_value = "null";
    unsigned int lease_ms = 5000;
    int device_index = 0;
    bool device_index_valid = true;
    double input_x = 0;
    double input_y = 0;
    bool world_selector_valid = true;
    bool game_input_selector_valid = true;
    bool game_input_metadata_get_valid = true;
    bool guarded = false;
    bool guard_valid = false;
    bool guard_payload_valid = true;
    bool guard_transport_valid = false;
    unsigned long long expected_revision = 0;
    int expected_profile = -1;
};

struct ClientConnection {
    SocketHandle socket = invalid_socket;
    std::shared_ptr<std::mutex> send_mutex;
    unsigned long long game_input_owner_id = 0;
    unsigned long long observe_client_id = 0;
    unsigned long long spatial_client_id = 0;
};

struct DispatchGuard { unsigned long long epoch, revision; int profile; };
using GuardedRequests = std::map<std::pair<bool, unsigned long long>, DispatchGuard>;

std::string spatial_wire(const gua_value_detail::json& j) {
    using gua_value_detail::json;
    if(j.type==json::string) return gua_value_detail::quote(j.text);
    if(j.type==json::number || j.type==json::boolean) return j.text;
    if(j.type==json::null) return "null";
    std::string out=j.type==json::array ? "[" : "{"; bool first=true;
    if(j.type==json::array) for(const auto& item:j.items) { if(!first) out+=','; first=false; out+=spatial_wire(item); }
    else for(const auto& [key,item]:j.fields) { if(!first) out+=','; first=false; out+=gua_value_detail::quote(key)+":"+spatial_wire(item); }
    return out+(j.type==json::array ? "]" : "}");
}

std::string escape_json(std::string_view value)
{
    std::string out;
    out.reserve(value.size());
    for (const char ch : value) {
        switch (ch) {
        case '"':
            out += "\\\"";
            break;
        case '\\':
            out += "\\\\";
            break;
        case '\n':
            out += "\\n";
            break;
        case '\r':
            out += "\\r";
            break;
        case '\t':
            out += "\\t";
            break;
        default:
            if (static_cast<unsigned char>(ch) < 0x20U) {
                constexpr char hex[] = "0123456789abcdef";
                const unsigned char byte = static_cast<unsigned char>(ch);
                out += "\\u00";
                out += hex[byte >> 4U];
                out += hex[byte & 0x0fU];
            } else {
                out += ch;
            }
            break;
        }
    }
    return out;
}

std::uint32_t rotate_left(std::uint32_t value, int bits)
{
    return (value << bits) | (value >> (32 - bits));
}

std::array<std::uint8_t, 20> sha1(std::string_view input)
{
    std::vector<std::uint8_t> message(input.begin(), input.end());
    const std::uint64_t bit_length = static_cast<std::uint64_t>(message.size()) * 8U;

    message.push_back(0x80U);
    while ((message.size() % 64U) != 56U) {
        message.push_back(0U);
    }
    for (int shift = 56; shift >= 0; shift -= 8) {
        message.push_back(static_cast<std::uint8_t>((bit_length >> shift) & 0xffU));
    }

    std::uint32_t h0 = 0x67452301U;
    std::uint32_t h1 = 0xefcdab89U;
    std::uint32_t h2 = 0x98badcfeU;
    std::uint32_t h3 = 0x10325476U;
    std::uint32_t h4 = 0xc3d2e1f0U;

    for (std::size_t chunk = 0; chunk < message.size(); chunk += 64U) {
        std::array<std::uint32_t, 80> words {};
        for (std::size_t i = 0; i < 16U; ++i) {
            const std::size_t offset = chunk + (i * 4U);
            words[i] = (static_cast<std::uint32_t>(message[offset]) << 24U)
                | (static_cast<std::uint32_t>(message[offset + 1U]) << 16U)
                | (static_cast<std::uint32_t>(message[offset + 2U]) << 8U)
                | static_cast<std::uint32_t>(message[offset + 3U]);
        }
        for (std::size_t i = 16U; i < 80U; ++i) {
            words[i] = rotate_left(words[i - 3U] ^ words[i - 8U] ^ words[i - 14U] ^ words[i - 16U], 1);
        }

        std::uint32_t a = h0;
        std::uint32_t b = h1;
        std::uint32_t c = h2;
        std::uint32_t d = h3;
        std::uint32_t e = h4;

        for (std::size_t i = 0; i < 80U; ++i) {
            std::uint32_t f = 0;
            std::uint32_t k = 0;
            if (i < 20U) {
                f = (b & c) | ((~b) & d);
                k = 0x5a827999U;
            } else if (i < 40U) {
                f = b ^ c ^ d;
                k = 0x6ed9eba1U;
            } else if (i < 60U) {
                f = (b & c) | (b & d) | (c & d);
                k = 0x8f1bbcdcU;
            } else {
                f = b ^ c ^ d;
                k = 0xca62c1d6U;
            }
            const std::uint32_t temp = rotate_left(a, 5) + f + e + k + words[i];
            e = d;
            d = c;
            c = rotate_left(b, 30);
            b = a;
            a = temp;
        }

        h0 += a;
        h1 += b;
        h2 += c;
        h3 += d;
        h4 += e;
    }

    const std::array<std::uint32_t, 5> words { h0, h1, h2, h3, h4 };
    std::array<std::uint8_t, 20> digest {};
    for (std::size_t i = 0; i < words.size(); ++i) {
        digest[(i * 4U) + 0U] = static_cast<std::uint8_t>((words[i] >> 24U) & 0xffU);
        digest[(i * 4U) + 1U] = static_cast<std::uint8_t>((words[i] >> 16U) & 0xffU);
        digest[(i * 4U) + 2U] = static_cast<std::uint8_t>((words[i] >> 8U) & 0xffU);
        digest[(i * 4U) + 3U] = static_cast<std::uint8_t>(words[i] & 0xffU);
    }
    return digest;
}

std::string base64_encode(const std::uint8_t* data, std::size_t size)
{
    constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve(((size + 2U) / 3U) * 4U);

    for (std::size_t i = 0; i < size; i += 3U) {
        const std::uint32_t a = data[i];
        const std::uint32_t b = i + 1U < size ? data[i + 1U] : 0U;
        const std::uint32_t c = i + 2U < size ? data[i + 2U] : 0U;
        const std::uint32_t triple = (a << 16U) | (b << 8U) | c;
        out.push_back(alphabet[(triple >> 18U) & 0x3fU]);
        out.push_back(alphabet[(triple >> 12U) & 0x3fU]);
        out.push_back(i + 1U < size ? alphabet[(triple >> 6U) & 0x3fU] : '=');
        out.push_back(i + 2U < size ? alphabet[triple & 0x3fU] : '=');
    }
    return out;
}

std::string websocket_accept_key(std::string_view key)
{
    std::string combined(key);
    combined += websocket_guid;
    const auto digest = sha1(combined);
    return base64_encode(digest.data(), digest.size());
}

void send_all(SocketHandle socket, const std::uint8_t* data, std::size_t size)
{
    std::size_t sent = 0;
    while (sent < size) {
        const std::ptrdiff_t result = gua::ws::platform::send_some(socket, data + sent, size - sent);
        if (result <= 0) {
            throw std::runtime_error("send failed");
        }
        sent += static_cast<std::size_t>(result);
    }
}

void send_all(SocketHandle socket, std::string_view text)
{
    send_all(socket, reinterpret_cast<const std::uint8_t*>(text.data()), text.size());
}

std::vector<std::uint8_t> recv_exact(SocketHandle socket, std::size_t size)
{
    std::vector<std::uint8_t> data(size);
    std::size_t received = 0;
    while (received < size) {
        const std::ptrdiff_t result = gua::ws::platform::receive_some(socket, data.data() + received, size - received);
        if (result <= 0) {
            throw std::runtime_error("connection closed");
        }
        received += static_cast<std::size_t>(result);
    }
    return data;
}

std::string read_http_headers(SocketHandle socket)
{
    std::string headers;
    std::array<char, 1024> buffer {};
    while (headers.find("\r\n\r\n") == std::string::npos) {
        const std::ptrdiff_t result = gua::ws::platform::receive_some(
            socket, reinterpret_cast<std::uint8_t*>(buffer.data()), buffer.size());
        if (result <= 0) {
            throw std::runtime_error("failed to read handshake");
        }
        headers.append(buffer.data(), static_cast<std::size_t>(result));
        if (headers.size() > 16384U) {
            throw std::runtime_error("handshake headers are too large");
        }
    }
    return headers;
}

std::optional<std::string> header_value(std::string_view headers, std::string_view name)
{
    const auto ascii_iequals = [](std::string_view left, std::string_view right) {
        if (left.size() != right.size()) return false;
        for (std::size_t i = 0; i < left.size(); ++i) {
            const auto lower = [](unsigned char ch) {
                return ch >= 'A' && ch <= 'Z' ? static_cast<unsigned char>(ch + ('a' - 'A')) : ch;
            };
            if (lower(static_cast<unsigned char>(left[i])) != lower(static_cast<unsigned char>(right[i]))) return false;
        }
        return true;
    };
    std::size_t start = 0;
    while (start < headers.size()) {
        const std::size_t end = headers.find("\r\n", start);
        if (end == std::string_view::npos) {
            break;
        }
        const std::string_view line = headers.substr(start, end - start);
        const std::size_t colon = line.find(':');
        if (colon != std::string_view::npos) {
            const std::string_view candidate = line.substr(0, colon);
            if (ascii_iequals(candidate, name)) {
                std::size_t value_start = colon + 1U;
                while (value_start < line.size() && line[value_start] == ' ') {
                    ++value_start;
                }
                return std::string(line.substr(value_start));
            }
        }
        start = end + 2U;
    }
    return std::nullopt;
}

void perform_handshake(SocketHandle socket)
{
    const std::string headers = read_http_headers(socket);
    const std::optional<std::string> key = header_value(headers, "Sec-WebSocket-Key");
    if (!key.has_value()) {
        throw std::runtime_error("missing Sec-WebSocket-Key");
    }

    const std::string response = "HTTP/1.1 101 Switching Protocols\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        "Sec-WebSocket-Accept: "
        + websocket_accept_key(*key)
        + "\r\n\r\n";
    send_all(socket, response);
}

std::optional<std::string> read_text_frame(SocketHandle socket)
{
    const auto header = recv_exact(socket, 2);
    const std::uint8_t opcode = header[0] & 0x0fU;
    const bool masked = (header[1] & 0x80U) != 0U;
    std::uint64_t length = header[1] & 0x7fU;

    if (opcode == 0x8U) {
        return std::nullopt;
    }
    if (opcode != 0x1U) {
        throw std::runtime_error("only text WebSocket frames are supported");
    }
    if (!masked) {
        throw std::runtime_error("client WebSocket frames must be masked");
    }
    if (length == 126U) {
        const auto bytes = recv_exact(socket, 2);
        length = (static_cast<std::uint64_t>(bytes[0]) << 8U) | bytes[1];
    } else if (length == 127U) {
        const auto bytes = recv_exact(socket, 8);
        length = 0;
        for (const std::uint8_t byte : bytes) {
            length = (length << 8U) | byte;
        }
    }
    if (length > 1024U * 1024U) {
        throw std::runtime_error("WebSocket frame is too large");
    }

    const auto mask = recv_exact(socket, 4);
    auto payload = recv_exact(socket, static_cast<std::size_t>(length));
    for (std::size_t i = 0; i < payload.size(); ++i) {
        payload[i] ^= mask[i % 4U];
    }
    return std::string(payload.begin(), payload.end());
}

void send_text_frame(SocketHandle socket, std::string_view text)
{
    std::vector<std::uint8_t> frame;
    frame.push_back(0x81U);
    if (text.size() <= 125U) {
        frame.push_back(static_cast<std::uint8_t>(text.size()));
    } else if (text.size() <= 65535U) {
        frame.push_back(126U);
        frame.push_back(static_cast<std::uint8_t>((text.size() >> 8U) & 0xffU));
        frame.push_back(static_cast<std::uint8_t>(text.size() & 0xffU));
    } else {
        frame.push_back(127U);
        const std::uint64_t size = static_cast<std::uint64_t>(text.size());
        for (int shift = 56; shift >= 0; shift -= 8) {
            frame.push_back(static_cast<std::uint8_t>((size >> shift) & 0xffU));
        }
    }
    frame.insert(frame.end(), text.begin(), text.end());
    send_all(socket, frame.data(), frame.size());
}

std::optional<std::size_t> json_top_level_field_start(std::string_view json, std::string_view field)
{
    const std::size_t root = json.find_first_not_of(" \t\r\n");
    if (root == std::string_view::npos || json[root] != '{') return std::nullopt;

    int object_depth = 0;
    int array_depth = 0;
    bool in_string = false;
    bool escaped = false;
    for (std::size_t index = root; index < json.size(); ++index) {
        const char ch = json[index];
        if (in_string) {
            if (escaped) escaped = false;
            else if (ch == '\\') escaped = true;
            else if (ch == '"') in_string = false;
            continue;
        }
        if (ch == '"') {
            std::size_t previous = index;
            while (previous > root && (json[previous - 1U] == ' ' || json[previous - 1U] == '\t' ||
                json[previous - 1U] == '\r' || json[previous - 1U] == '\n')) --previous;
            const bool top_level_key = object_depth == 1 && array_depth == 0 && previous > root &&
                (json[previous - 1U] == '{' || json[previous - 1U] == ',');
            if (!top_level_key) {
                in_string = true;
                continue;
            }

            std::size_t end = index + 1U;
            bool key_escaped = false;
            for (; end < json.size(); ++end) {
                if (key_escaped) key_escaped = false;
                else if (json[end] == '\\') key_escaped = true;
                else if (json[end] == '"') break;
            }
            if (end == json.size()) return std::nullopt;
            std::size_t colon = json.find_first_not_of(" \t\r\n", end + 1U);
            if (colon == std::string_view::npos || json[colon] != ':') return std::nullopt;
            const bool matches = json.substr(index + 1U, end - index - 1U) == field;
            std::size_t value = json.find_first_not_of(" \t\r\n", colon + 1U);
            if (matches) return value == std::string_view::npos ? std::nullopt : std::optional<std::size_t>(value);
            index = end;
            continue;
        }
        if (ch == '{') ++object_depth;
        else if (ch == '}') {
            if (--object_depth == 0) break;
        } else if (ch == '[') ++array_depth;
        else if (ch == ']') --array_depth;
    }
    return std::nullopt;
}

std::optional<std::string> json_string_field(std::string_view json, std::string_view field)
{
    const auto start = json_top_level_field_start(json, field);
    if (!start.has_value()) return std::nullopt;
    const std::size_t quote = *start;
    if (quote >= json.size() || json[quote] != '"') {
        return std::nullopt;
    }

    const auto hex_value = [](char ch) -> int {
        if (ch >= '0' && ch <= '9') return ch - '0';
        if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
        if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
        return -1;
    };
    const auto append_utf8 = [](std::string& out, std::uint32_t codepoint) {
        if (codepoint <= 0x7fU) {
            out.push_back(static_cast<char>(codepoint));
        } else if (codepoint <= 0x7ffU) {
            out.push_back(static_cast<char>(0xc0U | (codepoint >> 6U)));
            out.push_back(static_cast<char>(0x80U | (codepoint & 0x3fU)));
        } else if (codepoint <= 0xffffU) {
            out.push_back(static_cast<char>(0xe0U | (codepoint >> 12U)));
            out.push_back(static_cast<char>(0x80U | ((codepoint >> 6U) & 0x3fU)));
            out.push_back(static_cast<char>(0x80U | (codepoint & 0x3fU)));
        } else {
            out.push_back(static_cast<char>(0xf0U | (codepoint >> 18U)));
            out.push_back(static_cast<char>(0x80U | ((codepoint >> 12U) & 0x3fU)));
            out.push_back(static_cast<char>(0x80U | ((codepoint >> 6U) & 0x3fU)));
            out.push_back(static_cast<char>(0x80U | (codepoint & 0x3fU)));
        }
    };
    const auto read_hex_quad = [&](std::size_t start, std::uint32_t& result) -> bool {
        if (start + 4U > json.size()) return false;
        result = 0;
        for (std::size_t offset = 0; offset < 4U; ++offset) {
            const int digit = hex_value(json[start + offset]);
            if (digit < 0) return false;
            result = (result << 4U) | static_cast<std::uint32_t>(digit);
        }
        return true;
    };

    std::string value;
    for (std::size_t i = quote + 1U; i < json.size(); ++i) {
        const char ch = json[i];
        if (ch == '\\') {
            if (++i >= json.size()) return std::nullopt;
            switch (json[i]) {
            case '"': value.push_back('"'); break;
            case '\\': value.push_back('\\'); break;
            case '/': value.push_back('/'); break;
            case 'b': value.push_back('\b'); break;
            case 'f': value.push_back('\f'); break;
            case 'n': value.push_back('\n'); break;
            case 'r': value.push_back('\r'); break;
            case 't': value.push_back('\t'); break;
            case 'u': {
                std::uint32_t codepoint = 0;
                if (!read_hex_quad(i + 1U, codepoint)) return std::nullopt;
                i += 4U;
                if (codepoint >= 0xd800U && codepoint <= 0xdbffU) {
                    if (i + 6U >= json.size() || json[i + 1U] != '\\' || json[i + 2U] != 'u') return std::nullopt;
                    std::uint32_t low = 0;
                    if (!read_hex_quad(i + 3U, low) || low < 0xdc00U || low > 0xdfffU) return std::nullopt;
                    codepoint = 0x10000U + ((codepoint - 0xd800U) << 10U) + (low - 0xdc00U);
                    i += 6U;
                } else if (codepoint >= 0xdc00U && codepoint <= 0xdfffU) {
                    return std::nullopt;
                }
                append_utf8(value, codepoint);
                break;
            }
            default: return std::nullopt;
            }
            continue;
        }
        if (ch == '"') {
            return value;
        }
        value.push_back(ch);
    }
    return std::nullopt;
}

std::optional<std::vector<std::string>> json_string_array_field(std::string_view json, std::string_view field)
{
    const auto start = json_top_level_field_start(json, field);
    if (!start.has_value()) return std::vector<std::string> {};
    std::size_t index = *start;
    if (index >= json.size() || json[index++] != '[') return std::nullopt;
    std::vector<std::string> values;
    while (true) {
        index = json.find_first_not_of(" \t\r\n", index);
        if (index == std::string_view::npos) return std::nullopt;
        if (json[index] == ']') return values;
        if (json[index] != '"') return std::nullopt;
        const std::size_t value_start = index;
        bool escaped = false;
        for (++index; index < json.size(); ++index) {
            if (escaped) { escaped = false; continue; }
            if (json[index] == '\\') { escaped = true; continue; }
            if (json[index] == '"') break;
        }
        if (index >= json.size()) return std::nullopt;
        const std::string wrapped = std::string("{\"value\":") + std::string(json.substr(value_start, index - value_start + 1U)) + "}";
        const auto parsed = json_string_field(wrapped, "value");
        if (!parsed.has_value()) return std::nullopt;
        values.push_back(*parsed);
        index = json.find_first_not_of(" \t\r\n", index + 1U);
        if (index == std::string_view::npos) return std::nullopt;
        if (json[index] == ']') return values;
        if (json[index++] != ',') return std::nullopt;
        index = json.find_first_not_of(" \t\r\n", index);
        if (index == std::string_view::npos || json[index] == ']') return std::nullopt;
    }
}

std::optional<int> json_int_field(std::string_view json, std::string_view field)
{
    const auto value_start = json_top_level_field_start(json, field);
    if (!value_start.has_value()) return std::nullopt;
    const std::size_t start = *value_start;
    std::size_t end = start;
    while (end < json.size() && json[end] >= '0' && json[end] <= '9') {
        ++end;
    }
    if (end == start) {
        return std::nullopt;
    }
    std::size_t delimiter = end;
    while (delimiter < json.size() && (json[delimiter] == ' ' || json[delimiter] == '\t' ||
        json[delimiter] == '\r' || json[delimiter] == '\n')) ++delimiter;
    if (delimiter == json.size() || (json[delimiter] != ',' && json[delimiter] != '}' && json[delimiter] != ']'))
        return std::nullopt;
    try { return std::stoi(std::string(json.substr(start, end - start))); }
    catch (const std::exception&) { return std::nullopt; }
}

std::optional<unsigned long long> json_uint64_field(std::string_view json, std::string_view field)
{
    const auto value_start = json_top_level_field_start(json, field);
    if (!value_start.has_value()) return std::nullopt;
    const std::size_t start = *value_start;
    std::size_t end = start;
    while (end < json.size() && std::isdigit(static_cast<unsigned char>(json[end]))) ++end;
    if (end == start) return std::nullopt;
    std::size_t delimiter = end;
    while (delimiter < json.size() && (json[delimiter] == ' ' || json[delimiter] == '\t' ||
        json[delimiter] == '\r' || json[delimiter] == '\n')) ++delimiter;
    if (delimiter == json.size() || (json[delimiter] != ',' && json[delimiter] != '}' && json[delimiter] != ']'))
        return std::nullopt;
    try { return std::stoull(std::string(json.substr(start, end - start))); }
    catch (const std::exception&) { return std::nullopt; }
}

std::optional<double> json_number_field(std::string_view json, std::string_view field)
{
    const auto value_start = json_top_level_field_start(json, field);
    if (!value_start.has_value()) return std::nullopt;
    const std::size_t start = *value_start;

    std::size_t end = start;
    if (json[end] == '-') {
        if (++end == json.size()) return std::nullopt;
    }
    if (json[end] == '0') {
        ++end;
        if (end < json.size() && std::isdigit(static_cast<unsigned char>(json[end]))) return std::nullopt;
    } else if (json[end] >= '1' && json[end] <= '9') {
        while (end < json.size() && std::isdigit(static_cast<unsigned char>(json[end]))) ++end;
    } else {
        return std::nullopt;
    }
    if (end < json.size() && json[end] == '.') {
        ++end;
        const std::size_t fraction_start = end;
        while (end < json.size() && std::isdigit(static_cast<unsigned char>(json[end]))) ++end;
        if (end == fraction_start) return std::nullopt;
    }
    if (end < json.size() && (json[end] == 'e' || json[end] == 'E')) {
        ++end;
        if (end < json.size() && (json[end] == '+' || json[end] == '-')) ++end;
        const std::size_t exponent_start = end;
        while (end < json.size() && std::isdigit(static_cast<unsigned char>(json[end]))) ++end;
        if (end == exponent_start) return std::nullopt;
    }
    std::size_t delimiter = end;
    while (delimiter < json.size() && (json[delimiter] == ' ' || json[delimiter] == '\t' ||
        json[delimiter] == '\r' || json[delimiter] == '\n')) ++delimiter;
    if (delimiter == json.size() || (json[delimiter] != ',' && json[delimiter] != '}' && json[delimiter] != ']'))
        return std::nullopt;
    const std::string token(json.substr(start, end - start));
    std::istringstream stream(token);
    stream.imbue(std::locale::classic());
    double value = 0;
    stream >> value;
    return !stream.fail() && stream.peek() == std::char_traits<char>::eof() && std::isfinite(value)
        ? std::optional<double>(value) : std::nullopt;
}

bool json_has_field(std::string_view json, std::string_view field)
{
    return json_top_level_field_start(json, field).has_value();
}

bool json_has_non_null_field(std::string_view json, std::string_view field)
{
    const auto start = json_top_level_field_start(json, field);
    return start.has_value() && json.substr(*start, 4) != "null";
}

bool valid_optional_non_empty_string(std::string_view json, std::string_view field)
{
    if (!json_has_field(json, field)) return true;
    const auto value = json_string_field(json, field);
    return value.has_value() && !value->empty();
}

bool valid_game_input_identifier(std::string_view value)
{
    if (value.empty() || value.size() >= 128U || value.front() < 'a' || value.front() > 'z') return false;
    return std::all_of(value.begin(), value.end(), [](char ch) {
        return (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == '-';
    });
}

bool valid_game_input_text(std::string_view value, std::size_t maximum_code_points, bool allow_nul = false)
{
    if (value.empty()) return false;
    std::size_t code_points = 0;
    for (std::size_t index = 0; index < value.size();) {
        const unsigned char first = static_cast<unsigned char>(value[index]);
        if (first == 0U && !allow_nul) return false;
        const std::size_t width = first < 0x80U ? 1U : first >= 0xC2U && first <= 0xDFU ? 2U :
            first >= 0xE0U && first <= 0xEFU ? 3U : first >= 0xF0U && first <= 0xF4U ? 4U : 0U;
        if (width == 0U || index + width > value.size()) return false;
        for (std::size_t offset = 1; offset < width; ++offset)
            if ((static_cast<unsigned char>(value[index + offset]) & 0xC0U) != 0x80U) return false;
        if (width == 3U) {
            const unsigned char second = static_cast<unsigned char>(value[index + 1U]);
            if ((first == 0xE0U && second < 0xA0U) || (first == 0xEDU && second >= 0xA0U)) return false;
        }
        if (width == 4U) {
            const unsigned char second = static_cast<unsigned char>(value[index + 1U]);
            if ((first == 0xF0U && second < 0x90U) || (first == 0xF4U && second >= 0x90U)) return false;
        }
        index += width;
        if (++code_points > maximum_code_points) return false;
    }
    return true;
}

bool valid_game_input_selector(const gua::ws::GameInputQuerySelector& selector)
{
    if (selector.value_type < 0 || selector.value_type > 4 || selector.active < 0 || selector.active > 2 ||
        selector.limit < 1U || selector.limit > 100U ||
        (!selector.id.empty() && !valid_game_input_identifier(selector.id)) ||
        (!selector.query.empty() && !valid_game_input_text(selector.query, 128U)) ||
        (!selector.context.empty() && !valid_game_input_text(selector.context, std::numeric_limits<std::size_t>::max())) ||
        (!selector.category.empty() && !valid_game_input_identifier(selector.category)) || selector.tags.size() > 16U) return false;
    std::unordered_set<std::string> unique_tags;
    for (const auto& tag : selector.tags)
        if (!valid_game_input_text(tag, 64U) || !unique_tags.insert(tag).second) return false;
    return true;
}

bool valid_optional_int_range(std::string_view json, std::string_view field, int minimum, int maximum)
{
    if (!json_has_field(json, field)) return true;
    const auto value = json_int_field(json, field);
    return value.has_value() && *value >= minimum && *value <= maximum;
}

std::string unsupported_world_object_tree_json(std::string_view ui_tree_json)
{
    const auto session_epoch = json_uint64_field(ui_tree_json, "sessionEpoch").value_or(1);
    return "{\"schemaVersion\":1,\"sessionEpoch\":" + std::to_string(session_epoch) +
        ",\"frameSequence\":0,\"revision\":0,\"scene\":\"unsupported\",\"objects\":[]}";
}

bool json_bool_field(std::string_view json, std::string_view field, bool fallback = false)
{
    const auto start = json_top_level_field_start(json, field);
    return start.has_value() ? json.substr(*start, 4) == "true" : fallback;
}

std::optional<std::string> json_raw_field(std::string_view json, std::string_view field)
{
    const auto start_value = json_top_level_field_start(json, field);
    if (!start_value.has_value()) return std::nullopt;
    const std::size_t start = *start_value;
    if (start >= json.size()) return std::nullopt;
    bool quoted = json[start] == '"';
    int depth = 0;
    bool escaped = false;
    for (std::size_t index = start; index < json.size(); ++index) {
        const char ch = json[index];
        if (quoted) {
            if (escaped) { escaped = false; continue; }
            if (ch == '\\') { escaped = true; continue; }
            if (index > start && ch == '"') return std::string(json.substr(start, index - start + 1));
            continue;
        }
        if (ch == '{' || ch == '[') ++depth;
        else if (ch == '}' || ch == ']') {
            if (depth == 0) return std::string(json.substr(start, index - start));
            --depth;
        }
        if (depth == 0 && ch == ',') return std::string(json.substr(start, index - start));
    }
    return std::nullopt;
}

std::optional<bool> json_bool_field_optional(std::string_view json, std::string_view field)
{
    const auto start = json_top_level_field_start(json, field);
    if (!start.has_value()) return std::nullopt;
    if (json.substr(*start, 4) == "true") return true;
    if (json.substr(*start, 5) == "false") return false;
    return std::nullopt;
}

template <std::size_t Size>
bool json_has_only_top_level_fields(std::string_view json, const std::array<std::string_view, Size>& allowed)
{
    const std::size_t root = json.find_first_not_of(" \t\r\n");
    if (root == std::string_view::npos || json[root] != '{') return false;

    int object_depth = 0;
    int array_depth = 0;
    bool in_string = false;
    bool escaped = false;
    bool root_closed = false;
    std::size_t root_end = root;
    for (std::size_t index = root; index < json.size(); ++index) {
        const char ch = json[index];
        if (in_string) {
            if (escaped) escaped = false;
            else if (ch == '\\') escaped = true;
            else if (ch == '"') in_string = false;
            continue;
        }
        if (ch == '"') {
            std::size_t previous = index;
            while (previous > root && std::isspace(static_cast<unsigned char>(json[previous - 1U]))) --previous;
            const bool top_level_key = object_depth == 1 && array_depth == 0 && previous > root &&
                (json[previous - 1U] == '{' || json[previous - 1U] == ',');
            if (!top_level_key) {
                in_string = true;
                continue;
            }

            std::size_t end = index + 1U;
            bool key_escaped = false;
            for (; end < json.size(); ++end) {
                if (key_escaped) key_escaped = false;
                else if (json[end] == '\\') key_escaped = true;
                else if (json[end] == '"') break;
            }
            if (end == json.size()) return false;
            const auto key = json.substr(index + 1U, end - index - 1U);
            if (std::find(allowed.begin(), allowed.end(), key) == allowed.end()) return false;
            const std::size_t colon = json.find_first_not_of(" \t\r\n", end + 1U);
            if (colon == std::string_view::npos || json[colon] != ':') return false;
            index = end;
            continue;
        }
        if (ch == '{') ++object_depth;
        else if (ch == '}') {
            if (--object_depth == 0) {
                root_closed = true;
                root_end = index;
                break;
            }
            if (object_depth < 0) return false;
        } else if (ch == '[') ++array_depth;
        else if (ch == ']' && --array_depth < 0) return false;
    }
    return root_closed && json.find_first_not_of(" \t\r\n", root_end + 1U) == std::string_view::npos;
}

Command parse_command(std::string_view json)
{
    Command command;
    command.type = json_string_field(json, "type").value_or("");
    command.guarded = command.type.starts_with("guarded_");
    if (command.guarded) {
        command.type.erase(0, 8);
        // Capture correlation before rejecting malformed metadata. Parse guards
        // only once with from_chars; the legacy stoull path is unnecessary here.
        command.id = json_int_field(json, "id").value_or(0);
        command.guard_transport_valid = command.id > 0;
        // Read guards from the root object: nested payload fields cannot impersonate metadata.
        try {
            const auto root = gua_value_detail::parser(json).parse();
            constexpr std::array fields { std::string_view("id"), std::string_view("type"), std::string_view("expectedSessionEpoch"),
                std::string_view("expectedProfile"), std::string_view("expectedRevision"), std::string_view("requestId"),
                std::string_view("nodeId"), std::string_view("value"), std::string_view("key"), std::string_view("checked"),
                std::string_view("deltaX"), std::string_view("deltaY"), std::string_view("modifiers"), std::string_view("scrollUnit"),
                std::string_view("sensitive"), std::string_view("confirmed"), std::string_view("actionId"), std::string_view("code"),
                std::string_view("button"), std::string_view("axis"), std::string_view("text"), std::string_view("mode"),
                std::string_view("coordinateSpace"), std::string_view("wheelUnit"), std::string_view("leaseMs"),
                std::string_view("x"), std::string_view("y"), std::string_view("gamepadIndex"), std::string_view("secretKey") };
            for (const auto& [key, value] : root.fields)
                if (std::find(fields.begin(), fields.end(), key) == fields.end()) throw std::runtime_error("invalid_guard");
            auto number = [&](const char* name) -> unsigned long long {
                const auto& value = root.at(name);
                unsigned long long result = 0;
                const auto parsed = std::from_chars(value.text.data(), value.text.data() + value.text.size(), result);
                if (value.type != gua_value_detail::json::number || parsed.ec != std::errc() ||
                    parsed.ptr != value.text.data() + value.text.size()) throw std::runtime_error("invalid_guard");
                return result;
            };
            const auto transport_id = number("id");
            command.guard_transport_valid = transport_id > 0 && transport_id <= static_cast<unsigned long long>(std::numeric_limits<int>::max());
            if (!command.guard_transport_valid) throw std::runtime_error("invalid_request");
            command.id = static_cast<int>(transport_id);
            command.expected_revision = number("expectedRevision");
            const auto profile = number("expectedProfile");
            command.expected_profile = profile <= 1 ? static_cast<int>(profile) : -1;
            command.expected_session_epoch = number("expectedSessionEpoch");
            command.guard_valid = command.expected_session_epoch != 0 &&
                command.expected_session_epoch <= 9007199254740991ULL &&
                command.expected_revision <= 9007199254740991ULL && profile <= 1;
            // Guarded verbs must not inherit the legacy parser's absent-field defaults.
            // Validate required payload fields at the root before any host enqueue.
            try {
                const auto payload_json = json;
                using gua_value_detail::json;
                auto require = [&](const char* name, json::kind type, bool nonempty = false) {
                    const auto& value = root.at(name);
                    if (value.type != type || (nonempty && value.text.empty()))
                        throw std::runtime_error("invalid_request");
                };
                auto present = [&](const char* name) { return root.fields.contains(name); };
                auto numeric = [&](const char* name) {
                    require(name, json::number); const auto value = json_number_field(payload_json, name);
                    if (!value || !std::isfinite(*value)) throw std::runtime_error("invalid_request");
                    return *value;
                };
                auto allow = [&](std::initializer_list<std::string_view> payload) {
                    for (const auto& [name, value] : root.fields) {
                        if (name == "id" || name == "type" || name == "expectedSessionEpoch" || name == "expectedProfile" || name == "expectedRevision") continue;
                        if (std::find(payload.begin(), payload.end(), name) == payload.end()) throw std::runtime_error("invalid_request");
                    }
                };
                const auto& verb = command.type;
                if (verb == "click_node" || verb == "focus_node") { allow({"nodeId","requestId"}); require("nodeId",json::string,true); }
                else if (verb == "set_value" || verb == "select") {
                    if (verb == "set_value") allow({"nodeId","value","sensitive","requestId"}); else allow({"nodeId","value","requestId"});
                    require("nodeId",json::string,true); require("value",json::string,verb == "select");
                } else if (verb == "set_checked") { allow({"nodeId","checked","requestId"}); require("nodeId",json::string,true); require("checked",json::boolean); }
                else if (verb == "scroll") { allow({"nodeId","deltaX","deltaY","scrollUnit","requestId"}); require("nodeId",json::string,true); numeric("deltaX"); numeric("deltaY"); }
                else if (verb == "press_key") { allow({"nodeId","key","modifiers","requestId"}); require("key",json::string,true); if (present("nodeId")) require("nodeId",json::string,true); }
                else if (verb == "press_game_input_action" || verb == "set_game_input_action" || verb == "release_game_input_action") {
                    if (verb == "press_game_input_action") allow({"actionId","confirmed"});
                    else if (verb == "set_game_input_action") allow({"actionId","value","leaseMs","confirmed","sensitive"});
                    else allow({"actionId"});
                    require("actionId",json::string,true);
                    if (!valid_game_input_identifier(root.at("actionId").text)) throw std::runtime_error("invalid_request");
                    if (verb == "set_game_input_action") {
                        const auto& value = root.at("value");
                        if (value.type != json::boolean && value.type != json::number && value.type != json::string && value.type != json::object) throw std::runtime_error("invalid_request");
                        if (value.type == json::number) numeric("value");
                        if (value.type == json::string && !value.text.empty() && !valid_game_input_text(value.text,40,true)) throw std::runtime_error("invalid_request");
                    }
                } else if (verb == "key_down" || verb == "key_up" || verb == "press_physical_key") { allow({"code","leaseMs"}); require("code",json::string,true); }
                else if (verb == "pointer_move") {
                    allow({"mode","coordinateSpace","x","y"}); require("mode",json::string,true); const auto& mode = root.at("mode").text;
                    const double x = numeric("x"), y = numeric("y");
                    if (mode == "absolute") {
                        require("coordinateSpace",json::string,true); const auto& space = root.at("coordinateSpace").text;
                        if (space != "viewport_pixels" && space != "viewport_normalized") throw std::runtime_error("invalid_request");
                        if (space == "viewport_normalized" && (x < 0 || x > 1 || y < 0 || y > 1)) throw std::runtime_error("invalid_request");
                    } else if (mode != "delta" || present("coordinateSpace")) throw std::runtime_error("invalid_request");
                } else if (verb == "pointer_button_down" || verb == "pointer_button_up") { allow({"button","leaseMs"}); require("button",json::string,true); }
                else if (verb == "pointer_wheel") {
                    allow({"deltaX","deltaY","wheelUnit"}); if (!present("deltaX") && !present("deltaY")) throw std::runtime_error("invalid_request");
                    if (present("deltaX")) numeric("deltaX"); if (present("deltaY")) numeric("deltaY");
                    if (present("wheelUnit")) { require("wheelUnit",json::string,true); const auto& unit = root.at("wheelUnit").text; if (unit != "pixels" && unit != "lines") throw std::runtime_error("invalid_request"); }
                } else if (verb == "gamepad_button_down" || verb == "gamepad_button_up") { allow({"button","gamepadIndex","leaseMs"}); require("button",json::string,true); }
                else if (verb == "set_gamepad_axis") { allow({"axis","value","gamepadIndex","leaseMs"}); require("axis",json::string,true); const double value = numeric("value"); if (value < -1 || value > 1) throw std::runtime_error("invalid_request"); }
                else if (verb == "text_input") { allow({"text","sensitive","secretKey"}); require("text",json::string); const auto& text = root.at("text").text; if (!text.empty() && !valid_game_input_text(text,40,true)) throw std::runtime_error("invalid_request"); if (present("secretKey")) require("secretKey",json::string,true); }
                else if (verb == "poll_action" || verb == "poll_game_input") allow({"requestId"});
                for (auto field : {"checked","sensitive","confirmed"}) if (present(field)) require(field,json::boolean);
                if (present("leaseMs")) { const auto lease = number("leaseMs"); if (lease < 1 || lease > 60000) throw std::runtime_error("invalid_request"); }
                if (present("gamepadIndex") && number("gamepadIndex") > 3) throw std::runtime_error("invalid_request");
                if (present("scrollUnit") && number("scrollUnit") > 1) throw std::runtime_error("invalid_request");
                if (present("modifiers")) {
                    const auto modifiers = number("modifiers");
                    if (modifiers > std::numeric_limits<std::uint32_t>::max()) throw std::runtime_error("invalid_request");
                    command.modifiers = static_cast<unsigned int>(modifiers);
                }
                if (present("requestId") && number("requestId") == 0) throw std::runtime_error("invalid_request");
            } catch (...) { command.guard_payload_valid = false; }
        }
        catch (...) { command.guard_valid = false; }
    }
    // Observe validates the full request and its safe-width ID in handle_command.
    // Do not first narrow it through the legacy std::stoi parser.
    if (command.type == "get_observe_snapshot" || command.type == "subscribe_observations" ||
        command.type == "poll_observations" || command.type == "unsubscribe_observations" || command.type == "semantic_lint") return command;
    if (!command.guarded) command.id = json_int_field(json, "id").value_or(0);
    command.node_id = json_string_field(json, "nodeId").value_or("");
    command.key = json_string_field(json, "key").value_or("");
    command.selector.id = json_string_field(json, "selectorId").value_or("");
    command.selector.id_match = json_int_field(json, "idMatch").value_or(0);
    command.selector.role = json_string_field(json, "role").value_or("");
    command.selector.role_match = json_int_field(json, "roleMatch").value_or(0);
    command.selector.name = json_string_field(json, "name").value_or("");
    command.selector.name_match = json_int_field(json, "nameMatch").value_or(0);
    command.selector.text = json_string_field(json, "text").value_or("");
    command.selector.text_match = json_int_field(json, "textMatch").value_or(0);
    command.selector.parent_id = json_string_field(json, "parentId").value_or("");
    command.selector.direct_child = json_int_field(json, "directChild").value_or(0) != 0;
    command.selector.visible = json_int_field(json, "visible").value_or(0);
    command.selector.enabled = json_int_field(json, "enabled").value_or(0);
    command.world_selector.id = json_string_field(json, "worldId").value_or("");
    command.world_selector.id_match = json_int_field(json, "worldIdMatch").value_or(0);
    command.world_selector.kind = json_string_field(json, "kind").value_or("");
    command.world_selector.kind_match = json_int_field(json, "kindMatch").value_or(0);
    command.world_selector.label = json_string_field(json, "label").value_or("");
    command.world_selector.label_match = json_int_field(json, "labelMatch").value_or(0);
    command.world_selector.tag = json_string_field(json, "tag").value_or("");
    command.world_selector.tag_match = json_int_field(json, "tagMatch").value_or(0);
    command.world_selector.parent_id = json_string_field(json, "parentId").value_or("");
    const auto world_direct_child = json_int_field(json, "directChild");
    command.world_selector.direct_child = world_direct_child.value_or(0) == 1;
    command.world_selector.visible_to_player = json_int_field(json, "visibleToPlayer").value_or(0);
    command.world_selector.active = json_int_field(json, "active").value_or(0);
    command.world_selector.state_key = json_string_field(json, "stateKey").value_or("");
    command.world_selector.state_type = json_int_field(json, "stateType").value_or(-1);
    command.world_selector.state_string = json_string_field(json, "stateString").value_or("");
    command.world_selector.state_number = json_number_field(json, "stateNumber").value_or(0);
    command.world_selector.state_bool = json_bool_field(json, "stateBool");
    command.world_selector.relative_to_object_id = json_string_field(json, "relativeToObjectId").value_or("");
    command.world_selector.max_distance = json_number_field(json, "maxDistance").value_or(0);
    const auto world_limit = json_number_field(json, "limit");
    const bool world_limit_valid = world_limit.has_value() && *world_limit >= 1 &&
        *world_limit <= static_cast<double>(UINT32_MAX) && std::trunc(*world_limit) == *world_limit;
    command.world_selector.limit = world_limit_valid ? static_cast<unsigned int>(*world_limit) : 0;
    const bool relative_present = json_has_field(json, "relativeToObjectId");
    const bool max_distance_present = json_has_field(json, "maxDistance");
    const bool limit_present = json_has_field(json, "limit");
    command.world_selector.spatial = relative_present && max_distance_present;
    constexpr std::array world_query_fields { std::string_view("id"), std::string_view("type"), std::string_view("worldId"),
        std::string_view("worldIdMatch"), std::string_view("kind"), std::string_view("kindMatch"), std::string_view("label"),
        std::string_view("labelMatch"), std::string_view("tag"), std::string_view("tagMatch"), std::string_view("parentId"),
        std::string_view("directChild"), std::string_view("visibleToPlayer"), std::string_view("active"), std::string_view("stateKey"),
        std::string_view("stateType"), std::string_view("stateString"), std::string_view("stateNumber"), std::string_view("stateBool"),
        std::string_view("relativeToObjectId"), std::string_view("maxDistance"), std::string_view("limit") };
    command.world_selector_valid = (command.type != "query_world_objects" || json_has_only_top_level_fields(json, world_query_fields)) &&
        valid_optional_non_empty_string(json, "worldId") &&
        valid_optional_non_empty_string(json, "kind") && valid_optional_non_empty_string(json, "label") &&
        valid_optional_non_empty_string(json, "tag") && valid_optional_non_empty_string(json, "parentId") &&
        valid_optional_int_range(json, "worldIdMatch", 0, 2) && valid_optional_int_range(json, "kindMatch", 0, 2) &&
        valid_optional_int_range(json, "labelMatch", 0, 2) && valid_optional_int_range(json, "tagMatch", 0, 2) &&
        valid_optional_int_range(json, "directChild", 0, 1) &&
        valid_optional_int_range(json, "visibleToPlayer", 0, 2) && valid_optional_int_range(json, "active", 0, 2) &&
        (!command.world_selector.direct_child || !command.world_selector.parent_id.empty()) &&
        (relative_present == max_distance_present) && (!relative_present || (!command.world_selector.relative_to_object_id.empty() &&
            json_number_field(json, "maxDistance").has_value() && command.world_selector.max_distance >= 0)) &&
        (!limit_present || (command.world_selector.spatial && world_limit_valid));
    const bool state_key_present = json_has_field(json, "stateKey");
    const bool state_type_present = json_has_field(json, "stateType");
    const bool state_string_present = json_has_field(json, "stateString");
    const bool state_number_present = json_has_field(json, "stateNumber");
    const bool state_bool_present = json_has_field(json, "stateBool");
    const bool any_state = state_key_present || state_type_present || state_string_present || state_number_present || state_bool_present;
    command.world_selector_valid = command.world_selector_valid && (!any_state ||
        (state_key_present && !command.world_selector.state_key.empty() && state_type_present &&
            command.world_selector.state_type >= 0 && command.world_selector.state_type <= 3 &&
            ((command.world_selector.state_type == 0 && !state_string_present && !state_number_present && !state_bool_present) ||
             (command.world_selector.state_type == 1 && state_string_present && json_string_field(json, "stateString").has_value() && !state_number_present && !state_bool_present) ||
             (command.world_selector.state_type == 2 && state_number_present && json_number_field(json, "stateNumber").has_value() && !state_string_present && !state_bool_present) ||
             (command.world_selector.state_type == 3 && state_bool_present && json_bool_field_optional(json, "stateBool").has_value() && !state_string_present && !state_number_present))));
    command.game_input_selector.id = json_string_field(json, "actionId").value_or("");
    command.game_input_selector.query = json_string_field(json, "query").value_or("");
    command.game_input_selector.value_type = json_int_field(json, "valueType").value_or(0);
    command.game_input_selector.active = json_int_field(json, "active").value_or(0);
    command.game_input_selector.context = json_string_field(json, "context").value_or("");
    command.game_input_selector.category = json_string_field(json, "category").value_or("");
    const auto game_input_tags = json_string_array_field(json, "tags");
    if (game_input_tags.has_value()) command.game_input_selector.tags = *game_input_tags;
    const auto game_input_limit = json_int_field(json, "limit");
    command.game_input_selector.limit = static_cast<unsigned int>(game_input_limit.value_or(20));
    constexpr std::array game_input_query_fields { std::string_view("id"), std::string_view("type"),
        std::string_view("actionId"), std::string_view("query"), std::string_view("valueType"),
        std::string_view("active"), std::string_view("context"), std::string_view("category"),
        std::string_view("tags"), std::string_view("limit") };
    command.game_input_selector_valid = (command.type != "find_game_input_actions" && command.type != "find_game_input_actions_v2") ||
        (json_has_only_top_level_fields(json, game_input_query_fields) && game_input_tags.has_value() &&
            valid_optional_non_empty_string(json, "actionId") && valid_optional_non_empty_string(json, "query") &&
            valid_optional_non_empty_string(json, "context") && valid_optional_non_empty_string(json, "category") &&
            valid_optional_int_range(json, "valueType", 1, 4) && valid_optional_int_range(json, "active", 0, 2) &&
            valid_optional_int_range(json, "limit", 1, 100) &&
            gua::ws::detail::valid_game_input_query_selector(command.game_input_selector));
    const auto metadata_get_fields = std::array { std::string_view("id"), std::string_view("type") };
    command.game_input_metadata_get_valid = command.type != "get_game_input_actions_v2" ||
        json_has_only_top_level_fields(json, metadata_get_fields);
    command.value = json_string_field(json, "value").value_or("");
    command.delta_x = static_cast<float>(json_number_field(json, "deltaX").value_or(0));
    command.delta_y = static_cast<float>(json_number_field(json, "deltaY").value_or(0));
    command.bool_value = json_bool_field(json, "checked");
    if (!command.guarded) command.modifiers = static_cast<unsigned int>(json_int_field(json, "modifiers").value_or(0));
    command.sensitive = json_bool_field(json, "sensitive");
    command.confirmed = json_bool_field(json, "confirmed");
    command.scroll_unit = json_int_field(json, "scrollUnit").value_or(0);
    command.request_id = json_uint64_field(json, "requestId").value_or(0);
    if (!command.guarded) command.expected_session_epoch = json_uint64_field(json, "expectedSessionEpoch").value_or(0);
    command.after_frame_sequence = json_uint64_field(json, "afterFrameSequence").value_or(0);
    command.timeout_ms = static_cast<unsigned int>(std::clamp(json_int_field(json, "timeoutMs").value_or(10000), 1, 300000));
    const bool reset_flags_present = json_has_field(json, "flags");
    command.reset_flags = static_cast<unsigned int>(json_int_field(json, "flags").value_or(207));
    const auto reset_flags_version = json_int_field(json, "flagsVersion");
    command.reset_flags_version = static_cast<unsigned int>(reset_flags_version.value_or(reset_flags_present ? 0 : 2));
    command.reset_flags_version_valid = !json_has_field(json, "flagsVersion") ||
        (reset_flags_version.has_value() && (*reset_flags_version == 1 || *reset_flags_version == 2));
    command.strict = json_bool_field(json, "strict");
    const auto initial_time_ms = json_number_field(json, "initialTimeMs");
    command.initial_time_ms = initial_time_ms.value_or(0);
    command.initial_time_ms_valid = !json_has_field(json, "initialTimeMs") || initial_time_ms.has_value();
    const auto duration_ms = json_number_field(json, "durationMs");
    command.duration_ms = duration_ms.value_or(0);
    command.duration_ms_valid = duration_ms.has_value();
    command.step_ms = json_number_field(json, "stepMs").value_or(0);
    command.step_ms_present = json_has_field(json, "stepMs");
    command.action_id = json_string_field(json, "actionId").value_or("");
    command.code = json_string_field(json, "code").value_or("");
    command.button = json_string_field(json, "button").value_or("");
    command.axis = json_string_field(json, "axis").value_or("");
    command.mode = json_string_field(json, "mode").value_or("");
    command.coordinate_space = json_string_field(json, "coordinateSpace").value_or("");
    command.wheel_unit = json_string_field(json, "wheelUnit").value_or("");
    command.raw_value = json_raw_field(json, "value").value_or("null");
    const auto lease_ms = json_int_field(json, "leaseMs");
    command.lease_ms = !lease_ms.has_value() ? 5000U :
        (*lease_ms >= 1 && *lease_ms <= 60000 ? static_cast<unsigned int>(*lease_ms) : 60001U);
    const auto gamepad_index = json_int_field(json, "gamepadIndex");
    command.device_index = gamepad_index.value_or(0);
    command.device_index_valid = !json_has_field(json, "gamepadIndex") ||
        (gamepad_index.has_value() && *gamepad_index >= 0 && *gamepad_index <= 3);
    command.input_x = json_number_field(json, "x").value_or(command.delta_x);
    command.input_y = json_number_field(json, "y").value_or(command.delta_y);
    return command;
}

std::string ok_response(int64_t id, std::string_view result_json)
{
    return "{\"id\":" + std::to_string(id) + ",\"ok\":true,\"result\":" + std::string(result_json) + "}";
}

std::string ok_null_response(int id)
{
    return "{\"id\":" + std::to_string(id) + ",\"ok\":true,\"result\":null}";
}

std::string error_response(int64_t id, std::string_view message)
{
    return "{\"id\":" + std::to_string(id) + ",\"ok\":false,\"error\":\"" + escape_json(message) + "\"}";
}

std::string_view action_error_name(long long code)
{
    switch (code) {
    case -1: return "invalid_argument";
    case -2: return "node_not_found";
    case -3: return "hidden";
    case -4: return "disabled";
    case -5: return "unsupported";
    case -6: return "invalid_value";
    case -7: return "stale_guard";
    default: return "unknown";
    }
}

std::string_view game_input_error_name(long long code)
{
    switch (code) {
    case -1: return "invalid_argument";
    case -2: return "action_not_found";
    case -3: return "inactive";
    case -4: return "unsupported";
    case -5: return "invalid_value";
    case -6: return "invalid_lease";
    case -7: return "confirmation_required";
    default: return "unknown";
    }
}

} // namespace

bool gua::ws::detail::valid_game_input_query_selector(const GameInputQuerySelector& selector)
{
    return valid_game_input_selector(selector);
}

bool gua::ws::detail::valid_game_input_query_request_json(std::string_view json)
{
    const Command command = parse_command(json);
    return command.type == "find_game_input_actions" && command.game_input_selector_valid;
}

namespace gua::ws {

class BridgeServer::Impl {
public:
    Impl(BridgeHandlers handlers, BridgeOptions options)
        : handlers_(std::move(handlers))
        , options_(options)
        , bound_port_(options.port)
    {
    }

    ~Impl()
    {
        stop();
    }

    void start()
    {
        if (running_.exchange(true)) {
            return;
        }

        std::promise<bool> started;
        std::future<bool> started_future = started.get_future();
        thread_ = std::thread([this, started = std::move(started)]() mutable {
            run(std::move(started));
        });

        if (!started_future.get()) {
            if (thread_.joinable()) {
                thread_.join();
            }
        }
    }

    void stop()
    {
        if (!running_.exchange(false)) {
            return;
        }
        if (!gua::ws::platform::wake_listener(bound_port_.load())) {
            const SocketHandle listen_socket = listen_socket_.exchange(invalid_socket);
            gua::ws::platform::shutdown_socket(listen_socket);
            gua::ws::platform::close_socket(listen_socket);
        }
        {
            const std::lock_guard lock(clients_mutex_);
            for (const SocketHandle client_socket : active_client_sockets_) {
                gua::ws::platform::shutdown_socket(client_socket);
            }
        }
        if (thread_.joinable()) {
            thread_.join();
        }
        {
            const std::lock_guard lock(client_threads_mutex_);
            for (std::thread& client_thread : client_threads_) {
                if (client_thread.joinable()) {
                    client_thread.join();
                }
            }
            client_threads_.clear();
        }
    }

    void publish_snapshot()
    {
        if (!running_.load()) {
            return;
        }
        {
            const std::lock_guard lock(clients_mutex_);
            if (clients_.empty()) {
                return;
            }
        }

        std::string message;
        try {
            std::string snapshot;
            if (handlers_.get_snapshot_json) {
                snapshot = handlers_.get_snapshot_json();
            } else {
                const std::string ui_tree = handlers_.get_ui_tree_json();
                const std::string world_tree = handlers_.get_world_object_tree_json
                    ? handlers_.get_world_object_tree_json() : unsupported_world_object_tree_json(ui_tree);
                snapshot = "{\"uiTree\":" + ui_tree + ",\"worldObjectTree\":" + world_tree +
                    ",\"logs\":" + handlers_.get_logs_json() + ",\"screenshot\":" + handlers_.get_screenshot_json() + '}';
            }
            message = "{\"type\":\"snapshot\",\"snapshot\":" + snapshot + '}';
        } catch (const std::exception& error) {
            std::cerr << "Gua bridge snapshot failed: " << error.what() << std::endl;
            return;
        }

        std::vector<ClientConnection> clients;
        {
            const std::lock_guard lock(clients_mutex_);
            clients = clients_;
        }

        std::vector<SocketHandle> failed_clients;
        for (const ClientConnection& client : clients) {
            try {
                send_text_frame(client, message);
            } catch (...) {
                failed_clients.push_back(client.socket);
                gua::ws::platform::shutdown_socket(client.socket);
            }
        }

        if (!failed_clients.empty()) {
            const std::lock_guard lock(clients_mutex_);
            clients_.erase(
                std::remove_if(clients_.begin(), clients_.end(), [&](const ClientConnection& client) {
                    return std::find(failed_clients.begin(), failed_clients.end(), client.socket) != failed_clients.end();
                }),
                clients_.end());
        }
    }

    [[nodiscard]] bool running() const
    {
        return running_.load();
    }

    [[nodiscard]] unsigned short port() const
    {
        return bound_port_.load();
    }

private:
    void run(std::promise<bool> started)
    {
        bool startup_reported = false;
        try {
            const NetworkSession network;
            Socket listen_socket = gua::ws::platform::create_listen_socket(options_.port);
            const SocketHandle listen_handle = listen_socket.release();
            listen_socket_ = listen_handle;
            bound_port_.store(gua::ws::platform::bound_port(listen_handle));
            started.set_value(true);
            startup_reported = true;
            std::cout << "Gua WebSocket bridge listening on ws://127.0.0.1:" << bound_port_.load() << std::endl;

            while (running_.load()) {
                Socket client = gua::ws::platform::accept_socket(listen_handle);
                if (!client.valid()) {
                    if (running_.load()) {
                        continue;
                    }
                    break;
                }
                if (!running_.load()) {
                    break;
                }

                const SocketHandle client_socket = client.release();
                {
                    const std::lock_guard clients_lock(clients_mutex_);
                    active_client_sockets_.push_back(client_socket);
                }
                const std::lock_guard lock(client_threads_mutex_);
                client_threads_.emplace_back([this, client_socket]() {
                    Socket owned_client(client_socket);
                    try {
                        serve_client(owned_client.get());
                    } catch (const std::exception& error) {
                        if (running_.load()) {
                            std::cerr << "Gua bridge client error: " << error.what() << std::endl;
                        }
                    }
                    const std::lock_guard clients_lock(clients_mutex_);
                    clients_.erase(
                        std::remove_if(clients_.begin(), clients_.end(), [&](const ClientConnection& entry) {
                            return entry.socket == client_socket;
                        }),
                        clients_.end());
                    active_client_sockets_.erase(
                        std::remove(active_client_sockets_.begin(), active_client_sockets_.end(), client_socket),
                        active_client_sockets_.end());
                });
            }

            gua::ws::platform::close_socket(listen_socket_.exchange(invalid_socket));
        } catch (const std::exception& error) {
            gua::ws::platform::close_socket(listen_socket_.exchange(invalid_socket));
            std::cerr << "Gua bridge failed: " << error.what() << std::endl;
            running_.store(false);
            if (!startup_reported) {
                started.set_value(false);
            }
        }
    }

    void serve_client(SocketHandle client)
    {
        perform_handshake(client);
        ClientConnection connection {
            client,
            std::make_shared<std::mutex>(),
            handlers_.create_game_input_owner ? handlers_.create_game_input_owner() : 0,
            handlers_.create_observe_client ? handlers_.create_observe_client() : 0,
            handlers_.create_spatial_client ? handlers_.create_spatial_client() : 0,
        };
        const auto release_owner = [&]() noexcept {
            if(connection.spatial_client_id && handlers_.release_spatial_client) {
                try { handlers_.release_spatial_client(connection.spatial_client_id); } catch(...) {}
                connection.spatial_client_id=0;
            }
            if (connection.observe_client_id && handlers_.release_observe_client) {
                try { handlers_.release_observe_client(connection.observe_client_id); } catch (...) {}
                connection.observe_client_id = 0;
            }
            if (connection.game_input_owner_id == 0 || !handlers_.release_game_input_owner) return;
            try {
                handlers_.release_game_input_owner(connection.game_input_owner_id);
            } catch (...) {
                // Connection teardown must continue even if host cleanup reporting fails.
            }
            connection.game_input_owner_id = 0;
        };
        try {
            GuardedRequests guarded_requests; // bounded, destroyed with this connection owner
            {
                const std::lock_guard lock(clients_mutex_);
                clients_.push_back(connection);
            }
            std::cout << "Inspector connected." << std::endl;
            publish_snapshot();

            while (running_.load()) {
                const std::optional<std::string> message = read_text_frame(client);
                if (!message.has_value()) {
                    break;
                }

                const std::string response = handle_command(*message, connection.game_input_owner_id, connection.observe_client_id, connection.spatial_client_id, guarded_requests);
                send_text_frame(connection, response);
            }
        } catch (...) {
            release_owner();
            throw;
        }
        release_owner();

        {
            const std::lock_guard lock(clients_mutex_);
            clients_.erase(
                std::remove_if(clients_.begin(), clients_.end(), [&](const ClientConnection& entry) {
                    return entry.socket == client;
                }),
                clients_.end());
        }
        std::cout << "Inspector disconnected." << std::endl;
    }

    static void send_text_frame(const ClientConnection& client, std::string_view text)
    {
        const std::lock_guard lock(*client.send_mutex);
        ::send_text_frame(client.socket, text);
    }

    [[nodiscard]] std::string handle_command(std::string_view message, unsigned long long game_input_owner_id, unsigned long long observe_client_id, unsigned long long spatial_client_id, GuardedRequests& guarded_requests)
    {
        Command command;
        try { command = parse_command(message); }
        catch (...) { return error_response(0, "invalid_request"); }
        try {
            if (command.guarded && !command.guard_transport_valid) return error_response(0, "invalid_request");
            if (command.guarded && (!command.guard_valid || game_input_owner_id == 0))
                return error_response(command.id, "invalid_guard");
            if (command.guarded && !command.guard_payload_valid)
                return error_response(command.id, "invalid_request");
            if (command.guarded && (!handlers_.guarded_dispatch_supported || !handlers_.guarded_dispatch_supported()))
                return error_response(command.id, "unsupported");
            if (command.guarded && (command.type == "poll_action" || command.type == "poll_game_input")) {
                if (command.request_id == 0 || !handlers_.poll_guarded_result)
                    return error_response(command.id, "invalid_request");
                const auto key = std::pair(command.type == "poll_action", command.request_id);
                const auto original = guarded_requests.find(key);
                if (original == guarded_requests.end()) return key.first ? ok_response(command.id, "null") : error_response(command.id, "invalid_request");
                if (command.expected_revision != original->second.revision || command.expected_session_epoch != original->second.epoch ||
                    command.expected_profile != original->second.profile) return error_response(command.id, "stale_guard");
                const auto result = handlers_.poll_guarded_result(command.type == "poll_action", game_input_owner_id,
                    command.request_id, command.expected_session_epoch, command.expected_profile);
                if (result.ok && (key.first ? result.json != "null" : result.json.starts_with("{\"completed\":true"))) guarded_requests.erase(original);
                return result.ok ? ok_response(command.id, result.json) : error_response(command.id, result.error);
            }
            if (command.guarded && command.type != "click_node" && command.type != "focus_node" &&
                command.type != "set_value" && command.type != "set_checked" && command.type != "select" &&
                command.type != "scroll" && command.type != "press_key" &&
                command.type != "press_game_input_action" && command.type != "set_game_input_action" &&
                command.type != "release_game_input_action" && command.type != "key_down" && command.type != "key_up" &&
                command.type != "press_physical_key" && command.type != "pointer_move" && command.type != "pointer_wheel" &&
                command.type != "pointer_button_down" && command.type != "pointer_button_up" &&
                command.type != "gamepad_button_down" && command.type != "gamepad_button_up" &&
                command.type != "set_gamepad_axis" && command.type != "text_input") return error_response(command.id, "unsupported");
            if(command.type=="get_spatial_info" || command.type=="query_spatial_batch" || command.type=="poll_spatial_batch" || command.type=="cancel_spatial_batch") {
                int operation=command.type=="get_spatial_info" ? 1 : command.type=="query_spatial_batch" ? 2 : command.type=="poll_spatial_batch" ? 3 : 4;
                int response_id=0; uint64_t batch_id=0; std::string batch;
                try {
                    using namespace gua_value_detail;
                    if(message.size()>1048576) return error_response(0,"invalid_request");
                    auto request=parser(message).parse();
                    if(request.type!=json::object || request.at("id").type!=json::number) return error_response(0,"invalid_request");
                    auto id=integer(request.at("id").text,"$.id");
                    if(id<=0 || id>std::numeric_limits<int>::max()) return error_response(0,"invalid_request");
                    response_id=static_cast<int>(id);
                    if(request.fields.size()!=static_cast<size_t>(operation==1 ? 2 : 3)) return error_response(response_id,"invalid_request");
                    if(operation==2) batch=spatial_wire(request.at("batch"));
                    if(operation>=3) {
                        if(request.at("batchId").type!=json::number) return error_response(response_id,"invalid_request");
                        auto number=integer(request.at("batchId").text,"$.batchId");
                        if(number<=0 || number>9007199254740991LL) return error_response(response_id,"invalid_request");
                        batch_id=static_cast<uint64_t>(number);
                    }
                } catch(...) { return error_response(response_id,"invalid_request"); }
                if(!handlers_.spatial_command || !spatial_client_id) return error_response(response_id,"unsupported");
                auto result=handlers_.spatial_command(spatial_client_id,operation,batch,batch_id);
                return result.ok ? ok_response(response_id,result.json) : error_response(response_id,result.error);
            }
            if(command.type=="get_observe_snapshot" || command.type=="subscribe_observations" ||
                command.type=="poll_observations" || command.type=="unsubscribe_observations") {
                int operation=command.type=="get_observe_snapshot" ? 1 : command.type=="subscribe_observations" ? 2 : command.type=="poll_observations" ? 3 : 4;
                uint64_t subscription=0; int64_t response_id=0;
                try {
                    using namespace gua_value_detail;
                    const auto request=parser(message).parse();
                    if(request.type!=json::object) return error_response(response_id,"invalid_request");
                    if(request.at("type").type!=json::string || request.at("id").type!=json::number) return error_response(response_id,"invalid_request");
                    response_id=integer(request.at("id").text,"$.id");
                    if(response_id<=0) { response_id=0; return error_response(response_id,"invalid_request"); }
                    if(request.fields.size()!=static_cast<size_t>(operation>=3 ? 3 : 2)) return error_response(response_id,"invalid_request");
                    if(operation>=3) {
                        const auto& value=request.at("subscriptionId");
                        if(value.type!=json::number) return error_response(response_id,"invalid_request");
                        auto id=integer(value.text,"$.subscriptionId"); if(id<=0) return error_response(response_id,"invalid_request"); subscription=static_cast<uint64_t>(id);
                    }
                } catch(...) { return error_response(response_id,"invalid_request"); }
                if(!handlers_.observe_command || !observe_client_id) return error_response(response_id,"unsupported");
                auto result=handlers_.observe_command(observe_client_id,operation,subscription);
                return result.ok ? ok_response(response_id,result.json) : error_response(response_id,result.error);
            }
            if (command.type == "semantic_lint") {
                using namespace gua_value_detail;
                int response_id = 0;
                bool include_world = true;
                try {
                    const auto request = parser(message).parse();
                    if (request.type != json::object) return error_response(0, "invalid_request");
                    const auto& id = request.at("id");
                    if (id.type != json::number) return error_response(0, "invalid_request");
                    const auto number = integer(id.text, "$.id");
                    if (number <= 0 || number > std::numeric_limits<int>::max()) return error_response(0, "invalid_request");
                    response_id = static_cast<int>(number);
                    for (const auto& [key, value] : request.fields) {
                        if (key == "id") continue;
                        if (key == "type" && value.type == json::string && value.text == "semantic_lint") continue;
                        if (key == "includeWorld" && value.type == json::boolean) { include_world = value.text == "true"; continue; }
                        return error_response(response_id, "invalid_request");
                    }
                } catch (...) { return error_response(response_id, "invalid_request"); }
                if (!handlers_.semantic_lint) return error_response(response_id, "semantic_lint is not supported by this bridge");
                const auto result = handlers_.semantic_lint(include_world);
                return result.ok ? ok_response(response_id, result.json) : error_response(response_id, result.error);
            }
            if (command.type == "get_ui_tree") {
                return ok_response(command.id, handlers_.get_ui_tree_json());
            }
            if (command.type == "get_world_object_tree") {
                return handlers_.get_world_object_tree_json
                    ? ok_response(command.id, handlers_.get_world_object_tree_json())
                    : ok_response(command.id, unsupported_world_object_tree_json(handlers_.get_ui_tree_json()));
            }
            if (command.type == "get_logs") {
                return ok_response(command.id, handlers_.get_logs_json());
            }
            if (command.type == "get_screenshot") {
                return ok_response(command.id, handlers_.get_screenshot_json());
            }
            if (command.type == "capture_screenshot") {
                if (!handlers_.capture_screenshot) return error_response(command.id, "capture_screenshot is not supported by this bridge");
                const auto result = handlers_.capture_screenshot(command.after_frame_sequence, command.timeout_ms);
                return result.ok ? ok_response(command.id, result.json) : error_response(command.id, result.error);
            }
            if (command.type == "get_diagnostics") {
                if(handlers_.get_connection_diagnostics_json) return ok_response(command.id,handlers_.get_connection_diagnostics_json(spatial_client_id));
                return handlers_.get_diagnostics_json
                    ? ok_response(command.id, handlers_.get_diagnostics_json())
                    : error_response(command.id, "get_diagnostics is not supported by this bridge");
            }
            if (command.type == "get_version") {
                if(handlers_.get_connection_version_json) return ok_response(command.id,handlers_.get_connection_version_json(spatial_client_id));
                return handlers_.get_version_json
                    ? ok_response(command.id, handlers_.get_version_json())
                    : error_response(command.id, "get_version is not supported by this bridge");
            }
            if (command.type == "get_clock") {
                return handlers_.clock_supported && handlers_.clock_supported() && handlers_.get_clock_json
                    ? ok_response(command.id, handlers_.get_clock_json())
                    : error_response(command.id, "unsupported");
            }
            if (command.type == "clock_install" || command.type == "clock_pause" ||
                command.type == "clock_run_for" || command.type == "clock_resume") {
                if (!handlers_.clock_supported || !handlers_.clock_supported() || !handlers_.control_clock)
                    return error_response(command.id, "unsupported");
                if (command.type == "clock_install" && !command.initial_time_ms_valid)
                    return error_response(command.id, "invalid_duration");
                if (command.type == "clock_run_for" && !command.duration_ms_valid)
                    return error_response(command.id, "invalid_duration");
                const auto result = handlers_.control_clock(command.type,
                    command.type == "clock_install" ? command.initial_time_ms : command.duration_ms,
                    command.step_ms, command.step_ms_present);
                return result.ok ? ok_response(command.id, result.json) : error_response(command.id, result.error);
            }
            if (command.type == "query_nodes") {
                if (!handlers_.query_nodes_json) {
                    return error_response(command.id, "query_nodes is not supported by this bridge");
                }
                return ok_response(command.id, handlers_.query_nodes_json(command.selector));
            }
            if (command.type == "query_world_objects") {
                if (!command.world_selector_valid) return error_response(command.id, "invalid world selector");
                return handlers_.query_world_objects_json
                    ? ok_response(command.id, handlers_.query_world_objects_json(command.world_selector))
                    : error_response(command.id, "unsupported");
            }
            if (command.type == "get_context_status") {
                return handlers_.get_context_status_json
                    ? ok_response(command.id, handlers_.get_context_status_json())
                    : error_response(command.id, "get_context_status is not supported by this bridge");
            }
            if (command.type == "reset_context") {
                if (!handlers_.reset_context_json) return error_response(command.id, "reset_context is not supported by this bridge");
                if (command.expected_session_epoch == 0) return error_response(command.id, "reset_context requires expectedSessionEpoch");
                if (!command.reset_flags_version_valid) return error_response(command.id, "reset_context requires a supported flagsVersion when supplied");
                return ok_response(command.id, handlers_.reset_context_json(
                    command.expected_session_epoch, command.reset_flags, command.reset_flags_version, command.strict));
            }
            if (command.type == "poll_events") {
                return handlers_.poll_action_event_json
                    ? ok_response(command.id, handlers_.poll_action_event_json(command.request_id))
                    : error_response(command.id, "poll_events is not supported by this bridge");
            }
            if (command.type == "get_game_input_actions") {
                return handlers_.get_game_input_actions_json && handlers_.game_input_supported &&
                        handlers_.game_input_supported(1U)
                    ? ok_response(command.id, handlers_.get_game_input_actions_json())
                    : error_response(command.id, "unsupported");
            }
            if (command.type == "find_game_input_actions") {
                if (!command.game_input_selector_valid) return error_response(command.id, "invalid game input selector");
                if (!handlers_.query_game_input_actions_json || !handlers_.game_input_supported || !handlers_.game_input_supported(1U))
                    return error_response(command.id, "unsupported");
                const auto result = handlers_.query_game_input_actions_json(command.game_input_selector);
                return result.empty() ? error_response(command.id, "invalid game input selector") : ok_response(command.id, result);
            }
            if (command.type == "get_game_input_actions_v2") {
                if (!command.game_input_metadata_get_valid) return error_response(command.id, "invalid game input metadata request");
                if (!handlers_.get_game_input_actions_json_v2 || !handlers_.game_input_supported || !handlers_.game_input_supported(1U))
                    return error_response(command.id, "unsupported");
                const auto result = handlers_.get_game_input_actions_json_v2();
                return result.empty() ? error_response(command.id, "unsupported") : ok_response(command.id, result);
            }
            if (command.type == "find_game_input_actions_v2") {
                if (!command.game_input_selector_valid) return error_response(command.id, "invalid game input selector");
                if (!handlers_.query_game_input_actions_json_v2 || !handlers_.game_input_supported || !handlers_.game_input_supported(1U))
                    return error_response(command.id, "unsupported");
                const auto result = handlers_.query_game_input_actions_json_v2(command.game_input_selector);
                return result.empty() ? error_response(command.id, "unsupported") : ok_response(command.id, result);
            }
            if (command.type == "get_game_input_state") {
                return game_input_owner_id != 0 && handlers_.get_game_input_state_json
                    ? ok_response(command.id, handlers_.get_game_input_state_json(game_input_owner_id))
                    : error_response(command.id, "unsupported");
            }
            if (command.type == "poll_game_input") {
                if (guarded_requests.contains(std::pair(false, command.request_id))) return error_response(command.id, "guarded_poll_required");
                if (command.request_id == 0) return error_response(command.id, "poll_game_input requires requestId");
                return game_input_owner_id != 0 && handlers_.poll_game_input_result_json
                    ? ok_response(command.id, handlers_.poll_game_input_result_json(game_input_owner_id, command.request_id))
                    : error_response(command.id, "unsupported");
            }
            const bool game_input_command =
                command.type == "press_game_input_action" || command.type == "set_game_input_action" ||
                command.type == "release_game_input_action" || command.type == "release_all_game_inputs" ||
                command.type == "key_down" || command.type == "key_up" || command.type == "press_physical_key" ||
                command.type == "pointer_move" || command.type == "pointer_button_down" ||
                command.type == "pointer_button_up" || command.type == "pointer_wheel" ||
                command.type == "gamepad_button_down" || command.type == "gamepad_button_up" ||
                command.type == "set_gamepad_axis" || command.type == "reset_gamepad" ||
                command.type == "text_input";
            if (game_input_command) {
                if (command.guarded && guarded_requests.size() >= 256) return error_response(command.id, "guarded_request_limit");
                if (game_input_owner_id == 0 || !handlers_.enqueue_game_input)
                    return error_response(command.id, "unsupported");
                const bool gamepad_command = command.type == "gamepad_button_down" ||
                    command.type == "gamepad_button_up" || command.type == "set_gamepad_axis" ||
                    command.type == "reset_gamepad";
                if (gamepad_command && !command.device_index_valid)
                    return error_response(command.id, "invalid gamepadIndex");
                std::string target = command.action_id;
                std::string value_json = command.raw_value;
                double x = command.input_x, y = command.input_y;
                if (command.type == "key_down" || command.type == "key_up" || command.type == "press_physical_key")
                    target = command.code;
                else if (command.type == "pointer_move")
                    target = command.mode + ":" + command.coordinate_space;
                else if (command.type == "pointer_button_down" || command.type == "pointer_button_up")
                    target = command.button;
                else if (command.type == "pointer_wheel")
                    target = command.wheel_unit.empty() ? "pixels" : command.wheel_unit;
                else if (command.type == "gamepad_button_down" || command.type == "gamepad_button_up")
                    target = command.button;
                else if (command.type == "set_gamepad_axis")
                    target = command.axis;
                else if (command.type == "reset_gamepad")
                    target = "all";
                else if (command.type == "text_input") {
                    target = "text";
                    value_json = "\"" + escape_json(command.selector.text) + "\"";
                } else if (command.type == "release_all_game_inputs") target = "all";
                const long long request_id = handlers_.enqueue_game_input(game_input_owner_id, gua::ws::GameInputCommand {
                    command.type, std::move(target), std::move(value_json), x, y, command.lease_ms,
                    command.device_index, command.sensitive, command.confirmed, command.guarded,
                    command.expected_session_epoch, command.expected_revision, command.expected_profile });
                if (command.guarded && request_id > 0) guarded_requests.emplace(std::pair(false, static_cast<unsigned long long>(request_id)),
                    DispatchGuard {command.expected_session_epoch, command.expected_revision, command.expected_profile});
                return request_id > 0
                    ? ok_response(command.id, "{\"requestId\":" + std::to_string(request_id) + "}")
                    : error_response(command.id, "Gua game input rejected: " + std::string(game_input_error_name(request_id)));
            }
            if (handlers_.enqueue_action && (command.type == "click_node" || command.type == "focus_node" || command.type == "press_key" ||
                command.type == "set_value" || command.type == "set_checked" || command.type == "select" || command.type == "scroll")) {
                if (command.guarded && guarded_requests.size() >= 256) return error_response(command.id, "guarded_request_limit");
                const long long request_id = handlers_.enqueue_action(gua::ws::ActionCommand {
                    command.type, command.node_id, command.value, command.delta_x, command.delta_y, command.bool_value,
                    command.key, command.modifiers, command.sensitive, command.scroll_unit, command.guarded,
                    game_input_owner_id, command.expected_session_epoch, command.expected_revision, command.expected_profile });
                if (command.guarded && request_id > 0) guarded_requests.emplace(std::pair(true, static_cast<unsigned long long>(request_id)),
                    DispatchGuard {command.expected_session_epoch, command.expected_revision, command.expected_profile});
                return request_id > 0
                    ? ok_response(command.id, "{\"requestId\":" + std::to_string(request_id) + "}")
                    : error_response(command.id, "Gua action rejected: " + std::string(action_error_name(request_id)));
            }
            if (command.guarded) return error_response(command.id, "unsupported");
            if (command.type == "click_node") {
                return handlers_.click_node(command.node_id)
                    ? ok_null_response(command.id)
                    : error_response(command.id, "Gua node not found or not clickable: " + command.node_id);
            }
            if (command.type == "focus_node") {
                return handlers_.focus_node(command.node_id)
                    ? ok_null_response(command.id)
                    : error_response(command.id, "Gua node not found: " + command.node_id);
            }
            if (command.type == "press_key") {
                if (!handlers_.press_key) {
                    return error_response(command.id, "press_key is not supported by this bridge");
                }

                return handlers_.press_key(command.key)
                    ? ok_null_response(command.id)
                    : error_response(command.id, "Gua key was not accepted: " + command.key);
            }
            return error_response(command.id, "Unsupported command: " + command.type);
        } catch (const std::exception& error) {
            return error_response(command.id, error.what());
        }
    }

    BridgeHandlers handlers_;
    BridgeOptions options_;
    std::atomic_bool running_ = false;
    std::atomic<unsigned short> bound_port_ = 0;
    std::thread thread_;
    std::atomic<SocketHandle> listen_socket_ = invalid_socket;
    std::mutex clients_mutex_;
    std::vector<ClientConnection> clients_;
    std::vector<SocketHandle> active_client_sockets_;
    std::mutex client_threads_mutex_;
    std::vector<std::thread> client_threads_;
};

BridgeServer::BridgeServer(BridgeHandlers handlers, BridgeOptions options)
    : impl_(std::make_unique<Impl>(std::move(handlers), options))
{
}

BridgeServer::BridgeServer(BridgeServer&&) noexcept = default;

BridgeServer& BridgeServer::operator=(BridgeServer&&) noexcept = default;

BridgeServer::~BridgeServer() = default;

void BridgeServer::start()
{
    impl_->start();
}

void BridgeServer::stop()
{
    impl_->stop();
}

void BridgeServer::publish_snapshot()
{
    impl_->publish_snapshot();
}

bool BridgeServer::running() const
{
    return impl_->running();
}

unsigned short BridgeServer::port() const
{
    return impl_->port();
}

} // namespace gua::ws

#pragma once

#include <string>
#include <vector>

namespace gua::godot_detail {

// C ABI copy functions include the trailing NUL in their required size and
// report a new required size when publication races with the previous probe.
template<typename CopyJson>
std::string copy_json_with_retry(CopyJson copy_json)
{
    int required_size = copy_json(nullptr, 0);
    while (required_size > 0) {
        std::vector<char> buffer(static_cast<std::size_t>(required_size));
        const int actual_size = copy_json(buffer.data(), static_cast<int>(buffer.size()));
        if (actual_size <= 0) return {};
        if (actual_size <= static_cast<int>(buffer.size())) return std::string(buffer.data());
        required_size = actual_size;
    }
    return {};
}

} // namespace gua::godot_detail

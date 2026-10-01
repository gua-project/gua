#pragma once
#include "gua/spatial_host.h"
#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/string.hpp>

namespace godot {
// Trusted in-process host API. Deliberately absent from GuaContext/bridge/MCP.
class GuaSpatialHost : public RefCounted {
    GDCLASS(GuaSpatialHost, RefCounted)
    gua_spatial_host_t* host_ = nullptr;
    static Dictionary reply(int status, uint64_t handle = 0);
    Dictionary document_call(int operation, uint64_t handle, const String& json);
protected:
    static void _bind_methods();
public:
    ~GuaSpatialHost();
    Dictionary configure(const String& clock_id, int providers, int owners, int queue,
        int queries, int hits, double deadline_ms, double work_ms);
    Dictionary register_provider(const String& json);
    Dictionary open_owner(const String& json);
    Dictionary set_owner(uint64_t owner, const String& json);
    Dictionary enqueue(uint64_t owner, const String& json);
    Dictionary begin(uint64_t provider, const String& json);
    Dictionary complete(uint64_t lease, const String& json);
    Dictionary unregister_provider(uint64_t provider);
    Dictionary close_owner(uint64_t owner);
    Dictionary end(uint64_t lease);
    Dictionary take(uint64_t lease);
    Dictionary poll(uint64_t owner, uint64_t batch);
};
}

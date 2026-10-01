#include "gua/godot/spatial_host.hpp"
#include "gua/godot/copy_json.hpp"
#include <godot_cpp/core/class_db.hpp>
#include <limits>
namespace godot {
Dictionary GuaSpatialHost::reply(int status, uint64_t handle) {
    Dictionary result; result["status"] = status;
    if (status == 0 && handle != 0 && handle <= static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) result["handle"] = static_cast<int64_t>(handle);
    return result;
}
GuaSpatialHost::~GuaSpatialHost() { gua_spatial_host_destroy(host_); }
Dictionary GuaSpatialHost::configure(const String& clock, int providers, int owners, int queue,
    int queries, int hits, double deadline, double work) {
    if (host_ || providers <= 0 || owners <= 0 || queue <= 0 || queries <= 0 || hits <= 0) return reply(GUA_SPATIAL_INVALID);
    const gua_spatial_host_options_v1_t options{sizeof(options), static_cast<uint32_t>(providers), static_cast<uint32_t>(owners),
        static_cast<uint32_t>(queue), static_cast<uint32_t>(queries), static_cast<uint32_t>(hits), deadline, work};
    const auto utf8 = clock.utf8(); gua_spatial_error_t error{};
    return reply(gua_spatial_host_create(&options, {utf8.get_data(), static_cast<uint32_t>(utf8.length())}, &host_, &error));
}
Dictionary GuaSpatialHost::document_call(int operation, uint64_t handle, const String& json) {
    if (!host_) return reply(GUA_SPATIAL_STALE);
    int type = GUA_SPATIAL_EXECUTION;
    if (operation == 0) type = GUA_SPATIAL_REGISTRATION;
    if (operation == 1 || operation == 2) type = GUA_SPATIAL_OWNER;
    if (operation == 3) type = GUA_SPATIAL_BATCH;
    if (operation == 4) type = GUA_SPATIAL_BOUNDARY;
    const gua_spatial_parse_options_v1_t options{sizeof(options), type};
    const auto utf8 = json.utf8(); gua_spatial_document_t* doc = nullptr; gua_spatial_error_t error{};
    int status = gua_spatial_from_json(&options, {utf8.get_data(), static_cast<uint32_t>(utf8.length())}, &doc, &error);
    if (status != 0) return reply(status);
    uint64_t output = 0;
    switch (operation) {
    case 0: status = gua_spatial_host_register(host_, doc, &output, &error); break;
    case 1: status = gua_spatial_host_open_owner(host_, doc, &output, &error); break;
    case 2: status = gua_spatial_host_set_owner(host_, handle, doc, &error); break;
    case 3: status = gua_spatial_host_enqueue(host_, handle, doc, &error); break;
    case 4: status = gua_spatial_host_begin(host_, handle, doc, &output, &error); break;
    default: status = gua_spatial_host_complete(host_, handle, doc, &error); break;
    }
    gua_spatial_destroy(doc); return reply(status, output);
}
Dictionary GuaSpatialHost::register_provider(const String& json) { return document_call(0, 0, json); }
Dictionary GuaSpatialHost::open_owner(const String& json) { return document_call(1, 0, json); }
Dictionary GuaSpatialHost::set_owner(uint64_t owner, const String& json) { return document_call(2, owner, json); }
Dictionary GuaSpatialHost::enqueue(uint64_t owner, const String& json) { return document_call(3, owner, json); }
Dictionary GuaSpatialHost::begin(uint64_t provider, const String& json) { return document_call(4, provider, json); }
Dictionary GuaSpatialHost::complete(uint64_t lease, const String& json) { return document_call(5, lease, json); }
Dictionary GuaSpatialHost::unregister_provider(uint64_t p) { gua_spatial_error_t e{}; return reply(host_ ? gua_spatial_host_unregister(host_, p, &e) : GUA_SPATIAL_STALE); }
Dictionary GuaSpatialHost::close_owner(uint64_t o) { gua_spatial_error_t e{}; return reply(host_ ? gua_spatial_host_close_owner(host_, o, &e) : GUA_SPATIAL_STALE); }
Dictionary GuaSpatialHost::end(uint64_t l) { gua_spatial_error_t e{}; return reply(host_ ? gua_spatial_host_end(host_, l, &e) : GUA_SPATIAL_STALE); }
static Dictionary copied(int status, gua_spatial_document_t* doc) {
    Dictionary result; result["status"] = status;
    if (status == 0 && doc) {
        const auto json = gua::godot_detail::copy_json_with_retry([&](char* buffer, int capacity) { return gua_spatial_copy_json(doc, buffer, capacity); });
        result["json"] = String::utf8(json.c_str());
    }
    gua_spatial_destroy(doc); return result;
}
Dictionary GuaSpatialHost::take(uint64_t l) { gua_spatial_error_t e{}; gua_spatial_document_t* d = nullptr; const int s = host_ ? gua_spatial_host_take(host_, l, &d, &e) : GUA_SPATIAL_STALE; return copied(s, d); }
Dictionary GuaSpatialHost::poll(uint64_t o, uint64_t b) { gua_spatial_error_t e{}; gua_spatial_document_t* d = nullptr; const int s = host_ ? gua_spatial_host_poll(host_, o, b, &d, &e) : GUA_SPATIAL_STALE; return copied(s, d); }
void GuaSpatialHost::_bind_methods() {
    ClassDB::bind_method(D_METHOD("configure", "clock_id", "providers", "owners", "queue", "queries", "hits", "deadline_ms", "work_ms"), &GuaSpatialHost::configure);
    ClassDB::bind_method(D_METHOD("register_provider", "json"), &GuaSpatialHost::register_provider);
    ClassDB::bind_method(D_METHOD("open_owner", "json"), &GuaSpatialHost::open_owner);
    ClassDB::bind_method(D_METHOD("set_owner", "owner", "json"), &GuaSpatialHost::set_owner);
    ClassDB::bind_method(D_METHOD("enqueue", "owner", "json"), &GuaSpatialHost::enqueue);
    ClassDB::bind_method(D_METHOD("begin", "provider", "json"), &GuaSpatialHost::begin);
    ClassDB::bind_method(D_METHOD("complete", "lease", "json"), &GuaSpatialHost::complete);
    ClassDB::bind_method(D_METHOD("unregister_provider", "provider"), &GuaSpatialHost::unregister_provider);
    ClassDB::bind_method(D_METHOD("close_owner", "owner"), &GuaSpatialHost::close_owner);
    ClassDB::bind_method(D_METHOD("end", "lease"), &GuaSpatialHost::end);
    ClassDB::bind_method(D_METHOD("take", "lease"), &GuaSpatialHost::take);
    ClassDB::bind_method(D_METHOD("poll", "owner", "batch"), &GuaSpatialHost::poll);
}
}

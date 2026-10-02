// Included after gua_runtime_t. Physics remains exclusively in spatial-host-r1.
namespace {
std::string spatial_document_json(const gua_spatial_document_t* d) {
    int n=gua_spatial_copy_json(d,nullptr,0);
    if(n<=0 || n>1048577) throw std::runtime_error("spatial document unavailable");
    std::string text(static_cast<size_t>(n),'\0');
    if(gua_spatial_copy_json(d,text.data(),n)!=n) throw std::runtime_error("spatial document unavailable");
    text.pop_back(); return text;
}
uint64_t spatial_runtime_epoch(gua_runtime_t* r) {
    gua_context_status_t s{sizeof(s)};
    return gua_get_context_status(r->context,&s) ? s.session_epoch : 0;
}
void spatial_close_clients_unlocked(gua_runtime_t* r) {
    gua_spatial_error_t e{};
    for(const auto& [client,owner]:r->spatial_clients) gua_spatial_host_close_owner(r->spatial_host,owner,&e);
    r->spatial_clients.clear();
}
void spatial_clear_unlocked(gua_runtime_t* r) {
    spatial_close_clients_unlocked(r);
    gua_spatial_destroy(r->spatial_grants); r->spatial_grants=nullptr;
    gua_spatial_host_destroy(r->spatial_host); r->spatial_host=nullptr;
    r->spatial_provider=0; r->spatial_epoch=0;
}
bool spatial_enabled_unlocked(gua_runtime_t* r) {
    return r->spatial_host && r->spatial_grants &&
        r->observation_profile==GUA_OBSERVATION_PROFILE_DEBUG &&
        r->spatial_epoch==spatial_runtime_epoch(r);
}
std::string spatial_advertisement_unlocked(gua_runtime_t* r) {
    if(!spatial_enabled_unlocked(r)) return "";
    gua_spatial_error_t e{}; uint64_t owner=0; gua_spatial_document_t* d=nullptr;
    bool temporary=r->spatial_clients.empty();
    if(!temporary) owner=r->spatial_clients.begin()->second;
    else if(gua_spatial_host_open_owner(r->spatial_host,r->spatial_grants,&owner,&e)) return "";
    int status=gua_spatial_host_describe(r->spatial_host,owner,r->spatial_provider,&d,&e);
    std::unique_ptr<gua_spatial_document_t,decltype(&gua_spatial_destroy)> result(d,gua_spatial_destroy);
    if(temporary) gua_spatial_host_close_owner(r->spatial_host,owner,&e);
    return status==0 ? spatial_document_json(d) : "";
}
const char* spatial_safe_error(int status) {
    switch(status) {
    case GUA_SPATIAL_NOT_AUTHORIZED: return "not_authorized";
    case GUA_SPATIAL_CAPACITY: return "capacity";
    case GUA_SPATIAL_STALE: return "stale";
    case GUA_SPATIAL_UNSUPPORTED: return "unsupported";
    default: return "spatial_rejected";
    }
}
}
extern "C" int gua_runtime_bind_spatial(gua_runtime_t* r,gua_spatial_host_t* host,uint64_t provider,const gua_spatial_document_t* grants) {
    if(!r || !r->context) return GUA_SPATIAL_INVALID;
    try {
        std::lock_guard lock(r->context_mutex);
        if(!host) { spatial_clear_unlocked(r); return 0; }
        if(!grants || gua_spatial_document_type(grants)!=GUA_SPATIAL_OWNER) return GUA_SPATIAL_INVALID;
        auto wire=spatial_document_json(grants); auto value=gua_value_detail::parser(wire).parse();
        const auto epoch=gua_value_detail::integer(value.at("sessionEpoch").text,"$");
        if(r->observation_profile!=GUA_OBSERVATION_PROFILE_DEBUG || value.at("enabled").text!="true" ||
            (value.at("profile").text!="Debug" && value.at("profile").text!="Testing") ||
            static_cast<uint64_t>(epoch)!=spatial_runtime_epoch(r)) return GUA_SPATIAL_NOT_AUTHORIZED;
        uint64_t owner=0; gua_spatial_error_t e{}; gua_spatial_document_t* ad=nullptr;
        int status=gua_spatial_host_open_owner(host,grants,&owner,&e); if(status) return status;
        status=gua_spatial_host_describe(host,owner,provider,&ad,&e);
        gua_spatial_destroy(ad); gua_spatial_host_close_owner(host,owner,&e); if(status) return status;
        gua_spatial_document_t* copy=nullptr; gua_spatial_parse_options_v1_t options{sizeof(options),GUA_SPATIAL_OWNER};
        status=gua_spatial_from_json(&options,{wire.data(),static_cast<uint32_t>(wire.size())},&copy,&e); if(status) return status;
        gua_spatial_host_retain(host); spatial_clear_unlocked(r);
        r->spatial_host=host; r->spatial_grants=copy; r->spatial_provider=provider; r->spatial_epoch=static_cast<uint64_t>(epoch);
        return 0;
    } catch(...) { return GUA_SPATIAL_INTERNAL; }
}
extern "C" uint64_t gua_runtime_create_spatial_client(gua_runtime_t* r) {
    if(!r || !r->context) return 0;
    try {
        std::lock_guard lock(r->context_mutex); if(!spatial_enabled_unlocked(r)) return 0;
        uint64_t owner=0; gua_spatial_error_t e{};
        if(gua_spatial_host_open_owner(r->spatial_host,r->spatial_grants,&owner,&e)) return 0;
        auto client=r->next_spatial_client++;
        try { r->spatial_clients.emplace(client,owner); } catch(...) { gua_spatial_host_close_owner(r->spatial_host,owner,&e); throw; }
        return client;
    } catch(...) { return 0; }
}
extern "C" void gua_runtime_release_spatial_client(gua_runtime_t* r,uint64_t client) {
    if(!r || !r->context) return;
    try { std::lock_guard lock(r->context_mutex); auto found=r->spatial_clients.find(client);
        if(found==r->spatial_clients.end()) return;
        gua_spatial_error_t e{}; gua_spatial_host_close_owner(r->spatial_host,found->second,&e); r->spatial_clients.erase(found);
    } catch(...) {}
}
extern "C" int gua_runtime_spatial_command(gua_runtime_t* r,uint64_t client,int operation,const gua_spatial_document_t* batch,uint64_t batch_id,gua_spatial_document_t** result) {
    if(result) *result=nullptr;
    if(!r || !r->context || !result) return GUA_SPATIAL_INVALID;
    try {
        std::lock_guard lock(r->context_mutex);
        if(!spatial_enabled_unlocked(r)) { spatial_close_clients_unlocked(r); return GUA_SPATIAL_NOT_AUTHORIZED; }
        auto found=r->spatial_clients.find(client); if(found==r->spatial_clients.end()) return GUA_SPATIAL_STALE;
        gua_spatial_error_t e{}; auto owner=found->second;
        switch(operation) {
        case GUA_SPATIAL_INFO: return gua_spatial_host_describe(r->spatial_host,owner,r->spatial_provider,result,&e);
        case GUA_SPATIAL_ENQUEUE: {
            if(!batch || gua_spatial_document_type(batch)!=GUA_SPATIAL_BATCH) return GUA_SPATIAL_INVALID;
            gua_spatial_document_t* ad=nullptr;
            int status=gua_spatial_host_describe(r->spatial_host,owner,r->spatial_provider,&ad,&e);
            std::unique_ptr<gua_spatial_document_t,decltype(&gua_spatial_destroy)> descriptor(ad,gua_spatial_destroy);
            if(status) return status;
            auto provider=gua_value_detail::parser(spatial_document_json(ad)).parse().at("provider");
            auto value=gua_value_detail::parser(spatial_document_json(batch)).parse();
            for(const auto& query:value.at("queries").items)
                if(query.at("spaceId").text!=provider.at("spaceId").text ||
                    gua_value_detail::integer(query.at("spaceEpoch").text,"$")!=gua_value_detail::integer(provider.at("spaceEpoch").text,"$")) return GUA_SPATIAL_NOT_AUTHORIZED;
            return gua_spatial_host_enqueue(r->spatial_host,owner,batch,&e);
        }
        case GUA_SPATIAL_POLL: return gua_spatial_host_poll(r->spatial_host,owner,batch_id,result,&e);
        case GUA_SPATIAL_CANCEL: return gua_spatial_host_cancel(r->spatial_host,owner,batch_id,&e);
        default: return GUA_SPATIAL_INVALID;
        }
    } catch(...) { return GUA_SPATIAL_INTERNAL; }
}

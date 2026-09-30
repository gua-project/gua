// Included by gua.cpp: shares the context lock and published-tree projection.
namespace {
std::string observe_quote(const std::string& s) { return "\"" + escape_json(s) + "\""; }
bool observe_text(gua_value_text_t t, std::string& out, bool empty = false) {
    if (t.size > 4096 || (!t.data && t.size)) return false;
    gua_value_descriptor_v1_t d{}; d.struct_size = sizeof(d); d.type = GUA_VALUE_STRING; d.text = t;
    gua_value_t* v = nullptr; gua_value_error_t e{};
    if (gua_value_create(&d, nullptr, &v, &e)) return false;
    gua_value_destroy(v);
    out.assign(t.data ? t.data : "", t.size);
    return (empty || !out.empty()) && out.find('\0') == std::string::npos;
}
std::string observe_value(const ObserveSample& s) {
    int n = gua_value_copy_json(s.value.get(), nullptr, 0);
    if (n <= 0) throw std::bad_alloc();
    std::string r(static_cast<size_t>(n), '\0');
    if (gua_value_copy_json(s.value.get(), r.data(), n) != n) throw std::bad_alloc();
    r.pop_back(); return r;
}
bool observe_equal(const ObserveSample& a, const ObserveSample& b) {
    if (a.error != b.error) return false;
    if (a.error) return true;
    int equal = 0; gua_value_error_t e{};
    if (gua_value_equals(a.value.get(), b.value.get(), &equal, &e)) throw std::bad_alloc();
    return equal != 0;
}
bool observe_exists(const gua_context_t& ctx, const ObserveOwner& o) {
    if (o.source == GUA_OBSERVE_WORLD) return true;
    if (o.source == GUA_OBSERVE_UI) return std::any_of(ctx.nodes.begin(), ctx.nodes.end(), [&](const auto& n){return n.id == o.runtime_id;});
    return std::any_of(ctx.world_objects.begin(), ctx.world_objects.end(), [&](const auto& n){return n.id == o.runtime_id;});
}
std::string observe_catalogs(const ObserveSample* value, const ObserveSample* before = nullptr, const ObserveSample* after = nullptr) {
    std::string json="{"; bool comma=false;
    const ObserveSample* samples[]={value,before,after}; const char* keys[]={"value","before","after"};
    for(int i=0;i<3;++i) {
        const auto* s=samples[i];
        if(!s || s->error || (gua_value_get_type(s->value.get())!=GUA_VALUE_ENUM && gua_value_get_element_type(s->value.get())!=GUA_VALUE_ENUM)) continue;
        int n=gua_value_copy_enum_catalog_json(s->value.get(),nullptr,0); if(n<=0) throw std::bad_alloc();
        std::string catalog(static_cast<size_t>(n),'\0');
        if(gua_value_copy_enum_catalog_json(s->value.get(),catalog.data(),n)!=n) throw std::bad_alloc();
        catalog.pop_back(); if(comma) json+=","; comma=true; json+=observe_quote(keys[i])+":"+catalog;
    }
    return json+"}";
}
void observe_trim(ObserveState& s, ObserveJournal& j) {
    while (!j.events.empty() && (j.events.size() > s.max_events || j.bytes > s.max_bytes)) {
        j.bytes -= j.events.front().second.size()+j.catalogs.at(j.events.front().first).size();
        j.catalogs.erase(j.events.front().first); j.events.pop_front();
    }
}
// Changing the authorized owner set invalidates Player cursors before any old
// history can be read. No Debug sequence/count is exposed in the Player stream.
void observe_authorize(gua_context_t& ctx) {
    auto& s = ctx.observe;
    std::unordered_set<uint64_t> allowed;
    const auto ui = project_nodes(ctx.nodes, GUA_OBSERVATION_PROFILE_PLAYER);
    const auto world = project_world_objects(ctx.world_objects, GUA_OBSERVATION_PROFILE_PLAYER);
    for (const auto& [registration_id,r] : s.registrations) {
        if (!r.player) continue;
        const auto& o = s.owners.at(r.owner);
        const auto id = r.owner;
        if (o.source == GUA_OBSERVE_WORLD ||
            (o.source == GUA_OBSERVE_UI && std::any_of(ui.begin(), ui.end(), [&](const auto& n){return n.id == o.runtime_id;})) ||
            (o.source == GUA_OBSERVE_OBJECT && std::any_of(world.begin(), world.end(), [&](const auto& n){return n.id == o.runtime_id;}))) allowed.insert(id);
    }
    if (allowed != s.player_owners) {
        auto& j = s.journals[1]; j.events.clear(); j.catalogs.clear(); j.bytes = 0; ++j.generation; ++j.revision;
        s.player_owners = std::move(allowed);
    }
}
bool observe_visible(const ObserveState& s, const ObserveRegistration& r, int profile) {
    return profile == 0 || (r.player && s.player_owners.count(r.owner));
}
std::string observe_metadata(const gua_context_t& ctx, int profile) {
    const auto& s = ctx.observe; const auto& j = s.journals[profile];
    return "\"schemaVersion\":1,\"sourceId\":" + observe_quote(s.source_id) +
        ",\"sessionEpoch\":" + std::to_string(ctx.session_epoch) +
        ",\"profile\":" + observe_quote(profile ? "player" : "debug") +
        ",\"sequence\":" + std::to_string(j.sequence) + ",\"revision\":" + std::to_string(j.revision) +
        ",\"uiFrame\":" + std::to_string(ctx.frame_sequence) + ",\"uiRevision\":" + std::to_string(profile ? ctx.player_revision : ctx.revision) +
        ",\"worldFrame\":" + std::to_string(ctx.world_frame_sequence) + ",\"worldRevision\":" + std::to_string(profile ? ctx.player_world_revision : ctx.world_revision);
}
std::string observe_identity(const ObserveState& s, const ObserveRegistration& r) {
    const auto& o = s.owners.at(r.owner);
    return "\"ownerId\":" + std::to_string(o.id) + ",\"registrationId\":" + std::to_string(r.id) +
        ",\"source\":" + observe_quote(o.source == 1 ? "ui" : o.source == 2 ? "object" : "world") +
        ",\"runtimeId\":" + observe_quote(o.runtime_id) + ",\"name\":" + observe_quote(r.name);
}
void observe_change(gua_context_t& ctx, const ObserveRegistration& r, const ObserveSample* before,
    const ObserveSample* after, const char* kind, const char* boundary) {
    auto& s = ctx.observe;
    for (int p = 0; p < 2; ++p) {
        if (!observe_visible(s, r, p)) continue;
        auto& j = s.journals[p]; ++j.sequence; ++j.revision;
        std::string event = "{" + observe_metadata(ctx,p) + "," + observe_identity(s,r) +
            ",\"kind\":" + observe_quote(kind) + ",\"boundary\":" + observe_quote(boundary);
        if (before) {
            event += ",\"beforeStatus\":" + observe_quote(before->error ? "unavailable" : "available");
            if (!before->error) event += ",\"before\":" + observe_value(*before);
            else event += ",\"beforeError\":" + std::to_string(before->error);
        }
        if (after) {
            event += ",\"afterStatus\":" + observe_quote(after->error ? "unavailable" : "available");
            if (!after->error) event += ",\"after\":" + observe_value(*after);
            else event += ",\"afterError\":" + std::to_string(after->error);
        }
        event += "}"; auto catalogs=observe_catalogs(nullptr,before,after);
        j.bytes += event.size()+catalogs.size(); j.catalogs.emplace(j.sequence,std::move(catalogs));
        j.events.emplace_back(j.sequence,std::move(event)); observe_trim(s,j);
    }
}
void observe_apply(gua_context_t& ctx, ObserveRegistration& r, ObserveSample sample, const char* boundary) {
    if (observe_equal(r.current,sample)) return;
    const char* kind = sample.error ? "unavailable" : r.current.error == GUA_OBSERVE_NOT_SAMPLED ? "added" : r.current.error ? "recovered" : "changed";
    observe_change(ctx,r,&r.current,&sample,kind,boundary); r.current = std::move(sample);
}
void observe_erase_owner(gua_context_t& ctx, uint64_t id) {
    auto& s = ctx.observe;
    if (s.player_owners.erase(id)) {
        auto& player = s.journals[1];
        player.events.clear(); player.catalogs.clear(); player.bytes = 0; ++player.generation; ++player.revision;
    }
    for (auto i = s.registrations.begin(); i != s.registrations.end();) {
        if (i->second.owner != id) { ++i; continue; }
        observe_change(ctx,i->second,&i->second.current,nullptr,"removed","lifecycle");
        i = s.registrations.erase(i);
    }
    s.owners.erase(id); s.player_owners.erase(id);
}
void observe_discard(gua_context_t& ctx, bool ui) {
    for (auto& [id,r] : ctx.observe.registrations)
        if ((ctx.observe.owners.at(r.owner).source == GUA_OBSERVE_UI) == ui) r.staged.reset();
}
void observe_frame(gua_context_t& ctx, bool ui) {
    if (ctx.observe.owners.empty()) return;
    observe_authorize(ctx);
    auto& s = ctx.observe;
    std::vector<uint64_t> gone;
    for (const auto& [id,o] : s.owners)
        if ((o.source == GUA_OBSERVE_UI) == ui && !observe_exists(ctx,o)) gone.push_back(id);
    for (auto id : gone) observe_erase_owner(ctx,id);
    for (auto& [id,r] : s.registrations) {
        if ((s.owners.at(r.owner).source == GUA_OBSERVE_UI) != ui || !r.staged) continue;
        auto sample = std::move(*r.staged); r.staged.reset(); observe_apply(ctx,r,std::move(sample),"frame");
    }
}
void observe_reset(gua_context_t& ctx) {
    auto& s = ctx.observe; s.owners.clear(); s.registrations.clear(); s.player_owners.clear();
    // Subscriptions retain only their epoch/cursor, so poll can report stale_session.
    for (auto& j : s.journals) j = ObserveJournal{};
}
std::string observe_snapshot_json(const gua_context_t& ctx, int p) {
    const auto& s = ctx.observe; std::string json = "{" + observe_metadata(ctx,p) + ",\"kind\":\"snapshot\",\"entries\":[";
    bool comma = false;
    for (const auto& [id,r] : s.registrations) {
        if (!observe_visible(s,r,p)) continue;
        if (comma) json += ","; comma = true;
        json += "{" + observe_identity(s,r) + ",\"status\":" + observe_quote(r.current.error ? "unavailable" : "available");
        if (r.current.error) json += ",\"error\":" + std::to_string(r.current.error);
        else json += ",\"value\":" + observe_value(r.current);
        json += "}";
    }
    return json + "]}";
}
std::string observe_snapshot_catalogs(const gua_context_t& ctx, int p) {
    std::string json="["; bool comma=false;
    for(const auto& [id,r]:ctx.observe.registrations) {
        if(!observe_visible(ctx.observe,r,p)) continue;
        if(comma) json+=","; comma=true; json+=observe_catalogs(&r.current);
    }
    return json+"]";
}
}
#define OBS_TRY try
#define OBS_CATCH catch (...) { return GUA_OBSERVE_INTERNAL; }
extern "C" int gua_observe_create_owner(gua_context_t* c,int source,gua_value_text_t runtime,uint64_t* out) {
    if (out) *out=0;
    if (!c || !out || source<1 || source>3) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY {
        std::string id; if (!observe_text(runtime,id,source==3) || (source==3 && !id.empty())) return GUA_OBSERVE_ARGUMENT;
        std::lock_guard lock(c->mutex); auto& s=c->observe;
        ObserveOwner o{s.next_id,source,id}; if (!observe_exists(*c,o)) return GUA_OBSERVE_STALE;
        for (const auto& [key,other]:s.owners) if (other.source==source && other.runtime_id==id) return GUA_OBSERVE_DUPLICATE;
        s.owners.emplace(o.id,o); ++s.next_id; observe_authorize(*c); *out=o.id; return 0;
    } OBS_CATCH
}
extern "C" int gua_observe_destroy_owner(gua_context_t* c,uint64_t owner) {
    if (!c) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY { std::lock_guard lock(c->mutex); if (!c->observe.owners.count(owner)) return GUA_OBSERVE_STALE;
        observe_erase_owner(*c,owner); return 0; } OBS_CATCH
}
extern "C" int gua_observe_register_v1(gua_context_t* c,const gua_observe_registration_v1_t* d,uint64_t* out) {
    if (out) *out=0;
    if (!c || !d || !out || d->struct_size<sizeof(*d) || d->allow_player<0 || d->allow_player>1 || d->sensitive<0 || d->sensitive>1) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY {
        std::string name; if (!observe_text(d->name,name)) return GUA_OBSERVE_ARGUMENT;
        std::lock_guard lock(c->mutex); auto& s=c->observe;
        if (!s.owners.count(d->owner_id)) return GUA_OBSERVE_STALE;
        for (const auto& [id,r]:s.registrations) if (r.owner==d->owner_id && r.name==name) return GUA_OBSERVE_DUPLICATE;
        ObserveRegistration r{s.next_id++,d->owner_id,name,d->allow_player!=0,d->sensitive!=0,{},std::nullopt};
        if (r.sensitive) r.current.error=GUA_OBSERVE_SENSITIVE;
        s.registrations.emplace(r.id,r); observe_authorize(*c); observe_change(*c,r,nullptr,&r.current,"unavailable","lifecycle");
        *out=r.id; return 0;
    } OBS_CATCH
}
extern "C" int gua_observe_unregister(gua_context_t* c,uint64_t id) {
    if (!c) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY { std::lock_guard lock(c->mutex); auto& s=c->observe; auto i=s.registrations.find(id);
        if(i==s.registrations.end()) return GUA_OBSERVE_STALE;
        observe_change(*c,i->second,&i->second.current,nullptr,"removed","lifecycle"); s.registrations.erase(i); observe_authorize(*c); return 0;
    } OBS_CATCH
}
extern "C" int gua_observe_publish(gua_context_t* c,uint64_t id,const gua_value_t* value,int error,int stage) {
    if (!c || (stage!=0 && stage!=1) || (error==0 ? !value : value!=nullptr) ||
        (error!=0 && !(error>=1 && error<=11) && error!=GUA_OBSERVE_GETTER_FAILED)) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY {
        std::lock_guard lock(c->mutex); auto& s=c->observe; auto i=s.registrations.find(id);
        if(i==s.registrations.end()) return GUA_OBSERVE_STALE;
        auto& r=i->second; const bool ui=s.owners.at(r.owner).source==GUA_OBSERVE_UI;
        if(stage && !(ui ? c->frame_in_progress : c->world_frame_in_progress)) return GUA_OBSERVE_NO_FRAME;
        ObserveSample sample; sample.error=r.sensitive ? GUA_OBSERVE_SENSITIVE : error;
        if(!sample.error) { auto v=gua_value_clone(value); if(!v) return GUA_OBSERVE_INTERNAL; sample.value={v,gua_value_destroy}; }
        if(stage) r.staged=std::move(sample);
        else { r.staged.reset(); observe_apply(*c,r,std::move(sample),"explicit"); }
        return 0;
    } OBS_CATCH
}
extern "C" int gua_observe_set_limits(gua_context_t* c,uint32_t count,uint64_t bytes) {
    if(!c || !count || !bytes) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY { std::lock_guard lock(c->mutex); c->observe.max_events=count; c->observe.max_bytes=bytes;
        for(auto& j:c->observe.journals) observe_trim(c->observe,j); return 0; } OBS_CATCH
}
extern "C" int gua_observe_snapshot(gua_context_t* c,int p,gua_observe_result_t** out) {
    if(out) *out=nullptr; if(!c || !out || p<0 || p>1) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY { std::lock_guard lock(c->mutex); *out=new gua_observe_result_t{observe_snapshot_json(*c,p),observe_snapshot_catalogs(*c,p)}; return 0; } OBS_CATCH
}
extern "C" int gua_observe_subscribe(gua_context_t* c,int p,uint64_t* id,gua_observe_result_t** out) {
    if(out) *out=nullptr; if(id) *id=0; if(!c || !out || !id || p<0 || p>1) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY { std::lock_guard lock(c->mutex); auto& s=c->observe; auto& j=s.journals[p];
        auto result=std::make_unique<gua_observe_result_t>(gua_observe_result_t{observe_snapshot_json(*c,p),observe_snapshot_catalogs(*c,p)});
        auto key=s.next_id++; s.subscriptions.emplace(key,ObserveSubscription{p,c->session_epoch,j.sequence,j.generation});
        *out=result.release(); *id=key; return 0; } OBS_CATCH
}
extern "C" int gua_observe_poll(gua_context_t* c,uint64_t id,gua_observe_result_t** out) {
    if(out) *out=nullptr; if(!c || !out) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY { std::lock_guard lock(c->mutex); auto& s=c->observe; auto i=s.subscriptions.find(id);
        if(i==s.subscriptions.end()) return GUA_OBSERVE_STALE;
        auto& sub=i->second; const auto& j=s.journals[sub.profile];
        const char* status="ok";
        if(sub.epoch!=c->session_epoch) status="stale_session";
        else if(sub.generation!=j.generation || (sub.cursor<j.sequence && (j.events.empty() || sub.cursor+1<j.events.front().first))) status="gap";
        std::string json="{"+observe_metadata(*c,sub.profile)+",\"kind\":\"changes\",\"status\":"+observe_quote(status)+",\"events\":[";
        bool comma=false; std::string catalogs="[";
        if(std::strcmp(status,"ok")==0) for(const auto& [sequence,event]:j.events) if(sequence>sub.cursor) { if(comma) { json+=","; catalogs+=","; } comma=true; json+=event; catalogs+=j.catalogs.at(sequence); }
        json+="]}"; catalogs+="]"; *out=new gua_observe_result_t{std::move(json),std::move(catalogs)};
        if(std::strcmp(status,"ok")==0) sub.cursor=j.sequence; // gaps remain sticky; resubscribe explicitly
        return 0;
    } OBS_CATCH
}
extern "C" int gua_observe_unsubscribe(gua_context_t* c,uint64_t id) {
    if(!c) return GUA_OBSERVE_ARGUMENT;
    OBS_TRY { std::lock_guard lock(c->mutex); return c->observe.subscriptions.erase(id) ? 0 : GUA_OBSERVE_STALE; } OBS_CATCH
}
extern "C" int gua_observe_result_copy_json(const gua_observe_result_t* r,char* out,int cap) {
    if(!r || cap<0 || (!out && cap)) return 0;
    if(r->json.size()>=static_cast<size_t>(INT_MAX)) return 0;
    int n=static_cast<int>(r->json.size())+1;
    if(out && cap>=n) std::memcpy(out,r->json.c_str(),static_cast<size_t>(n));
    else if(out && cap) out[0]='\0';
    return n;
}
extern "C" void gua_observe_result_destroy(gua_observe_result_t* r) { delete r; }
extern "C" int gua_observe_result_copy_transport_json(const gua_observe_result_t* r,char* out,int cap) {
    if(!r) return 0;
    try { gua_observe_result_t envelope{"{\"document\":"+r->json+",\"catalogs\":"+r->catalogs+"}"};
        return gua_observe_result_copy_json(&envelope,out,cap);
    } catch(...) { return 0; }
}
#undef OBS_TRY
#undef OBS_CATCH
// Registration liveness is checked before host getters, preventing callbacks
// into an owner removed by an earlier committed frame/reset.
extern "C" int gua_observe_registration_alive(gua_context_t* c,uint64_t id) {
    if(!c) return GUA_OBSERVE_ARGUMENT;
    try { std::lock_guard lock(c->mutex); return c->observe.registrations.count(id) ? 0 : GUA_OBSERVE_STALE; }
    catch(...) { return GUA_OBSERVE_INTERNAL; }
}

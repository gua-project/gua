// Implementation shares the strict geometry/semantic validator with spatial.cpp.
namespace {
void version(const json& j,const char* type,const char* v="spatial-host-r1") {
    require(text(j,"schemaVersion")==v&&text(j,"documentType")==type);
}
void host_document(const json& j,int type) {
    if(type==4||type==10) { result(j,type==4?1:2); return; }
    if(type==5) {
        object(j,{"schemaVersion","documentType","batchId","consistency","queries"});
        sequence(j.at("batchId"),true); tag(j.at("consistency"),{"bestEffort","samePhysicsSample"});
        const auto& q=j.at("queries"); require(q.type==json::array&&!q.items.empty()&&q.items.size()<=64);
        std::set<int64_t> ids; std::set<std::string> names;
        const auto& first=q.items.front();
        for(const auto& a:q.items) {
            version(a,"request","spatial-r1"); request(a);
            require(ids.insert(sequence(a.at("requestId"))).second&&names.insert(text(a,"queryId")).second);
            for(auto k:{"sessionEpoch","spaceEpoch"}) require(sequence(a.at(k))==sequence(first.at(k)),GUA_SPATIAL_CONTEXT);
            require(text(a,"spaceId")==text(first,"spaceId")&&text(a,"consistency")==text(j,"consistency"),GUA_SPATIAL_CONTEXT);
        }
    } else if(type==7) {
        object(j,{"schemaVersion","documentType","provider","policies"},{"loadedRegion"});
        version(j.at("provider"),"provider","spatial-r1"); provider(j.at("provider"));
        const auto& p=j.at("policies"); require(p.type==json::array&&!p.items.empty()&&p.items.size()<=64);
        std::set<std::string> ids;
        for(const auto& a:p.items) {
            object(a,{"id","revision","region"}); require(ids.insert(text(a,"id")).second);
            sequence(a.at("revision"),true); region(a.at("region"));
        }
        require(ids.size()==j.at("provider").at("policies").items.size());
        for(const auto& a:j.at("provider").at("policies").items) require(ids.contains(text(a)));
        if(has(j,"loadedRegion")) region(j.at("loadedRegion"));
    } else if(type==8) {
        object(j,{"schemaVersion","documentType","sessionEpoch","profile","enabled","policies","region"});
        sequence(j.at("sessionEpoch"),true); tag(j.at("profile"),{"Debug","Testing","Player","PublicAgent"});
        boolean(j.at("enabled")); string_set(j.at("policies")); require(j.at("policies").items.size()<=64); region(j.at("region"));
    } else if(type==9) {
        object(j,{"schemaVersion","documentType","physicsSampleId"},{"tick","worldSnapshot"});
        text(j,"physicsSampleId"); if(has(j,"tick")) sequence(j.at("tick"));
        if(has(j,"worldSnapshot")) {
            const auto& w=j.at("worldSnapshot"); object(w,{"sessionEpoch","frameSequence","revision"});
            sequence(w.at("sessionEpoch"),true); sequence(w.at("frameSequence")); sequence(w.at("revision"));
        }
    } else if(type==11) {
        object(j,{"schemaVersion","documentType","provider","budgets"});
        version(j.at("provider"),"provider","spatial-r1"); provider(j.at("provider"));
        const auto& b=j.at("budgets"); object(b,{"maxQueueDepth","queryDeadlineMs","maxBatchWorkTimeMs"});
        require(sequence(b.at("maxQueueDepth"),true)<=4096); positive(b.at("queryDeadlineMs")); positive(b.at("maxBatchWorkTimeMs"));
    } else if(type==6) {
        object(j,{"schemaVersion","documentType","batchId","items"}); sequence(j.at("batchId"),true);
        const auto& items=j.at("items"); require(items.type==json::array&&!items.items.empty()&&items.items.size()<=64);
        std::set<int64_t> ids; std::set<std::string> names;
        for(const auto& a:items.items) {
            object(a,{"requestId","queryId","state"},{"result","reason"});
            require(ids.insert(sequence(a.at("requestId"),true)).second&&names.insert(text(a,"queryId")).second);
            const auto state=tag(a.at("state"),{"completed","failed","notExecuted"});
            require(has(a,"result")== (state=="completed")&&has(a,"reason")==(state!="completed"));
            if(state=="completed") {
                const auto& r=a.at("result"); version(r,"result"); result(r,1);
                require(text(r,"status")=="completed"&&text(r,"queryId")==text(a,"queryId")&&sequence(r.at("requestId"))==sequence(a.at("requestId")),GUA_SPATIAL_CONTEXT);
            } else tag(a.at("reason"),{"cancelled","deadline_exceeded","work_budget","not_authorized","provider_unregistered","boundary_ended","internal","unsupported_operation","unsupported_shape","unsupported_consistency","stale_space"});
        }
    } else fail(GUA_SPATIAL_INVALID);
}
json str(std::string s) { json j; j.type=json::string; j.text=std::move(s); return j; }
json number(double n) { json j; j.type=json::number; std::ostringstream s; s.imbue(std::locale::classic()); s.precision(17); s<<n; j.text=s.str(); return j; }
json integer(uint64_t n) { json j; j.type=json::number; j.text=std::to_string(n); return j; }
json obj(std::initializer_list<std::pair<const std::string,json>> fields) { json j; j.type=json::object; j.fields=fields; return j; }
std::unique_ptr<gua_spatial_document_t> doc(int type,json j) {
    auto d=std::make_unique<gua_spatial_document_t>(); d->type=type; d->wire=dump(j); d->value=std::move(j); return d;
}
struct bounds { vec lo,hi; };
bounds aabb(const json& r) { return {vector(r.at("min")),vector(r.at("max"))}; }
// Error-free TwoSum determines the direction of addition rounding without
// relying on long double (binary64 on MSVC) or changing process rounding mode.
double outward_add(double a,double b,bool upper) {
    const double s=a+b;
    require(std::isfinite(s),GUA_SPATIAL_NOT_AUTHORIZED);
    const double z=s-a, error=(a-(s-z))+(b-z);
    require(std::isfinite(error),GUA_SPATIAL_NOT_AUTHORIZED);
    if((upper&&error>0)||(!upper&&error<0)) return std::nextafter(s,upper?INFINITY:-INFINITY);
    return s;
}
double upper_product(double a,double b) {
    if(a==0||b==0) return 0;
    if(a==1) return b;
    if(b==1) return a;
    const double p=a*b;
    require(std::isfinite(p),GUA_SPATIAL_NOT_AUTHORIZED);
    // Conservatively cover multiplication residuals including underflow. Exact
    // aligned axes retain exact bounds via the 0/1 cases above.
    return std::nextafter(p,INFINITY);
}
bounds query_bounds(const json& q) {
    bounds b{};
    if(text(q,"kind")=="raycast") {
        auto a=vector(q.at("segment").at("from")),z=vector(q.at("segment").at("to"));
        for(size_t i=0;i<3;++i) { b.lo[i]=std::min(a[i],z[i]); b.hi[i]=std::max(a[i],z[i]); }
    } else {
        const auto& s=q.at("shape"); const auto k=text(s,"type"); vec center{},ext{};
        if(k=="capsule") {
            auto a=vector(s.at("pointA")),z=vector(s.at("pointB")); auto r=num(s.at("radius"));
            for(size_t i=0;i<3;++i) { b.lo[i]=outward_add(std::min(a[i],z[i]),-r,false); b.hi[i]=outward_add(std::max(a[i],z[i]),r,true); }
        } else {
            center=vector(s.at("center"));
            if(k=="sphere") ext.fill(num(s.at("radius")));
            else {
                auto h=vector(s.at("halfExtents")); auto x=vector(s.at("basis").at("x")),y=vector(s.at("basis").at("y")),z=vector(s.at("basis").at("z"));
                for(size_t i=0;i<3;++i) ext[i]=outward_add(outward_add(upper_product(std::abs(x[i]),h[0]),upper_product(std::abs(y[i]),h[1]),true),upper_product(std::abs(z[i]),h[2]),true);
            }
            for(size_t i=0;i<3;++i) { b.lo[i]=outward_add(center[i],-ext[i],false); b.hi[i]=outward_add(center[i],ext[i],true); }
        }
        if(has(q,"delta")) { auto d=vector(q.at("delta")); for(size_t i=0;i<3;++i) { b.lo[i]=outward_add(b.lo[i],std::min(0.0,d[i]),false); b.hi[i]=outward_add(b.hi[i],std::max(0.0,d[i]),true); } }
    }
    for(size_t i=0;i<3;++i) require(std::isfinite(b.lo[i])&&std::isfinite(b.hi[i]),GUA_SPATIAL_NOT_AUTHORIZED);
    return b;
}
bool fits(bounds b,bounds r) { for(size_t i=0;i<3;++i) if(b.lo[i]<r.lo[i]||b.hi[i]>r.hi[i]) return false; return true; }
struct host_provider { json registration; uint64_t lease=0; };
struct host_owner { json grants; };
struct host_batch {
    uint64_t owner=0,provider=0,id=0,lease=0,order=0;
    json input,boundary; std::vector<json> items;
    std::vector<bool> dispatched;
    std::vector<std::optional<bounds>> prepared;
    size_t next=0; bool inflight=false,done=false,cancelled=false,owner_closed=false;
    double accepted=0,started=0; uint64_t revision=0;
    size_t retained_bytes=512;
};
}
struct gua_spatial_host_t {
    std::mutex mutex; gua_spatial_host_options_v1_t limits; std::string clock;
    std::chrono::steady_clock::time_point origin=std::chrono::steady_clock::now();
    uint64_t token=0,order=0;
    std::map<uint64_t,host_provider> providers;
    std::map<uint64_t,host_owner> owners;
    std::map<std::pair<uint64_t,uint64_t>,host_batch> batches;
    std::map<uint64_t,uint64_t> leases;
};
namespace {
double now(const gua_spatial_host_t& h) { return std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-h.origin).count(); }
uint64_t token(gua_spatial_host_t& h) { require(h.token<9007199254740991ULL,GUA_SPATIAL_CAPACITY); return ++h.token; }
void host(gua_spatial_host_t* h) { require(h); }
void document(const gua_spatial_document_t* d,int type) { require(d&&d->type==type); }
const json* policy(const host_provider& p,const std::string& id) {
    for(const auto& a:p.registration.at("policies").items) if(text(a,"id")==id) return &a; return nullptr;
}
bool owner_authorized(gua_spatial_host_t& h,const host_batch& b,const json& q) {
    auto o=h.owners.find(b.owner);
    if(o==h.owners.end()) return false;
    const auto& g=o->second.grants;
    bounds volume=query_bounds(q);
    const auto& qs=b.input.at("queries").items;
    for(size_t i=0;i<b.prepared.size();++i) if(sequence(qs[i].at("requestId"))==sequence(q.at("requestId"))&&b.prepared[i]) volume=*b.prepared[i];
    return boolean(g.at("enabled"))&&(text(g,"profile")=="Debug"||text(g,"profile")=="Testing")&&sequence(g.at("sessionEpoch"))==sequence(q.at("sessionEpoch"))&&contains(g.at("policies"),text(q,"queryPolicyId"))&&fits(volume,aabb(g.at("region")));
}
const json* authorized(gua_spatial_host_t& h,const host_batch& b,const json& q) {
    auto p=h.providers.find(b.provider);
    if(p==h.providers.end()||!owner_authorized(h,b,q)) return nullptr;
    auto pol=policy(p->second,text(q,"queryPolicyId")); if(!pol) return nullptr;
    bounds volume=query_bounds(q);
    const auto& qs=b.input.at("queries").items;
    for(size_t i=0;i<b.prepared.size();++i) if(sequence(qs[i].at("requestId"))==sequence(q.at("requestId"))&&b.prepared[i]) volume=*b.prepared[i];
    return fits(volume,aabb(pol->at("region")))?pol:nullptr;
}
json item(const json& q,const char* state,const char* reason) {
    return obj({{"requestId",q.at("requestId")},{"queryId",q.at("queryId")},{"state",str(state)},{"reason",str(reason)}});
}
void terminate(host_batch& b,const char* reason) {
    const auto& qs=b.input.at("queries").items;
    for(size_t i=0;i<qs.size();++i) if(b.items[i].type==json::null)
        b.items[i]=item(qs[i],b.dispatched[i]?"failed":"notExecuted",reason);
    b.done=true; b.inflight=false;
}
void advance(host_batch& b) {
    if(!b.inflight) while(b.next<b.items.size()&&b.items[b.next].type!=json::null) ++b.next;
    b.done=std::all_of(b.items.begin(),b.items.end(),[](const json& i) { return i.type!=json::null; });
}
void retained(host_batch& b) {
    b.retained_bytes=512;
    const auto& qs=b.input.at("queries").items;
    for(size_t i=0;i<qs.size();++i) b.retained_bytes+=dump(b.items[i].type==json::null?item(qs[i],"notExecuted","provider_unregistered"):b.items[i]).size();
}
void expire(gua_spatial_host_t& h,host_batch& b) {
    const auto& qs=b.input.at("queries").items; const auto elapsed=now(h)-b.accepted;
    for(size_t i=0;i<qs.size();++i) if(b.items[i].type==json::null&&elapsed>=num(qs[i].at("deadlineMs"))) b.items[i]=item(qs[i],b.dispatched[i]?"failed":"notExecuted","deadline_exceeded");
    advance(b); retained(b);
}
void revoke(gua_spatial_host_t& h,host_batch& b) {
    const auto& qs=b.input.at("queries").items;
    for(size_t i=0;i<qs.size();++i) if(h.providers.contains(b.provider)?!authorized(h,b,qs[i]):!owner_authorized(h,b,qs[i])) b.items[i]=item(qs[i],b.dispatched[i]?"failed":"notExecuted","not_authorized");
    advance(b); retained(b);
}
const char* stopped(gua_spatial_host_t& h,host_batch& b) {
    if(b.cancelled) return "cancelled";
    if(!h.providers.contains(b.provider)) return "provider_unregistered";
    if(b.next<b.items.size()&&!authorized(h,b,b.input.at("queries").items[b.next])) return "not_authorized";
    auto t=now(h);
    if(b.lease&&t-b.started>=h.limits.max_batch_work_ms) return "work_budget";
    if(b.next<b.items.size()&&t-b.accepted>=num(b.input.at("queries").items[b.next].at("deadlineMs"))) return "deadline_exceeded";
    return nullptr;
}
host_batch& leased(gua_spatial_host_t& h,uint64_t lease) {
    require(lease!=0,GUA_SPATIAL_STALE);
    for(auto& [k,b]:h.batches) { (void)k; if(b.lease==lease&&!b.done) return b; }
    fail(GUA_SPATIAL_STALE);
}
void finish(gua_spatial_host_t& h,host_batch& b,const char* reason) {
    if(std::string_view(reason)=="not_authorized") revoke(h,b);
    else if(std::string_view(reason)=="deadline_exceeded") expire(h,b);
    else terminate(b,reason);
}
}
extern "C" {
int gua_spatial_host_create(const gua_spatial_host_options_v1_t* o,gua_value_text_t clock,gua_spatial_host_t** out,gua_spatial_error_t* e) {
    if(out) *out=nullptr;
    return boundary(e,[&] {
        require(out&&o&&o->struct_size==sizeof(*o)&&o->max_providers&&o->max_providers<=64&&o->max_owners&&o->max_owners<=1024&&o->max_queue_depth&&o->max_queue_depth<=4096&&o->max_queries_per_batch&&o->max_queries_per_batch<=64&&o->max_hits_per_query&&o->max_hits_per_query<=32);
        require(std::isfinite(o->query_deadline_ms)&&o->query_deadline_ms>0&&std::isfinite(o->max_batch_work_ms)&&o->max_batch_work_ms>0&&clock.data&&clock.size&&clock.size<=256);
        require(gua_value_detail::unicode(std::string_view(clock.data,clock.size)));
        auto h=std::make_unique<gua_spatial_host_t>(); h->limits=*o; h->clock.assign(clock.data,clock.size); *out=h.release();
    });
}
void gua_spatial_host_destroy(gua_spatial_host_t* h) { delete h; }
int gua_spatial_host_register(gua_spatial_host_t* h,const gua_spatial_document_t* d,uint64_t* out,gua_spatial_error_t* e) {
    if(out) *out=0;
    return boundary(e,[&] {
        host(h); document(d,7); require(out); std::lock_guard lock(h->mutex); require(h->providers.size()<h->limits.max_providers,GUA_SPATIAL_CAPACITY);
        auto j=d->value; auto& p=j.fields.at("provider");
        for(auto& [k,v]:h->providers) { (void)k; require(text(v.registration.at("provider"),"spaceId")!=text(p,"spaceId"),GUA_SPATIAL_CONTEXT); }
        auto& l=p.fields.at("limits");
        l.fields["maxQueriesPerBatch"]=integer(std::min<uint64_t>(sequence(l.at("maxQueriesPerBatch")),h->limits.max_queries_per_batch));
        l.fields["maxHitsPerQuery"]=integer(std::min<uint64_t>(sequence(l.at("maxHitsPerQuery")),h->limits.max_hits_per_query));
        l.fields["maxDeadlineMs"]=number(std::min(num(l.at("maxDeadlineMs")),h->limits.query_deadline_ms));
        auto id=token(*h); h->providers.emplace(id,host_provider{std::move(j)}); *out=id;
    });
}
int gua_spatial_host_unregister(gua_spatial_host_t* h,uint64_t p,gua_spatial_error_t* e) {
    return boundary(e,[&] {
        host(h); std::lock_guard lock(h->mutex); require(h->providers.erase(p)!=0,GUA_SPATIAL_STALE);
        for(auto it=h->batches.begin();it!=h->batches.end();) {
            auto& b=it->second;
            if(b.provider==p) { if(!b.done) finish(*h,b,"provider_unregistered"); if(b.owner_closed) { it=h->batches.erase(it); continue; } }
            ++it;
        }
        for(auto it=h->leases.begin();it!=h->leases.end();) { if(it->second==p) it=h->leases.erase(it); else ++it; }
    });
}
int gua_spatial_host_open_owner(gua_spatial_host_t* h,const gua_spatial_document_t* d,uint64_t* out,gua_spatial_error_t* e) {
    if(out) *out=0;
    return boundary(e,[&] { host(h); document(d,8); require(out); std::lock_guard lock(h->mutex); require(h->owners.size()<h->limits.max_owners,GUA_SPATIAL_CAPACITY); auto id=token(*h); h->owners.emplace(id,host_owner{d->value}); *out=id; });
}
int gua_spatial_host_set_owner(gua_spatial_host_t* h,uint64_t o,const gua_spatial_document_t* d,gua_spatial_error_t* e) {
    return boundary(e,[&] { host(h); document(d,8); std::lock_guard lock(h->mutex); auto it=h->owners.find(o); require(it!=h->owners.end(),GUA_SPATIAL_STALE); it->second.grants=d->value; for(auto& [k,b]:h->batches) { (void)k; if(b.owner==o) { for(const auto& q:b.input.at("queries").items) if(!authorized(*h,b,q)) { revoke(*h,b); break; } } } });
}
int gua_spatial_host_close_owner(gua_spatial_host_t* h,uint64_t o,gua_spatial_error_t* e) {
    return boundary(e,[&] {
        host(h); std::lock_guard lock(h->mutex); require(h->owners.erase(o)!=0,GUA_SPATIAL_STALE);
        for(auto it=h->batches.begin();it!=h->batches.end();) {
            auto& b=it->second;
            if(b.owner!=o) { ++it; continue; }
            if(b.lease&&h->leases.contains(b.lease)) {
                const bool running=b.inflight;
                b.owner_closed=true; revoke(*h,b);
                // Retain consumed correlation until completion/end, with no
                // publishable geometry or closed-owner polling authority.
                b.inflight=running; b.done=!running; ++it;
            } else it=h->batches.erase(it);
        }
    });
}
int gua_spatial_host_enqueue(gua_spatial_host_t* h,uint64_t o,const gua_spatial_document_t* d,gua_spatial_error_t* e) {
    return boundary(e,[&] {
        host(h); document(d,5); std::lock_guard lock(h->mutex); host_batch b; b.owner=o; b.id=static_cast<uint64_t>(sequence(d->value.at("batchId"))); b.input=d->value;
        const auto& qs=b.input.at("queries").items;
        // Resolve privately; public authorization precedes context/support/capacity.
        for(const auto& [id,p]:h->providers) if(text(p.registration.at("provider"),"spaceId")==text(qs.front(),"spaceId")) b.provider=id;
        for(const auto& q:qs) require(authorized(*h,b,q)!=nullptr,GUA_SPATIAL_NOT_AUTHORIZED);
        auto& p=h->providers.at(b.provider).registration.at("provider");
        require(qs.size()<=static_cast<size_t>(sequence(p.at("limits").at("maxQueriesPerBatch"))),GUA_SPATIAL_INVALID);
        gua_spatial_document_t pd{3,p,{}};
        for(auto& q:b.input.fields.at("queries").items) {
            if(!has(q,"maxHits")) q.fields["maxHits"]=p.at("limits").at("maxHitsPerQuery");
            gua_spatial_document_t qd{1,q,{}}; gua_spatial_error_t error{};
            const auto status=gua_spatial_check_request(&qd,&pd,&error); require(status==0,status);
        }
        require(!h->batches.contains({o,b.id}),GUA_SPATIAL_CONTEXT);
        for(const auto& [k,a]:h->batches) if(k.first==o) for(const auto& q:a.input.at("queries").items) for(const auto& n:qs) require(sequence(q.at("requestId"))!=sequence(n.at("requestId")),GUA_SPATIAL_CONTEXT);
        require(h->batches.size()<h->limits.max_queue_depth,GUA_SPATIAL_CAPACITY);
        for(const auto& q:qs) b.retained_bytes+=dump(item(q,"notExecuted","provider_unregistered")).size();
        require(b.retained_bytes<=1024*1024,GUA_SPATIAL_CAPACITY);
        b.accepted=now(*h); b.order=++h->order; b.items.resize(qs.size()); b.dispatched.resize(qs.size()); b.prepared.resize(qs.size()); h->batches.emplace(std::make_pair(o,b.id),std::move(b));
    });
}
int gua_spatial_host_describe(gua_spatial_host_t* h,uint64_t o,uint64_t p,gua_spatial_document_t** out,gua_spatial_error_t* e) {
    if(out) *out=nullptr;
    return boundary(e,[&] {
        host(h); require(out); std::lock_guard lock(h->mutex); auto oi=h->owners.find(o); auto pi=h->providers.find(p);
        require(oi!=h->owners.end()&&pi!=h->providers.end(),GUA_SPATIAL_NOT_AUTHORIZED);
        const auto& g=oi->second.grants; require(boolean(g.at("enabled"))&&(text(g,"profile")=="Debug"||text(g,"profile")=="Testing"),GUA_SPATIAL_NOT_AUTHORIZED);
        auto descriptor=pi->second.registration.at("provider"); auto& policies=descriptor.fields.at("policies").items;
        policies.erase(std::remove_if(policies.begin(),policies.end(),[&](const json& id) { return !contains(g.at("policies"),text(id)); }),policies.end());
        require(!policies.empty(),GUA_SPATIAL_NOT_AUTHORIZED);
        auto r=obj({{"schemaVersion",str("spatial-host-r1")},{"documentType",str("advertisement")},{"provider",std::move(descriptor)},
            {"budgets",obj({{"maxQueueDepth",integer(h->limits.max_queue_depth)},{"queryDeadlineMs",number(h->limits.query_deadline_ms)},{"maxBatchWorkTimeMs",number(h->limits.max_batch_work_ms)}})}});
        host_document(r,11); *out=doc(11,std::move(r)).release();
    });
}
int gua_spatial_host_cancel(gua_spatial_host_t* h,uint64_t o,uint64_t id,gua_spatial_error_t* e) {
    return boundary(e,[&] { host(h); std::lock_guard lock(h->mutex); require(h->owners.contains(o),GUA_SPATIAL_STALE); auto it=h->batches.find({o,id}); require(it!=h->batches.end(),GUA_SPATIAL_STALE); auto& b=it->second; if(!b.done) { b.cancelled=true; finish(*h,b,"cancelled"); } });
}
int gua_spatial_host_begin(gua_spatial_host_t* h,uint64_t p,const gua_spatial_document_t* d,uint64_t* out,gua_spatial_error_t* e) {
    if(out) *out=0;
    return boundary(e,[&] {
        host(h); document(d,9); require(out); std::lock_guard lock(h->mutex); auto pi=h->providers.find(p); require(pi!=h->providers.end(),GUA_SPATIAL_STALE); require(pi->second.lease==0,GUA_SPATIAL_NOT_READY);
        host_batch* first=nullptr;
        for(auto& [k,b]:h->batches) {
            (void)k; if(b.provider!=p||b.done||b.lease) continue;
            while(!b.done) { auto reason=stopped(*h,b); if(!reason) break; finish(*h,b,reason); if(std::string_view(reason)!="deadline_exceeded") break; }
            if(!b.done&&(!first||b.order<first->order)) first=&b;
        }
        require(first,GUA_SPATIAL_NOT_READY);
        if(has(d->value,"worldSnapshot")) require(sequence(d->value.at("worldSnapshot").at("sessionEpoch"))==sequence(first->input.at("queries").items.front().at("sessionEpoch")),GUA_SPATIAL_CONTEXT);
        first->boundary=d->value; first->started=now(*h); first->lease=token(*h); h->leases.emplace(first->lease,p); pi->second.lease=first->lease; *out=first->lease;
    });
}
int gua_spatial_host_take(gua_spatial_host_t* h,uint64_t lease,gua_spatial_document_t** out,gua_spatial_error_t* e) {
    if(out) *out=nullptr;
    return boundary(e,[&] {
        host(h); require(out); std::lock_guard lock(h->mutex);
        require(h->leases.contains(lease),GUA_SPATIAL_STALE);
        host_batch* pending=nullptr;
        for(auto& [key,candidate]:h->batches) { (void)key; if(candidate.lease==lease&&!candidate.done) pending=&candidate; }
        require(pending,GUA_SPATIAL_NOT_READY); auto& b=*pending; require(!b.inflight,GUA_SPATIAL_NOT_READY);
        while(auto reason=stopped(*h,b)) { finish(*h,b,reason); if(b.done||std::string_view(reason)!="deadline_exceeded") fail(GUA_SPATIAL_NOT_READY); }
        require(b.next<b.items.size(),GUA_SPATIAL_NOT_READY);
        const auto& q=b.input.at("queries").items[b.next]; auto pol=authorized(*h,b,q); require(pol,GUA_SPATIAL_NOT_AUTHORIZED);
        auto owned=doc(1,q); b.revision=static_cast<uint64_t>(sequence(pol->at("revision"))); b.inflight=true; b.dispatched[b.next]=true; *out=owned.release();
    });
}
int gua_spatial_host_check_engine_bounds(gua_spatial_host_t* h,uint64_t lease,const gua_spatial_engine_bounds_v1_t* input,uint32_t* loaded,gua_spatial_error_t* e) {
    if(loaded) *loaded=0;
    return boundary(e,[&] {
        host(h); require(input&&loaded&&input->struct_size==sizeof(*input));
        bounds volume{{input->min_x,input->min_y,input->min_z},{input->max_x,input->max_y,input->max_z}};
        for(size_t i=0;i<3;++i) require(std::isfinite(volume.lo[i])&&std::isfinite(volume.hi[i])&&volume.lo[i]<=volume.hi[i]);
        std::lock_guard lock(h->mutex); auto& b=leased(*h,lease);
        require(b.inflight&&!b.owner_closed&&b.items[b.next].type==json::null,GUA_SPATIAL_NOT_READY);
        require(!b.prepared[b.next],GUA_SPATIAL_NOT_READY);
        if(auto reason=stopped(*h,b)) { finish(*h,b,reason); fail(GUA_SPATIAL_NOT_READY); }
        const auto& q=b.input.at("queries").items[b.next];
        require(fits(query_bounds(q),volume),GUA_SPATIAL_GEOMETRY);
        auto pol=authorized(*h,b,q); require(pol,GUA_SPATIAL_NOT_AUTHORIZED);
        require(fits(volume,aabb(pol->at("region")))&&fits(volume,aabb(h->owners.at(b.owner).grants.at("region"))),GUA_SPATIAL_NOT_AUTHORIZED);
        b.prepared[b.next]=volume;
        const auto& registration=h->providers.at(b.provider).registration;
        *loaded=has(registration,"loadedRegion")&&fits(volume,aabb(registration.at("loadedRegion")))?1u:0u;
    });
}
int gua_spatial_host_complete(gua_spatial_host_t* h,uint64_t lease,const gua_spatial_document_t* d,gua_spatial_error_t* e) {
    return boundary(e,[&] {
        host(h); document(d,10); std::lock_guard lock(h->mutex); auto& b=leased(*h,lease); require(b.inflight,GUA_SPATIAL_NOT_READY);
        // Expired backend work is discarded before its result facts can turn
        // the truthful stop reason into a payload validation failure.
        if(!b.owner_closed) {
            if(b.items[b.next].type!=json::null) { b.inflight=false; ++b.next; advance(b); fail(GUA_SPATIAL_NOT_READY); }
            if(auto reason=stopped(*h,b)) { finish(*h,b,reason); if(std::string_view(reason)=="deadline_exceeded") { b.inflight=false; ++b.next; advance(b); } fail(GUA_SPATIAL_NOT_READY); }
        }
        const auto& q=b.input.at("queries").items[b.next]; gua_spatial_document_t qd{1,q,{}}; gua_spatial_error_t error{};
        if(gua_spatial_check_result(&qd,d,&error)!=0) { finish(*h,b,"internal"); fail(error.code); }
        if(b.owner_closed) { b.inflight=false; b.done=true; return; }
        if(auto reason=stopped(*h,b)) { finish(*h,b,reason); if(std::string_view(reason)=="deadline_exceeded") { b.inflight=false; ++b.next; advance(b); } fail(GUA_SPATIAL_NOT_READY); }
        if(text(d->value,"status")!="completed") {
            auto code=text(d->value.at("error"),"code"); if(code=="invalid_request"||code=="unsupported_policy") code="internal";
            b.items[b.next]=item(q,"failed",code.c_str());
        } else {
            auto r=d->value; auto& registration=h->providers.at(b.provider).registration;
            if(text(r.at("coverage"),"state")=="complete") {
                const auto volume=b.prepared[b.next].value_or(query_bounds(q));
                if(!has(registration,"loadedRegion")||!fits(volume,aabb(registration.at("loadedRegion")))||!fits(aabb(r.at("coverage").at("loadedRegion")),aabb(registration.at("loadedRegion")))||!fits(volume,aabb(r.at("coverage").at("loadedRegion")))) { finish(*h,b,"internal"); fail(GUA_SPATIAL_SEMANTICS); }
            }
            json s=obj({{"physicsSampleId",b.boundary.at("physicsSampleId")},{"clockId",str(h->clock)},{"observedFromMs",number(b.started)},{"observedToMs",number(now(*h))},{"queryPolicyRevision",integer(b.revision)}});
            if(has(b.boundary,"tick")) s.fields["tick"]=b.boundary.at("tick");
            if(has(b.boundary,"worldSnapshot")) s.fields["worldSnapshot"]=b.boundary.at("worldSnapshot");
            r.fields["sample"]=std::move(s); r.fields["documentType"]=str("result"); host_document(r,4);
            auto completed=obj({{"requestId",q.at("requestId")},{"queryId",q.at("queryId")},{"state",str("completed")},{"result",std::move(r)}});
            const size_t extra=dump(completed).size()-dump(item(q,"notExecuted","provider_unregistered")).size();
            if(extra>1024*1024-b.retained_bytes) { finish(*h,b,"internal"); fail(GUA_SPATIAL_CAPACITY); }
            b.retained_bytes+=extra; b.items[b.next]=std::move(completed);
        }
        b.inflight=false; ++b.next;
        advance(b);
    });
}
int gua_spatial_host_end(gua_spatial_host_t* h,uint64_t lease,gua_spatial_error_t* e) {
    return boundary(e,[&] {
        host(h); std::lock_guard lock(h->mutex); auto li=h->leases.find(lease); require(li!=h->leases.end(),GUA_SPATIAL_STALE);
        for(auto it=h->batches.begin();it!=h->batches.end();) {
            auto& b=it->second;
            if(b.lease!=lease) { ++it; continue; }
            if(!b.done) if(auto reason=stopped(*h,b)) finish(*h,b,reason);
            // Every boundary end is terminal even when the specific stop above
            // affected only one query. Never re-lease a partially stopped batch.
            terminate(b,"boundary_ended");
            b.lease=0;
            if(b.owner_closed) it=h->batches.erase(it); else ++it;
        }
        auto p=h->providers.find(li->second); if(p!=h->providers.end()&&p->second.lease==lease) p->second.lease=0; h->leases.erase(li);
    });
}
int gua_spatial_host_poll(gua_spatial_host_t* h,uint64_t o,uint64_t id,gua_spatial_document_t** out,gua_spatial_error_t* e) {
    if(out) *out=nullptr;
    return boundary(e,[&] {
        host(h); require(out); std::lock_guard lock(h->mutex); require(h->owners.contains(o),GUA_SPATIAL_STALE); auto it=h->batches.find({o,id}); require(it!=h->batches.end(),GUA_SPATIAL_STALE); auto& b=it->second;
        if(!b.done) { if(auto reason=stopped(*h,b)) finish(*h,b,reason); }
        require(b.done,GUA_SPATIAL_NOT_READY);
        // Reauthorize even previously completed items before crossing public boundary.
        if(!h->providers.contains(b.provider)) {
            // Provider removal invalidates geometry, not the recorded termination
            // reason. Independently recheck the owner's current public authority.
            const auto& qs=b.input.at("queries").items;
            for(size_t i=0;i<qs.size();++i) {
                if(!owner_authorized(*h,b,qs[i])) b.items[i]=item(qs[i],b.dispatched[i]?"failed":"notExecuted","not_authorized");
                else if(has(b.items[i],"result")) b.items[i]=item(qs[i],"failed","provider_unregistered");
            }
        } else for(const auto& q:b.input.at("queries").items) if(!authorized(*h,b,q)) { revoke(*h,b); break; }
        json items; items.type=json::array; items.items=b.items;
        auto r=obj({{"schemaVersion",str("spatial-host-r1")},{"documentType",str("batchResult")},{"batchId",integer(b.id)},{"items",std::move(items)}}); host_document(r,6);
        auto owned=doc(6,std::move(r)); h->batches.erase(it); *out=owned.release();
    });
}
}

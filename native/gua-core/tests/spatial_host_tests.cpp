#include "gua/spatial_host.h"
#include "gua/spatial.hpp"
#include "../src/value_json.hpp"
#include <cassert>
#include <fstream>
#include <sstream>
#include <thread>
#include <chrono>
#include <atomic>
using gua_value_detail::json;
std::string wire(const json& j) {
    if(j.type==json::string) return gua_value_detail::quote(j.text);
    if(j.type==json::number||j.type==json::boolean) return j.text;
    if(j.type==json::null) return "null";
    std::string s=j.type==json::array?"[":"{"; bool first=true;
    if(j.type==json::array) for(const auto& v:j.items) { if(!first) s+=','; first=false; s+=wire(v); }
    else for(const auto& [k,v]:j.fields) { if(!first) s+=','; first=false; s+=gua_value_detail::quote(k)+':'+wire(v); }
    return s+(j.type==json::array?"]":"}");
}
json parse(const std::string& s) { return gua_value_detail::parser(s).parse(); }
std::string copy(gua_spatial_document_t* d) {
    int n=gua_spatial_copy_json(d,nullptr,0); std::string s(static_cast<size_t>(n),'\0');
    assert(gua_spatial_copy_json(d,s.data(),n)==n); s.pop_back(); gua_spatial_destroy(d); return s;
}
struct Fixture {
    json f,reg,owner,batch,boundary,execution;
    gua_spatial_host_t* h=nullptr; uint64_t p=0,o=0; gua_spatial_error_t e{};
    Fixture(double deadline=1000,double work=1000,uint32_t capacity=4,uint32_t hits=32) {
        std::ifstream in(GUA_SPATIAL_HOST_FIXTURE); std::stringstream s; s<<in.rdbuf(); f=parse(s.str());
        auto valid=f.at("valid").items; reg=valid[0].at("json"); owner=valid[1].at("json"); batch=valid[2].at("json"); boundary=valid[3].at("json"); execution=valid[4].at("json");
        gua_spatial_host_options_v1_t options{sizeof(options),2,4,capacity,64,hits,deadline,work};
        assert(gua_spatial_host_create(&options,{"clock",5},&h,&e)==0);
        gua::SpatialDocument rd(7,wire(reg)),od(8,wire(owner));
        assert(gua_spatial_host_register(h,rd.get(),&p,&e)==0); assert(gua_spatial_host_open_owner(h,od.get(),&o,&e)==0);
    }
    ~Fixture() { gua_spatial_host_destroy(h); }
    int enqueue(const json& j) { gua::SpatialDocument d(5,wire(j)); return gua_spatial_host_enqueue(h,o,d.get(),&e); }
    int enqueue() { return enqueue(batch); }
    uint64_t begin() { gua::SpatialDocument d(9,wire(boundary)); uint64_t lease=0; assert(gua_spatial_host_begin(h,p,d.get(),&lease,&e)==0); return lease; }
    json take(uint64_t lease) { gua_spatial_document_t* d=nullptr; assert(gua_spatial_host_take(h,lease,&d,&e)==0); return parse(copy(d)); }
    json poll() { gua_spatial_document_t* d=nullptr; assert(gua_spatial_host_poll(h,o,1,&d,&e)==0); return parse(copy(d)); }
    int complete(uint64_t lease,const json& q) {
        auto r=execution; for(auto k:{"requestId","queryId","sessionEpoch","spaceId","spaceEpoch","kind"}) r.fields[k]=q.at(k);
        gua::SpatialDocument d(10,wire(r)); return gua_spatial_host_complete(h,lease,d.get(),&e);
    }
    void set_owner(json j) { gua::SpatialDocument d(8,wire(j)); assert(gua_spatial_host_set_owner(h,o,d.get(),&e)==0); }
};
int main() {
    {
        Fixture x;
        for(const auto& c:x.f.at("valid").items) { gua::SpatialDocument d(std::stoi(c.at("type").text),wire(c.at("json"))); gua::SpatialDocument again(d.type(),d.to_json()); assert(d.to_json()==again.to_json()); }
        for(const auto& c:x.f.at("invalid").items) { bool rejected=false; try { gua::SpatialDocument d(std::stoi(c.at("type").text),wire(c.at("json"))); } catch(const gua::SpatialError&) { rejected=true; } assert(rejected); }
        assert(x.enqueue()==0); uint64_t l=x.begin();
        gua_spatial_document_t* advertised=nullptr;
        assert(gua_spatial_host_describe(x.h,x.o,x.p,&advertised,&x.e)==0);
        auto advertisement=parse(copy(advertised)); assert(advertisement.at("budgets").at("maxQueueDepth").text=="4");
        assert(gua_spatial_host_describe(x.h,x.o,999,&advertised,&x.e)==GUA_SPATIAL_NOT_AUTHORIZED&&advertised==nullptr);
        auto a=x.take(l); assert(x.complete(l,a)==0); auto b=x.take(l); assert(x.complete(l,b)==0);
        gua_spatial_document_t* exhausted=reinterpret_cast<gua_spatial_document_t*>(1); assert(gua_spatial_host_take(x.h,l,&exhausted,&x.e)==GUA_SPATIAL_NOT_READY&&exhausted==nullptr);
        gua::SpatialDocument bd(9,wire(x.boundary)); uint64_t other=77;
        assert(gua_spatial_host_begin(x.h,x.p,bd.get(),&other,&x.e)==GUA_SPATIAL_NOT_READY&&other==0);
        auto r=x.poll(); assert(r.at("items").items.size()==2);
        assert(gua_spatial_host_take(x.h,l,&exhausted,&x.e)==GUA_SPATIAL_NOT_READY&&exhausted==nullptr);
        for(const auto& it:r.at("items").items) {
            const auto& s=it.at("result").at("sample"); assert(!s.fields.contains("tick")); assert(s.at("physicsSampleId").text=="sample"); assert(s.at("clockId").text=="clock"); assert(s.at("queryPolicyRevision").text=="7");
            assert(it.at("result").at("coverage").at("state").text=="unknown");
        }
        assert(gua_spatial_host_end(x.h,l,&x.e)==0); assert(gua_spatial_host_end(x.h,l,&x.e)==GUA_SPATIAL_STALE);
        gua_spatial_document_t* d=reinterpret_cast<gua_spatial_document_t*>(1); assert(gua_spatial_host_poll(x.h,x.o,1,&d,&x.e)==GUA_SPATIAL_STALE&&d==nullptr);
    }
    {
        Fixture x; auto forbidden=x.batch; forbidden.fields.at("queries").items[0].fields["queryPolicyId"]=parse("\"forbidden\"");
        auto unknown=forbidden; unknown.fields.at("queries").items[0].fields["queryPolicyId"]=parse("\"unknown\"");
        assert(x.enqueue(forbidden)==GUA_SPATIAL_NOT_AUTHORIZED); auto e=x.e; assert(x.enqueue(unknown)==e.code&&std::string(x.e.path)==e.path);
        for(auto profile:{"Player","PublicAgent"}) { auto g=x.owner; g.fields["profile"]=parse(gua_value_detail::quote(profile)); x.set_owner(g); assert(x.enqueue()==GUA_SPATIAL_NOT_AUTHORIZED); }
        auto g=x.owner; g.fields["enabled"]=parse("false"); x.set_owner(g); assert(x.enqueue()==GUA_SPATIAL_NOT_AUTHORIZED);
        x.set_owner(x.owner); auto b=x.batch; b.fields.at("queries").items[0].fields["spaceEpoch"]=parse("2"); b.fields.at("queries").items[1].fields["spaceEpoch"]=parse("2"); assert(x.enqueue(b)==GUA_SPATIAL_CONTEXT);
    }
    {
        Fixture x; auto q=x.batch.at("queries").items[0]; q.fields.erase("segment"); q.fields["kind"]=parse("\"overlap\"");
        for(auto s:{R"({"type":"sphere","center":{"x":9,"y":0,"z":0},"radius":2})",R"({"type":"sphere","center":{"x":10,"y":0,"z":0},"radius":1e-16})",R"({"type":"capsule","pointA":{"x":0,"y":0,"z":0},"pointB":{"x":10,"y":0,"z":0},"radius":1e-16})",R"({"type":"capsule","pointA":{"x":0,"y":0,"z":0},"pointB":{"x":9,"y":0,"z":0},"radius":2})",R"({"type":"box","center":{"x":9,"y":0,"z":0},"halfExtents":{"x":2,"y":1,"z":1},"basis":{"x":{"x":1,"y":0,"z":0},"y":{"x":0,"y":1,"z":0},"z":{"x":0,"y":0,"z":1}}})",R"({"type":"box","center":{"x":10,"y":0,"z":0},"halfExtents":{"x":1e-16,"y":1,"z":1},"basis":{"x":{"x":1,"y":0,"z":0},"y":{"x":0,"y":1,"z":0},"z":{"x":0,"y":0,"z":1}}})"}) {
            q.fields["shape"]=parse(s); auto b=x.batch; b.fields.at("queries").items={q}; assert(x.enqueue(b)==GUA_SPATIAL_NOT_AUTHORIZED);
        }
        q.fields["shape"]=parse(R"({"type":"sphere","center":{"x":0,"y":0,"z":0},"radius":1})"); q.fields["kind"]=parse("\"sweep\""); q.fields["delta"]=parse(R"({"x":10,"y":0,"z":0})"); auto b=x.batch; b.fields.at("queries").items={q}; assert(x.enqueue(b)==GUA_SPATIAL_NOT_AUTHORIZED);
        q.fields["shape"]=parse(R"({"type":"sphere","center":{"x":9,"y":0,"z":0},"radius":1})"); q.fields["delta"]=parse(R"({"x":1e-16,"y":0,"z":0})"); b.fields.at("queries").items={q}; assert(x.enqueue(b)==GUA_SPATIAL_NOT_AUTHORIZED);
        q.fields.erase("delta"); q.fields["kind"]=parse("\"overlap\""); b.fields.at("queries").items={q}; assert(x.enqueue(b)==0); // exact contact with region boundary stays legal
    }
    {
        Fixture x(1000,1000,1,2); assert(x.enqueue()==0);
        auto b=x.batch; b.fields["batchId"]=parse("2"); for(auto& q:b.fields.at("queries").items) { q.fields["requestId"]=parse(q.at("requestId").text+"0"); q.fields["queryId"]=parse(gua_value_detail::quote(q.at("queryId").text+"0")); }
        assert(x.enqueue(b)==GUA_SPATIAL_CAPACITY); auto l=x.begin(); auto q=x.take(l); assert(q.at("maxHits").text=="2"); assert(x.complete(l,q)==0);
        assert(gua_spatial_host_end(x.h,l,&x.e)==0); assert(x.enqueue(b)==GUA_SPATIAL_CAPACITY);
        auto r=x.poll(); assert(r.at("items").items[0].at("state").text=="completed"); assert(r.at("items").items[1].at("state").text=="notExecuted"); assert(x.enqueue(b)==0);
    }
    {
        Fixture x; assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l);
        std::thread cancel([&] { gua_spatial_error_t e{}; assert(gua_spatial_host_cancel(x.h,x.o,1,&e)==0); }); cancel.join();
        assert(x.complete(l,q)==GUA_SPATIAL_STALE); auto r=x.poll(); assert(r.at("items").items[0].at("state").text=="failed"); assert(r.at("items").items[1].at("state").text=="notExecuted"); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
    }
    {
        Fixture x; assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l); assert(x.complete(l,q)==0);
        auto g=x.owner; g.fields["enabled"]=parse("false"); x.set_owner(g); auto r=x.poll();
        assert(r.at("items").items[0].at("reason").text=="not_authorized"); assert(!r.at("items").items[0].fields.contains("result")); assert(r.at("items").items[1].at("state").text=="notExecuted"); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
    }
    {
        Fixture x; assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l); assert(gua_spatial_host_unregister(x.h,x.p,&x.e)==0);
        assert(x.complete(l,q)==GUA_SPATIAL_STALE); auto r=x.poll(); assert(!r.at("items").items[0].fields.contains("result")); assert(gua_spatial_host_end(x.h,l,&x.e)==GUA_SPATIAL_STALE);
    }
    {
        Fixture x(1000,1000,1); assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l); assert(gua_spatial_host_close_owner(x.h,x.o,&x.e)==0);
        assert(gua_spatial_host_cancel(x.h,x.o,1,&x.e)==GUA_SPATIAL_STALE);
        gua::SpatialDocument grants(8,wire(x.owner)); uint64_t new_owner=0; assert(gua_spatial_host_open_owner(x.h,grants.get(),&new_owner,&x.e)==0);
        gua::SpatialDocument batch(5,wire(x.batch)); assert(gua_spatial_host_enqueue(x.h,new_owner,batch.get(),&x.e)==GUA_SPATIAL_CAPACITY);
        assert(x.complete(l,q)==0);
        gua_spatial_document_t* result=nullptr; assert(gua_spatial_host_poll(x.h,x.o,1,&result,&x.e)==GUA_SPATIAL_STALE&&result==nullptr);
        assert(x.complete(l,q)==GUA_SPATIAL_STALE); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
        assert(gua_spatial_host_enqueue(x.h,new_owner,batch.get(),&x.e)==0);
    }
    {
        Fixture x(1000,5); assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l); std::this_thread::sleep_for(std::chrono::milliseconds(15));
        assert(x.complete(l,q)==GUA_SPATIAL_NOT_READY); auto r=x.poll(); assert(r.at("items").items[0].at("reason").text=="work_budget"); assert(r.at("items").items[1].at("state").text=="notExecuted"); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
    }
    {
        Fixture x(1000,1000,1); assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l);
        assert(gua_spatial_host_close_owner(x.h,x.o,&x.e)==0); assert(gua_spatial_host_unregister(x.h,x.p,&x.e)==0);
        assert(x.complete(l,q)==GUA_SPATIAL_STALE); assert(gua_spatial_host_end(x.h,l,&x.e)==GUA_SPATIAL_STALE);
        x.reg.fields.at("provider").fields["spaceEpoch"]=parse("2"); for(auto& query:x.batch.fields.at("queries").items) query.fields["spaceEpoch"]=parse("2");
        gua::SpatialDocument registration(7,wire(x.reg)),grants(8,wire(x.owner));
        assert(gua_spatial_host_register(x.h,registration.get(),&x.p,&x.e)==0); assert(gua_spatial_host_open_owner(x.h,grants.get(),&x.o,&x.e)==0); assert(x.enqueue()==0);
    }
    {
        Fixture x(5); for(auto& q:x.batch.fields.at("queries").items) q.fields["deadlineMs"]=parse("5"); assert(x.enqueue()==0); std::this_thread::sleep_for(std::chrono::milliseconds(15));
        auto r=x.poll(); for(auto& it:r.at("items").items) assert(it.at("state").text=="notExecuted"&&it.at("reason").text=="deadline_exceeded");
    }
    {
        Fixture x; assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l); auto r=x.execution; r.fields["queryId"]=parse("\"wrong\""); gua::SpatialDocument d(10,wire(r));
        assert(gua_spatial_host_complete(x.h,l,d.get(),&x.e)==GUA_SPATIAL_CONTEXT); auto result=x.poll(); assert(result.at("items").items[0].at("state").text=="failed"); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
    }
    {
        Fixture x; x.execution.fields["coverage"]=parse(R"({"state":"complete","loadedRegion":{"min":{"x":-20,"y":-20,"z":-20},"max":{"x":20,"y":20,"z":20}}})"); assert(x.enqueue()==0); auto l=x.begin(); auto q=x.take(l); assert(x.complete(l,q)==GUA_SPATIAL_SEMANTICS); auto r=x.poll(); assert(r.at("items").items[0].at("reason").text=="internal"); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
    }
    {
        Fixture x; x.execution.fields["outcome"]=parse("\"hit\"");
        auto hit=parse(R"({"relation":"unknown","missing":{"position":"unknown","distance":"unknown","normal":"unknown","collisionRef":"unknown","worldObjectId":"unknown"}})");
        hit.fields.at("missing").fields.at("normal").text=std::string(700000,'x');
        x.execution.fields["hits"].items.push_back(hit); assert(x.enqueue()==0); auto l=x.begin(); auto a=x.take(l); assert(x.complete(l,a)==0); auto b=x.take(l); assert(x.complete(l,b)==GUA_SPATIAL_CAPACITY);
        auto r=x.poll(); assert(wire(r).size()<1024*1024); gua::SpatialDocument roundtrip(6,wire(r)); assert(r.at("items").items[0].at("state").text=="completed"); assert(r.at("items").items[1].at("state").text=="failed"); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
    }
    // Dispatch/host mutation race: neither side holds the mutex during fake engine work.
    for(int i=0;i<50;++i) {
        Fixture x; assert(x.enqueue()==0); auto l=x.begin(); std::atomic<bool> go=false;
        std::thread t([&] { while(!go.load()) std::this_thread::yield(); gua_spatial_error_t e{}; assert(gua_spatial_host_cancel(x.h,x.o,1,&e)==0); });
        go=true; gua_spatial_document_t* d=nullptr; auto code=gua_spatial_host_take(x.h,l,&d,&x.e); assert(code==0||code==GUA_SPATIAL_NOT_READY); gua_spatial_destroy(d); t.join(); auto r=x.poll(); assert(r.at("items").items[1].at("state").text=="notExecuted"); assert(gua_spatial_host_end(x.h,l,&x.e)==0);
    }
}

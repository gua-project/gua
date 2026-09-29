#include "gua/observe.hpp"
#include "../src/value_json.hpp"
#include <cassert>
#include <fstream>
#include <sstream>
#include <thread>
#include <iostream>
using gua_value_detail::json;
using gua_value_detail::parser;
static json parse(const std::string& s) { return parser(s).parse(); }
static std::string snap(gua_context_t* c,int p=0) { gua_observe_result_t* r=nullptr; assert(!gua_observe_snapshot(c,p,&r)); return gua::observe_result(r); }
static uint64_t owner(gua_context_t* c,int source,const char* id="") { uint64_t out=0; assert(!gua_observe_create_owner(c,source,gua::value_text(id),&out)); return out; }
static uint64_t reg(gua_context_t* c,uint64_t o,const char* name="phase",bool player=false,bool sensitive=false) {
    gua_observe_registration_v1_t d{sizeof(d),o,gua::value_text(name),player?1:0,sensitive?1:0}; uint64_t r=0; assert(!gua_observe_register_v1(c,&d,&r)); return r;
}
static void put(gua_context_t* c,uint64_t id,int n,bool stage=false) { auto v=gua::Value::integer(n); assert(!gua_observe_publish(c,id,v.get(),0,stage?1:0)); }
static void ui(gua_context_t* c,bool second=true,bool visible=true) {
    gua_begin_frame(c,"test"); gua_register_node(c,"a","button","A",{},visible,1);
    if(second) gua_register_node(c,"b","button","B",{},1,1);
    gua_end_frame(c);
}
static uint64_t subscribe(gua_context_t* c,int p=0) { gua_observe_result_t* r=nullptr; uint64_t id=0; assert(!gua_observe_subscribe(c,p,&id,&r)); gua_observe_result_destroy(r); return id; }
static json poll(gua_context_t* c,uint64_t id) { gua_observe_result_t* r=nullptr; assert(!gua_observe_poll(c,id,&r)); return parse(gua::observe_result(r)); }
static void reset(gua_context_t* c) { gua_reset_options_t o{}; o.struct_size=sizeof(o); o.flags=GUA_RESET_DEFAULT_V3; o.flags_version=GUA_RESET_FLAGS_VERSION_CURRENT; gua_reset_report_t r{};r.struct_size=sizeof(r);assert(gua_reset_context(c,&o,&r)==GUA_RESET_SUCCEEDED); }
static void lifetimes() {
    gua::Context context; auto c=context.native_handle(); ui(c);
    auto a=owner(c,1,"a"), b=owner(c,1,"b"), x=reg(c,a), y=reg(c,b); put(c,x,1);put(c,y,2);
    assert(parse(snap(c)).at("entries").items.size()==2);
    gua_observe_registration_v1_t duplicate{sizeof(duplicate),a,gua::value_text("phase"),0,0}; uint64_t id=99;
    assert(gua_observe_register_v1(c,&duplicate,&id)==GUA_OBSERVE_DUPLICATE && id==0);
    auto sub=subscribe(c); assert(!gua_observe_unregister(c,x)); auto replacement=reg(c,a); assert(replacement!=x);
    assert(gua_observe_publish(c,x,nullptr,100,0)==GUA_OBSERVE_STALE);
    auto events=poll(c,sub).at("events").items; assert(events.size()==2 && events[0].at("kind").text=="removed"); assert(events[0].at("before").at("value").text=="1");
    ui(c,false,false); // hidden a stays alive; b is gone
    assert(gua_observe_registration_alive(c,replacement)==0); assert(gua_observe_registration_alive(c,y)==GUA_OBSERVE_STALE);
    ui(c,true); auto b2=owner(c,1,"b"); assert(b2!=b);
    assert(!gua_observe_destroy_owner(c,a)); assert(gua_observe_registration_alive(c,replacement)==GUA_OBSERVE_STALE);
    auto fresh=reg(c,b2); put(c,fresh,3); auto old=subscribe(c); reset(c);
    assert(poll(c,old).at("status").text=="stale_session"); assert(parse(snap(c)).at("entries").items.empty());
}
static void publication() {
    gua::Context context; auto c=context.native_handle(); ui(c); auto o=owner(c,1,"a"), r=reg(c,o); auto sub=subscribe(c);
    put(c,r,1); auto first=poll(c,sub).at("events").items; assert(first[0].at("kind").text=="added"); assert(!first[0].fields.count("before"));
    gua_begin_frame(c,"test"); gua_register_node(c,"a","button","A",{},1,1); put(c,r,2,true);
    assert(parse(snap(c)).at("entries").items[0].at("value").at("value").text=="1");
    // Invalid frame discards the staged sample.
    gua_node_descriptor_v2_t bad{}; bad.struct_size=sizeof(bad); assert(!gua_register_node_v2(c,&bad)); gua_end_frame(c);
    assert(poll(c,sub).at("events").items.empty()); ui(c,false); assert(poll(c,sub).at("events").items.empty());
    put(c,r,2);put(c,r,1); auto changes=poll(c,sub).at("events").items; assert(changes.size()==2); assert(changes[0].at("before").at("value").text=="1");
    assert(!gua_observe_publish(c,r,nullptr,100,0)); auto unavailable=poll(c,sub).at("events").items[0];
    assert(unavailable.at("kind").text=="unavailable" && !unavailable.fields.count("after"));
    assert(!parse(snap(c)).at("entries").items[0].fields.count("value")); put(c,r,1);
    assert(poll(c,sub).at("events").items[0].at("kind").text=="recovered");
    auto one=gua::Value::integer(1),two=gua::Value::integer(2);
    auto set1=gua::Value::collection(GUA_VALUE_SET,GUA_VALUE_INTEGER,{&one,&two}); auto set2=gua::Value::collection(GUA_VALUE_SET,GUA_VALUE_INTEGER,{&two,&one});
    assert(!gua_observe_publish(c,r,set1.get(),0,0)); poll(c,sub); assert(!gua_observe_publish(c,r,set2.get(),0,0)); assert(poll(c,sub).at("events").items.empty());
    auto world=owner(c,3), property=reg(c,world,"weather"); assert(gua_observe_publish(c,property,one.get(),0,1)==GUA_OBSERVE_NO_FRAME);
    assert(gua_begin_world_frame(c,"world")); assert(!gua_observe_publish(c,property,one.get(),0,1)); assert(gua_end_world_frame(c));
    assert(snap(c).find("weather")!=std::string::npos);
}
static void continuity() {
    gua::Context context; auto c=context.native_handle(); auto o=owner(c,3), r=reg(c,o);put(c,r,0);
    auto s1=subscribe(c),s2=subscribe(c);put(c,r,1);assert(poll(c,s1).at("events").items.size()==1);assert(poll(c,s2).at("events").items.size()==1);
    assert(!gua_observe_set_limits(c,2,100000));put(c,r,2);put(c,r,3);put(c,r,4);assert(poll(c,s1).at("status").text=="gap");assert(poll(c,s1).at("status").text=="gap");
    auto s3=subscribe(c);assert(!gua_observe_set_limits(c,1024,1));put(c,r,5);assert(poll(c,s3).at("status").text=="gap");
    assert(!gua_observe_set_limits(c,1024,8*1024*1024));
    // Snapshot+cursor must partition every concurrent publication exactly once.
    std::thread writer([&]{for(int i=6;i<206;++i) put(c,r,i);});
    gua_observe_result_t* result=nullptr; uint64_t subscription=0;
    assert(!gua_observe_subscribe(c,0,&subscription,&result)); auto initial=parse(gua::observe_result(result));writer.join();
    auto after=poll(c,subscription); auto sequence=std::stoull(initial.at("sequence").text);
    for(const auto& event:after.at("events").items) assert(std::stoull(event.at("sequence").text)==++sequence);
    assert(sequence==std::stoull(after.at("sequence").text));
    gua_observe_result_t* immutable=nullptr; assert(!gua_observe_snapshot(c,0,&immutable)); int n=gua_observe_result_copy_json(immutable,nullptr,0);put(c,r,1000);
    char tiny[2]={'x','y'}; assert(gua_observe_result_copy_json(immutable,tiny,2)==n && tiny[0]==0 && tiny[1]=='y');gua_observe_result_destroy(immutable);
}
static void privacy() {
    gua::Context context; auto c=context.native_handle(); ui(c); auto a=owner(c,1,"a"), x=reg(c,a,"phase",true),secret=reg(c,a,"secret",true,true),debug=reg(c,a,"debug");
    auto marker=gua::Value::string("SECRET_MARKER"); assert(!gua_observe_publish(c,secret,marker.get(),0,0)); assert(!gua_observe_publish(c,debug,marker.get(),0,0));put(c,x,1);
    auto p=subscribe(c,1);put(c,x,2);ui(c,true,false);
    assert(poll(c,p).at("status").text=="gap"); assert(snap(c,1).find("SECRET_MARKER")==std::string::npos);assert(parse(snap(c,1)).at("entries").items.empty());
    assert(snap(c).find("\"error\":102")!=std::string::npos);
    ui(c); auto restored=subscribe(c,1);assert(poll(c,restored).at("events").items.empty());
    assert(snap(c,1).find("SECRET_MARKER")==std::string::npos);
    gua::Context isolated; assert(parse(snap(isolated.native_handle())).at("sourceId").text!=parse(snap(c)).at("sourceId").text);
}
static void cpp_getters() {
    gua::Context c;ui(c.native_handle()); gua::ObserveOwner owner(c,1,"a"); int value=0;
    auto r=owner.observe("phase",[&]{return gua::Value::integer(value);});gua::ObserveSubscription sub(c);
    auto frame=[&]{c.begin_frame("test");c.node("a","button","A",{});c.end_frame();};
    frame();sub.poll();value=1;value=0;frame();assert(parse(sub.poll()).at("events").items.empty());
    value=1;r.notify();value=0;r.notify();assert(parse(sub.poll()).at("events").items.size()==2);
    bool fail=true;auto failing=owner.observe("bad",[&]{if(fail) throw std::runtime_error("SECRET_MARKER");return gua::Value::boolean(true);});
    frame();assert(gua::observe_snapshot(c).find("SECRET_MARKER")==std::string::npos);fail=false;frame();assert(sub.poll().find("recovered")!=std::string::npos);
    owner.reset();frame(); // stale getters are pruned without execution
}
static void cpp_move_in_getter() {
    gua::Context c; ui(c.native_handle()); std::optional<gua::Context> moved;
    gua::ObserveOwner owner(c,1,"a"); bool first=true;
    auto registration=owner.observe("move",[&] { if(first) { first=false; moved.emplace(std::move(c)); } return gua::Value::integer(1); });
    c.begin_frame("test"); c.node("a","button","A",{}); c.end_frame();
    assert(moved); moved->end_frame();
    assert(gua::observe_snapshot(*moved).find("\"value\":1")!=std::string::npos);
}
static void cpp_sampling_failure_preserves_callbacks() {
    gua::Context c; ui(c.native_handle());
    gua::ObserveOwner world(c,GUA_OBSERVE_WORLD), ui_owner(c,GUA_OBSERVE_UI,"a");
    int world_calls=0, first_calls=0, last_calls=0;
    auto property=world.property("world",[&]{++world_calls;return gua::Value::integer(10);});
    auto first=ui_owner.observe("first",[&]{++first_calls;return gua::Value::integer(20);});
    auto last=ui_owner.observe("last",[&]{++last_calls;return gua::Value::integer(30);});
    bool failed=false;
    try { c.end_frame(); }
    catch(const gua::ObserveError& e) { failed=e.code==GUA_OBSERVE_NO_FRAME; }
    if(!failed) throw std::runtime_error("Expected missing-frame error");
    c.begin_frame("test"); c.node("a","button","A",{}); c.end_frame();
    if(first_calls!=2 || last_calls!=1) throw std::runtime_error("Sampling failure lost failed or unvisited callbacks");
    if(!gua_begin_world_frame(c.native_handle(),"world")) throw std::runtime_error("World begin failed");
    c.end_world_frame();
    c.begin_frame("test"); c.node("a","button","A",{}); c.end_frame();
    if(world_calls!=1 || first_calls!=3 || last_calls!=2) throw std::runtime_error("Callbacks lost or duplicated after recovery");
    const auto entries=parse(gua::observe_snapshot(c)).at("entries").items;
    if(entries.size()!=3) throw std::runtime_error("Registration count changed");
    for(const auto& entry:entries) if(entry.at("status").text!="available") throw std::runtime_error("Recovered registration was not sampled");
}
static void shared_fixture() {
    std::ifstream file(GUA_OBSERVE_FIXTURE); assert(file); std::stringstream buffer; buffer<<file.rdbuf(); auto f=parse(buffer.str());
    gua::Context context;auto c=context.native_handle();auto o=owner(c,3),r=reg(c,o),sub=subscribe(c);
    for(const auto& step:f.at("transitions").items) {
        if(step.fields.count("value")) put(c,r,std::stoi(step.at("value").text));
        else if(step.fields.count("error")) assert(!gua_observe_publish(c,r,nullptr,std::stoi(step.at("error").text),0));
        else assert(!gua_observe_unregister(c,r));
        auto events=poll(c,sub).at("events").items;assert(events.size()==1);assert(events[0].at("kind").text==step.at("kind").text);
    }
}
int main() { try { cpp_sampling_failure_preserves_callbacks(); cpp_move_in_getter(); shared_fixture(); lifetimes();publication();continuity();privacy();cpp_getters(); } catch(const std::exception& e) { std::cerr << e.what() << std::endl; return 1; } }
